using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Authentication;
using Idb.Transport;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--console"))
{
    Console.Error.WriteLine("Usage: InterfaceDB.TcpStore.Host <hostsettings.json> [--console]");
    return 2;
}

try
{
    var configurationPath = Path.GetFullPath(args[0]);
    var configurationDirectory = Path.GetDirectoryName(configurationPath)!;
    var configuration = JsonSerializer.Deserialize<HostConfiguration>(
        await File.ReadAllTextAsync(configurationPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter<TcpStoreAuthenticationMode>(allowIntegerValues: false),
                new JsonStringEnumConverter<X509RevocationMode>(allowIntegerValues: false)
            }
        }) ?? throw new ArgumentException("The host configuration is empty.");
    configuration.Validate();
    using var identityProviderCa = configuration.IdentityProviderCaCertificatePath is null ? null
        : X509CertificateLoader.LoadCertificateFromFile(Path.GetFullPath(configuration.IdentityProviderCaCertificatePath, configurationDirectory));
    if (identityProviderCa is not null && (!CertificateTrust.IsCurrentlyValid(identityProviderCa)
        || !identityProviderCa.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)))
        throw new ArgumentException("The identity provider trust certificate must be a currently valid CA certificate.");
    // The validator outlives HostLifetime; disposal therefore happens only after host work drains.
    using var tokenValidator = configuration.AuthenticationMode == TcpStoreAuthenticationMode.MutualTls
        ? null : new JwtAccessTokenValidator(configuration.JwtValidation!,
            identityProviderCa is null ? null : CertificateTrust.IdentityProviderHandler(identityProviderCa, configuration.PrivateCaRevocationMode));
    var trustedPins = configuration.TrustedClientCertificateSha256.Select(Convert.FromHexString).ToArray();
    var password = Environment.GetEnvironmentVariable(configuration.ServerCertificatePasswordEnvironmentVariable)
        ?? throw new ArgumentException("The configured server certificate password environment variable is not set.");

    using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
        Path.GetFullPath(configuration.ServerCertificatePath, configurationDirectory), password,
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
    if (!certificate.HasPrivateKey || !CertificateTrust.IsCurrentlyValid(certificate)
        || !CertificateTrust.HasUsage(certificate, CertificateTrust.ServerAuthentication))
        throw new ArgumentException("The server certificate must be valid now, have a private key and include the server authentication EKU.");

    bool IsTrustedClient(X509Certificate2 client) => CertificateTrust.IsCurrentlyValid(client)
        && CertificateTrust.HasUsage(client, CertificateTrust.ClientAuthentication)
        && trustedPins.Any(pin => CryptographicOperations.FixedTimeEquals(pin, client.GetCertHash(HashAlgorithmName.SHA256)));

    IAsyncDisposable owner;
    TcpStoreRegistration registration;
    if (configuration.VersionModuleType is { } moduleName)
    {
        // This type is trusted code shipped with the host. Network clients cannot select or upload modules.
        var moduleType = Type.GetType(moduleName, throwOnError: true)!;
        var module = Activator.CreateInstance(moduleType) as ITcpStoreVersionModule
            ?? throw new ArgumentException("The deployed type must implement ITcpStoreVersionModule.");
        var master = await VersionedTcpStore.OpenAsync(
            Path.GetFullPath(configuration.DataDirectory, configurationDirectory), configuration.StoreAlias,
            module.Versions, configuration.Limits, configuration.MaximumEnumerationKeys,
            legacyStoreDirectory: configuration.LegacyStoreDirectory is { } legacy
                ? Path.GetFullPath(legacy, configurationDirectory) : null,
            legacyManifest: configuration.Manifest,
            maximumPartitionKeys: configuration.MaximumSubjectKeys);
        owner = master; registration = master.Registration;
    }
    else
    {
        var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(
            Path.GetFullPath(configuration.DataDirectory, configurationDirectory), configuration.Limits,
            configuration.MaximumEnumerationKeys, maximumPartitionKeys: configuration.MaximumSubjectKeys);
        owner = store; registration = new(configuration.StoreAlias, configuration.Manifest, store);
    }
    await using var lifetime = new HostLifetime(owner);
    var host = await TcpStoreHost.StartAsync(new TcpStoreHostOptions
    {
        ListenEndPoint = new IPEndPoint(IPAddress.Parse(configuration.BindAddress), configuration.Port),
        ServerCertificate = certificate,
        Stores = [registration],
        Limits = configuration.Limits,
        MaximumConnections = configuration.MaximumConnections,
        MaximumSessionLifetime = configuration.MaximumSessionLifetime,
        AuthenticationMode = configuration.AuthenticationMode,
        IsolateBySubject = configuration.IsolateBySubject,
        TokenValidator = tokenValidator,
        // The configured scope grants store operations; IsolateBySubject additionally restricts all keys to the validated principal.
        // Applications requiring narrower operations/resources supply their own authorization policy.
        AuthorizeIdentity = tokenValidator is null ? null : (identity, alias, _) => string.Equals(alias, configuration.StoreAlias, StringComparison.Ordinal)
            && configuration.RequiredTokenScopes.All(required => identity.Principal.FindAll("scope")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(required, StringComparer.Ordinal)),
        ClientCertificateValidation = (_, presented, _, _) =>
        {
            if (presented is null) return false;
            if (presented is X509Certificate2 client) return IsTrustedClient(client);
            using var clientCopy = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
            return IsTrustedClient(clientCopy);
        },
        AuthorizeClient = (client, alias) => string.Equals(alias, configuration.StoreAlias, StringComparison.Ordinal)
            && IsTrustedClient(client),
        OnError = error => Console.Error.WriteLine($"Transport connection error: {error.GetType().Name}.")
    });
    lifetime.Host = host;

    using var shutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, signal) => { signal.Cancel = true; shutdown.Cancel(); };
    EventHandler onExit = (_, _) =>
    {
        shutdown.Cancel();
        // ProcessExit cannot await. Share the same disposal task with normal shutdown.
        try { lifetime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { Console.Error.WriteLine("Host cleanup did not complete during process exit."); }
    };
    Console.CancelKeyPress += onCancel;
    AppDomain.CurrentDomain.ProcessExit += onExit;
    using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(
        PosixSignal.SIGTERM, signal => { signal.Cancel = true; shutdown.Cancel(); });

    try
    {
        Console.WriteLine($"InterfaceDB TLS TCP host listening on {host.LocalEndPoint}, store '{configuration.StoreAlias}'.");
        Console.WriteLine("Stop with Ctrl+C or SIGTERM; one remote session can own this store at a time.");
        if (args.Length == 2)
        {
            Console.WriteLine("Console mode: enter 'stop' to close the host.");
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!shutdown.IsCancellationRequested)
                    {
                        var command = await Console.In.ReadLineAsync(shutdown.Token);
                        if (command is null) return;
                        if (string.Equals(command.Trim(), "stop", StringComparison.Ordinal))
                        {
                            shutdown.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            });
        }
        var stopped = Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
        if (await Task.WhenAny(host.Completion, stopped) == host.Completion)
            await host.Completion;
    }
    finally
    {
        shutdown.Cancel();
        Console.CancelKeyPress -= onCancel;
        AppDomain.CurrentDomain.ProcessExit -= onExit;
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Host startup or runtime failure: {error.GetType().Name}: {error.Message}");
    return 1;
}

internal sealed class HostConfiguration
{
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 7443;
    public required string StoreAlias { get; init; }
    public required string DataDirectory { get; init; }
    public string? VersionModuleType { get; init; }
    public string? LegacyStoreDirectory { get; init; }
    public required string ServerCertificatePath { get; init; }
    public string ServerCertificatePasswordEnvironmentVariable { get; init; } = "IDB_SERVER_PFX_PASSWORD";
    public string[] TrustedClientCertificateSha256 { get; init; } = [];
    public TcpStoreAuthenticationMode AuthenticationMode { get; init; } = TcpStoreAuthenticationMode.MutualTls;
    public bool IsolateBySubject { get; init; }
    public JwtAccessTokenValidatorOptions? JwtValidation { get; init; }
    public string? IdentityProviderCaCertificatePath { get; init; }
    public X509RevocationMode PrivateCaRevocationMode { get; init; } = X509RevocationMode.Online;
    public string[] RequiredTokenScopes { get; init; } = ["interfacedb.storage"];
    public required CompatibilityManifest Manifest { get; init; }
    public TransportLimits Limits { get; init; } = new();
    public int MaximumConnections { get; init; } = 16;
    public int MaximumEnumerationKeys { get; init; } = 100_000;
    public int MaximumSubjectKeys { get; init; } = 10_000;
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out _) || Port is < 1 or > 65535)
            throw new ArgumentException("BindAddress must be an IP address and Port must be 1 through 65535.");
        ArgumentException.ThrowIfNullOrWhiteSpace(StoreAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        if (LegacyStoreDirectory is not null && VersionModuleType is null)
            throw new ArgumentException("Legacy store adoption requires a deployed version module.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ServerCertificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ServerCertificatePasswordEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(Manifest);
        ArgumentNullException.ThrowIfNull(Limits);
        Limits.Validate();
        if (MaximumConnections is < 1 or > 1024 || MaximumEnumerationKeys < 1
            || MaximumSubjectKeys < 1 || MaximumSubjectKeys > MaximumEnumerationKeys
            || MaximumSessionLifetime < TimeSpan.FromMilliseconds(1)
            || MaximumSessionLifetime > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Invalid connection or enumeration limits.");
        if (IsolateBySubject && AuthenticationMode == TcpStoreAuthenticationMode.MutualTls)
            throw new ArgumentException("Subject isolation requires JWT authentication.");
        if (!Enum.IsDefined(AuthenticationMode)) throw new ArgumentException("The configured authentication mode is unknown.");
        if (AuthenticationMode == TcpStoreAuthenticationMode.MutualTls
            && (JwtValidation is not null || IdentityProviderCaCertificatePath is not null))
            throw new ArgumentException("JWT validation settings require JwtBearer or MutualTlsAndJwtBearer authentication mode.");
        if (IdentityProviderCaCertificatePath is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(IdentityProviderCaCertificatePath);
        if (PrivateCaRevocationMode is not (X509RevocationMode.Online or X509RevocationMode.NoCheck))
            throw new ArgumentException("Private CA revocation mode must be Online or NoCheck.");
        if (TrustedClientCertificateSha256 is null
            || (AuthenticationMode != TcpStoreAuthenticationMode.JwtBearer && TrustedClientCertificateSha256.Length == 0)
            || TrustedClientCertificateSha256.Any(pin => pin is null || pin.Length != 64 || pin.Any(c => !Uri.IsHexDigit(c))))
            throw new ArgumentException("At least one trusted client certificate SHA-256 fingerprint is required, using exactly 64 hexadecimal characters per certificate.");
        if (AuthenticationMode != TcpStoreAuthenticationMode.MutualTls)
        {
            if (JwtValidation?.Authorities is not { Count: > 0 })
                throw new ArgumentException("JWT authentication requires explicitly configured trusted authorities.");
            if (JwtValidation.Authorities.Any(authority => authority is null || authority.SigningKeys is not { Count: 0 }))
                throw new ArgumentException("The sample host accepts HTTPS JWKS configuration; configure offline signing keys through the host API.");
            if (RequiredTokenScopes is not { Length: > 0 and <= 32 }
                || RequiredTokenScopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Length > 256 || scope.Any(char.IsWhiteSpace)))
                throw new ArgumentException("JWT authentication requires explicit non-empty scope words.");
        }
    }
}

internal static class CertificateTrust
{
    internal const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    internal const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    internal static bool IsCurrentlyValid(X509Certificate2 certificate)
    {
        var now = DateTime.UtcNow;
        return now >= certificate.NotBefore.ToUniversalTime() && now <= certificate.NotAfter.ToUniversalTime();
    }

    internal static bool HasUsage(X509Certificate2 certificate, string usage) => certificate.Extensions
        .OfType<X509EnhancedKeyUsageExtension>()
        .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == usage));

    internal static HttpClientHandler IdentityProviderHandler(X509Certificate2 root, X509RevocationMode revocationMode) => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, certificate, presentedChain, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch
                | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0
                || !IsCurrentlyValid(certificate) || !HasUsage(certificate, ServerAuthentication)) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = revocationMode;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
            if (presentedChain is not null)
                foreach (var element in presentedChain.ChainElements)
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(certificate);
        }
    };
}

internal sealed class HostLifetime(IAsyncDisposable store) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _stop;
    internal TcpStoreHost? Host { get; set; }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_stop ??= StopAsync());
    }

    private async Task StopAsync()
    {
        try { if (Host is not null) await Host.DisposeAsync().ConfigureAwait(false); }
        finally { await store.DisposeAsync().ConfigureAwait(false); }
    }
}

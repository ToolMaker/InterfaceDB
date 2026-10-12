using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Authentication;
using Idb.Transport;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--probe"))
{
    Console.Error.WriteLine("Usage: InterfaceDB.TcpStore.Client <clientsettings.json> [--probe]");
    return 2;
}

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; shutdown.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var configurationPath = Path.GetFullPath(args[0]);
    var directory = Path.GetDirectoryName(configurationPath)!;
    var configuration = await ClientConfiguration.LoadAsync(configurationPath, shutdown.Token);
    using var serverTrust = PrivateRootTrust.Load(configuration.ServerCaCertificatePath, directory, configuration.PrivateCaRevocationMode);
    using var identityTrust = PrivateRootTrust.Load(configuration.IdentityProviderCaCertificatePath, directory, configuration.PrivateCaRevocationMode);
    using var clientCertificate = configuration.ClientCertificatePath is null ? null : X509CertificateLoader.LoadPkcs12FromFile(
        Path.GetFullPath(configuration.ClientCertificatePath, directory),
        ClientConfiguration.RequiredEnvironment(configuration.ClientCertificatePasswordEnvironmentVariable!),
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
    if (clientCertificate is { HasPrivateKey: false }) throw new ClientConfigurationException("The client certificate needs a private key.");

    IAccessTokenProvider tokenProvider;
    if (configuration.AccessTokenEnvironmentVariable is { } tokenVariable)
        tokenProvider = new FixedAccessTokenProvider(new AccessToken(ClientConfiguration.RequiredEnvironment(tokenVariable)));
    else
    {
        var settings = configuration.ClientCredentials!;
        // Fail missing environment setup before contacting the identity service. The provider reads it again for each refresh.
        _ = ClientConfiguration.RequiredEnvironment(settings.ClientSecretEnvironmentVariable);
        HttpMessageHandler? handler = identityTrust is null ? null : new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) => identityTrust.Validate(certificate, chain, errors)
        };
        tokenProvider = new ClientCredentialsAccessTokenProvider(settings.ToOptions(), handler);
    }
    using var tokenLifetime = tokenProvider as IDisposable;
    var probe = args.Length == 2;
    await using var store = await TcpEncodedEnvelopeStore.ConnectAsync(new TcpStoreClientOptions
    {
        Host = configuration.Host,
        Port = configuration.Port,
        TargetHost = configuration.TargetHost,
        StoreAlias = configuration.StoreAlias,
        Manifest = configuration.Manifest,
        Limits = configuration.Limits,
        AuthenticationMode = configuration.AuthenticationMode,
        ClientCertificate = clientCertificate,
        TokenProvider = tokenProvider,
        ConnectTimeout = configuration.ConnectTimeout,
        OperationTimeout = configuration.OperationTimeout,
        RequiredCapabilities = EncodedStoreCapabilities.DurableCommits | (probe
            ? EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Write | EncodedStoreCapabilities.Delete
            : EncodedStoreCapabilities.None),
        ServerCertificateValidation = serverTrust is null ? null
            : (_, certificate, chain, errors) => serverTrust.Validate(certificate, chain, errors)
    }, shutdown.Token);
    await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Ping }, shutdown.Token);
    Console.WriteLine("Authenticated TLS connection and Ping succeeded.");
    if (probe)
    {
        var key = "interfacedb-client-probe-" + Guid.NewGuid().ToString("N");
        var payload = RandomNumberGenerator.GetBytes(64);
        if (Encoding.UTF8.GetByteCount(key) > store.Limits.MaximumKeyBytes || payload.Length > store.Limits.MaximumPayloadBytes)
            throw new ClientConfigurationException("Negotiated limits are too small for the probe.");
        if (await store.GetAsync(key, shutdown.Token) is not null)
            throw new ClientConfigurationException("The generated probe key already exists; no probe data was written.");
        // Keep this identifier available for reconciliation if any mutation loses its acknowledgement.
        Console.WriteLine($"Temporary probe key: {key}");
        await store.PutAsync(key, payload, shutdown.Token);
        var stored = await store.GetAsync(key, shutdown.Token);
        if (stored is null || !stored.AsSpan().SequenceEqual(payload))
            throw new ClientConfigurationException("Probe read did not match the written bytes; inspect the reported key.");
        if (!await store.DeleteAsync(key, shutdown.Token) || await store.GetAsync(key, shutdown.Token) is not null)
            throw new ClientConfigurationException("Probe deletion could not be verified; inspect the reported key.");
        Console.WriteLine("Opaque-byte write, read, delete and missing-value verification succeeded.");
    }
    return 0;
}
catch (RemoteCommitOutcomeUnknownException)
{
    Console.Error.WriteLine("A probe mutation may have committed without acknowledgement. It was not retried. Inspect the reported probe key before repeating.");
    return 3;
}
catch (RemoteStoreException error)
{
    Console.Error.WriteLine($"Remote store request failed: {error.Code}. If a probe key was reported, inspect it before repeating.");
    return 1;
}
catch (TokenAcquisitionException error)
{
    Console.Error.WriteLine($"Access token acquisition failed: {error.Failure}.");
    return 1;
}
catch (ClientConfigurationException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Client operation cancelled or timed out. If a probe key was reported, inspect it before repeating.");
    return 1;
}
catch (Exception error)
{
    // JSON, certificate, network and credential exceptions can contain sensitive input. Print only their type.
    Console.Error.WriteLine($"Client configuration or connection failed ({error.GetType().Name}). If a probe key was reported, inspect it before repeating.");
    return 1;
}
finally { Console.CancelKeyPress -= cancel; }

internal sealed class ClientConfiguration
{
    public required string Host { get; init; }
    public int Port { get; init; } = 7443;
    public string? TargetHost { get; init; }
    public required string StoreAlias { get; init; }
    public required CompatibilityManifest Manifest { get; init; }
    public TcpStoreAuthenticationMode AuthenticationMode { get; init; } = TcpStoreAuthenticationMode.JwtBearer;
    public string? AccessTokenEnvironmentVariable { get; init; }
    public ServiceCredentialsConfiguration? ClientCredentials { get; init; }
    public string? ServerCaCertificatePath { get; init; }
    public string? IdentityProviderCaCertificatePath { get; init; }
    public X509RevocationMode PrivateCaRevocationMode { get; init; } = X509RevocationMode.Online;
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificatePasswordEnvironmentVariable { get; init; }
    public TransportLimits Limits { get; init; } = new();
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal static async Task<ClientConfiguration> LoadAsync(string path, CancellationToken cancellationToken)
    {
        const int maximumBytes = 64 * 1024;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (file.Length > maximumBytes) throw new ClientConfigurationException("Configuration exceeds 64 KiB.");
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await file.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes) throw new ClientConfigurationException("Configuration exceeds 64 KiB.");
            memory.Write(buffer, 0, read);
        }
        var bytes = memory.ToArray().AsMemory();
        if (bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateMembers(document.RootElement);
        var configuration = document.Deserialize<ClientConfiguration>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter<TcpStoreAuthenticationMode>(allowIntegerValues: false),
                new JsonStringEnumConverter<ClientSecretAuthenticationMethod>(allowIntegerValues: false),
                new JsonStringEnumConverter<X509RevocationMode>(allowIntegerValues: false)
            }
        }) ?? throw new ClientConfigurationException("Configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsWhiteSpace) || Host.Any(char.IsControl)
            || Port is < 1 or > 65535 || TargetHost is not null && (string.IsNullOrWhiteSpace(TargetHost) || TargetHost.Any(char.IsWhiteSpace)))
            throw new ClientConfigurationException("A server host and port 1 through 65535 are required.");
        if (string.IsNullOrEmpty(StoreAlias) || StoreAlias.Length > 128 || StoreAlias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ClientConfigurationException("StoreAlias needs 1-128 ASCII letters, digits, hyphens or underscores.");
        if (Manifest is null || Limits is null) throw new ClientConfigurationException("Manifest and Limits are required.");
        foreach (var value in new[] { Manifest.ApiVersion, Manifest.StorageEngine, Manifest.StorageFormat, Manifest.EnvelopeFormat,
            Manifest.ContractIdentity, Manifest.ContractVersion, Manifest.ContractSchemaHash, Manifest.ImplementationManifestHash, Manifest.IndexDefinitionHash })
            if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 1024)
                throw new ClientConfigurationException("All compatibility manifest values must be nonempty and at most 1024 UTF-8 bytes.");
        if (Manifest.ApprovedBuildIdentity is { } identity && (string.IsNullOrWhiteSpace(identity) || Encoding.UTF8.GetByteCount(identity) > 1024))
            throw new ClientConfigurationException("ApprovedBuildIdentity must be null or a nonempty bounded value.");
        Limits.Validate();
        if (ConnectTimeout < TimeSpan.FromMilliseconds(1) || ConnectTimeout > TimeSpan.FromMinutes(10)
            || OperationTimeout < TimeSpan.FromMilliseconds(1) || OperationTimeout > TimeSpan.FromMinutes(10))
            throw new ClientConfigurationException("Connection and operation timeouts must be between one millisecond and ten minutes.");
        if (AuthenticationMode is not (TcpStoreAuthenticationMode.JwtBearer or TcpStoreAuthenticationMode.MutualTlsAndJwtBearer))
            throw new ClientConfigurationException("This client requires JwtBearer or MutualTlsAndJwtBearer authentication.");
        if ((AccessTokenEnvironmentVariable is null) == (ClientCredentials is null))
            throw new ClientConfigurationException("Configure exactly one token environment variable or client credentials block.");
        if (AccessTokenEnvironmentVariable is not null) ValidateEnvironmentName(AccessTokenEnvironmentVariable);
        if (ClientCredentials is not null)
        {
            ValidateEnvironmentName(ClientCredentials.ClientSecretEnvironmentVariable);
            // Constructor validation is independent of HTTP and secret acquisition.
            using var validation = new ClientCredentialsAccessTokenProvider(ClientCredentials.ToOptions());
        }
        if (AuthenticationMode == TcpStoreAuthenticationMode.MutualTlsAndJwtBearer)
        {
            if (string.IsNullOrWhiteSpace(ClientCertificatePath) || ClientCertificatePasswordEnvironmentVariable is null)
                throw new ClientConfigurationException("Combined authentication requires a client PFX path and password environment variable.");
            ValidateEnvironmentName(ClientCertificatePasswordEnvironmentVariable);
        }
        else if (ClientCertificatePath is not null || ClientCertificatePasswordEnvironmentVariable is not null)
            throw new ClientConfigurationException("Client certificates require MutualTlsAndJwtBearer authentication.");
        if (PrivateCaRevocationMode is not (X509RevocationMode.Online or X509RevocationMode.NoCheck))
            throw new ClientConfigurationException("Private CA revocation mode must be Online or explicitly NoCheck.");
        foreach (var path in new[] { ServerCaCertificatePath, IdentityProviderCaCertificatePath, ClientCertificatePath })
            if (path is not null && string.IsNullOrWhiteSpace(path)) throw new ClientConfigurationException("Certificate paths cannot be empty.");
        if (IdentityProviderCaCertificatePath is not null && ClientCredentials is null)
            throw new ClientConfigurationException("An identity provider CA is only used with client credentials.");
    }

    internal static string RequiredEnvironment(string name)
    {
        ValidateEnvironmentName(name);
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(value) ? value : throw new ClientConfigurationException("A configured credential environment variable is missing or empty.");
    }

    private static void ValidateEnvironmentName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Contains('=') || name.Any(char.IsControl))
            throw new ClientConfigurationException("Credential environment variable names must be nonempty and valid.");
    }

    private static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ClientConfigurationException("Duplicate configuration properties are not accepted.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateMembers(item);
    }
}

internal sealed class ServiceCredentialsConfiguration
{
    public required string Authority { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecretEnvironmentVariable { get; init; }
    public string? Scope { get; init; }
    public ClientSecretAuthenticationMethod AuthenticationMethod { get; init; } = ClientSecretAuthenticationMethod.Basic;
    public string[] AdditionalTrustedTokenOrigins { get; init; } = [];
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal ClientCredentialsTokenOptions ToOptions() => new()
    {
        Authority = Authority, ClientId = ClientId, ClientSecretEnvironmentVariable = ClientSecretEnvironmentVariable,
        Scope = Scope, AuthenticationMethod = AuthenticationMethod,
        AdditionalTrustedTokenOrigins = AdditionalTrustedTokenOrigins, RequestTimeout = RequestTimeout
    };
    public override string ToString() => "[REDACTED SERVICE CREDENTIALS CONFIGURATION]";
}

internal sealed class PrivateRootTrust(X509Certificate2 root, X509RevocationMode revocationMode) : IDisposable
{
    internal static PrivateRootTrust? Load(string? path, string directory, X509RevocationMode revocationMode)
    {
        if (path is null) return null;
        var root = X509CertificateLoader.LoadCertificateFromFile(Path.GetFullPath(path, directory));
        if (!root.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
        {
            root.Dispose();
            throw new ClientConfigurationException("A private CA file must contain a CA certificate.");
        }
        return new(root, revocationMode);
    }

    internal bool Validate(X509Certificate? certificate, X509Chain? presentedChain, SslPolicyErrors errors)
    {
        // Keep the TLS stack's hostname check, even when the custom root replaces platform trust.
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        chain.ChainPolicy.RevocationMode = revocationMode;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
        if (presentedChain is not null)
            foreach (var element in presentedChain.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(leaf);
    }

    public void Dispose() => root.Dispose();
}

internal sealed class ClientConfigurationException(string message) : Exception(message);

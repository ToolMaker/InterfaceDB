using System.Security.Cryptography;
using Idb.Authentication;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Implementation.Encryption;
using Idb.Transport;
using Microsoft.Extensions.DependencyInjection;

internal sealed class LocalAccountHostConfiguration
{
    public required LocalAccountOptions Accounts { get; init; }
    public string StorageProvider { get; init; } = "SegmentCore";
    public required string StorageDirectory { get; init; }
    public required string EncryptionMethod { get; init; }
    public required string EncryptionKeyEnvironmentVariable { get; init; }
    public string PasswordHashingMethod { get; init; } = "Pbkdf2Sha256";
    public int PasswordHashIterations { get; init; } = 600_000;
    public string? BootstrapUsername { get; init; }
    public string? BootstrapPasswordEnvironmentVariable { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Accounts); Accounts.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(StorageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(EncryptionKeyEnvironmentVariable);
        if (StorageProvider != "SegmentCore" || EncryptionMethod != "AesGcm" || PasswordHashingMethod != "Pbkdf2Sha256")
            throw new ArgumentException("The sample supports SegmentCore, AesGcm and Pbkdf2Sha256. Register custom implementations through the host API.");
        if (PasswordHashIterations is < 600_000 or > 2_000_000) throw new ArgumentException("Invalid password hash work factor.");
        if ((BootstrapUsername is null) != (BootstrapPasswordEnvironmentVariable is null))
            throw new ArgumentException("Both bootstrap username and password environment variable are required together.");
        if (BootstrapUsername is not null) _ = AccountCredentialValidation.NormalizeUsername(BootstrapUsername);
    }

    internal async ValueTask<LocalAccountHostLifetime> OpenAsync(string configurationDirectory, string dataDirectory)
    {
        Validate();
        var path = Path.GetFullPath(StorageDirectory, configurationDirectory);
        var data = Path.GetFullPath(dataDirectory, configurationDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.Equals(data, comparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(data) + Path.DirectorySeparatorChar, comparison)
            || data.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Credential storage must be separate from application storage.");
        byte[] key;
        try { key = Convert.FromBase64String(Environment.GetEnvironmentVariable(EncryptionKeyEnvironmentVariable) ?? ""); }
        catch (FormatException) { throw new ArgumentException("The credential encryption key must be base64."); }
        if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new ArgumentException("A 256-bit credential encryption key is required."); }
        var keyProvider = new HostCredentialKeyProvider(key);
        IEncodedEnvelopeStore? backing = null;
        EncryptedDatabaseAccountStore? credentials = null;
        ServiceProvider? services = null;
        try
        {
            var limits = new TransportLimits { MaximumFrameBytes = 64 * 1024 * 1024, MaximumPayloadBytes = 32 * 1024 * 1024 };
            backing = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, limits);
            credentials = await EncryptedDatabaseAccountStore.OpenAsync(backing, new AesGcmEncryptor(keyProvider), Accounts.Authority);
            services = new ServiceCollection().AddSingleton<IDatabaseAccountStore>(credentials)
                .AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(PasswordHashIterations))
                .AddLocalAccountAuthentication(Accounts).BuildServiceProvider();
            var provider = services.GetRequiredService<LocalAccountAuthenticationProvider>();
            if (BootstrapUsername is not null && await credentials.CountAsync() == 0)
            {
                var bootstrapPassword = Environment.GetEnvironmentVariable(BootstrapPasswordEnvironmentVariable!)
                    ?? throw new ArgumentException("The bootstrap password environment variable is missing.");
                await provider.ProvisionFirstAccountAsync(BootstrapUsername, bootstrapPassword);
            }
            if (await credentials.CountAsync() == 0) throw new ArgumentException("Provision the initial account before starting the host.");
            await provider.ValidateConfigurationAsync();
            return new(backing, credentials, services, keyProvider, provider);
        }
        catch
        {
            if (services is not null) await services.DisposeAsync();
            if (credentials is not null) await credentials.DisposeAsync();
            if (backing is not null) await backing.DisposeAsync();
            keyProvider.Dispose(); throw;
        }
    }
}

internal sealed class HostCredentialKeyProvider(byte[] key) : IKeyProvider, IDisposable
{
    public ReadOnlyMemory<byte> GetCurrentKey() => key;
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

internal sealed class LocalAccountHostLifetime(IEncodedEnvelopeStore backing, IDatabaseAccountStore credentials,
    ServiceProvider services, HostCredentialKeyProvider key, IAuthenticationProvider authentication) : IAsyncDisposable
{
    internal IAuthenticationProvider Authentication { get; } = authentication;
    internal IDatabaseAccountManager Accounts => services.GetRequiredService<IDatabaseAccountManager>();
    public async ValueTask DisposeAsync()
    {
        try { await services.DisposeAsync(); }
        finally { try { await credentials.DisposeAsync(); await backing.DisposeAsync(); } finally { key.Dispose(); } }
    }
}

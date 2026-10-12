using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Idb.Authentication;
using Idb.Transport;
using InterfaceDB.Transport.Tests;

namespace InterfaceDB.Authentication.KeycloakTests;

/// <summary>Explicit Docker qualification: excluded from the ordinary solution, no shared realms or trust-store changes.</summary>
[TestClass]
[DoNotParallelize]
public sealed partial class KeycloakTests
{
    private const string Audience = "interfacedb-storage";
    private const string Scope = "interfacedb.storage";
    private static KeycloakFixture _fixture = null!;

    [ClassInitialize]
    public static async Task Initialize(TestContext _) => _fixture = await KeycloakFixture.StartAsync();

    [ClassCleanup]
    public static async Task Cleanup() { if (_fixture is not null) await _fixture.DisposeAsync(); }

    [TestMethod]
    public async Task ServiceLoginAndSuppliedTokenStoreDataAcrossHostRestart()
    {
        using var provider = _fixture.Provider("storage-client");
        var token = await provider.GetAccessTokenAsync();
        using var validator = _fixture.Validator();
        var identity = await validator.ValidateAsync(token.Value);
        Assert.IsTrue(identity.Principal.Identity?.IsAuthenticated);
        Assert.AreEqual("Bearer", identity.Principal.FindFirst("typ")?.Value);
        var path = Path.Combine(_fixture.RootDirectory, "persistent-store");
        byte[] value = [0, 17, 255, 193, 44];
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path))
        await using (var host = await _fixture.Host(store, validator))
        await using (var client = await TcpEncodedEnvelopeStore.ConnectAsync(_fixture.Client(host, provider)))
        {
            await client.PutAsync("keycloak-proof", value);
            CollectionAssert.AreEqual(value, await client.GetAsync("keycloak-proof"));
        }
        // A separately opened host/store and supplied-token provider exercise durable data + validation-only access.
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path);
        await using var restarted = await _fixture.Host(reopened, validator);
        await using var reader = await TcpEncodedEnvelopeStore.ConnectAsync(
            _fixture.Client(restarted, new FixedAccessTokenProvider(token)));
        CollectionAssert.AreEqual(value, await reader.GetAsync("keycloak-proof"));
        Assert.IsTrue(await reader.DeleteAsync("keycloak-proof"));
        Assert.IsNull(await reader.GetAsync("keycloak-proof"));
    }

    [TestMethod]
    public async Task MissingTokenWrongAudienceAndMissingScopeAreDenied()
    {
        using var validator = _fixture.Validator();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_fixture.RootDirectory, "denials"));
        await using var host = await _fixture.Host(store, validator);
        foreach (var clientId in new[] { "no-scope-client", "wrong-audience-client" })
        {
            using var provider = _fixture.Provider(clientId);
            var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            {
                await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(_fixture.Client(host, provider));
            });
            Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        }
        var missing = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(_fixture.Client(host, null));
        });
        Assert.AreEqual(RemoteErrorCode.AuthenticationRequired, missing.Code);
        using var accepted = _fixture.Provider("storage-client");
        await using var valid = await TcpEncodedEnvelopeStore.ConnectAsync(_fixture.Client(host, accepted));
        Assert.AreEqual(0, (await valid.EnumerateAsync(10)).Records!.Length);
    }

    [TestMethod]
    public async Task RunnableHostAndClientAuthenticateAndProbeStorage() => await _fixture.ProbeSamples();

    [TestMethod]
    public async Task WrongClientSecretCannotObtainTokenAndIsRedacted()
    {
        const string incorrect = "incorrect-client-secret-not-for-logs";
        using var provider = _fixture.Provider("storage-client", incorrect);
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(async () => await provider.GetAccessTokenAsync());
        Assert.IsFalse(error.ToString().Contains(incorrect, StringComparison.Ordinal));
    }

    private sealed partial class KeycloakFixture : IAsyncDisposable
    {
        private const string Image = "quay.io/keycloak/keycloak:26.7.1";
        private const string OwnerLabel = "com.interfacedb.keycloak-test.owner";
        private readonly string _owner = Guid.NewGuid().ToString("N");
        private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly TestCertificates _certificates = new();
        private string? _container;
        private string? _network;
        private string _authority = "";
        private string _repository = "";
        internal string RootDirectory { get; private set; } = "";
        private static readonly CompatibilityManifest Manifest = new()
        {
            ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1", EnvelopeFormat = "opaque-v1",
            ContractIdentity = "keycloak-test", ContractVersion = "1", ContractSchemaHash = "test",
            ImplementationManifestHash = "test", IndexDefinitionHash = "none"
        };

        internal static async Task<KeycloakFixture> StartAsync()
        {
            var fixture = new KeycloakFixture();
            try { await fixture.Start(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task Start()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "InterfaceDB.sln")))
            {
                var sibling = Path.Combine(repository.FullName, "InterfaceDB");
                if (File.Exists(Path.Combine(sibling, "InterfaceDB.sln"))) { repository = new DirectoryInfo(sibling); break; }
                repository = repository.Parent;
            }
            if (repository is null) throw new InvalidOperationException("Cannot locate public test source and artifacts directory.");
            _repository = repository.FullName;
            RootDirectory = Path.Combine(repository.FullName, "artifacts", "jwt-keycloak", _owner);
            var importPath = Path.Combine(RootDirectory, "import");
            var tlsPath = Path.Combine(RootDirectory, "tls");
            Directory.CreateDirectory(importPath);
            Directory.CreateDirectory(tlsPath);
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            _authority = $"https://localhost:{port}/realms/interfacedb-test";
            using var key = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost"); names.AddDnsName("idb-keycloak");
            certificateRequest.CertificateExtensions.Add(names.Build());
            using var leaf = certificateRequest.Create(_certificates.Root, DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow.AddHours(2), RandomNumberGenerator.GetBytes(16));
            await File.WriteAllTextAsync(Path.Combine(tlsPath, "server.crt"), leaf.ExportCertificatePem());
            await File.WriteAllTextAsync(Path.Combine(tlsPath, "server.key"), key.ExportPkcs8PrivateKeyPem());
            object Client(string clientId, bool scope, string audience, bool userLogin = false) => new
            {
                clientId, secret = _secret, enabled = true, protocol = "openid-connect", publicClient = userLogin,
                serviceAccountsEnabled = !userLogin, standardFlowEnabled = false, directAccessGrantsEnabled = userLogin,
                defaultClientScopes = scope ? new[] { "basic", Scope } : ["basic"],
                protocolMappers = new[] {
                    new { name = "test-subject", protocol = "openid-connect",
                        protocolMapper = "oidc-sub-mapper", consentRequired = false,
                        config = new Dictionary<string, string> { ["access.token.claim"] = "true" } },
                    new { name = "storage-audience", protocol = "openid-connect",
                    protocolMapper = "oidc-audience-mapper", consentRequired = false,
                    config = new Dictionary<string, string> { ["included.custom.audience"] = audience,
                        ["id.token.claim"] = "false", ["access.token.claim"] = "true" } } }
            };
            await File.WriteAllTextAsync(Path.Combine(importPath, "interfacedb-test-realm.json"), JsonSerializer.Serialize(new
            {
                realm = "interfacedb-test", enabled = true, sslRequired = "all", accessTokenLifespan = 120,
                clientScopes = new[] { new { name = Scope, protocol = "openid-connect",
                    attributes = new Dictionary<string, string> { ["include.in.token.scope"] = "true", ["display.on.consent.screen"] = "false" } } },
                clients = new[] { Client("storage-client", true, Audience), Client("no-scope-client", false, Audience),
                    Client("wrong-audience-client", true, "another-service"), Client("test-only-user-login", true, Audience, true) },
                // Password grant is confined to this disposable test realm; native apps use their existing user login.
                users = new[] { "parent-a", "parent-b" }.Select(username => new {
                    username, enabled = true, emailVerified = true, email = username + "@example.test",
                    firstName = "Test", lastName = username, requiredActions = Array.Empty<string>(),
                    credentials = new[] { new { type = "password", value = _secret, temporary = false } }
                }).ToArray()
            }));
            var imageId = (await Docker(["image", "inspect", "--format", "{{.Id}}", Image])).Trim();
            if (imageId.Length != 71 || !imageId.StartsWith("sha256:", StringComparison.Ordinal)
                || imageId[7..].Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("Expected immutable Keycloak image ID.");
            _network = "idb-keycloak-net-" + _owner;
            await Docker(["network", "create", "--label", OwnerLabel + "=" + _owner, _network]);
            _container = "idb-keycloak-test-" + _owner;
            await Docker(["run", "--detach", "--pull", "never", "--name", _container,
                "--network", _network, "--network-alias", "idb-keycloak", "--label", OwnerLabel + "=" + _owner, "--publish", $"127.0.0.1:{port}:8443", "--memory", "1g",
                "--mount", "type=bind,source=" + importPath + ",target=/opt/keycloak/data/import,readonly",
                "--mount", "type=bind,source=" + tlsPath + ",target=/run/idb-tls,readonly",
                imageId, "start-dev", "--import-realm", "--http-enabled=false", "--https-port=8443",
                "--https-certificate-file=/run/idb-tls/server.crt", "--https-certificate-key-file=/run/idb-tls/server.key",
                $"--hostname=https://localhost:{port}"]);
            using var client = new HttpClient(Handler()) { Timeout = TimeSpan.FromSeconds(4) };
            using var ready = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (!ready.IsCancellationRequested)
            {
                try
                {
                    using var response = await client.GetAsync(_authority + "/.well-known/openid-configuration", ready.Token);
                    if (response.IsSuccessStatusCode) { Console.WriteLine($"Isolated HTTPS Keycloak ready; image {imageId}; loopback port {port}."); return; }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) { }
                if (!ready.IsCancellationRequested) await Task.Delay(500);
            }
            throw new TimeoutException("The isolated HTTPS Keycloak did not become ready. Inspect the test container logs locally.");
        }

        private HttpClientHandler Handler() => new()
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                _certificates.ValidateServer(request, certificate, chain, errors)
        };

        internal ClientCredentialsAccessTokenProvider Provider(string clientId, string? secret = null) => new(new()
        {
            Authority = _authority, ClientId = clientId, ClientSecretProvider = _ => ValueTask.FromResult(secret ?? _secret)
        }, Handler());

        internal JwtAccessTokenValidator Validator() => new(new()
        {
            Authorities = [new() { Id = "local-keycloak", Issuer = _authority, Audiences = [Audience], TokenTypes = ["JWT"],
                RequiredClaimValues = new Dictionary<string, string> { ["typ"] = "Bearer" },
                MetadataAddress = new(_authority + "/.well-known/openid-configuration"),
                JwksAddress = new(_authority + "/protocol/openid-connect/certs") }]
        }, Handler());

        internal Task<TcpStoreHost> Host(IEncodedEnvelopeStore store, IAccessTokenValidator validator) => TcpStoreHost.StartAsync(new()
        {
            ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
            Stores = [new("keycloak-test", Manifest, store)], AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
            TokenValidator = validator,
            AuthorizeIdentity = (identity, alias, _) => alias == "keycloak-test" && identity.Principal.FindAll("scope")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(Scope, StringComparer.Ordinal)
        }).AsTask();

        internal TcpStoreClientOptions Client(TcpStoreHost host, IAccessTokenProvider? provider) => new()
        {
            Host = "127.0.0.1", TargetHost = "localhost", Port = host.LocalEndPoint.Port, StoreAlias = "keycloak-test",
            Manifest = Manifest, AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer, TokenProvider = provider,
            ServerCertificateValidation = _certificates.ValidateServer
        };

        internal async Task ProbeSamples()
        {
            var directory = Path.Combine(RootDirectory, "samples");
            Directory.CreateDirectory(directory);
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pfx = Path.Combine(directory, "server.pfx");
            using var sampleCertificate = X509Certificate2.CreateFromPemFile(Path.Combine(RootDirectory, "tls", "server.crt"),
                Path.Combine(RootDirectory, "tls", "server.key"));
            await File.WriteAllBytesAsync(pfx, sampleCertificate.Export(X509ContentType.Pfx, password));
            await File.WriteAllTextAsync(Path.Combine(directory, "ca.crt"), _certificates.Root.ExportCertificatePem());
            var hostConfiguration = Path.Combine(directory, "hostsettings.json");
            await File.WriteAllTextAsync(hostConfiguration, JsonSerializer.Serialize(new
            {
                bindAddress = "127.0.0.1", port, storeAlias = "keycloak-test", dataDirectory = "./data",
                serverCertificatePath = "server.pfx", serverCertificatePasswordEnvironmentVariable = "IDB_KEYCLOAK_TEST_PFX",
                authenticationMode = "JwtBearer", identityProviderCaCertificatePath = "ca.crt", privateCaRevocationMode = "NoCheck",
                requiredTokenScopes = new[] { Scope }, manifest = Manifest,
                jwtValidation = new { authorities = new[] { new { id = "keycloak", issuer = _authority, audiences = new[] { Audience },
                    tokenTypes = new[] { "JWT" }, requiredClaimValues = new Dictionary<string, string> { ["typ"] = "Bearer" },
                    metadataAddress = _authority + "/.well-known/openid-configuration", jwksAddress = _authority + "/protocol/openid-connect/certs" } } }
            }));
            var clientConfiguration = Path.Combine(directory, "clientsettings.json");
            await File.WriteAllTextAsync(clientConfiguration, JsonSerializer.Serialize(new
            {
                host = "localhost", port, storeAlias = "keycloak-test", manifest = Manifest, authenticationMode = "JwtBearer",
                serverCaCertificatePath = "ca.crt", identityProviderCaCertificatePath = "ca.crt", privateCaRevocationMode = "NoCheck",
                clientCredentials = new { authority = _authority, clientId = "storage-client", clientSecretEnvironmentVariable = "IDB_KEYCLOAK_TEST_SECRET", scope = Scope }
            }));
            using var host = SampleProcess("Host", hostConfiguration, "--console");
            host.StartInfo.Environment["IDB_KEYCLOAK_TEST_PFX"] = password;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            host.Start();
            var hostErrors = host.StandardError.ReadToEndAsync();
            try
            {
                while (true)
                {
                    var line = await host.StandardOutput.ReadLineAsync(deadline.Token);
                    if (line is null) throw new InvalidOperationException("Sample JWT host failed: " + await hostErrors);
                    if (line.StartsWith("InterfaceDB TLS TCP host listening", StringComparison.Ordinal)) break;
                }
                var restOfOutput = host.StandardOutput.ReadToEndAsync();
                using var client = SampleProcess("Client", clientConfiguration, "--probe");
                client.StartInfo.Environment["IDB_KEYCLOAK_TEST_SECRET"] = _secret;
                client.Start();
                var clientOutput = client.StandardOutput.ReadToEndAsync();
                var clientError = client.StandardError.ReadToEndAsync();
                try { await client.WaitForExitAsync(deadline.Token); }
                finally { if (!client.HasExited) client.Kill(entireProcessTree: true); }
                var output = await clientOutput;
                var error = await clientError;
                Assert.AreEqual(0, client.ExitCode, error);
                StringAssert.Contains(output, "Opaque-byte write, read, delete and missing-value verification succeeded.");
                Assert.IsFalse((output + error).Contains(_secret, StringComparison.Ordinal));
                await host.StandardInput.WriteLineAsync("stop");
                await host.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, host.ExitCode, await hostErrors);
                _ = await restOfOutput;
            }
            finally
            {
                if (!host.HasExited) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(); }
                File.Delete(pfx);
            }
        }

        private Process SampleProcess(string name, params string[] arguments)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var assembly = Path.Combine(_repository, "samples", "InterfaceDB.TcpStore." + name, "bin", configuration,
                "net10.0", "InterfaceDB.TcpStore." + name + ".dll");
            var process = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add(assembly);
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            return process;
        }

        private static async Task<string> Docker(string[] arguments, bool allowMissing = false, string? certificatePassword = null)
        {
            using var process = new Process { StartInfo = new("docker")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (certificatePassword is not null) process.StartInfo.Environment["IDB_TYPED_TEST_PFX"] = certificatePassword;
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0 && !(allowMissing &&
                (error.Contains("No such object", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("No such container", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Isolated Keycloak Docker command failed: " + error);
            return process.ExitCode == 0 ? output : "";
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_container is not null)
                {
                    var label = (await Docker(["inspect", "--format", "{{ index .Config.Labels \"" + OwnerLabel + "\" }}", _container], true)).Trim();
                    if (label.Length > 0)
                    {
                        if (label != _owner) throw new InvalidOperationException("Refusing to remove a container not owned by this test.");
                        await Docker(["rm", "--force", _container]);
                    }
                }
            }
            finally
            {
                if (_network is not null)
                {
                    var label = (await Docker(["network", "inspect", "--format", "{{ index .Labels \"" + OwnerLabel + "\" }}", _network])).Trim();
                    if (label != _owner) throw new InvalidOperationException("Refusing to remove an unowned test network.");
                    await Docker(["network", "rm", _network]);
                }
                _certificates.Dispose();
                // Remove only the two known credential files in this fixture's unique artifacts directory.
                if (RootDirectory.Length > 0)
                {
                    File.Delete(Path.Combine(RootDirectory, "tls", "server.key"));
                    File.Delete(Path.Combine(RootDirectory, "import", "interfacedb-test-realm.json"));
                }
            }
        }
    }
}

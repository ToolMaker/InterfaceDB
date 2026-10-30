using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Idb.Transport;
using Idb.Authentication;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class HostProcessTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private string _temporaryDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "InterfaceDB.Transport.HostTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // This exact, newly generated per-test directory is the only deletion target.
        if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [TestMethod]
    public async Task ConfiguredHostProcessAuthenticatesPersistsAndStopsGracefully()
    {
        using var certificates = new TestCertificates();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "secrets"));
        await File.WriteAllBytesAsync(Path.Combine(_temporaryDirectory, "secrets", "server.pfx"),
            CreateServerPfx(certificates.Root, password));
        var port = AvailablePort();
        var configuration = await WriteConfigurationAsync(port, certificates.Client.GetCertHashString(HashAlgorithmName.SHA256));
        var payload = new byte[] { 0xc1, 0xff, 0x80, 0, 17 };

        using (var process = StartHost(configuration, password))
        {
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                var firstLine = await process.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
                StringAssert.Contains(firstLine ?? "", "host listening", "The configured host must start before the client connects.");
                var output = process.StandardOutput.ReadToEndAsync();
                await using (var client = await TcpEncodedEnvelopeStore.ConnectAsync(new()
                {
                    Host = "127.0.0.1", TargetHost = "localhost", Port = port,
                    StoreAlias = "process-test", Manifest = Manifest(), ClientCertificate = certificates.Client,
                    ServerCertificateValidation = certificates.ValidateServer
                }))
                {
                    await client.PutAsync("persisted", payload);
                    CollectionAssert.AreEqual(payload, await client.GetAsync("persisted"));
                }

                await process.StandardInput.WriteLineAsync("stop");
                await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync().WaitAsync(Deadline);
                Assert.AreEqual(0, process.ExitCode, "The host must exit cleanly after the console stop command.");
                Assert.AreEqual("", await errors, "Successful host lifecycle should not report connection errors.");
                Assert.IsFalse((await output).Contains(password, StringComparison.Ordinal), "The host must never print its PFX password.");
            }
            finally { await KillIfRunningAsync(process); }
        }

        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_temporaryDirectory, "database"));
        CollectionAssert.AreEqual(payload, await reopened.GetAsync("persisted"));
    }

    [TestMethod]
    public async Task InvalidClientPinConfigurationFailsBeforeOpeningStoreOrListener()
    {
        var port = AvailablePort();
        var configuration = await WriteConfigurationAsync(port, "invalid-pin");
        using var process = StartHost(configuration, "test-only-password-not-a-secret");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(Deadline);
            Assert.AreEqual(1, process.ExitCode);
            StringAssert.Contains(await errors, "SHA-256 fingerprint");
            Assert.IsFalse((await output).Contains("listening", StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(Path.Combine(_temporaryDirectory, "database")));
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start(); // The rejected configuration must not retain the listening port.
        }
        finally { await KillIfRunningAsync(process); }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("MutualTls")]
    public async Task JwtSettingsCannotBeSilentlyIgnoredByCertificateOnlyMode(string? mode)
    {
        var port = AvailablePort();
        var path = await WriteConfigurationAsync(port, new string('0', 64));
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        if (mode is not null) configuration["authenticationMode"] = mode;
        configuration["jwtValidation"] = JsonNode.Parse("""
            {"authorities":[{"id":"primary","issuer":"https://identity.example.test/realm",
              "audiences":["storage"],"jwksAddress":"https://identity.example.test/realm/keys"}]}
            """);
        await File.WriteAllTextAsync(path, configuration.ToJsonString());
        using var process = StartHost(path, "test-only-password-not-a-secret");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(Deadline);
            Assert.AreEqual(1, process.ExitCode);
            StringAssert.Contains(await errors, "JWT validation settings require");
            Assert.IsFalse((await output).Contains("listening", StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(Path.Combine(_temporaryDirectory, "database")));
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
        }
        finally { await KillIfRunningAsync(process); }
    }

    [TestMethod]
    public async Task IdentityProviderCaCannotBeSilentlyIgnoredByCertificateOnlyMode()
    {
        var port = AvailablePort();
        var path = await WriteConfigurationAsync(port, new string('0', 64));
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        configuration["identityProviderCaCertificatePath"] = "missing-ca.pem";
        await File.WriteAllTextAsync(path, configuration.ToJsonString());
        using var process = StartHost(path, "test-only-password-not-a-secret");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(Deadline);
            Assert.AreEqual(1, process.ExitCode);
            StringAssert.Contains(await errors, "JWT validation settings require");
            Assert.IsFalse((await output).Contains("listening", StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(Path.Combine(_temporaryDirectory, "database")));
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
        }
        finally { await KillIfRunningAsync(process); }
    }

    [TestMethod]
    public async Task NativePasswordConfigurationBootstrapsPersistsAndDoesNotResetAccountsOnRestart()
    {
        using var certificates = new TestCertificates();
        var pfxPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var password = "native-host-process-password-123";
        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "secrets"));
        await File.WriteAllBytesAsync(Path.Combine(_temporaryDirectory, "secrets", "server.pfx"), CreateServerPfx(certificates.Root, pfxPassword));
        var port = AvailablePort(); var path = await WriteNativeConfiguration(port);
        var environment = new Dictionary<string, string> { ["IDB_TEST_NATIVE_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["IDB_TEST_NATIVE_PASSWORD"] = password };
        for (var run = 0; run < 2; run++)
        {
            using var process = StartHost(path, pfxPassword, environment);
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                var first = await process.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
                if (first is null) Assert.Fail(await errors);
                StringAssert.Contains(first, "host listening");
                var output = process.StandardOutput.ReadToEndAsync();
                var options = new TcpStoreClientOptions { Host = "127.0.0.1", Port = port, TargetHost = "localhost", StoreAlias = "process-test", Manifest = Manifest(),
                    AuthenticationMode = TcpStoreAuthenticationMode.Provider, ServerCertificateValidation = certificates.ValidateServer,
                    CredentialsProvider = new UsernamePasswordCredentialsProvider("admin", _ => ValueTask.FromResult(password)) };
                await using (var session = await TcpEncodedEnvelopeStore.ConnectAsync(options))
                {
                    if (run == 0) await session.PutAsync("persisted", [8, 2]);
                    CollectionAssert.AreEqual(new byte[] { 8, 2 }, await session.GetAsync("persisted"));
                }
                await process.StandardInput.WriteLineAsync("stop"); await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync().WaitAsync(Deadline);
                Assert.AreEqual(0, process.ExitCode, await errors);
                Assert.IsFalse((await output).Contains(password, StringComparison.Ordinal));
                Assert.IsFalse((await errors).Contains(password, StringComparison.Ordinal));
                // A changed bootstrap environment must never overwrite an already created account.
                environment["IDB_TEST_NATIVE_PASSWORD"] = "different-unused-bootstrap-password";
            }
            finally { await KillIfRunningAsync(process); }
        }
    }

    [TestMethod]
    [DataRow("encryption")]
    [DataRow("key")]
    [DataRow("policy")]
    [DataRow("storage")]
    public async Task NativePasswordConfigurationRejectsMissingProtectionBeforeListening(string defect)
    {
        using var certificates = new TestCertificates();
        var pfxPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "secrets"));
        await File.WriteAllBytesAsync(Path.Combine(_temporaryDirectory, "secrets", "server.pfx"), CreateServerPfx(certificates.Root, pfxPassword));
        var port = AvailablePort(); var path = await WriteNativeConfiguration(port);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        if (defect == "encryption") config["localAccounts"]!["encryptionMethod"] = "None";
        if (defect == "policy") config["dataAccessPolicy"] = "none";
        if (defect == "storage") config["localAccounts"]!["storageDirectory"] = "database";
        await File.WriteAllTextAsync(path, config.ToJsonString());
        var environment = new Dictionary<string, string> { ["IDB_TEST_NATIVE_KEY"] = defect == "key" ? "" : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["IDB_TEST_NATIVE_PASSWORD"] = "process-test-native-password" };
        using var process = StartHost(path, pfxPassword, environment);
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(Deadline);
            Assert.AreEqual(1, process.ExitCode);
            Assert.IsFalse((await output).Contains("listening", StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(Path.Combine(_temporaryDirectory, "database")));
            using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
            Assert.IsFalse((await errors).Contains(environment["IDB_TEST_NATIVE_PASSWORD"], StringComparison.Ordinal));
        }
        finally { await KillIfRunningAsync(process); }
    }

    private async Task<string> WriteNativeConfiguration(int port)
    {
        var path = await WriteConfigurationAsync(port, new string('0', 64));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        config["authenticationMode"] = "Provider"; config["trustedClientCertificateSha256"] = new JsonArray(); config["dataAccessPolicy"] = "subject";
        config["localAccounts"] = JsonNode.Parse("""
            {"accounts":{"authority":"idb-local:process-test","accountMode":"MultipleAccounts","creationMode":"Administrators"},
             "storageProvider":"SegmentCore","storageDirectory":"credentials","encryptionMethod":"AesGcm",
             "encryptionKeyEnvironmentVariable":"IDB_TEST_NATIVE_KEY","passwordHashingMethod":"Pbkdf2Sha256",
             "bootstrapUsername":"admin","bootstrapPasswordEnvironmentVariable":"IDB_TEST_NATIVE_PASSWORD"}
            """);
        await File.WriteAllTextAsync(path, config.ToJsonString()); return path;
    }

    private async Task<string> WriteConfigurationAsync(int port, string pin)
    {
        var path = Path.Combine(_temporaryDirectory, "hostsettings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            bindAddress = "127.0.0.1", port, storeAlias = "process-test", dataDirectory = "database",
            serverCertificatePath = "secrets/server.pfx",
            serverCertificatePasswordEnvironmentVariable = "IDB_PROCESS_TEST_PFX_PASSWORD",
            trustedClientCertificateSha256 = new[] { pin }, manifest = Manifest()
        }));
        return path;
    }

    private static Process StartHost(string configuration, string password, Dictionary<string, string>? environment = null)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InterfaceDB.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var assembly = Path.Combine(directory.FullName, "samples", "InterfaceDB.TcpStore.Host", "bin",
            buildConfiguration, "net10.0", "InterfaceDB.TcpStore.Host.dll");
        Assert.IsTrue(File.Exists(assembly), "The sample host must be built with the test project.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // Relative data/certificate paths must resolve against config, not this unrelated directory.
            WorkingDirectory = directory.FullName
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(configuration);
        start.ArgumentList.Add("--console");
        start.Environment["IDB_PROCESS_TEST_PFX_PASSWORD"] = password;
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        return Process.Start(start) ?? throw new InvalidOperationException("Failed to start the host process.");
    }

    private static int AvailablePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static byte[] CreateServerPfx(X509Certificate2 root, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
        using var certificate = signed.CopyWithPrivateKey(key);
        return certificate.Export(X509ContentType.Pfx, password);
    }

    private static async Task KillIfRunningAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(Deadline);
        }
    }

    private static CompatibilityManifest Manifest() => new()
    {
        ApiVersion = "test-api-1", StorageEngine = "SegmentCore", StorageFormat = "test-store-1",
        EnvelopeFormat = "test-envelope-1", ContractIdentity = "HostProcessTests", ContractVersion = "1",
        ContractSchemaHash = "test-schema", ImplementationManifestHash = "test-implementations", IndexDefinitionHash = "test-index"
    };
}

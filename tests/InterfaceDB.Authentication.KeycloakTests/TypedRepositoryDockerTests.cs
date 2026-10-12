using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Idb.Authentication;
using Idb.Transport;
using InterfaceDB.TcpRepository.Sample;

namespace InterfaceDB.Authentication.KeycloakTests;

public sealed partial class KeycloakTests
{
    [TestMethod]
    public async Task TypedUserRepositoriesAcrossProcessesLinuxContainerRecreationAndPrincipals()
        => await _fixture.ProbeTypedDocker();

    private sealed partial class KeycloakFixture
    {
        // Optional downstream conformance checks can reuse the isolated host's lifetime.
        partial void ConfigureAdditionalProbe(ref Func<int, string, Task>? probe);
        private async Task<string> UserToken(string username)
        {
            using var client = new HttpClient(Handler()) { Timeout = TimeSpan.FromSeconds(15) };
            // Test-only disposable users/realm. No password login is added to the product.
            using var response = await client.PostAsync(_authority + "/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password", ["client_id"] = "test-only-user-login",
                    ["username"] = username, ["password"] = _secret, ["scope"] = Scope
                }));
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Isolated test user login failed.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            return body.RootElement.GetProperty("access_token").GetString()!;
        }

        internal async Task ProbeTypedDocker()
        {
            var directory = Path.Combine(RootDirectory, "typed");
            Directory.CreateDirectory(directory);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pfx = Path.Combine(directory, "server.pfx");
            using (var certificate = X509Certificate2.CreateFromPemFile(Path.Combine(RootDirectory, "tls", "server.crt"),
                Path.Combine(RootDirectory, "tls", "server.key")))
                await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
            await File.WriteAllTextAsync(Path.Combine(directory, "ca.crt"), _certificates.Root.ExportCertificatePem());
            await File.WriteAllTextAsync(Path.Combine(directory, "hostsettings.json"), JsonSerializer.Serialize(new
            {
                bindAddress = "0.0.0.0", port = 7443, storeAlias = "example-profiles",
                dataDirectory = "/var/lib/interfacedb", serverCertificatePath = "server.pfx",
                serverCertificatePasswordEnvironmentVariable = "IDB_TYPED_TEST_PFX",
                authenticationMode = "JwtBearer", isolateBySubject = true,
                identityProviderCaCertificatePath = "ca.crt", privateCaRevocationMode = "NoCheck",
                requiredTokenScopes = new[] { Scope }, manifest = ExampleProfileConfig.Manifest,
                jwtValidation = new { authorities = new[] { new {
                    id = "keycloak", issuer = _authority, audiences = new[] { Audience }, tokenTypes = new[] { "JWT" },
                    requiredClaimValues = new Dictionary<string, string> { ["typ"] = "Bearer" },
                    // Pin the operator-configured JWKS URL on the isolated Docker network.
                    // Token issuer stays exact. Discovery is omitted because its public URL differs.
                    jwksAddress = "https://idb-keycloak:8443/realms/interfacedb-test/protocol/openid-connect/certs"
                } } }
            }));
            var imageId = (await Docker(["image", "inspect", "--format", "{{.Id}}", Environment.GetEnvironmentVariable("IDB_TYPED_TEST_IMAGE") ?? "interfacedb-composition-test:local"])).Trim();
            if (imageId.Length != 71 || !imageId.StartsWith("sha256:", StringComparison.Ordinal)
                || imageId[7..].Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("Expected immutable test image.");
            var container = "idb-typed-host-" + _owner;
            var volume = "idb-typed-data-" + _owner;
            var volumeCreated = false;
            var containerCreated = false;
            var clientConfig = Path.Combine(directory, "clientsettings.json");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            async Task Own(string kind, string name)
            {
                var template = kind == "container" ? "{{ index .Config.Labels \"" + OwnerLabel + "\" }}"
                    : "{{ index .Labels \"" + OwnerLabel + "\" }}";
                var label = (await Docker([kind, "inspect", "--format", template, name])).Trim();
                if (label != _owner) throw new InvalidOperationException("Refusing to change a resource not owned by this test.");
            }
            async Task<int> Start()
            {
                await Docker(["run", "--detach", "--pull", "never", "--name", container,
                    "--network", _network!, "--label", OwnerLabel + "=" + _owner,
                    "--publish", "127.0.0.1::7443", "--env", "IDB_TYPED_TEST_PFX",
                    "--mount", "type=volume,source=" + volume + ",target=/var/lib/interfacedb",
                    "--mount", "type=bind,source=" + directory + ",target=/run/interfacedb,readonly",
                    "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=64m",
                    "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--stop-timeout", "30",
                    imageId], certificatePassword: password);
                containerCreated = true;
                await Own("container", container);
                var port = int.Parse((await Docker(["inspect", "--format",
                    "{{ (index (index .NetworkSettings.Ports \"7443/tcp\") 0).HostPort }}", container])).Trim(), CultureInfo.InvariantCulture);
                while (!(await Docker(["logs", container])).Contains("InterfaceDB TLS TCP host listening", StringComparison.Ordinal))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var running = (await Docker(["inspect", "--format", "{{.State.Running}}", container])).Trim();
                    if (running != "true") throw new InvalidOperationException("Isolated Linux database host exited.");
                    await Task.Delay(200, deadline.Token);
                }
                await File.WriteAllTextAsync(clientConfig, JsonSerializer.Serialize(new ExampleClientConfiguration
                {
                    Host = "127.0.0.1", Port = port, TargetHost = "localhost",
                    ServerCaCertificatePath = "ca.crt", DevelopmentCaWithoutRevocation = true
                }));
                return port;
            }
            Process StartClient(string token, params string[] arguments)
            {
                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                var assembly = Path.Combine(_repository, "samples", "InterfaceDB.TcpRepository.Client",
                    "bin", configuration, "net10.0", "InterfaceDB.TcpRepository.Client.dll");
                var process = new Process { StartInfo = new("dotnet")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                } };
                process.StartInfo.ArgumentList.Add(assembly);
                process.StartInfo.ArgumentList.Add(clientConfig);
                foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
                process.StartInfo.Environment["IDB_USER_ACCESS_TOKEN"] = token;
                process.Start();
                return process;
            }
            async Task<(int Code, JsonElement Result)> Finish(Process process, string token)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(deadline.Token); }
                finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                var output = await outputTask; var error = await errorTask;
                Assert.IsFalse((output + error).Contains(token, StringComparison.Ordinal), "Client must not log credentials.");
                var code = process.ExitCode;
                if (code is 0 or 2)
                {
                    using var json = JsonDocument.Parse(output);
                    return (code, json.RootElement.Clone());
                }
                return (code, default);
            }
            async Task<JsonElement> Run(string token, params string[] args)
            {
                using var process = StartClient(token, args);
                var result = await Finish(process, token);
                Assert.AreEqual(0, result.Code, "Typed client command failed: " + args[0]);
                return result.Result;
            }
            try
            {
                await Docker(["volume", "create", "--label", OwnerLabel + "=" + _owner, volume]);
                volumeCreated = true;
                await Own("volume", volume);
                var port = await Start();
                var a = await UserToken("parent-a");
                var b = await UserToken("parent-b");
                var decoded = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(a);
                Assert.AreEqual("JWT", decoded.Typ, "Test user token header type.");
                Assert.AreEqual(_authority, decoded.Issuer, "Test user token issuer.");
                Assert.IsTrue(decoded.Audiences.Contains(Audience), "Test user token lacks the database audience.");
                Assert.IsTrue(decoded.TryGetClaim("sub", out _), "Test realm user token lacks sub.");
                Assert.IsTrue(decoded.TryGetClaim("typ", out var discriminator) && discriminator.Value == "Bearer",
                    "Test user access-token discriminator.");
                using (var validator = Validator())
                {
                    var identityA = await validator.ValidateAsync(a);
                    var identityB = await validator.ValidateAsync(b);
                    Assert.AreNotEqual(identityA.Principal.FindFirst("sub")!.Value, identityB.Principal.FindFirst("sub")!.Value);
                }
                Assert.IsFalse((await Run(a, "read")).GetProperty("found").GetBoolean());
                var created = await Run(a, "create", "private-a");
                var revision = created.GetProperty("revision").GetInt64();
                Assert.AreEqual("private-a", (await Run(a, "read")).GetProperty("name").GetString());
                Assert.IsFalse((await Run(b, "read")).GetProperty("found").GetBoolean());
                await Run(b, "create", "private-b"); // Same logical/physical key, different validated subject.
                await Run(b, "update", "9");
                await Run(b, "delete");
                Assert.AreEqual("private-a", (await Run(a, "read")).GetProperty("name").GetString());

                // Two unrelated processes both read the same baseline, then race their conditional updates.
                var release = Path.Combine(directory, "release");
                var readyA = Path.Combine(directory, "ready-a");
                var readyB = Path.Combine(directory, "ready-b");
                using (var first = StartClient(a, "update", "1", readyA, release))
                using (var second = StartClient(a, "update", "2", readyB, release))
                {
                    try
                    {
                        while (!File.Exists(readyA) || !File.Exists(readyB))
                        {
                            if (first.HasExited || second.HasExited) throw new InvalidOperationException("A client exited before the concurrency barrier.");
                            await Task.Delay(25, deadline.Token);
                        }
                        Assert.AreEqual(revision.ToString(CultureInfo.InvariantCulture), await File.ReadAllTextAsync(readyA));
                        Assert.AreEqual(await File.ReadAllTextAsync(readyA), await File.ReadAllTextAsync(readyB));
                        await File.WriteAllTextAsync(release, "go");
                        var results = await Task.WhenAll(Finish(first, a), Finish(second, a));
                        CollectionAssert.AreEquivalent(new[] { 0, 2 }, results.Select(result => result.Code).ToArray());
                    }
                    finally
                    {
                        foreach (var process in new[] { first, second })
                            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                    }
                }
                var winner = await Run(a, "read");
                Assert.IsTrue(winner.GetProperty("counter").GetInt32() is 1 or 2);
                Assert.IsTrue(winner.GetProperty("revision").GetInt64() > revision);

                // Validate rejected credentials through the actual Linux executable, before touching its store.
                foreach (var badClient in new[] { "wrong-audience-client", "no-scope-client" })
                {
                    using var provider = Provider(badClient);
                    var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
                    {
                        await using var raw = await TcpEncodedEnvelopeStore.ConnectAsync(new()
                        {
                            Host = "127.0.0.1", Port = port, TargetHost = "localhost",
                            StoreAlias = "example-profiles", Manifest = ExampleProfileConfig.Manifest,
                            AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer, TokenProvider = provider,
                            ServerCertificateValidation = _certificates.ValidateServer
                        });
                    });
                    Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
                }

                await Own("container", container);
                await Docker(["stop", container]);
                await Docker(["rm", container]);
                containerCreated = false;
                await Own("volume", volume);
                port = await Start(); // Brand new Linux container, same exclusively owned named volume.
                var reopened = await Run(a, "read");
                Assert.AreEqual(winner.GetRawText(), reopened.GetRawText());
                await Run(a, "delete");
                var next = await Run(a, "create", "after-recreation");
                Assert.IsTrue(next.GetProperty("revision").GetInt64() > winner.GetProperty("revision").GetInt64());
                Func<int, string, Task>? additionalProbe = null;
                ConfigureAdditionalProbe(ref additionalProbe);
                if (additionalProbe is not null) await additionalProbe(port, a);
                Console.WriteLine("Typed TCP: real Keycloak user tokens; two processes; one conflict; principal isolation; Linux container recreation; durable revisions. Image " + imageId);
            }
            finally
            {
                if (containerCreated) { await Own("container", container); await Docker(["rm", "--force", container]); }
                if (volumeCreated) { await Own("volume", volume); await Docker(["volume", "rm", volume]); }
                File.Delete(pfx);
            }
        }
    }
}

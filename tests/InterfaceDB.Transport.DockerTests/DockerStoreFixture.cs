using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Idb.Transport;
using InterfaceDB.Transport.Tests;

namespace InterfaceDB.Transport.DockerTests;

/// <summary>Owns only uniquely labelled test resources; never uses the local sample's container or volume.</summary>
internal sealed class DockerStoreFixture : IAsyncDisposable
{
    private static string Image => Environment.GetEnvironmentVariable("IDB_DOCKER_TEST_IMAGE") ?? "interfacedb-tcp-local:dev";
    private const string OwnerLabel = "com.interfacedb.docker-test.owner";
    private const string PasswordVariable = "IDB_SERVER_PFX_PASSWORD";
    private const string Alias = "docker-test";
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(30);
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly TestCertificates _certificates = new();
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _artifactsRoot;
    private readonly string _volumeName;
    private readonly object _disposeGate = new();
    private string? _imageId;
    private string? _containerId;
    private bool _volumeCreated;
    private int _generation;
    private Task? _disposeTask;

    private DockerStoreFixture(CompatibilityManifest manifest)
    {
        Manifest = manifest;
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "InterfaceDB.sln")))
            repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Cannot locate the public repository for Docker test artifacts.");
        _artifactsRoot = Path.GetFullPath(Path.Combine(repository.FullName, "artifacts", "tcp-migration-tests"));
        RootDirectory = Path.Combine(_artifactsRoot, _owner);
        _volumeName = "idb-tcp-test-" + _owner;
    }

    internal CompatibilityManifest Manifest { get; private set; }
    internal int Port { get; private set; }
    internal string RootDirectory { get; }

    internal static async Task<DockerStoreFixture> StartAsync(CompatibilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var fixture = new DockerStoreFixture(manifest);
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture.RootDirectory, "config"));
            Directory.CreateDirectory(Path.Combine(fixture.RootDirectory, "certificates"));
            await File.WriteAllBytesAsync(Path.Combine(fixture.RootDirectory, "certificates", "server.pfx"),
                fixture.CreateServerPfx());
            await fixture.WriteConfigurationAsync();
            var image = await fixture.DockerAsync(["image", "inspect", "--format", "{{.Id}}", Image]);
            fixture._imageId = image.Output.Trim();
            if (fixture._imageId.Length != 71 || !fixture._imageId.StartsWith("sha256:", StringComparison.Ordinal)
                || fixture._imageId[7..].Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidOperationException("Docker did not return an immutable image digest.");
            await fixture.DockerAsync(["volume", "create", "--label", OwnerLabel + "=" + fixture._owner, fixture._volumeName]);
            fixture._volumeCreated = true;
            await fixture.AssertVolumeOwnershipAsync();
            await fixture.StartContainerAsync();
            Console.WriteLine($"Docker fixture {fixture._owner}; image {fixture._imageId}; loopback port {fixture.Port}.");
            return fixture;
        }
        catch (Exception startupFailure)
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Docker fixture startup and cleanup failed.", startupFailure, cleanupFailure);
            }
            throw;
        }
    }

    internal async Task<TcpEncodedEnvelopeStore> ConnectAsync(CompatibilityManifest? manifest = null)
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        using var deadline = new CancellationTokenSource(ConnectionDeadline);
        Exception? lastFailure = null;
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                return await TcpEncodedEnvelopeStore.ConnectAsync(new()
                {
                    Host = "127.0.0.1", TargetHost = "localhost", Port = Port,
                    StoreAlias = Alias, Manifest = manifest ?? Manifest,
                    ClientCertificate = _certificates.Client,
                    ServerCertificateValidation = _certificates.ValidateServer,
                    ConnectTimeout = TimeSpan.FromSeconds(3), OperationTimeout = TimeSpan.FromSeconds(15)
                }, deadline.Token);
            }
            catch (Exception error) when (IsRetryableConnectionFailure(error))
            {
                lastFailure = error;
                try { await Task.Delay(100, deadline.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
        }
        throw new TimeoutException("The isolated Docker storage host did not accept a TLS storage session. "
            + await ContainerDiagnosticsAsync(), lastFailure);
    }

    internal async Task RecreateAsync(CompatibilityManifest? manifest = null, bool crash = false)
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (_containerId is null) throw new InvalidOperationException("The fixture has no active container.");
        await AssertContainerOwnershipAsync();
        await AssertVolumeOwnershipAsync();
        if (crash)
        {
            await DockerAsync(["container", "kill", "--signal", "KILL", _containerId]);
            var killed = await DockerAsync(["container", "inspect", "--format", "{{.State.Running}}/{{.State.ExitCode}}", _containerId]);
            if (killed.Output.Trim() != "false/137")
                throw new InvalidOperationException("The crash test did not terminate the host with SIGKILL: " + killed.Output.Trim()
                    + ". " + await ContainerDiagnosticsAsync());
        }
        else
        {
            await DockerAsync(["container", "stop", "--time", "30", _containerId]);
            var stopped = await DockerAsync(["container", "inspect", "--format", "{{.State.ExitCode}}", _containerId]);
            if (stopped.Output.Trim() != "0")
                throw new InvalidOperationException("The Docker storage host did not exit cleanly: " + stopped.Output.Trim()
                    + ". " + await ContainerDiagnosticsAsync());
        }
        await RemoveContainerAsync();
        Manifest = manifest ?? Manifest;
        await WriteConfigurationAsync();
        await StartContainerAsync();
    }

    internal Task RestartAsync(CompatibilityManifest? manifest = null) => RecreateAsync(manifest);

    private async Task StartContainerAsync()
    {
        await AssertVolumeOwnershipAsync();
        var name = "idb-tcp-test-" + _owner + "-" + ++_generation;
        var configPath = Path.Combine(RootDirectory, "config");
        var certificatePath = Path.Combine(RootDirectory, "certificates", "server.pfx");
        // Remember the unique name before run, so a partly created container can still be cleaned up.
        _containerId = name;
        var result = await DockerAsync([
            "container", "run", "--detach", "--pull", "never", "--name", name,
            "--label", OwnerLabel + "=" + _owner,
            "--publish", "127.0.0.1::7443",
            "--env", PasswordVariable,
            "--mount", "type=volume,source=" + _volumeName + ",target=/var/lib/interfacedb",
            "--mount", "type=bind,source=" + configPath + ",target=/run/interfacedb,readonly",
            "--mount", "type=bind,source=" + certificatePath + ",target=/run/secrets/server.pfx,readonly",
            "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=64m",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--stop-timeout", "30",
            _imageId ?? throw new InvalidOperationException("No immutable image was selected for this fixture.")
        ], supplyPassword: true);
        var id = result.Output.Trim();
        if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Docker did not return a valid isolated container ID.");
        _containerId = id;
        await AssertContainerOwnershipAsync();
        var published = await DockerAsync(["container", "inspect", "--format",
            "{{ (index (index .NetworkSettings.Ports \"7443/tcp\") 0).HostPort }}", id]);
        if (!int.TryParse(published.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
            throw new InvalidOperationException("Docker did not allocate an ephemeral host TCP port.");
        Port = port;
        await using var readiness = await ConnectAsync();
    }

    private Task WriteConfigurationAsync() => File.WriteAllTextAsync(
        Path.Combine(RootDirectory, "config", "hostsettings.json"), JsonSerializer.Serialize(new
        {
            bindAddress = "0.0.0.0", port = 7443, storeAlias = Alias,
            dataDirectory = "/var/lib/interfacedb", serverCertificatePath = "/run/secrets/server.pfx",
            serverCertificatePasswordEnvironmentVariable = PasswordVariable,
            trustedClientCertificateSha256 = new[] { _certificates.Client.GetCertHashString(HashAlgorithmName.SHA256) },
            manifest = Manifest
        }));

    private async Task AssertContainerOwnershipAsync()
    {
        if (_containerId is null) throw new InvalidOperationException("No fixture container is registered.");
        var owner = await DockerAsync(["container", "inspect", "--format",
            "{{ index .Config.Labels \"" + OwnerLabel + "\" }}", _containerId]);
        if (owner.Output.Trim() != _owner)
            throw new InvalidOperationException("Refusing to mutate a container without this fixture's ownership label.");
    }

    private async Task AssertVolumeOwnershipAsync()
    {
        if (!_volumeCreated) throw new InvalidOperationException("No fixture volume is registered.");
        var owner = await DockerAsync(["volume", "inspect", "--format",
            "{{ index .Labels \"" + OwnerLabel + "\" }}", _volumeName]);
        if (owner.Output.Trim() != _owner)
            throw new InvalidOperationException("Refusing to use or remove a volume without this fixture's ownership label.");
    }

    private async Task RemoveContainerAsync()
    {
        if (_containerId is null) return;
        var owner = await DockerAsync(["container", "inspect", "--format",
            "{{ index .Config.Labels \"" + OwnerLabel + "\" }}", _containerId], allowFailure: true);
        if (owner.ExitCode != 0)
        {
            if (owner.Error.Contains("No such container", StringComparison.OrdinalIgnoreCase)
                || owner.Error.Contains("No such object", StringComparison.OrdinalIgnoreCase))
            {
                _containerId = null;
                return;
            }
            throw new InvalidOperationException("Could not verify the fixture container before cleanup. " + owner.Error);
        }
        if (owner.Output.Trim() != _owner)
            throw new InvalidOperationException("Refusing to remove a container without this fixture's ownership label.");
        await DockerAsync(["container", "rm", "--force", _containerId]);
        _containerId = null;
    }

    private async Task<string> ContainerDiagnosticsAsync()
    {
        if (_containerId is null) return "No container was registered.";
        var state = await DockerAsync(["container", "inspect", "--format", "{{.State.Status}}/{{.State.ExitCode}}", _containerId], allowFailure: true);
        var logs = await DockerAsync(["container", "logs", "--tail", "30", _containerId], allowFailure: true);
        return "Container state: " + state.Output.Trim() + ". Logs: " + logs.Output + logs.Error;
    }

    private static bool IsRetryableConnectionFailure(Exception error) => error switch
    {
        RemoteStoreException remote => remote.Code == RemoteErrorCode.Busy,
        SocketException or IOException or TimeoutException => true,
        _ => false
    };

    private async Task<CommandResult> DockerAsync(string[] arguments, bool supplyPassword = false, bool allowFailure = false)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (supplyPassword) start.Environment[PasswordVariable] = _password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Docker CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(CommandDeadline); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw new TimeoutException("Docker command timed out: " + string.Join(" ", arguments.Take(2))
                + ". " + Redact(await error));
        }
        var result = new CommandResult(process.ExitCode, Redact(await output), Redact(await error));
        if (result.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException("Docker command failed: " + string.Join(" ", arguments.Take(2))
                + " (exit " + result.ExitCode + "). " + result.Error + result.Output);
        return result;
    }

    private string Redact(string text) => text.Replace(_password, "[REDACTED]", StringComparison.Ordinal);

    private byte[] CreateServerPfx()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.Create(_certificates.Root, DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var certificate = signed.CopyWithPrivateKey(key);
        return certificate.Export(X509ContentType.Pfx, _password);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        try { await RemoveContainerAsync(); }
        catch (Exception error) { failures.Add(error); }
        if (_volumeCreated)
        {
            try
            {
                await AssertVolumeOwnershipAsync();
                await DockerAsync(["volume", "rm", _volumeName]);
                _volumeCreated = false;
            }
            catch (Exception error) { failures.Add(error); }
        }
        _certificates.Dispose();
        try
        {
            var path = Path.GetFullPath(RootDirectory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(path), _artifactsRoot, comparison)
                || Path.GetFileName(path) != _owner)
                throw new InvalidOperationException("Refusing to remove a directory outside this fixture's artifact root.");
            if (Directory.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing to recursively remove a redirected fixture directory.");
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Isolated Docker fixture cleanup failed.", failures);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}

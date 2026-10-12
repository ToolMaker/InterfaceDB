using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Idb.Authentication;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Serialization;
using InterfaceDB.TcpRepository.Sample;

if (args.Length < 2) { Console.Error.WriteLine("Usage: <config.json> create|read|update|delete [value] [ready-file release-file]"); return 1; }
try
{
    var config = JsonSerializer.Deserialize<ExampleClientConfiguration>(File.ReadAllText(args[0]),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
        ?? throw new ArgumentException("Configuration is missing.");
    var token = Environment.GetEnvironmentVariable(config.AccessTokenEnvironmentVariable);
    if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A signed-in user's access token must be supplied through the configured environment variable.");
    using var root = config.ServerCaCertificatePath is null ? null : X509CertificateLoader.LoadCertificateFromFile(
        Path.GetFullPath(config.ServerCaCertificatePath, Path.GetDirectoryName(Path.GetFullPath(args[0]))!));
    bool Validate(object sender, X509Certificate? certificate, X509Chain? _, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root!);
        chain.ChainPolicy.RevocationMode = config.DevelopmentCaWithoutRevocation ? X509RevocationMode.NoCheck : X509RevocationMode.Online;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(leaf);
    }
    await using var repository = new TcpRepository<IExampleProfile>(new()
    {
        Contract = new ExampleProfileConfig(), RuntimeTypes = new(typeof(ExampleProfile)),
        Connection = new()
        {
            Host = config.Host, Port = config.Port, TargetHost = config.TargetHost, StoreAlias = config.StoreAlias,
            Manifest = ExampleProfileConfig.Manifest, AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
            TokenProvider = new FixedAccessTokenProvider(new AccessToken(token)),
            ServerCertificateValidation = root is null ? null : Validate
        }
    });
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    IExampleProfile? profile;
    switch (args[1])
    {
        case "create":
            profile = await repository.SaveAsync(new ExampleProfile { DisplayName = args.Length > 2 ? args[2] : "Example" }, deadline.Token);
            break;
        case "read":
            profile = await repository.QueryFirstAsync(_ => true, deadline.Token);
            break;
        case "update":
            profile = await repository.QuerySingleAsync(_ => true, deadline.Token);
            profile.Counter = int.Parse(args[2], CultureInfo.InvariantCulture);
            if (args.Length == 5)
            {
                await File.WriteAllTextAsync(args[3], profile.Envelope!.RevisionTimestampUtcMicroseconds.ToString(CultureInfo.InvariantCulture), deadline.Token);
                while (!File.Exists(args[4])) await Task.Delay(25, deadline.Token);
            }
            profile = await repository.SaveAsync(profile, deadline.Token);
            break;
        case "delete":
            profile = await repository.QueryFirstAsync(_ => true, deadline.Token);
            if (profile is not null) await repository.DeleteAsync(profile, deadline.Token);
            profile = null;
            break;
        default: throw new ArgumentException("Unknown command.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { status = "ok", found = profile is not null,
        name = profile?.DisplayName, counter = profile?.Counter, revision = profile?.Envelope?.RevisionTimestampUtcMicroseconds }));
    return 0;
}
catch (WriteConflictException<IExampleProfile> exception)
{
    Console.WriteLine(JsonSerializer.Serialize(new { status = "conflict", expected = exception.ExpectedRevision, current = exception.CurrentRevision }));
    return 2;
}
catch (RemoteRepositoryCommitOutcomeUnknownException)
{
    Console.Error.WriteLine("Outcome unknown. Read fresh state and reconcile before another write.");
    return 3;
}
catch (Exception exception)
{
    // Avoid dumping credentials, configuration or token-validation exception details.
    Console.Error.WriteLine("Repository operation failed: " + exception.GetType().Name);
    return 1;
}

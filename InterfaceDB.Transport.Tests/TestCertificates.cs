using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace InterfaceDB.Transport.Tests;

/// <summary>Per-test certificate authority. Never installs anything in a machine or user trust store.</summary>
internal sealed class TestCertificates : IDisposable
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    internal TestCertificates()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=InterfaceDB ephemeral test CA " + Guid.NewGuid().ToString("N"),
            rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        Server = CreateLeaf("localhost", ServerAuthentication, server: true);
        Client = CreateLeaf("interfacedb-test-client", ClientAuthentication, server: false);
    }

    internal X509Certificate2 Root { get; }
    internal X509Certificate2 Server { get; }
    internal X509Certificate2 Client { get; }
    internal RemoteCertificateValidationCallback ValidateServer => (_, certificate, _, errors) => Validate(certificate, errors, ServerAuthentication);
    internal RemoteCertificateValidationCallback ValidateClient => (_, certificate, _, errors) => Validate(certificate, errors, ClientAuthentication);

    private X509Certificate2 CreateLeaf(string commonName, string purpose, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(purpose) }, true));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
        }
        using var signed = request.Create(Root, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        // Windows Schannel requires a temporary key container instead of an ephemeral RSA key.
        // DefaultKeySet deletes that container on disposal; no certificate enters a trust store.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.DefaultKeySet);
    }

    private bool Validate(X509Certificate? certificate, SslPolicyErrors errors, string purpose)
    {
        // A private CA override changes trust only. Hostname and presence still come from TLS.
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(purpose));
        return chain.Build(leaf);
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        Root.Dispose();
    }
}

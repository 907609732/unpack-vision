using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UnpackVision.StationHost;

public sealed record StationCertificateMaterial(
    X509Certificate2 Certificate,
    string Fingerprint,
    string CertificatePemPath,
    string PrivateKeyPemPath);

public static class StationCertificateStore
{
    private static readonly byte[] Entropy = "UnpackVision.StationCertificate.v1"u8.ToArray();

    public static StationCertificateMaterial LoadOrCreate(
        string stationId,
        IReadOnlyCollection<IPAddress> addresses,
        string securityDirectory)
    {
        var root = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(securityDirectory));
        Directory.CreateDirectory(root);
        var protectedPfxPath = Path.Combine(root, "station-certificate.pfx.protected");
        X509Certificate2 certificate;
        if (File.Exists(protectedPfxPath))
        {
            var protectedBytes = File.ReadAllBytes(protectedPfxPath);
            var pfx = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            certificate = X509CertificateLoader.LoadPkcs12(
                pfx,
                password: null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        else
        {
            certificate = CreateCertificate(stationId, addresses);
            var pfx = certificate.Export(X509ContentType.Pfx);
            var protectedBytes = ProtectedData.Protect(
                pfx,
                Entropy,
                DataProtectionScope.CurrentUser);
            var temporary = protectedPfxPath + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, protectedPfxPath, true);
        }

        var certificatePemPath = Path.Combine(root, "station-certificate.pem");
        var privateKeyPemPath = Path.Combine(root, "station-private-key.pem");
        File.WriteAllText(certificatePemPath, certificate.ExportCertificatePem());
        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new CryptographicException("工位证书没有可用的 RSA 私钥");
        File.WriteAllText(privateKeyPemPath, rsa.ExportPkcs8PrivateKeyPem());

        return new StationCertificateMaterial(
            certificate,
            Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(),
            certificatePemPath,
            privateKeyPemPath);
    }

    private static X509Certificate2 CreateCertificate(
        string stationId,
        IReadOnlyCollection<IPAddress> addresses)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=UnpackVision-{SanitizeName(stationId)}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        var sanitized = SanitizeName(stationId).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            san.AddDnsName(sanitized);
            san.AddDnsName($"{sanitized}.local");
        }
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var address in addresses.Where(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
        {
            san.AddIpAddress(address);
        }
        request.CertificateExtensions.Add(san.Build());

        var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static string SanitizeName(string value)
    {
        var sanitized = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "station" : sanitized;
    }
}

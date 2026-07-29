using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UnpackVision.StationHost;

namespace UnpackVision.Tests;

public sealed class StationCertificateTests
{
    [Fact]
    public async Task GeneratedCertificateSupportsTlsAndHasStableFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnpackVision-Certificate-{Guid.NewGuid():N}");
        try
        {
            var material = StationCertificateStore.LoadOrCreate(
                "station-test",
                [IPAddress.Parse("192.168.31.100")],
                root);
            Assert.True(material.Certificate.HasPrivateKey);
            Assert.Matches("^[0-9a-f]{64}$", material.Fingerprint);
            using (var rsa = material.Certificate.GetRSAPrivateKey())
            {
                Assert.NotNull(rsa);
                Assert.NotEmpty(rsa.SignData(
                    "tls-test"u8,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1));
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync();
                await using var stream = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                await stream.AuthenticateAsServerAsync(
                    material.Certificate,
                    clientCertificateRequired: false,
                    SslProtocols.Tls12 | SslProtocols.Tls13,
                    checkCertificateRevocation: false);
            });
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port);
            await using var clientStream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            await clientStream.AuthenticateAsClientAsync("192.168.31.100");
            await server;
            listener.Stop();

            var reloaded = StationCertificateStore.LoadOrCreate(
                "station-test",
                [IPAddress.Parse("192.168.31.100")],
                root);
            Assert.Equal(material.Fingerprint, reloaded.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

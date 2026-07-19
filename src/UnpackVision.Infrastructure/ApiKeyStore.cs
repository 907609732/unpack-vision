using System.Security.Cryptography;
using System.Text;

namespace UnpackVision.Infrastructure;

public sealed class ApiKeyStore
{
    private readonly string _path;

    public ApiKeyStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnpackVision",
            "api-key.protected");
    }

    public string Path => _path;

    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path))
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        await File.WriteAllBytesAsync(_path, encrypted, cancellationToken);
        return key;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace UnpackVision.Infrastructure;

public static class CameraCredentialProtector
{
    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }
}

public static class CameraSourceUrlBuilder
{
    public static string Build(CameraOptions options)
    {
        var password = CameraCredentialProtector.Unprotect(options.NetworkPasswordProtected);
        return options.SourceKind switch
        {
            CameraSourceKind.NetworkStream => AddCredentials(options.NetworkStreamUrl, options.NetworkUsername, password),
            CameraSourceKind.HikvisionRecorder => BuildHikvisionRtspUrl(
                options.HikvisionHost,
                options.HikvisionRtspPort,
                options.HikvisionChannel,
                options.HikvisionSubStream,
                options.NetworkUsername,
                password),
            _ => throw new InvalidOperationException("当前相机来源不是网络视频流")
        };
    }

    public static string BuildHikvisionRtspUrl(
        string host,
        int port,
        int channel,
        bool subStream,
        string username = "",
        string password = "")
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("海康录像机地址不能为空", nameof(host));
        }
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        if (channel is < 1 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        var cleanHost = host.Trim().Replace("rtsp://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var credentials = string.IsNullOrWhiteSpace(username)
            ? string.Empty
            : $"{Uri.EscapeDataString(username.Trim())}:{Uri.EscapeDataString(password)}@";
        var streamNumber = subStream ? "02" : "01";
        return $"rtsp://{credentials}{cleanHost}:{port}/Streaming/channels/{channel}{streamNumber}";
    }

    public static string AddCredentials(string url, string username, string password)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) &&
             uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("网络视频流地址必须是有效的 RTSP、HTTP 或 HTTPS 地址", nameof(url));
        }
        if (string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.AbsoluteUri;
        }
        var builder = new UriBuilder(uri)
        {
            UserName = username.Trim(),
            Password = password
        };
        return builder.Uri.AbsoluteUri;
    }
}

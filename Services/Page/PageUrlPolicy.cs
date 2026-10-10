using System.Net;
using System.Net.Sockets;

namespace RainBot.Services.Page;

public static class PageUrlPolicy
{
    private static readonly string[] ExcludedHosts =
    [
        "weibo.com", "weibo.cn", "sinaweibo.com", "t.cn", "weibocdn.com", "sinaimg.cn",
        "xiaohongshu.com", "xhslink.com", "xhscdn.com", "zhihu.com", "zhimg.com"
    ];

    public static Uri Validate(string input)
    {
        if (input.Length > 4096 || !Uri.TryCreate(input, UriKind.Absolute, out Uri? url)
            || (url.Scheme != "http" && url.Scheme != "https") || !string.IsNullOrEmpty(url.UserInfo)
            || url.Port is not (80 or 443))
            throw new InvalidOperationException("仅允许不含账号信息、使用 80/443 端口的 HTTP(S) 公网地址。");
        string host = url.IdnHost.TrimEnd('.');
        if (ExcludedHosts.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不读取微博、小红书、知乎及其相关链接。");
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') && !host.Contains(':')
            || IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip) && !IsPublicAddress(ip))
            throw new InvalidOperationException("不允许访问本机、内网或保留地址。");
        return new UriBuilder(url) { Fragment = "" }.Uri;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || b[0] == 100 && b[1] is >= 64 and <= 127
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99)
                || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        // 仅允许全球单播，排除文档、隧道及特殊用途前缀。
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
            && (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0)
            && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8));
    }

    internal static void ValidateResolvedAddresses(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new InvalidOperationException("域名解析结果包含内网或保留地址，已拒绝连接。");
    }

    /// <summary>在实际连接时校验 DNS 并直接连接选定 IP，避免二次解析绕过校验。</summary>
    public static SocketsHttpHandler CreateHandler() => CreateHandler(null);
    public static SocketsHttpHandler CreateHandler(Func<string, CancellationToken, Task<IPAddress[]>>? resolver) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, cancellationToken) =>
        {
            IPAddress[] addresses = resolver == null
                ? await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
                : await resolver(context.DnsEndPoint.Host, cancellationToken);
            ValidateResolvedAddresses(addresses);
            foreach (IPAddress address in addresses)
            {
                Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("无法连接目标站点。");
        }
    };
}

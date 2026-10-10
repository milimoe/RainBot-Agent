using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace RainBot.Services.Page;

/// <summary>显式启用的公网 DoH，适用于系统 DNS 返回代理假 IP 的环境。解析结果仍须通过公网检查。</summary>
public sealed class PublicPageDnsResolver(IHttpClientFactory clients) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 512 });

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal))
        {
            PageUrlPolicy.ValidateResolvedAddresses([literal]);
            return [literal];
        }
        if (_cache.TryGetValue(host, out IPAddress[]? cached)) return cached!;
        using HttpClient client = clients.CreateClient("page_dns");
        using HttpRequestMessage request = new(HttpMethod.Get,
            "https://dns.alidns.com/resolve?name=" + Uri.EscapeDataString(host) + "&type=A");
        request.Headers.Accept.ParseAdd("application/dns-json");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 64 * 1024) throw new InvalidOperationException("公网 DNS 响应超过大小上限。");
            buffer.Write(chunk, 0, count);
        }
        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        IPAddress[] addresses = Parse(document.RootElement, out int ttl);
        _cache.Set(host, addresses, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl) });
        return addresses;
    }

    internal static IPAddress[] Parse(JsonElement root, out int ttl)
    {
        ttl = 300;
        if (root.GetProperty("Status").GetInt32() != 0 || !root.TryGetProperty("Answer", out JsonElement answers))
            throw new InvalidOperationException("公网 DNS 未取得地址。");
        List<IPAddress> addresses = [];
        foreach (JsonElement answer in answers.EnumerateArray())
        {
            if (answer.GetProperty("type").GetInt32() != 1) continue;
            if (IPAddress.TryParse(answer.GetProperty("data").GetString(), out IPAddress? address)) addresses.Add(address);
            if (answer.TryGetProperty("TTL", out JsonElement lifetime)) ttl = Math.Min(ttl, lifetime.GetInt32());
        }
        PageUrlPolicy.ValidateResolvedAddresses(addresses.ToArray());
        ttl = Math.Clamp(ttl, 1, 300);
        return addresses.Distinct().ToArray();
    }

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, cancellationToken) =>
        {
            if (context.DnsEndPoint.Host != "dns.alidns.com" || context.DnsEndPoint.Port != 443)
                throw new InvalidOperationException("公网 DNS 客户端仅允许固定解析服务。");
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("223.5.5.5"), 443), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };
    public void Dispose() => _cache.Dispose();
}

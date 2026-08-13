using RainBot.Services.Config;

namespace RainBot.Services.Fun;

/// <summary>一张 OSM 梗图（相对路径 + 基于公网域名的完整 URL）</summary>
public sealed record OsmImage(string RelativePath, string Url);

/// <summary>
/// OSM 梗图目录：
/// - 图片不再单独配置路径：自动扫描 wwwroot/osm/（含子目录）；
/// - 对外 URL = Rain.PublicBaseUrl（域名，只设置一次）+ wwwroot 相对路径；
/// - 域名未设置时返回空列表，OSM 功能自动禁用。
/// </summary>
public class OsmImageCatalog(RuntimeConfig config, ILogger<OsmImageCatalog> logger, IWebHostEnvironment? env = null)
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];
    private static readonly TimeSpan ScanTtl = TimeSpan.FromSeconds(60);

    private readonly RuntimeConfig _config = config;
    private readonly ILogger<OsmImageCatalog> _logger = logger;
    private readonly Lock _lock = new();
    private List<string>? _cachedFiles;
    private DateTime _cachedAt = DateTime.MinValue;

    /// <summary>OSM 图片目录（wwwroot/osm；测试环境无 wwwroot 时使用运行目录）</summary>
    public string OsmDirectory
    {
        get
        {
            string wwwroot = env?.WebRootPath
                ?? (string.IsNullOrWhiteSpace(env?.ContentRootPath)
                    ? Path.Combine(AppContext.BaseDirectory, "wwwroot")
                    : Path.Combine(env.ContentRootPath, "wwwroot"));
            return Path.Combine(wwwroot, "osm");
        }
    }

    /// <summary>公网域名（已去除末尾斜杠；未设置时为 null）</summary>
    public string? BaseUrl
    {
        get
        {
            string value = (_config.Config.PublicBaseUrl ?? "").Trim().TrimEnd('/');
            return value.Length > 0 ? value : null;
        }
    }

    /// <summary>扫描到的图片相对路径（相对 wwwroot，如 osm/osm.jpg），带 60 秒缓存</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_lock)
            {
                if (_cachedFiles != null && DateTime.UtcNow - _cachedAt < ScanTtl)
                {
                    return _cachedFiles;
                }
                _cachedFiles = Scan();
                _cachedAt = DateTime.UtcNow;
                return _cachedFiles;
            }
        }
    }

    /// <summary>可直接发送给 QQ 的完整图片 URL 列表（域名未设置时为空 → OSM 自动禁用）</summary>
    public IReadOnlyList<OsmImage> Images
    {
        get
        {
            string? baseUrl = BaseUrl;
            if (baseUrl == null)
            {
                return [];
            }
            return Files.Select(f => new OsmImage(f, $"{baseUrl}/{f.Replace('\\', '/')}")).ToList();
        }
    }

    private List<string> Scan()
    {
        string root = OsmDirectory;
        try
        {
            if (!Directory.Exists(root))
            {
                return [];
            }
            List<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f =>
                {
                    string relative = Path.GetRelativePath(Path.GetDirectoryName(root)!, f).Replace('\\', '/');
                    return relative;
                })
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("OSM 目录扫描：{Count} 张图片（{Root}）", files.Count, root);
            return files;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OSM 目录扫描失败（{Root}）", root);
            return [];
        }
    }
}

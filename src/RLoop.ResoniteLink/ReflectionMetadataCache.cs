using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using RLoop.Core;

namespace RLoop.ResoniteLink;

internal sealed record CachedReflection<T>(T Value, DateTimeOffset ObservedAt, bool Live);

// SDK metadata stays behind the adapter. Never store instance IDs or field values here.
internal sealed class ReflectionMetadataCache
{
    private sealed record Entry<T>(string Key, DateTimeOffset ObservedAt, T Value);
    private readonly string directory;
    private readonly string scope;
    private readonly ReflectionCacheOptions options;
    private readonly bool identified;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };
    public int DiskHits { get; private set; }
    public int DiskMisses { get; private set; }
    public int WriteFailures { get; private set; }

    public ReflectionMetadataCache(SessionInfo session, ReflectionCacheOptions options)
    {
        options.Validate();
        this.options = options;
        directory = Path.GetFullPath(options.Directory ?? Path.Combine(Environment.CurrentDirectory, ".resoloop", "cache", "reflection"));
        // Build identity also invalidates entries after changes to metadata mapping/serialization.
        scope = JsonSerializer.Serialize(new { schema = 2, endpoint = CacheEndpoint(session.Url), session.ResoniteVersion,
            session.ResoniteLinkVersion, build = typeof(ResoniteLinkClientAdapter).Assembly.ManifestModule.ModuleVersionId, coreBuild = typeof(ComponentTypeInfo).Assembly.ManifestModule.ModuleVersionId }, Json);
        identified = !string.IsNullOrWhiteSpace(session.ResoniteVersion) && !string.IsNullOrWhiteSpace(session.ResoniteLinkVersion);
    }

    public CachedReflection<T>? Read<T>(string kind, string name, Func<T, bool> valid) where T : class
    {
        if (options.Mode != "auto" || !identified) return null;
        var key = Key(kind, name);
        try
        {
            var path = PathFor(key);
            if (new FileInfo(path) is { Exists: true, Length: > 0 and <= 8 * 1024 * 1024 })
            {
                var entry = JsonSerializer.Deserialize<Entry<T>>(File.ReadAllText(path), Json);
                var now = DateTimeOffset.UtcNow;
                if (entry is not null && entry.Key == key && entry.ObservedAt <= now &&
                    (options.MaxAge is null || now - entry.ObservedAt <= options.MaxAge.Value) &&
                    entry.Value is not null && valid(entry.Value))
                {
                    DiskHits++;
                    return new(entry.Value, entry.ObservedAt, false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        DiskMisses++;
        return null;
    }

    public void Write<T>(string kind, string name, T value, DateTimeOffset observedAt)
    {
        if (options.Mode == "off" || !identified) return;
        var key = Key(kind, name);
        var path = PathFor(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry<T>(key, observedAt, value), Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteFailures++; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void ResetMetrics() { DiskHits = 0; DiskMisses = 0; WriteFailures = 0; }

    public void Invalidate(string kind, string name)
    {
        if (options.Mode == "off") return;
        try { File.Delete(PathFor(Key(kind, name))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteFailures++; }
    }

    private string Key(string kind, string name) => Hash(scope + "\n" + kind + "\n" + name);
    // Local world restarts commonly change the port. It is not part of a type's identity.
    private static string CacheEndpoint(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback ? "loopback" : url;
    private string PathFor(string key) => Path.Combine(directory, key + ".json");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

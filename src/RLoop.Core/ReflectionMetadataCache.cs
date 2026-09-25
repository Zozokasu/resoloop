namespace RLoop.Core;

public sealed record ReflectionCacheOptions(string Mode = "auto", string? Directory = null, TimeSpan? MaxAge = null)
{
    public void Validate()
    {
        if (Mode is not ("auto" or "off" or "refresh") || MaxAge is { } age && age <= TimeSpan.Zero)
            throw new RLoopException("REFLECTION_CACHE_INVALID", "Cache mode must be auto, off or refresh; an explicit max age must be positive.", ExitCodes.InvalidArguments);
    }
}

// Live records were read in this connection; disk records are trusted by version identity.
public sealed record ReflectionMetadata<T>(T Value, DateTimeOffset ObservedAt, bool Live);
public sealed record ReflectionCacheStatistics(int DiskHits, int DiskMisses, int WriteFailures);

public interface IReflectionMetadataClient
{
    void ConfigureReflectionCache(ReflectionCacheOptions options);
    Task<ReflectionMetadata<ComponentTypeInfo>> DescribeComponentMetadataAsync(string type, bool refresh = false, CancellationToken ct = default);
    Task<ReflectionMetadata<TypeInfo>> DescribeTypeMetadataAsync(string type, bool refresh = false, CancellationToken ct = default);
    ReflectionCacheStatistics SnapshotReflectionCache();
}

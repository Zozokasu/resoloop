using Link = ResoniteLink;

namespace RLoop.ResoniteLink;

/// <summary>
/// Narrow seam over the ResoniteLink calls that read type metadata, so the adapter's
/// unknown-versus-not-found handling can be tested offline. Production uses <see cref="SdkMetadataLink"/>.
/// </summary>
internal interface IMetadataLink : IDisposable
{
    bool IsConnected { get; }
    Task<LinkTypeList> GetAllComponentTypes();
    Task<LinkTypeList> GetComponentTypes(string category);
    Task<LinkComponentDefinition> GetComponentDefinition(string type);
    Task<LinkTypeDefinition> GetTypeDefinition(string type);
    Task<LinkEnumDefinition> GetEnumDefinition(string type);
}

internal sealed record LinkTypeList(bool Success, string? ErrorInfo, IReadOnlyList<string>? ComponentTypes, IReadOnlyList<string>? SubCategories);
internal sealed record LinkComponentDefinition(bool Success, string? ErrorInfo, Link.ComponentDefinition Definition);
internal sealed record LinkTypeDefinition(bool Success, string? ErrorInfo, Link.TypeDefinition Definition);
internal sealed record LinkEnumDefinition(bool Success, string? ErrorInfo, IReadOnlyDictionary<string, long> Values, bool IsFlags);

internal sealed class SdkMetadataLink(Link.LinkInterface link) : IMetadataLink
{
    public bool IsConnected => link.IsConnected;

    public async Task<LinkTypeList> GetAllComponentTypes()
    {
        var r = await link.GetAllComponentTypes().ConfigureAwait(false);
        return new(r.Success, r.ErrorInfo, r.ComponentTypes, r.SubCategories?.ToArray());
    }

    public async Task<LinkTypeList> GetComponentTypes(string category)
    {
        var r = await link.GetComponentTypes(category).ConfigureAwait(false);
        return new(r.Success, r.ErrorInfo, r.ComponentTypes, r.SubCategories?.ToArray());
    }

    public async Task<LinkComponentDefinition> GetComponentDefinition(string type)
    {
        var r = await link.GetComponentDefinition(type, true).ConfigureAwait(false);
        return new(r.Success, r.ErrorInfo, r.Definition);
    }

    public async Task<LinkTypeDefinition> GetTypeDefinition(string type)
    {
        var r = await link.GetTypeDefinition(type).ConfigureAwait(false);
        return new(r.Success, r.ErrorInfo, r.Definition);
    }

    public async Task<LinkEnumDefinition> GetEnumDefinition(string type)
    {
        var r = await link.GetEnumDefinition(type).ConfigureAwait(false);
        return new(r.Success, r.ErrorInfo, r.Definition?.Values!, r.Definition?.IsFlags ?? false);
    }

    public void Dispose() => link.Dispose();
}

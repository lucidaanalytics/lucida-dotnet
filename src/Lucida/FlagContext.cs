using System.Text.Json;

namespace Lucida;

/// <summary>Everything a flag may read. Fields the caller does not supply are treated as absent.</summary>
public sealed class FlagContext
{
    public string? DistinctId { get; init; }
    /// <summary>Overrides <see cref="DistinctId"/> for person aggregation.</summary>
    public string? BucketingKey { get; init; }
    public string? DeviceId { get; init; }
    public string? AppVersion { get; init; }
    /// <summary>Group type to group key.</summary>
    public Dictionary<string, string>? Groups { get; init; }
    public Dictionary<string, JsonElement>? PersonProperties { get; init; }
    /// <summary>Group type to that group's properties.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? GroupProperties { get; init; }
    public Dictionary<string, JsonElement>? DeviceProperties { get; init; }
    public Dictionary<string, JsonElement>? GeoProperties { get; init; }
    /// <summary>Flag key to the unit's stored value (true or a variant key) for sticky flags.</summary>
    public Dictionary<string, JsonElement>? StickyAssignments { get; init; }
    /// <summary>Evaluation clock (RFC 3339) for relative date operators; null means the wall clock.</summary>
    public string? Now { get; init; }
    /// <summary>App contexts this request declares; flags scoped to none of them are dropped (null: the client default).</summary>
    public List<string>? AppContexts { get; init; }
    /// <summary>Pre-identify id of this person, sent on remote requests so the gateway links it to <see cref="DistinctId"/>.</summary>
    public string? AnonDistinctId { get; init; }
}

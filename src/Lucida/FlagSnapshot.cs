using System.Collections.Concurrent;
using System.Text.Json;

namespace Lucida;

/// <summary>Flags evaluated once for one distinct id. Reads never call the network or emit exposure events.</summary>
public sealed class FlagSnapshot(IReadOnlyDictionary<string, object> values, IReadOnlyDictionary<string, JsonElement>? payloads = null)
{
    private readonly ConcurrentDictionary<string, bool> _accessed = new();

    public IReadOnlyDictionary<string, object> Values { get; } = new Dictionary<string, object>(values);
    public IReadOnlyDictionary<string, JsonElement> Payloads { get; } = new Dictionary<string, JsonElement>(payloads ?? new Dictionary<string, JsonElement>());

    /// <summary><c>$feature/&lt;key&gt;</c> per flag plus sorted <c>$active_feature_flags</c>; entries already in <paramref name="props"/> win.</summary>
    public static Dictionary<string, object?> EventProps(IReadOnlyDictionary<string, object?>? props, IReadOnlyDictionary<string, object> values)
    {
        var o = new Dictionary<string, object?>();
        if (values.Count > 0)
        {
            foreach (var (k, v) in values) o["$feature/" + k] = v;
            o["$active_feature_flags"] = values.Where(kv => kv.Value is not false).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        }
        if (props is not null) foreach (var (k, v) in props) o[k] = v;
        return o;
    }

    /// <summary><c>bool</c>, a variant key, or null when the flag is not in the snapshot.</summary>
    public object? GetFlag(string key)
    {
        _accessed[key] = true;
        return Values.GetValueOrDefault(key);
    }

    public bool IsEnabled(string key) => GetFlag(key) is { } v and not false;

    /// <summary>Alias of <see cref="GetFlag"/> without recording the read as accessed.</summary>
    public object? Get(string key) => Values.GetValueOrDefault(key);

    /// <summary>Alias of <see cref="GetPayload"/>.</summary>
    public JsonElement? Payload(string key) => GetPayload(key);

    public JsonElement? GetPayload(string key) => Payloads.TryGetValue(key, out var p) ? p : null;

    public IReadOnlyList<string> Keys => Values.Keys.Order(StringComparer.Ordinal).ToList();

    public FlagSnapshot Only(IEnumerable<string> keys)
    {
        var ks = keys.ToHashSet();
        return new FlagSnapshot(Values.Where(kv => ks.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Payloads.Where(kv => ks.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    public FlagSnapshot OnlyAccessed() => Only(_accessed.Keys);

    public Dictionary<string, object?> ToEventProps(IReadOnlyDictionary<string, object?>? props = null) => EventProps(props, Values);
}

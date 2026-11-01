using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucida;

/// <summary>Per-client super properties and the bounded set of identify/group calls already sent.</summary>
internal sealed class IdentityState
{
    private const int DedupeSize = 1000;
    private readonly ConcurrentDictionary<string, object?> _super = new();
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _seen = [];

    public void Register(IReadOnlyDictionary<string, object?> props)
    {
        foreach (var kv in props) _super[kv.Key] = kv.Value;
    }

    public void Unregister(string key) => _super.TryRemove(key, out _);

    public void MergeSuper(JsonObject into)
    {
        foreach (var kv in _super) into[kv.Key] = JsonSerializer.SerializeToNode(kv.Value);
    }

    /// <summary>True the first time this exact call (kind, id, properties) is seen.</summary>
    public bool First(params object?[] parts)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
        lock (_order)
        {
            var fresh = _seen.Add(key);
            if (!fresh) _order.Remove(key);
            _order.AddLast(key);
            if (_order.Count > DedupeSize)
            {
                _seen.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            return fresh;
        }
    }
}

public sealed partial class LucidaClient
{
    /// <summary>Adds super properties to every later event; context and per-call properties win.</summary>
    public void Register(IReadOnlyDictionary<string, object?> properties) => _identity.Register(properties);

    public void Unregister(string key) => _identity.Unregister(key);

    /// <summary><c>$set</c> event updating person properties.</summary>
    public void SetPersonProperties(string distinctId, IReadOnlyDictionary<string, object?> properties) =>
        Capture(distinctId, "$set", set: properties);

    /// <summary><c>$set</c> event with <c>$set_once</c>: only fills properties that are not set yet.</summary>
    public void SetPersonPropertiesOnce(string distinctId, IReadOnlyDictionary<string, object?> properties) =>
        Capture(distinctId, "$set", setOnce: properties);

    /// <summary>Capture with exactly the snapshot's flag values attached; explicit properties win.</summary>
    public void CaptureWithFlags(string? distinctId, string eventName, IReadOnlyDictionary<string, object?>? properties, FlagSnapshot flags)
    {
        Capture(distinctId, eventName, flags.ToEventProps(properties));
    }
}

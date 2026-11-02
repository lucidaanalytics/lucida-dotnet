using System.Text.Json;
using System.Threading.Channels;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Lucida.OpenFeature;

/// <summary>
/// OpenFeature server provider over the Lucida .NET SDK. The targeting key is the Lucida distinct id;
/// every other context attribute becomes a person property, except the reserved spec v2 inputs: <c>groups</c> (type to key),
/// <c>group_properties</c>, <c>device_properties</c>, <c>geo_properties</c>, <c>device_id</c>, <c>app_version</c>, <c>app_contexts</c>
/// and <c>now</c>. Variants resolve to the string value, payload flags to number/structure evaluations. Flags are decided locally;
/// the ones that need the server (cohorts) are asked of it unless the client is local-only (see <see cref="LucidaClient.GetFlagResultAsync"/>).
/// Raw Lucida reasons map through <c>testdata/openfeature/reasons.json</c>; every resolution carries flag metadata
/// (<c>lucidaReason</c>, <c>conditionIndex</c>, <c>requestId</c>, <c>evaluatedLocally</c>). A client fallback value resolves with reason DEFAULT and no error.
/// Events: Ready after the first load (<see cref="InitializeAsync"/>), ConfigurationChanged on a reload, Stale when a reload fails, Error when the first load fails.
/// </summary>
public sealed class LucidaProvider : FeatureProvider
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private readonly LucidaClient _client;
    private int _state; // 0 init, 1 ready, 2 error

    public LucidaProvider(LucidaClient client)
    {
        _client = client;
        client.FlagsChanged += Changed;
        client.FlagsStale += Stale;
    }

    public override async Task InitializeAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (!await _client.AwaitFlagsAsync(ReadyTimeout).ConfigureAwait(false))
        {
            Volatile.Write(ref _state, 2);
            throw new InvalidOperationException("first flag definitions load failed");
        }
        Volatile.Write(ref _state, 1);
    }

    private void Emit(ProviderEventTypes type, string message) =>
        EventChannel.Writer.TryWrite(new ProviderEventPayload { Type = type, ProviderName = "lucida", Message = message });

    internal void Changed()
    {
        var was = Interlocked.Exchange(ref _state, 1);
        if (was == 2) Emit(ProviderEventTypes.ProviderReady, "flag definitions loaded");
        else if (was == 1) Emit(ProviderEventTypes.ProviderConfigurationChanged, "flag definitions changed");
    }

    internal void Stale()
    {
        if (Volatile.Read(ref _state) == 1) Emit(ProviderEventTypes.ProviderStale, "flag definitions reload failed");
    }

    /// <summary>OpenFeature tracking: a Lucida event named <paramref name="trackingEventName"/> for the targeting key; details become properties, the value is <c>$value</c>.</summary>
    public override void Track(string trackingEventName, EvaluationContext? evaluationContext = null, TrackingEventDetails? trackingEventDetails = null)
    {
        var id = evaluationContext?.TargetingKey;
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("targetingKey (distinct id) is required");
        var props = new Dictionary<string, object?>();
        if (trackingEventDetails is not null)
        {
            foreach (var (k, v) in trackingEventDetails.AsDictionary()) props[k] = ToObject(v);
            if (trackingEventDetails.Value is { } val) props["$value"] = val;
        }
        _client.Capture(id, trackingEventName, props, groups: ToFlagContext(id, evaluationContext!).Groups);
    }

    public override Metadata GetMetadata() => new("lucida");

    private static ResolutionDetails<T> Fail<T>(string key, T def, ErrorType type, string msg) =>
        new(key, def, type, Reason.Error, errorMessage: msg);

    private static ResolutionDetails<T> Ok<T>(string key, T value, FlagResult r)
    {
        var meta = new Dictionary<string, object> { ["lucidaReason"] = r.Reason ?? "", ["evaluatedLocally"] = r.EvaluatedLocally };
        if (r.ConditionIndex >= 0) meta["conditionIndex"] = r.ConditionIndex;
        if (r.RequestId is not null) meta["requestId"] = r.RequestId;
        return new(key, value, ErrorType.None, ReasonOf(r), r.Value as string ?? r.Variant, flagMetadata: new ImmutableMetadata(meta));
    }

    /// <summary>Maps a raw Lucida reason to an OpenFeature one (table: testdata/openfeature/reasons.json); a matched variant string is a SPLIT.</summary>
    internal static string ReasonOf(FlagResult r) => (r.Reason ?? "") switch
    {
        "condition_match" => r.Value is string ? Reason.Split : Reason.TargetingMatch,
        "sticky_bucket" => Reason.Cached,
        "no_condition_match" or "out_of_rollout" or "holdout" or "namespace_excluded" or "no_bucketing_key" or "prerequisite_failed"
            or "prerequisite_missing" or "fallback" => Reason.Default,
        "prerequisite_cycle" => Reason.Error,
        "inactive" or "kill_switch" or "app_context_excluded" => Reason.Disabled,
        "preview" or "user_preview" => Reason.Static,
        "remote" => Reason.TargetingMatch, // a gateway too old to send a reason
        _ => Reason.Unknown,
    };

    /// <summary>The payload of a read; a fallback value is its own payload (so int / double / structure fallbacks work).</summary>
    private static JsonElement? PayloadOf(FlagResult r) => r.IsFallback ? JsonSerializer.SerializeToElement(r.Value) : r.Payload;

    private static readonly HashSet<string> Reserved =
        ["targetingKey", "groups", "group_properties", "groupProperties", "device_properties", "geo_properties", "device_id", "app_version", "app_contexts", "now"];

    private static Dictionary<string, JsonElement>? Elements(Value? v) =>
        v is { IsStructure: true } ? v.AsStructure!.AsDictionary().ToDictionary(e => e.Key, e => JsonSerializer.SerializeToElement(ToObject(e.Value))) : null;

    internal static FlagContext ToFlagContext(string id, EvaluationContext ctx)
    {
        var all = ctx.AsDictionary();
        Value? Get(string k) => all.TryGetValue(k, out var v) ? v : null;
        string? Str(string k) => Get(k) is { IsString: true } s ? s.AsString : null;
        return new FlagContext
        {
            DistinctId = id,
            DeviceId = Str("device_id"),
            AppVersion = Str("app_version"),
            Now = Str("now"),
            PersonProperties = all.Where(kv => !Reserved.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(ToObject(kv.Value))),
            Groups = Get("groups") is { IsStructure: true } g ? g.AsStructure!.AsDictionary().ToDictionary(e => e.Key, e => e.Value.AsString ?? "") : null,
            GroupProperties = (Get("group_properties") ?? Get("groupProperties")) is { IsStructure: true } gp
                ? gp.AsStructure!.AsDictionary().ToDictionary(e => e.Key, e => Elements(e.Value) ?? new())
                : null,
            DeviceProperties = Elements(Get("device_properties")),
            GeoProperties = Elements(Get("geo_properties")),
            AppContexts = Get("app_contexts") is { IsList: true } l ? l.AsList!.Select(x => x.AsString ?? "").ToList() : null,
        };
    }

    /// <summary>Evaluates the flag, or returns the error details to hand back.</summary>
    private FlagResult? Resolve<T>(string key, T def, EvaluationContext? ctx, out ResolutionDetails<T> error)
    {
        error = default!;
        var id = ctx?.TargetingKey;
        if (string.IsNullOrEmpty(id))
        {
            error = Fail(key, def, ErrorType.TargetingKeyMissing, "targetingKey (distinct id) is required");
            return null;
        }
        var r = _client.GetFlagResultAsync(key, ToFlagContext(id, ctx!)).GetAwaiter().GetResult();
        if (r is null) error = Fail(key, def, ErrorType.FlagNotFound, $"flag {key} not found");
        return r;
    }

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var r = Resolve(flagKey, defaultValue, context, out var err);
        // a multivariate flag is on for any variant, mirroring IsEnabled
        return Task.FromResult(r is null ? err : Ok(flagKey, r.Value is true or string, r));
    }

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var r = Resolve(flagKey, defaultValue, context, out var err);
        if (r is null) return Task.FromResult(err);
        return Task.FromResult(r.Value is string s ? Ok(flagKey, s, r) : Fail(flagKey, defaultValue, ErrorType.TypeMismatch, $"flag {flagKey} has no string variant"));
    }

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var r = Resolve(flagKey, defaultValue, context, out var err);
        if (r is null) return Task.FromResult(err);
        if (PayloadOf(r) is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var d) && d == Math.Truncate(d) && Math.Abs(d) <= int.MaxValue)
            return Task.FromResult(Ok(flagKey, (int)d, r));
        return Task.FromResult(Fail(flagKey, defaultValue, ErrorType.TypeMismatch, $"flag {flagKey} payload is not an integer"));
    }

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var r = Resolve(flagKey, defaultValue, context, out var err);
        if (r is null) return Task.FromResult(err);
        if (PayloadOf(r) is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var d)) return Task.FromResult(Ok(flagKey, d, r));
        return Task.FromResult(Fail(flagKey, defaultValue, ErrorType.TypeMismatch, $"flag {flagKey} payload is not a number"));
    }

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var r = Resolve(flagKey, defaultValue, context, out var err);
        if (r is null) return Task.FromResult(err);
        if (PayloadOf(r) is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } p) return Task.FromResult(Ok(flagKey, ToValue(p), r));
        return Task.FromResult(Fail(flagKey, defaultValue, ErrorType.TypeMismatch, $"flag {flagKey} has no payload"));
    }

    internal static object? ToObject(Value v) =>
        v.IsBoolean ? v.AsBoolean :
        v.IsNumber ? v.AsDouble :
        v.IsString ? v.AsString :
        v.IsList ? v.AsList!.Select(ToObject).ToList() :
        v.IsStructure ? v.AsStructure!.AsDictionary().ToDictionary(e => e.Key, e => ToObject(e.Value)) :
        null;

    internal static Value ToValue(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.True: return new Value(true);
            case JsonValueKind.False: return new Value(false);
            case JsonValueKind.Number: return new Value(e.GetDouble());
            case JsonValueKind.String: return new Value(e.GetString()!);
            case JsonValueKind.Array: return new Value(e.EnumerateArray().Select(ToValue).ToList());
            case JsonValueKind.Object:
                var b = Structure.Builder();
                foreach (var p in e.EnumerateObject()) b.Set(p.Name, ToValue(p.Value));
                return new Value(b.Build());
            default: return new Value();
        }
    }
}

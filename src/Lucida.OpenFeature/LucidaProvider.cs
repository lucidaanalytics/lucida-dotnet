using System.Text.Json;
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
/// </summary>
public sealed class LucidaProvider : FeatureProvider
{
    private readonly LucidaClient _client;

    public LucidaProvider(LucidaClient client) => _client = client;

    public override Metadata GetMetadata() => new("lucida");

    private static ResolutionDetails<T> Fail<T>(string key, T def, ErrorType type, string msg) =>
        new(key, def, type, Reason.Error, errorMessage: msg);

    private static ResolutionDetails<T> Ok<T>(string key, T value, FlagResult r) =>
        new(key, value, ErrorType.None, ReasonOf(r), r.Value as string);

    /// <summary>Maps a Lucida evaluation reason to an OpenFeature one.</summary>
    internal static string ReasonOf(FlagResult r) => r.Reason switch
    {
        "inactive" or "kill_switch" or "app_context_excluded" => Reason.Disabled,
        "condition_match" or "sticky_bucket" or "remote" => Reason.TargetingMatch,
        _ => Reason.Default, // no condition matched, out of rollout, holdout, prerequisite failed...
    };

    private static readonly HashSet<string> Reserved =
        ["targetingKey", "groups", "group_properties", "device_properties", "geo_properties", "device_id", "app_version", "app_contexts", "now"];

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
            GroupProperties = Get("group_properties") is { IsStructure: true } gp
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
        if (r.Payload is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var d) && d == Math.Truncate(d) && Math.Abs(d) <= int.MaxValue)
            return Task.FromResult(Ok(flagKey, (int)d, r));
        return Task.FromResult(Fail(flagKey, defaultValue, ErrorType.TypeMismatch, $"flag {flagKey} payload is not an integer"));
    }

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var r = Resolve(flagKey, defaultValue, context, out var err);
        if (r is null) return Task.FromResult(err);
        if (r.Payload is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var d)) return Task.FromResult(Ok(flagKey, d, r));
        return Task.FromResult(Fail(flagKey, defaultValue, ErrorType.TypeMismatch, $"flag {flagKey} payload is not a number"));
    }

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var r = Resolve(flagKey, defaultValue, context, out var err);
        if (r is null) return Task.FromResult(err);
        if (r.Payload is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } p) return Task.FromResult(Ok(flagKey, ToValue(p), r));
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

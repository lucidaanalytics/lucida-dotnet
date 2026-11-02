using System.Text.Json;

namespace Lucida;

public sealed partial class LucidaClient
{
    /// <summary>Loads definitions from a fixture or a <c>/v1/flags/definitions</c> body: <c>{"flags": [...]}</c> or a bare array.</summary>
    public void SetFlagDefinitions(string json) => _sync.Set(json);

    /// <summary>Fetches definitions once (needs <see cref="LucidaOptions.SecretKey"/>; ETag aware: 304 counts as success); keeps the previous ones on any failure.</summary>
    public Task<bool> ReloadFlagsAsync() => _sync.ReloadAsync();

    /// <summary>Evaluates one flag locally; null when the key is unknown. <see cref="Evaluator.RequiresServer"/> means cohorts or other server-only data are involved.</summary>
    public FlagResult? Evaluate(string flagKey, FlagContext context) => EvaluateAllLocal(context).GetValueOrDefault(flagKey);

    private FlagContext WithDefaults(FlagContext c) =>
        c.AppContexts is null && _o.AppContexts is not null
            ? new FlagContext
            {
                DistinctId = c.DistinctId, BucketingKey = c.BucketingKey, DeviceId = c.DeviceId, AppVersion = c.AppVersion, Groups = c.Groups,
                PersonProperties = c.PersonProperties, GroupProperties = c.GroupProperties, DeviceProperties = c.DeviceProperties,
                GeoProperties = c.GeoProperties, StickyAssignments = c.StickyAssignments, Now = c.Now, AppContexts = _o.AppContexts, AnonDistinctId = c.AnonDistinctId,
            }
            : c;

    /// <summary>Every flag evaluated locally, minus the ones scoped to other app contexts.</summary>
    private Dictionary<string, FlagResult> EvaluateAllLocal(FlagContext c) =>
        Evaluator.EvaluateAll(_sync.Definitions, WithDefaults(c)).Where(kv => kv.Value.Reason != "app_context_excluded").ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Flag values decided locally (no server round trip), for event enrichment.</summary>
    private Dictionary<string, object> LocalValues(FlagContext c) =>
        EvaluateAllLocal(c).Where(kv => kv.Value.Reason != Evaluator.RequiresServer).ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    /// <summary>Evaluates every flag once; read the snapshot freely and attach it with <c>Capture(..., flags: snapshot)</c>. No exposure events.</summary>
    public async Task<FlagSnapshot> EvaluateFlagsAsync(FlagContext context)
    {
        var all = await GetAllFlagResultsAsync(context).ConfigureAwait(false);
        return new FlagSnapshot(all.ToDictionary(kv => kv.Key, kv => kv.Value.Value),
            all.Where(kv => kv.Value.Payload is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Payload!.Value));
    }

    /// <summary>
    /// Payload of a remote-config flag, decrypted by the server when stored encrypted (needs <see cref="LucidaOptions.SecretKey"/>).
    /// Null when unknown or on failure.
    /// </summary>
    public async Task<JsonElement?> GetRemoteConfigPayloadAsync(string key)
    {
        if (_o.SecretKey is null) throw new InvalidOperationException("GetRemoteConfigPayloadAsync requires a secret key");
        return _o.Disabled ? null : await _sync.RemoteConfigAsync(key).ConfigureAwait(false);
    }

    public bool IsEnabled(string flagKey, FlagContext context) => Evaluate(flagKey, context)?.Value is true or string;

    /// <summary>
    /// Value, payload and reason of one flag. Decided locally when possible; otherwise (unknown key, cohorts, nothing loaded)
    /// asked of the server with reason <c>"remote"</c>, unless <see cref="LucidaOptions.OnlyEvaluateLocally"/> or
    /// <paramref name="onlyEvaluateLocally"/>. Null when undecided.
    /// </summary>
    public async Task<FlagResult?> GetFlagResultAsync(string flagKey, FlagContext context, bool? onlyEvaluateLocally = null)
    {
        var r = Evaluate(flagKey, context);
        if (r is not null && r.Reason != Evaluator.RequiresServer) return r;
        if (!(onlyEvaluateLocally ?? _o.OnlyEvaluateLocally))
            r = (await _sync.RemoteAsync(WithDefaults(context), [flagKey]).ConfigureAwait(false)).GetValueOrDefault(flagKey);
        else r = null;
        if (r is null && _o.Fallbacks is { } fb && fb.TryGetValue(flagKey, out var v))
            return new FlagResult(v, FlagResult.FallbackReason, -1, v as string);
        return r;
    }

    /// <summary>Value, payload, variant, raw reason, condition index, request id and local flag of one read (same as <see cref="GetFlagResultAsync"/>).</summary>
    public Task<FlagResult?> GetFeatureFlagDetailsAsync(string flagKey, FlagContext context) => GetFlagResultAsync(flagKey, context);

    /// <summary>Raised after definitions are (re)loaded with a change (first load included).</summary>
    public event Action? FlagsChanged
    {
        add => _sync.Changed += value;
        remove => _sync.Changed -= value;
    }

    /// <summary>Raised when a definitions reload fails.</summary>
    public event Action? FlagsStale
    {
        add => _sync.Stale += value;
        remove => _sync.Stale -= value;
    }

    /// <summary>Waits for the first definitions fetch; true when loaded or nothing is polled.</summary>
    public Task<bool> AwaitFlagsAsync(TimeSpan max) => _sync.AwaitAsync(max);

    /// <summary><c>bool</c>, a variant key, or null when unknown / undecidable.</summary>
    public async Task<object?> GetFeatureFlagAsync(string flagKey, FlagContext context) =>
        (await GetFlagResultAsync(flagKey, context).ConfigureAwait(false))?.Value;

    /// <summary>The flag's JSON payload for its resolved variant, or null.</summary>
    public async Task<System.Text.Json.JsonElement?> GetFeatureFlagPayloadAsync(string flagKey, FlagContext context) =>
        (await GetFlagResultAsync(flagKey, context).ConfigureAwait(false))?.Payload;

    /// <summary>The AI Config variation (completion or agent mode) served for <paramref name="context"/>, or <paramref name="fallback"/> (P-M49).</summary>
    public async Task<AiConfig> GetAiConfigAsync(string flagKey, FlagContext context, AiConfig fallback, IReadOnlyDictionary<string, string>? vars = null) =>
        AiConfig.From(await GetFlagResultAsync(flagKey, context).ConfigureAwait(false), fallback, vars);

    /// <summary>Every flag decided locally, plus (unless local-only) whatever the server decides for the rest.</summary>
    public async Task<Dictionary<string, FlagResult>> GetAllFlagResultsAsync(FlagContext context)
    {
        var out_ = new Dictionary<string, FlagResult>();
        var undecided = !_sync.Loaded;
        foreach (var (k, r) in EvaluateAllLocal(context))
        {
            if (r.Reason == Evaluator.RequiresServer) undecided = true;
            else out_[k] = r;
        }
        if (undecided && !_o.OnlyEvaluateLocally)
            foreach (var (k, r) in await _sync.RemoteAsync(WithDefaults(context), null).ConfigureAwait(false)) out_.TryAdd(k, r);
        return out_;
    }

    public async Task<Dictionary<string, object>> GetAllFlagsAsync(FlagContext context) =>
        (await GetAllFlagResultsAsync(context).ConfigureAwait(false)).ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    /// <summary>Values and payloads; flags without a payload are absent from <c>Payloads</c>.</summary>
    public sealed record FlagsAndPayloads(Dictionary<string, object> Flags, Dictionary<string, System.Text.Json.JsonElement> Payloads);

    public async Task<FlagsAndPayloads> GetAllFlagsAndPayloadsAsync(FlagContext context)
    {
        var all = await GetAllFlagResultsAsync(context).ConfigureAwait(false);
        return new FlagsAndPayloads(
            all.ToDictionary(kv => kv.Key, kv => kv.Value.Value),
            all.Where(kv => kv.Value.Payload is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Payload!.Value));
    }
}

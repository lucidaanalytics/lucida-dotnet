using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucida;

/// <summary>Outcome of evaluating one flag. <see cref="Value"/> is a <c>bool</c> or the variant key (<c>string</c>).</summary>
/// <summary>
/// Outcome of evaluating one flag. <c>Reason</c> is the raw Lucida reason (<c>"fallback"</c> for a <see cref="LucidaOptions.Fallbacks"/> value,
/// <c>"remote"</c> when the server sent none); <c>RequestId</c> is set for server answers.
/// </summary>
public sealed record FlagResult(object Value, string Reason, int ConditionIndex = -1, string? Variant = null, JsonElement? Payload = null,
    string? RequestId = null, bool EvaluatedLocally = true)
{
    public const string FallbackReason = "fallback";
    public bool IsFallback => Reason == FallbackReason;
}

/// <summary>Local flag evaluator; behaviour pinned by testdata/flags vectors (see testdata/flags/README.md).</summary>
public static class Evaluator
{
    public const string RequiresServer = "requires_server";
    private const double Unit = 4503599627370496d; // 16^13

    public static double Hash01(string text)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..13];
        return Convert.ToInt64(hex, 16) / Unit;
    }

    private static FlagResult Off(string reason, int idx = -1) => new(false, reason, idx);

    private static JsonElement? Prop(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : null;

    private static string Str(JsonElement o, string name) => Prop(o, name) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : "";

    private static bool IsNum(JsonElement v) => v.ValueKind == JsonValueKind.Number;

    internal static double? ToFloat(JsonElement v)
    {
        if (IsNum(v)) return v.GetDouble();
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!.Trim();
        if (s.Contains('_')) return null;
        var lower = s.ToLowerInvariant();
        var sign = lower.StartsWith('-') ? -1d : 1d;
        var bare = lower.TrimStart('+', '-');
        if (lower.Length - bare.Length <= 1)
        {
            if (bare is "inf" or "infinity") return sign * double.PositiveInfinity;
            if (bare == "nan") return double.NaN;
        }
        if (!Regex.IsMatch(lower, @"^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$")) return null;
        return double.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static bool Equal(JsonElement a, JsonElement b)
    {
        if (IsNum(a) || IsNum(b)) return IsNum(a) && IsNum(b) && a.GetDouble() == b.GetDouble();
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Equal(p.First, p.Second));
            case JsonValueKind.Object:
                var pa = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var pb = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                return pa.Count == pb.Count && pa.All(kv => pb.TryGetValue(kv.Key, out var o) && Equal(kv.Value, o));
            default: return true; // true/true, false/false, null/null
        }
    }

    internal static BigInteger[]? Semver(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!.Trim();
        if (s.StartsWith('v')) s = s[1..];
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        var parts = s.Split('.');
        if (!parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return null;
        return parts.Select(BigInteger.Parse).ToArray();
    }

    internal static int? CmpSemver(JsonElement a, JsonElement b)
    {
        if (Semver(a) is not { } pa || Semver(b) is not { } pb) return null;
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var c = (i < pa.Length ? pa[i] : 0).CompareTo(i < pb.Length ? pb[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    private static bool? RegexMatch(string pattern, string s)
    {
        try { return Regex.IsMatch(s, pattern); }
        catch (ArgumentException) { return null; }
    }

    private static bool Match(string op, JsonElement prop, bool present, JsonElement want, bool semver, DateTimeOffset now)
    {
        var isSet = present && prop.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        switch (op)
        {
            case "is_set": return isSet;
            case "is_not_set": return !isSet;
        }
        if (!isSet) return false;
        switch (op)
        {
            case "eq" or "neq":
                if (semver) return CmpSemver(prop, want) is { } c && (c == 0) == (op == "eq");
                return Equal(prop, want) == (op == "eq");
            case "contains" or "ncontains" or "regex" or "nregex":
                if (prop.ValueKind != JsonValueKind.String || want.ValueKind != JsonValueKind.String) return false;
                var (p, w) = (prop.GetString()!, want.GetString()!);
                if (op is "contains" or "ncontains") return p.Contains(w, StringComparison.Ordinal) == (op == "contains");
                return RegexMatch(w, p) is { } r && r == (op == "regex");
            case "gt" or "gte" or "lt" or "lte":
                int? cmp;
                if (semver) cmp = CmpSemver(prop, want);
                else
                {
                    var (x, y) = (ToFloat(prop), ToFloat(want));
                    cmp = x is null || y is null ? null : x > y ? 1 : x < y ? -1 : 0; // NaN compares equal, as the reference (Python) does
                }
                if (cmp is not { } k) return false;
                return op switch { "gt" => k > 0, "gte" => k >= 0, "lt" => k < 0, _ => k <= 0 };
            case "in" or "not_in":
                if (want.ValueKind != JsonValueKind.Array) return false;
                return want.EnumerateArray().Any(item => Equal(prop, item)) == (op == "in");
        }
        return OpsV2.Match(op, prop, want, now);
    }

    private static JsonElement ValueElement(object v) => JsonSerializer.SerializeToElement(v);

    private static bool FilterMatches(JsonElement fl, FlagContext c, Dictionary<string, FlagResult> resolved)
    {
        var want = Prop(fl, "value") ?? default;
        var op = Str(fl, "op");
        var key = Str(fl, "key");
        var now = OpsV2.ParseNow(c.Now);
        switch (Str(fl, "type"))
        {
            case "app_version":
                var v = c.AppVersion ?? "";
                return Match(op, ValueElement(v), v != "", want, true, now);
            case "flag":
                return resolved.TryGetValue(key, out var r) && Match(op, ValueElement(r.Value), true, want, false, now);
        }
        Dictionary<string, JsonElement>? props = Str(fl, "type") switch
        {
            "person" => c.PersonProperties,
            "group" => c.GroupProperties?.GetValueOrDefault(Str(fl, "group_type")),
            "device" => c.DeviceProperties,
            "geo" => c.GeoProperties,
            _ => null,
        };
        if (Str(fl, "type") is not ("person" or "group" or "device" or "geo")) return false;
        props ??= new();
        var present = props.TryGetValue(key, out var pv);
        return Match(op, pv, present, want, false, now);
    }

    private static bool ValueMatches(object got, JsonElement? want)
    {
        if (want is { ValueKind: JsonValueKind.True or JsonValueKind.False } b)
            return got is bool gb ? gb == (b.ValueKind == JsonValueKind.True) : b.ValueKind == JsonValueKind.True;
        return want is { ValueKind: JsonValueKind.String } s && got is string gs && gs == s.GetString();
    }

    private static string BucketingKey(JsonElement f, FlagContext c) => UnitKey(Prop(f, "aggregation"), c);

    private static string UnitKey(JsonElement? agg, FlagContext c)
    {
        switch (agg is { } a ? Str(a, "kind") : "")
        {
            case "group": return c.Groups?.GetValueOrDefault(Str(agg!.Value, "group_type")) ?? "";
            case "device": return c.DeviceId ?? "";
            default: return !string.IsNullOrEmpty(c.BucketingKey) ? c.BucketingKey : c.DistinctId ?? "";
        }
    }

    private static JsonElement PickVariant(JsonElement f, JsonElement cond, string key)
    {
        var variants = f.GetProperty("variants").EnumerateArray().ToList();
        var over = Str(cond, "variant_override");
        if (over != "" && variants.FirstOrDefault(v => Str(v, "key") == over) is { ValueKind: JsonValueKind.Object } hit) return hit;
        var h = Hash01($"{Str(f, "key")}.{Str(f, "salt")}.variant.{key}") * 100;
        var cum = 0d;
        foreach (var v in variants)
        {
            cum += Prop(v, "rollout_pct") is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : 0;
            if (h < cum) return v;
        }
        return variants[^1]; // percentages summed < 100: last variant takes the remainder
    }

    private static IEnumerable<JsonElement> Items(JsonElement o, string name) =>
        Prop(o, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    private static double Num(JsonElement o, string name) => Prop(o, name) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : 0;

    /// <summary>A stored value of a sticky flag decides once a condition's filters passed (LC-EXT-29).</summary>
    private static FlagResult? Sticky(JsonElement f, JsonElement cond, int i, FlagContext c)
    {
        if (Prop(f, "sticky") is not { ValueKind: JsonValueKind.True } || c.StickyAssignments?.GetValueOrDefault(Str(f, "key")) is not { } stored) return null;
        if (Str(f, "type") == "multivariate" && Items(f, "variants").Any())
        {
            var over = Prop(cond, "variant_override") is { ValueKind: JsonValueKind.String } o ? o.GetString() : null;
            if (Items(f, "variants").Any(v => Str(v, "key") == over)) return null; // the override wins
            if (stored.ValueKind != JsonValueKind.String) return null;
            foreach (var v in Items(f, "variants"))
                if (Str(v, "key") == stored.GetString()) return new FlagResult(Str(v, "key"), "sticky_bucket", i, Str(v, "key"), Prop(v, "payload"));
            return null;
        }
        if (stored.ValueKind != JsonValueKind.True) return null;
        return new FlagResult(true, "sticky_bucket", i, null, Str(f, "type") == "json" ? Prop(f, "payload") : null);
    }

    private static bool AppContextAllowed(JsonElement f, FlagContext c)
    {
        var want = Items(f, "app_contexts").Select(x => x.GetString() ?? "").ToList();
        return want.Count == 0 || c.AppContexts is not { Count: > 0 } have || want.Any(have.Contains);
    }

    public static FlagResult Evaluate(JsonElement f, FlagContext c, Dictionary<string, FlagResult> resolved)
    {
        if (!AppContextAllowed(f, c)) return Off("app_context_excluded");
        if (Prop(f, "active") is not { ValueKind: JsonValueKind.True }) return Off("inactive");
        if (Prop(f, "kill_switch") is { ValueKind: JsonValueKind.True }) return Off("kill_switch");
        var server = false;
        foreach (var p in Items(f, "prerequisites"))
        {
            if (!resolved.TryGetValue(Str(p, "flag"), out var r)) return Off("prerequisite_missing");
            if (r.Reason == RequiresServer) server = true; // a definite failure elsewhere still wins
            else if (r.Reason == "prerequisite_cycle" || !ValueMatches(r.Value, Prop(p, "value"))) return Off("prerequisite_failed");
        }
        if (server) return Off(RequiresServer);
        var key = BucketingKey(f, c);
        var conds = Items(f, "conditions").ToList();
        var perCondition = conds.Any(cd => Prop(cd, "aggregation") is { ValueKind: JsonValueKind.Object });
        if (key == "" && (!perCondition || Prop(f, "holdout") is { ValueKind: JsonValueKind.Object } || Prop(f, "namespace") is { ValueKind: JsonValueKind.Object }))
            return Off("no_bucketing_key");
        if (Prop(f, "holdout") is { ValueKind: JsonValueKind.Object } ho && Hash01($"holdout.{Str(ho, "salt")}.{key}") * 100 < Num(ho, "percentage")) return Off("holdout");
        if (Prop(f, "namespace") is { ValueKind: JsonValueKind.Object } ns && Num(ns, "total") > 0)
        {
            var b = (long)(Hash01($"namespace.{Str(ns, "salt")}.{key}") * Num(ns, "total"));
            if (b < Num(ns, "start") || b >= Num(ns, "end")) return Off("namespace_excluded");
        }
        var i = -1;
        var skipped = 0;
        foreach (var cond in conds)
        {
            i++;
            var own = Prop(cond, "aggregation") is { ValueKind: JsonValueKind.Object } agg ? agg : (JsonElement?)null;
            var ckey = own is null ? key : UnitKey(own, c);
            if (ckey == "")
            {
                skipped++;
                continue;
            }
            var filters = Items(cond, "filters").ToList();
            bool Undecidable(JsonElement fl) => Str(fl, "type") == "flag"
                ? resolved.TryGetValue(Str(fl, "key"), out var r) && r.Reason == RequiresServer
                : Str(fl, "type") == "cohort";
            if (!filters.Where(fl => !Undecidable(fl)).All(fl => FilterMatches(fl, c, resolved))) continue;
            if (filters.Any(Undecidable)) return Off(RequiresServer, i);
            if (own is null && Sticky(f, cond, i, c) is { } stuck) return stuck; // the store keys on the flag-level unit
            if (Hash01($"{Str(f, "key")}.{Str(f, "salt")}.{ckey}") * 100 >= Num(cond, "rollout_pct"))
            {
                if (Prop(f, "early_exit") is { ValueKind: JsonValueKind.True }) return Off("out_of_rollout", i);
                continue;
            }
            if (Str(f, "type") == "multivariate" && Items(f, "variants").Any())
            {
                var v = PickVariant(f, cond, ckey);
                var vk = Str(v, "key");
                return new FlagResult(vk, "condition_match", i, vk, Prop(v, "payload"));
            }
            return new FlagResult(true, "condition_match", i, null, Str(f, "type") == "json" ? Prop(f, "payload") : null);
        }
        return Off(conds.Count > 0 && skipped == conds.Count ? "no_bucketing_key" : "no_condition_match");
    }

    private static IEnumerable<string> Deps(JsonElement f) =>
        Items(f, "prerequisites").Select(p => Str(p, "flag"))
            .Concat(Items(f, "conditions").SelectMany(c => Items(c, "filters")).Where(fl => Str(fl, "type") == "flag").Select(fl => Str(fl, "key")));

    /// <summary>Evaluates every flag, resolving prerequisites and <c>flag</c> filters depth-first.</summary>
    public static Dictionary<string, FlagResult> EvaluateAll(IReadOnlyList<JsonElement> flags, FlagContext c)
    {
        var byKey = flags.ToDictionary(f => Str(f, "key"), f => f);
        var output = new Dictionary<string, FlagResult>();
        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done

        void Visit(string key)
        {
            if (!byKey.TryGetValue(key, out var f) || state.ContainsKey(key)) return;
            state[key] = 1;
            var cyc = false;
            foreach (var dep in Deps(f))
            {
                Visit(dep);
                if (state.GetValueOrDefault(dep) == 1 || (output.TryGetValue(dep, out var d) && d.Reason == "prerequisite_cycle")) cyc = true;
            }
            output[key] = cyc ? Off("prerequisite_cycle") : Evaluate(f, c, output);
            state[key] = 2;
        }

        foreach (var f in flags) Visit(Str(f, "key"));
        return output;
    }
}

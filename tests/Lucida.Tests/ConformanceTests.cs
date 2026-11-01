using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lucida.Tests;

/// <summary>testdata/flags vectors (evaluator) and testdata/events wire fixtures (client).</summary>
public class ConformanceTests
{
    internal static string TestData(string rel)
    {
        var env = Environment.GetEnvironmentVariable("LUCIDA_TESTDATA");
        if (!string.IsNullOrEmpty(env)) return Path.Combine(env, rel);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "testdata", rel);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(rel);
    }

    private static readonly Dictionary<string, string[]> CtxFields = new()
    {
        ["distinct_id"] = ["distinct_id", "bucketing_key"], ["groups"] = ["groups"], ["person_properties"] = ["person_properties"],
        ["group_properties"] = ["group_properties"], ["device_properties"] = ["device_properties"], ["geo_properties"] = ["geo_properties"],
        ["app_version"] = ["app_version"],
    };

    private static Dictionary<string, JsonElement>? Obj(JsonElement c, string name) =>
        c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : null;

    /// <summary>Only the context fields named by `needs`: anything else must not influence the result.</summary>
    private static FlagContext Supplied(JsonElement vec)
    {
        var ctx = vec.GetProperty("context");
        var keep = vec.TryGetProperty("needs", out var needs)
            ? needs.EnumerateArray().SelectMany(n => CtxFields.GetValueOrDefault(n.GetString()!, [])).ToHashSet()
            : ctx.EnumerateObject().Select(p => p.Name).ToHashSet();
        string? S(string n) => keep.Contains(n) && ctx.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new FlagContext
        {
            DistinctId = S("distinct_id"), BucketingKey = S("bucketing_key"), DeviceId = S("device_id"), AppVersion = S("app_version"),
            Groups = keep.Contains("groups") ? Obj(ctx, "groups")?.ToDictionary(p => p.Key, p => p.Value.GetString()!) : null,
            PersonProperties = keep.Contains("person_properties") ? Obj(ctx, "person_properties") : null,
            DeviceProperties = keep.Contains("device_properties") ? Obj(ctx, "device_properties") : null,
            GeoProperties = keep.Contains("geo_properties") ? Obj(ctx, "geo_properties") : null,
            StickyAssignments = keep.Contains("sticky_assignments") ? Obj(ctx, "sticky_assignments") : null,
            Now = S("now"),
            AppContexts = keep.Contains("app_contexts") && ctx.TryGetProperty("app_contexts", out var ac) && ac.ValueKind == JsonValueKind.Array
                ? ac.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
            GroupProperties = keep.Contains("group_properties") && ctx.TryGetProperty("group_properties", out var gp) && gp.ValueKind == JsonValueKind.Object
                ? gp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateObject().ToDictionary(q => q.Name, q => q.Value.Clone()))
                : null,
        };
    }

    private static int CheckFile(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData($"flags/{name}")));
        var root = doc.RootElement;
        foreach (var h in root.GetProperty("hash_vectors").EnumerateArray())
            Assert.Equal(h.GetProperty("hash").GetDouble(), Evaluator.Hash01(h.GetProperty("input").GetString()!));
        var flags = root.GetProperty("flags").EnumerateArray().ToList();
        var n = 0;
        foreach (var v in root.GetProperty("vectors").EnumerateArray())
        {
            var r = Evaluator.EvaluateAll(flags, Supplied(v))[v.GetProperty("flag").GetString()!];
            var e = v.GetProperty("expected");
            var name_ = v.GetProperty("name").GetString();
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(r.Value), JsonNode.Parse(e.GetProperty("value").GetRawText())), $"{name_}: value {r.Value}");
            Assert.Equal(e.GetProperty("reason").GetString(), r.Reason);
            Assert.Equal(e.GetProperty("condition_index").GetInt32(), r.ConditionIndex);
            Assert.Equal(e.TryGetProperty("variant", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() : null, r.Variant);
            var wantPayload = e.TryGetProperty("payload", out var ep) && ep.ValueKind != JsonValueKind.Null ? JsonNode.Parse(ep.GetRawText()) : null;
            var gotPayload = r.Payload is { ValueKind: not JsonValueKind.Null } p ? JsonNode.Parse(p.GetRawText()) : null;
            Assert.True(JsonNode.DeepEquals(wantPayload, gotPayload), $"{name_}: payload");
            n++;
        }
        return n;
    }

    [Fact] public void VectorsBasic() => Assert.True(CheckFile("vectors_basic.json") > 20);
    [Fact] public void VectorsG10() => Assert.True(CheckFile("vectors_g10.json") > 15);
    [Fact] public void VectorsSticky() => Assert.True(CheckFile("vectors_sticky.json") >= 19);
    [Fact] public void VectorsOpsV2() => Assert.True(CheckFile("vectors_ops.json") >= 100);
    [Fact] public void VectorsGenerated() => Assert.Equal(10_000, CheckFile("vectors.json"));

    public static IEnumerable<object[]> EventCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData("events/server_sdk.json")));
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetRawText() }).ToList();
    }

    private static Dictionary<string, object?>? Dict(JsonElement c, string name) =>
        c.TryGetProperty(name, out var v) ? JsonSerializer.Deserialize<Dictionary<string, object?>>(v.GetRawText()) : null;

    [Theory, MemberData(nameof(EventCases))]
    public async Task EventFixtures(string caseJson)
    {
        var c = JsonDocument.Parse(caseJson).RootElement;
        var call = c.GetProperty("call");
        var fake = new FakeHandler();
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, Host = "http://lucida.test" });
        var id = call.TryGetProperty("distinct_id", out var di) ? di.GetString()! : "";
        switch (call.GetProperty("method").GetString())
        {
            case "capture":
                lc.Capture(id, call.GetProperty("event").GetString()!, Dict(call, "properties"),
                    call.TryGetProperty("groups", out var g) ? JsonSerializer.Deserialize<Dictionary<string, string>>(g.GetRawText()) : null,
                    call.TryGetProperty("timestamp", out var ts) ? DateTimeOffset.Parse(ts.GetString()!) : null,
                    call.TryGetProperty("uuid", out var u) ? u.GetString() : null, Dict(call, "set"), Dict(call, "set_once"));
                break;
            case "identify": lc.Identify(id, Dict(call, "properties"), Dict(call, "properties_set_once")); break;
            case "alias": lc.Alias(id, call.GetProperty("alias").GetString()!); break;
            default:
                lc.GroupIdentify(call.GetProperty("group_type").GetString()!, call.GetProperty("group_key").GetString()!, Dict(call, "properties"),
                    call.TryGetProperty("distinct_id", out var d) ? d.GetString() : null);
                break;
        }
        Assert.True(await lc.FlushAsync());
        var body = fake.Bodies.Single();
        Assert.Equal("lc_pk_x", body["api_key"]!.GetValue<string>());
        var ev = (JsonObject)body["batch"]!.AsArray().Single()!;
        var props = (JsonObject)ev["properties"]!;
        Assert.StartsWith("lucida-", props["$lib"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(props["$lib_version"]!.GetValue<string>()));
        props.Remove("$lib");
        props.Remove("$lib_version");
        var want = (JsonObject)JsonNode.Parse(c.GetProperty("expected").GetRawText())!;
        foreach (var k in new[] { "uuid", "timestamp" })
            if (want[k]!.GetValue<string>() == "*") want[k] = ev[k]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(want, ev), $"{ev.ToJsonString()} != {want.ToJsonString()}");
    }
}

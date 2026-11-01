using Xunit;

namespace Lucida.Tests;

/// <summary>Super properties, person properties, identify/group dedupe, geoip and the evaluate-once snapshot.</summary>
public class IdentityTests
{
    private const string Defs = "{\"flags\":["
        + "{\"key\":\"on\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"conditions\":[{\"filters\":[],\"rollout_pct\":100}]},"
        + "{\"key\":\"off\",\"type\":\"boolean\",\"active\":false,\"salt\":\"s\",\"conditions\":[{\"filters\":[],\"rollout_pct\":100}]}]}";

    private static LucidaClient Test(bool geoip = false, IReadOnlyDictionary<string, object?>? defaults = null) =>
        new("k", new LucidaOptions { TestModeFixtureJson = Defs, OnlyEvaluateLocally = true, DisableGeoip = geoip, DefaultProperties = defaults });

    private static Dictionary<string, object?> P(string k, object? v) => new() { [k] = v };

    [Fact]
    public void SuperPropertiesPerCallWinsAndGeoipDisabled()
    {
        using var lc = Test(true, new Dictionary<string, object?> { ["app"] = "a", ["env"] = "prod" });
        lc.Register(P("app", "b"));
        lc.Unregister("env");
        lc.Capture("u", "e1");
        lc.Capture("u", "e2", P("app", "mine"));
        var ev = lc.Captured;
        Assert.Equal("b", (string?)ev[0]["properties"]!["app"]);
        Assert.Null(ev[0]["properties"]!["env"]);
        Assert.True((bool?)ev[0]["properties"]!["$geoip_disable"]);
        Assert.Equal("mine", (string?)ev[1]["properties"]!["app"]);
    }

    [Fact]
    public void PersonPropertiesAndDedupe()
    {
        using var lc = Test();
        lc.SetPersonProperties("u", P("plan", "pro"));
        lc.SetPersonPropertiesOnce("u", P("first", "x"));
        for (var i = 0; i < 3; i++)
        {
            lc.Identify("u", P("a", 1));
            lc.GroupIdentify("org", "acme", P("n", 1));
        }
        lc.Identify("u", P("a", 2));
        var ev = lc.Captured;
        Assert.Equal("pro", (string?)ev[0]["$set"]!["plan"]);
        Assert.Equal("x", (string?)ev[1]["$set_once"]!["first"]);
        Assert.Equal(new[] { "$set", "$set", "$identify", "$groupidentify", "$identify" }, ev.Select(e => (string?)e["event"]).ToArray());
    }

    [Fact]
    public async Task SnapshotAttachesFeatureProps()
    {
        using var lc = Test();
        var snap = await lc.EvaluateFlagsAsync(new FlagContext { DistinctId = "u" });
        Assert.True(snap.IsEnabled("on"));
        Assert.False(snap.IsEnabled("off"));
        lc.CaptureWithFlags("u", "page", P("$feature/on", "mine"), snap);
        var p = lc.Captured[0]["properties"]!;
        Assert.Equal("mine", (string?)p["$feature/on"]);
        Assert.False((bool?)p["$feature/off"]);
        Assert.Equal("[\"on\"]", p["$active_feature_flags"]!.ToJsonString());
    }
}

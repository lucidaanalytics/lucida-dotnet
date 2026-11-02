using System.Text.Json;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using Xunit;

namespace Lucida.OpenFeature.Tests;

public class LucidaProviderTests : IAsyncLifetime
{
    private const string Fixture = """
        {"flags":[
          {"key":"on","type":"boolean","active":true,"salt":"s","conditions":[{"filters":[],"rollout_pct":100}]},
          {"key":"color","type":"multivariate","active":true,"salt":"s","conditions":[{"filters":[],"rollout_pct":100}],
           "variants":[{"key":"blue","rollout_pct":100,"payload":{"hex":"#00f"}}]},
          {"key":"limit","type":"json","active":true,"salt":"s","payload":5,"conditions":[{"filters":[],"rollout_pct":100}]},
          {"key":"ver","type":"boolean","active":true,"salt":"s","conditions":[{"filters":[{"type":"app_version","op":"semver_caret","value":"1.2.3"}],"rollout_pct":100}]},
          {"key":"web","type":"boolean","active":true,"salt":"s","app_contexts":["web"],"conditions":[{"filters":[],"rollout_pct":100}]},
          {"key":"off","type":"boolean","active":false,"salt":"s","conditions":[]},
          {"key":"prefix","type":"boolean","active":true,"salt":"s","conditions":[{"filters":[{"type":"person","key":"email","op":"ends_with","value":"@acme.io"}],"rollout_pct":100}]},
          {"key":"ratio","type":"json","active":true,"salt":"s","payload":1.5,"conditions":[{"filters":[],"rollout_pct":100}]}
        ]}
        """;

    private readonly LucidaClient _lucida = new("lc_pk_x", new LucidaOptions { TestModeFixtureJson = Fixture, Fallbacks = new Dictionary<string, object> { ["dark"] = true } });
    private readonly LucidaProvider _provider;
    private FeatureClient _client = null!;

    public LucidaProviderTests() => _provider = new LucidaProvider(_lucida);

    public async Task InitializeAsync()
    {
        await Api.Instance.SetProviderAsync(_provider);
        _client = Api.Instance.GetClient();
    }

    public async Task DisposeAsync()
    {
        await Api.Instance.ShutdownAsync();
        _lucida.Dispose();
    }

    private static EvaluationContext Ctx() => EvaluationContext.Builder().SetTargetingKey("u1").Build();

    [Fact]
    public async Task BooleanAndVariant()
    {
        var on = await _client.GetBooleanDetailsAsync("on", false, Ctx());
        Assert.True(on.Value);
        Assert.Equal(Reason.TargetingMatch, on.Reason);
        Assert.True(await _client.GetBooleanValueAsync("color", false, Ctx())); // any variant is on

        var color = await _client.GetStringDetailsAsync("color", "none", Ctx());
        Assert.Equal("blue", color.Value);
        Assert.Equal("blue", color.Variant);
    }

    private static EvaluationContext With(params (string, Value)[] attrs)
    {
        var b = EvaluationContext.Builder().SetTargetingKey("u1");
        foreach (var (k, v) in attrs) b.Set(k, v);
        return b.Build();
    }

    [Fact]
    public async Task SpecV2ContextAndReasons()
    {
        Assert.True(await _client.GetBooleanValueAsync("ver", false, With(("app_version", new Value("1.9.0")))));
        Assert.False(await _client.GetBooleanValueAsync("ver", false, With(("app_version", new Value("2.0.0")))));
        Assert.True(await _client.GetBooleanValueAsync("prefix", false, With(("email", new Value("a@acme.io")))));
        Assert.False(await _client.GetBooleanValueAsync("prefix", false, With(("email", new Value("a@other.io")))));
        Assert.Equal(Reason.Disabled, (await _client.GetBooleanDetailsAsync("off", true, Ctx())).Reason);
        var api = With(("app_contexts", new Value(new List<Value> { new("api") })));
        Assert.Equal(ErrorType.FlagNotFound, (await _client.GetBooleanDetailsAsync("web", false, api)).ErrorType);
        var web = With(("app_contexts", new Value(new List<Value> { new("web") })));
        Assert.True(await _client.GetBooleanValueAsync("web", false, web));
    }

    [Fact]
    public async Task Payloads()
    {
        Assert.Equal(5, await _client.GetIntegerValueAsync("limit", 0, Ctx()));
        Assert.Equal(1.5, await _client.GetDoubleValueAsync("ratio", 0.0, Ctx()));
        var v = await _client.GetObjectValueAsync("color", new Value(), Ctx());
        Assert.Equal("#00f", v.AsStructure!["hex"].AsString);
    }

    [Fact]
    public async Task Errors()
    {
        var missing = await _client.GetBooleanDetailsAsync("nope", true, Ctx());
        Assert.True(missing.Value);
        Assert.Equal(ErrorType.FlagNotFound, missing.ErrorType);

        var noKey = await _client.GetBooleanDetailsAsync("on", false, EvaluationContext.Empty);
        Assert.False(noKey.Value);
        Assert.Equal(ErrorType.TargetingKeyMissing, noKey.ErrorType);

        var mismatch = await _client.GetStringDetailsAsync("on", "d", Ctx());
        Assert.Equal("d", mismatch.Value);
        Assert.Equal(ErrorType.TypeMismatch, mismatch.ErrorType);
        Assert.Equal(ErrorType.TypeMismatch, (await _client.GetIntegerDetailsAsync("ratio", 7, Ctx())).ErrorType);
    }

    private static string FindReasons()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "testdata", "openfeature", "reasons.json");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("testdata/openfeature/reasons.json");
    }

    [Fact]
    public void ReasonTableMatchesSharedJson()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindReasons()));
        foreach (var row in doc.RootElement.GetProperty("reasons").EnumerateObject())
        {
            Assert.Equal(row.Value.GetProperty("reason").GetString(), LucidaProvider.ReasonOf(new FlagResult(true, row.Name)));
            if (row.Value.TryGetProperty("variant_reason", out var vr))
                Assert.Equal(vr.GetString(), LucidaProvider.ReasonOf(new FlagResult("v", row.Name, 0, "v")));
        }
        Assert.Equal(Reason.Unknown, LucidaProvider.ReasonOf(new FlagResult(true, "brand_new")));
    }

    [Fact]
    public async Task MetadataAndSplit()
    {
        var color = await _client.GetStringDetailsAsync("color", "none", Ctx());
        Assert.Equal(Reason.Split, color.Reason);
        Assert.Equal("condition_match", color.FlagMetadata!.GetString("lucidaReason"));
        Assert.Equal((int?)0, color.FlagMetadata.GetInt("conditionIndex"));
        Assert.True(color.FlagMetadata.GetBool("evaluatedLocally") == true);
    }

    [Fact]
    public async Task FallbackKeepsValueWithoutError()
    {
        var d = await _client.GetBooleanDetailsAsync("dark", false, Ctx());
        Assert.True(d.Value);
        Assert.Equal(Reason.Default, d.Reason);
        Assert.Equal(ErrorType.None, d.ErrorType);
        Assert.Equal("fallback", d.FlagMetadata!.GetString("lucidaReason"));
    }

    [Fact]
    public void GroupsFeedLucidaGroups()
    {
        var ctx = With(("groups", new Value(Structure.Builder().Set("org", new Value("acme")).Build())));
        Assert.Equal("acme", LucidaProvider.ToFlagContext("u1", ctx).Groups!["org"]);
    }

    [Fact]
    public async Task TrackCapturesAnEvent()
    {
        var details = TrackingEventDetails.Builder().SetValue(9.5).Set("sku", "a").Build();
        _provider.Track("purchase", Ctx(), details);
        var e = _lucida.Captured.Last();
        Assert.Equal("purchase", e["event"]!.GetValue<string>());
        Assert.Equal(9.5, e["properties"]!["$value"]!.GetValue<double>());
        Assert.Equal("a", e["properties"]!["sku"]!.GetValue<string>());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EmitsConfigurationChangedAndStale()
    {
        var changed = new TaskCompletionSource();
        var stale = new TaskCompletionSource();
        Api.Instance.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, _ => changed.TrySetResult());
        Api.Instance.AddHandler(ProviderEventTypes.ProviderStale, _ => stale.TrySetResult());
        _provider.Changed();
        _provider.Stale();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stale.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

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

    private readonly LucidaClient _lucida = new("lc_pk_x", new LucidaOptions { TestModeFixtureJson = Fixture });
    private FeatureClient _client = null!;

    public async Task InitializeAsync()
    {
        await Api.Instance.SetProviderAsync(new LucidaProvider(_lucida));
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
}

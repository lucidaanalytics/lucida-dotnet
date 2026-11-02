using System.Text.Json;
using Xunit;

namespace Lucida.Tests;

/// <summary>AI Config helper: completion and agent modes, placeholder filling, fallback (P-M49).</summary>
public class AiConfigTests
{
    private const string Agent = """{"model":"m-large","mode":"agent","version":2,"instructions":"Help {{name}} {{missing}}","tools":[{"name":"search_docs","parameters":{"type":"object"}}]}""";
    private static readonly AiConfig Fallback = new("m-small", [new AiMessage("system", "Hi {{name}}")]);
    private static readonly Dictionary<string, string> Vars = new() { ["name"] = "Ada" };

    [Fact]
    public void RendersAgentCompletionAndFallback()
    {
        var agent = new FlagResult("a", "condition_match", 0, "a", JsonDocument.Parse(Agent).RootElement.Clone());
        var c = AiConfig.From(agent, Fallback, Vars);
        Assert.True(c.Enabled);
        Assert.Equal("agent", c.Mode);
        Assert.Equal(2, c.Version);
        Assert.Equal("Help Ada {{missing}}", c.Instructions);
        Assert.Equal("search_docs", c.Tools![0].Name);
        Assert.Empty(c.Messages!);

        var chat = new FlagResult("b", "condition_match", 0, "b",
            JsonDocument.Parse("""{"model":"m","messages":[{"role":"user","content":"Hi {{name}}"}]}""").RootElement.Clone());
        var d = AiConfig.From(chat, Fallback, Vars);
        Assert.Equal("completion", d.Mode);
        Assert.Equal("Hi Ada", d.Messages![0].Content);

        var off = AiConfig.From(null, Fallback, Vars);
        Assert.False(off.Enabled);
        Assert.Equal("m-small", off.Model);
        Assert.Equal("Hi Ada", off.Messages![0].Content);
        Assert.Equal("agent", AiConfig.From(null, new AiConfig("x", Instructions: "Be brief")).Mode);
    }

    [Fact]
    public async Task ClientServesTheVariation()
    {
        var defs = "{\"flags\":[{\"key\":\"assistant\",\"type\":\"multivariate\",\"active\":true,\"salt\":\"s\",\"aggregation\":{\"kind\":\"person\"},"
            + "\"variants\":[{\"key\":\"agent\",\"rollout_pct\":100,\"payload\":" + Agent + "}],\"conditions\":[{\"rollout_pct\":100}]}]}";
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { TestModeFixtureJson = defs });
        var c = await lc.GetAiConfigAsync("assistant", new FlagContext { DistinctId = "u" }, Fallback, Vars);
        Assert.True(c.Enabled);
        Assert.Equal("agent", c.Variation);
        Assert.Equal("Help Ada {{missing}}", c.Instructions);
    }
}

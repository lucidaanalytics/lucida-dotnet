using Xunit;

namespace Lucida.Tests;

/// <summary>The pre-identify id travels on remote flag requests (LC-FLG-34).</summary>
public class AnonDistinctIdTests
{
    [Fact]
    public void RemoteBodyCarriesAnonDistinctIdOnlyWhenSet()
    {
        using var sync = new FlagSync(null, "http://h", "lc_pk", null, new Reporter(false, null, null), TimeSpan.FromSeconds(1));
        var with = sync.RemoteBody(new FlagContext { DistinctId = "u1", AnonDistinctId = "anon-9" }, ["k"]);
        Assert.Equal("anon-9", (string?)with["anon_distinct_id"]);
        Assert.Equal("u1", (string?)with["distinct_id"]);
        Assert.False(sync.RemoteBody(new FlagContext { DistinctId = "u1" }, null).ContainsKey("anon_distinct_id"));
    }
}

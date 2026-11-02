# Lucida .NET SDK

Server SDK (`Lucida`, net8.0). Delivery is at-least-once: a retried event keeps its `uuid`, the server dedupes.
Packages: `Lucida` (core), `Lucida.AspNetCore` (DI, middleware), `Lucida.OpenFeature`.

```csharp
using var lc = new LucidaClient("lc_pk_...", new LucidaOptions { SecretKey = "lc_sk_...", FlagPollInterval = TimeSpan.FromSeconds(30) });
lc.Capture("user-1", "purchase", new Dictionary<string, object?> { ["total"] = 42 });
var v = await lc.GetFeatureFlagAsync("new-checkout", new FlagContext { DistinctId = "user-1" });
```

`Dispose()` stops timers and flushes.

## Options

| Option | Meaning |
|---|---|
| `FlushAt`, `FlushInterval`, `MaxQueueSize` | flush trigger, timer, bounded queue (overflow counts in `DroppedEvents`) |
| `MaxBatchSize` | events per request (<= 1000), independent of `FlushAt` |
| `MaxRetries`, `RetryBase` | exponential backoff with jitter; honours `Retry-After` |
| `RequestTimeout` | per-request timeout for every HTTP call (default 10s) |
| `Debug`, `DebugSink` | debug lines to the sink (default stderr) |
| `OnError` | `Action<Exception>` for dropped batches and failed flag fetches; its exceptions are swallowed |
| `BeforeSend` | `Func<JsonObject, JsonObject?>`; return `null` to drop, throwing keeps the original |
| `FlagPollInterval` | poll `/v1/flags/definitions` in the background (needs `SecretKey`); **floor 5s**, shorter values are raised |
| `OnlyEvaluateLocally` | never call the server for flags; undecidable ones read as unknown |
| `TestModeFixtureJson`, `Disabled`, `Handler` | offline fixture mode, switch-off, inject an `HttpMessageHandler` |

Definitions use `ETag` / `If-None-Match`; a `304` keeps the current set. `ReloadFlagsAsync()` does one fetch on demand.

## Flags

Local evaluation first; flags it cannot decide (unknown key, cohorts, nothing loaded yet) go to `POST /v1/flags` unless `OnlyEvaluateLocally`.

- `GetFlagResultAsync(key, ctx)` value + payload + reason (`"remote"` for server answers)
- `GetFeatureFlagDetailsAsync(key, ctx)` (same read as `GetFlagResultAsync`): `Value`, `Payload`, `Variant`, raw `Reason` (the gateway's, `"remote"` if it sent none, `"fallback"`), `ConditionIndex`, `RequestId`, `EvaluatedLocally`
- `LucidaOptions.Fallbacks` (`{flagKey: value}`): served with reason `"fallback"` when a flag cannot be evaluated (network error, nothing loaded, quota limited, unknown key); no exposure is sent for it
- `FlagsChanged` / `FlagsStale` events: definitions reloaded with a change / a reload failed
- `GetFeatureFlagAsync`, `GetFeatureFlagPayloadAsync`, `GetAllFlagsAsync`, `GetAllFlagsAndPayloadsAsync`, `GetAllFlagResultsAsync`
- `Evaluate`, `IsEnabled`: local only

## Events

`Identify`/`Alias`/`GroupIdentify` queue; `IdentifyAsync`/`AliasAsync`/`GroupIdentifyAsync` queue and deliver at once (true when sent).

`ImportEventsAsync(events)` is the historical import: needs `SecretKey`, posts `/v1/s2s/batch` in chunks of `MaxBatchSize`, timestamps kept, `LucidaException` on the first rejected chunk.

Personless: `lc.CapturePersonless("page_seen")` or `Capture(..., personless: true)` sends `$process_person_profile=false`; with no distinct id a random UUID v4 is generated.

## Request context

```csharp
using (LucidaContext.With("user-1", sessionId: "s1", props)) { lc.Capture(null, "checkout"); }
```

`LucidaContext` is an `AsyncLocal`, so it flows through awaits. `LucidaContext.FromHeaders(...)` / `ReadHeader(...)` read `X-Lucida-Distinct-Id` and `X-Lucida-Session-Id` (case-insensitive, control chars stripped, trimmed, capped at 1000 chars). They are client-controlled: **never use them for auth**. Explicit capture arguments override context values.

## ASP.NET Core (`Lucida.AspNetCore`)

```csharp
builder.Services.AddLucida(builder.Configuration);   // section "Lucida"
app.UseAuthentication();
app.UseLucida();                                      // after authentication
```

```json
{ "Lucida": { "ApiKey": "lc_pk_...", "SecretKey": "lc_sk_...", "Host": "https://app.lucida.dev", "FlushAt": 50, "FlagPollInterval": "00:00:30", "TrustProxy": false } }
```

Any `LucidaOptions` property binds from the section; code-only options (handler, `BeforeSend`, `OnError`) use `AddLucida(apiKey, options, trustProxy)`. The middleware sets distinct id (the authenticated `NameIdentifier`/`sub` claim, else the tracing header), session id, `$current_url`, `$request_method`, `$request_path`, `$user_agent` and `$ip` (first `X-Forwarded-For` entry only with `TrustProxy`). A hosted service flushes the queue when the host stops.

Tests: `make sdk-dotnet`.

---

This repository is a read-only mirror of the Lucida monorepo (synced by a script, never edited by hand); releases and provenance attestations are built here by GitHub Actions. Issues are welcome, changes land upstream.

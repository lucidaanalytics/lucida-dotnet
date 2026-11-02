# Lucida.OpenFeature

OpenFeature (Apache-2.0) server provider over the Lucida .NET SDK:

```csharp
await Api.Instance.SetProviderAsync(new LucidaProvider(new LucidaClient("lc_pk_...", options)));
```

The targeting key is the Lucida distinct id and every other context attribute becomes a person property. Flags evaluate locally; a variant resolves as a string, multivariate flags are "on" for boolean evaluations, and integer/double/structure evaluations read the flag payload. Errors: `FlagNotFound`, `TypeMismatch`, `TargetingKeyMissing`. `groups` (type to key) and `groupProperties` (or `group_properties`) feed Lucida groups. Raw Lucida reasons map through `testdata/openfeature/reasons.json` (Split for a matched variant); flag metadata carries `lucidaReason`, `conditionIndex`, `requestId`, `evaluatedLocally`. A client `Fallbacks` value resolves with reason Default, no error and `lucidaReason` `fallback`. `Track(...)` captures a Lucida event (`$value` from the details value). Events: Ready after the first definitions load, ConfigurationChanged on reload, Stale when a reload fails, Error when the first load fails.

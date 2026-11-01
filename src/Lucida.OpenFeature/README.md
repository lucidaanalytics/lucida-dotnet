# Lucida.OpenFeature

OpenFeature (Apache-2.0) server provider over the Lucida .NET SDK:

```csharp
await Api.Instance.SetProviderAsync(new LucidaProvider(new LucidaClient("lc_pk_...", options)));
```

The targeting key is the Lucida distinct id and every other context attribute becomes a person property. Flags evaluate locally; a variant resolves as a string, multivariate flags are "on" for boolean evaluations, and integer/double/structure evaluations read the flag payload. Errors: `FlagNotFound`, `TypeMismatch`, `TargetingKeyMissing`. Group context is not mapped.

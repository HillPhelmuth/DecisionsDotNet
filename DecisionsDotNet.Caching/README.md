# DecisionsDotNet.Caching

Caches [DecisionsDotNet](https://www.nuget.org/packages/DecisionsDotNet) evaluations in `HybridCache`, so the same question about the same input is paid for once.

```powershell
dotnet add package DecisionsDotNet.Caching
```

> Experimental (`DECISIONS002`). Add `<NoWarn>$(NoWarn);DECISIONS002</NoWarn>` to use it.

```csharp
using DecisionsDotNet.Caching;
using DecisionsDotNet.Pipeline;

builder.Services.AddHybridCache();
builder.Services.AddKeyedOpenRouterDecisionsClient("openrouter", builder.Configuration);
builder.Services.AddDecisionsClient(sp => sp.GetRequiredKeyedService<IDecisionsClient>("openrouter"))
    .UseCaching(TimeSpan.FromHours(1))
    .UseLogging();
```

The key is a SHA-256 hash of the request's state, questions, criteria, and model, so any change to the input or the wording is a miss. Concurrent identical requests share one provider call. Only successful responses are cached. A hit returns the response as first received, including its usage, so count spend from the provider call, not from hits. Remove every entry with `cache.RemoveByTagAsync(CachingDecisionsClient.CacheTag)`.

Put `UseCaching` before `UseFallback` so a fallback answer is cached too, and after `UseLogging` or `UseOpenTelemetry` if you want hits to show in logs and traces.

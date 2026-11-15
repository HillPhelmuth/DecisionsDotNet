# DecisionsDotNet and ExtractionsDotNet

.NET 10 SDKs for calibrated decision and extraction models: TypeSafe AI's Jev models, and the Decisions and Extractions APIs from OpenRouter and OpenAI.

| Package | What it does | Docs |
| --- | --- | --- |
| [DecisionsDotNet](https://www.nuget.org/packages/DecisionsDotNet) | Typed yes/no, choice, score, and bounding-box questions through TypeSafe, OpenRouter, or OpenAI, with DI, batching, and bounded retries. | [README](DecisionsDotNet/README.md) |
| [DecisionsDotNet.AI](https://www.nuget.org/packages/DecisionsDotNet.AI) | Exposes any Decisions client as a Microsoft.Extensions.AI tool for chat clients and agents. | [README](DecisionsDotNet.AI/README.md) |
| [ExtractionsDotNet](https://www.nuget.org/packages/ExtractionsDotNet) | Typed field extraction from text, images, and PDFs through OpenRouter. | [README](ExtractionsDotNet/README.md) |

```powershell
dotnet add package DecisionsDotNet
dotnet add package DecisionsDotNet.AI
dotnet add package ExtractionsDotNet
```

`DecisionsDotNet` and `DecisionsDotNet.AI` always share a version. `ExtractionsDotNet` versions on its own. See the [changelog](CHANGELOG.md) for releases and the migration from 0.3.

The repository, demo, and test project keep their original `JevDotNet` names.

## Demo

[JevDotNet.Demo](JevDotNet.Demo/README.md) is a Blazor Server app with support triage, image decisions, bulk triage, Jev as Judge, a chase game, and the Extractions Lab. It needs an OpenRouter API key:

```powershell
dotnet user-secrets set "OpenRouter:ApiKey" "<your-api-key>" --project JevDotNet.Demo
dotnet run --project JevDotNet.Demo
```

## Build and test

```powershell
dotnet test --project JevDotNet.Tests/JevDotNet.Tests.csproj
dotnet pack DecisionsDotNet/DecisionsDotNet.csproj
```

The xUnit v3 tests use fake HTTP handlers and need no API key. The direct client follows the [TypeSafe AI API reference](https://docs.typesafe.ai/api) and [model listing reference](https://docs.typesafe.ai/models).

Three checks run on every build of the packages:

- Every public member needs an XML doc comment.
- The public surface must match each package's `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. When you change public API on purpose, update `PublicAPI.Unshipped.txt` in the same commit; the IDE offers this as a quick fix on the RS0016 error.
- `dotnet pack` runs package validation.

Package versions live in `Directory.Build.props`.

## License

[MIT](LICENSE)

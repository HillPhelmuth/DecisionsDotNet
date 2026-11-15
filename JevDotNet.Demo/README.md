# JevDotNet.Demo

A Blazor Interactive Server showcase for [DecisionsDotNet](../DecisionsDotNet/README.md) and [ExtractionsDotNet](../ExtractionsDotNet/README.md). Commands below run from the repository root.

Two image demos use the embedded PNGs in `JevDotNet.Demo/Images/`, loaded through `FileHelper`:

- **Image decisions** (`/image-decisions`) lets you select an image-capable model, edit a yes/no question, and evaluate Noul, Choice, and Score questions together.
- **Find it in the image** (`/image-box`) lets you describe a target, then draws the returned `BoxAnswer` over the original image using a responsive SVG overlay. A companion Noul answer reports target visibility; you can toggle the box and inspect its coordinates and confidence.

Both pages support switching between the breakfast and alien-invasion images, cancellation, response JSON, elapsed time, token usage, and reported cost. Changing an input clears the previous result. Requests run only on **Evaluate image** or **Find target**. The image selector uses `DemoDecisionModel.ModelSupportsImageInput()`; the Box page further limits its models to known Jev 1.14 entries because image support alone does not establish Box support. `FileHelper.ExtractImageBytesFromAssembly` removes bytes appended after the PNG `IEND` marker before previewing or encoding the image; the PNG chunks and pixels remain unchanged. Strict provider decoders can reject that appended data even when a browser displays the image. Providers with smaller upload limits may still reject an image; the page displays a retryable error.

The **Pick a lane** demo at `/pick-a-lane` lets the selected model drive alone on an animated three-lane road. Start, pause/resume, or reset; there are no steering controls. Relaxed, Normal, and Fast give barriers 5, 3, and 2 seconds to reach the car. Driving continues during requests, with one request in flight and at most two starts per second. A collision or 100 applied decisions ends the round. The dashboard shows lane probabilities, barriers passed, latency, tokens, and reported cost. Hiding the tab pauses the game.

`JevDotNet.Demo` is an Interactive Server showcase with support triage, content review, lead qualification, image decisions, image localization, bulk support triage, Jev as Judge, and a live chase game. It injects `IDecisionsClient` configured for OpenRouter. Model selectors and validation use the IDs and names returned by [OpenRouter's Decisions model catalog](https://openrouter.ai/api/v1/models?output_modalities=decisions), with Jev 1.14 as the default selection. The catalog loads at startup without an API key and refreshes every 15 minutes. Startup requires a successful catalog fetch; later fetch failures are logged and retain the last successful catalog. Those pages start with editable sample input and call the selected model only when you click **Evaluate**. In the game, clicking **Start chase** begins a bounded round of up to 100 chaser moves, with one live Decisions request per move. Pause or leave the page to stop the requests.

The **Bulk triage** page uses 95 bundled support messages. Select a Decisions model, then choose a batch size from 2 to 95 messages per request, or evaluate one message per request. Each message adds five questions, and the final batch may be smaller than the chosen size. A Microsoft Agent Framework agent sends the same five questions to `openai/gpt-6-luna` one message at a time with JSON-schema structured output. The three tabs show each model's scores and request times, then yes/no agreement at 50% and the average absolute score gap. Percentages from the bundled JSON appear as reference values only; they are never sent to either model or counted in the agreement metrics. Cancel stops future requests, and a rejected Decisions batch is reported without changing modes automatically.

## Extractions Lab

Open [Extractions Lab](http://localhost:5120/extractions) from the demo home page or sidebar. It uses the same server-side `OpenRouter:ApiKey` user secret as the Decisions demos. Samples and schemas can be browsed without a key. Click **Extract fields** to send the current dispatch or selected bundled file.

| Demo | Input | Fields |
| --- | --- | --- |
| [Expedition Dispatch](http://localhost:5120/extractions/dispatch) | Editable text | Text, number, date, time, datetime, duration, lists of text/numbers/dates, numeric estimate, three-day numeric forecast |
| [Cargo Tag](http://localhost:5120/extractions/cargo-tag) | Bundled PNG, JPEG, or WebP | Crate ID, weight, inspection date, contents list |
| [Permit Desk](http://localhost:5120/extractions/permit) | Bundled PDF | Permit ID, issuing office, approved activities list, deliberately absent emergency phone |

Dispatch and Cargo Tag use Clairic. Permit Desk uses ScaleDown Extract, which accepts PDFs and returns text and text lists. See the [OpenRouter Extractions guide](https://openrouter.ai/docs/guides/community/extractions) for model capabilities.

## Jev as Judge

The **[Jev as Judge](http://localhost:5120/jev-as-judge)** page evaluates Relevance, Completeness, Equivalence, and Groundedness on five-level rubrics. It bundles 100 hand-authored examples in `JevDotNet.Demo/Data/judge-data.json`: twenty-five per evaluator across five scenario families, with five examples at each expected rating from 1 to 5. These labels illustrate the rubrics rather than serving as a validated benchmark.

Choose a decision model and evaluator above the **Single execution run** and **Bulk execution run** tabs. In Single, choose an example, edit its inputs, and click **Judge response**. Relevance uses a query and response; Completeness and Equivalence also use a reference answer; Groundedness uses source context. Expected scores and author-written explanations are never sent to the model. Editing hides the original reference comparison until **Reset example**; selecting a dataset row opens its inputs and full completed Jev result in Single. Switching tabs preserves edits and completed results.

The displayed integer Likert rating is the most probable level (ties favor the lower level). The weighted score is Jev's zero-based score plus one, so it can be fractional. Results show all five probabilities, distribution confidence, actual model, measured request time, token usage, and reported cost when available. Dataset summaries report exact integer-rating agreement and mean absolute rating error across successful requests only. Failed items are reported individually and remaining items continue; cancellation preserves completed results. All examples remain browsable without an API key.

In Bulk, **Run 25 examples · Jev + Luna** runs the selected decision model and `openai/gpt-6-luna` sequentially on the original twenty-five examples with the same rubric and evidence. Every bulk execution includes both judges. The nested **Luna vs Jev** and **Jev details** tabs switch between the comparison and detailed Jev table from the same run. Luna returns an integer rating and a short rationale through Microsoft Agent Framework with strict JSON-schema output. The comparison shows both judges' agreement with reference labels, their agreement with each other, rating gaps, and original request times. Edited inputs are evaluated only by the single-response decision-model action.

Luna requests and raw responses are saved in `JevDotNet.Demo/Data/judge-luna-cache/`, keyed by a SHA-256 fingerprint of the model, instructions, rubric, inputs, output schema, and output limit. Identical successful judgments are read from disk across runs, viewers, decision-model changes, and process restarts. Cache files contain original ratings, rationales, model, token usage, timestamps, and measured model latency; stored latency is not presented as cache retrieval time. Cache JSON files are included in build/publish output. Set `Judge:LunaCachePath` to a persistent writable directory when deploying; keep that directory across upgrades.

The **Only show results that differ from expected** toggle filters both bulk result tabs and persists when switching views. Jev details includes rows with a completed Jev rating different from the expected integer score; Luna vs Jev includes rows where either judge differs from expected, even if the judges agree with each other. Pending and failed requests have no rating and are hidden by the filter; turn it off to inspect those rows. Filtering changes only displayed rows: summary metrics, execution, and saved judgments remain unchanged.

A file lock serializes identical requests, and SDK HTTP retries are disabled. Only valid successful Luna judgments are cached. Failed, malformed, or cancelled requests are reported for the current run without saving an error entry, so a later run can try them again. Legacy failed or pending entries are discarded when encountered. Successful judgments remain reusable; corrupt or inconsistent stored results are surfaced as storage errors rather than silently replaced. No credentials are stored in the cache.

Run all four comparisons and save a detailed report to `Data/judge-comparison-results.json` with:

```powershell
dotnet run --project JevDotNet.Demo -- --Judge:RunComparison=true
```

Repeating this command re-evaluates the decision model and reuses stored Luna responses.

Configure an API key as an ASP.NET Core user secret:

```powershell
dotnet user-secrets set "OpenRouter:ApiKey" "<your-api-key>" --project JevDotNet.Demo
dotnet run --project JevDotNet.Demo
```

The key remains server-side. Without it, the app shows setup guidance and disables evaluation. The recommendations in the demo are illustrative C# rules and do not perform ticket routing, moderation, or CRM actions.

The chase game starts with the runner at column 6, row 6 and the chaser at column 1, row 1. Move the runner with arrow keys or the on-screen controls. Each request describes both positions, the walls, and the shortest path after each candidate chaser move. Criteria retain the move descriptions and distances, with exactly `up` and `right` as criteria. The game calculates `down = 1 - up` and `left = 1 - right`, then chooses the legal move with the highest probability. The page shows that move, all four probabilities, token use, reported cost, and average completed decisions per second. The rate includes the 400 ms turn pacing and excludes pauses. Reset the game to choose another model.

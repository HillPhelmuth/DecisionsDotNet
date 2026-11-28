# DecisionsDotNet.Testing

Test doubles for [DecisionsDotNet](https://www.nuget.org/packages/DecisionsDotNet), so code that branches on Decisions answers can be unit tested without HTTP stubs or a provider key.

[Source, demo, and changelog on GitHub](https://github.com/HillPhelmuth/DecisionsDotNet).

```powershell
dotnet add package DecisionsDotNet.Testing
```

> Experimental (`DECISIONS002`). Add `<NoWarn>$(NoWarn);DECISIONS002</NoWarn>` to your test project.

## FakeDecisionsClient

Answers from scripted rules. Rules are checked newest first, so add broad defaults before specific overrides. A question no rule matches fails the call with a message naming it, so a test never passes on an answer it did not script.

```csharp
using DecisionsDotNet;
using DecisionsDotNet.Testing;

var fake = new FakeDecisionsClient()
    .AnswerNoul(0.1)                                    // every yes/no question
    .AnswerNoul("urgent", 0.95)                          // except this question ID
    .AnswerNoul(0.9, q => q.Instructions.Contains("refund"))
    .AnswerChoice(Team.Billing, probability: 0.8)        // enum member names are the labels ChooseAsync sends
    .AnswerScore(1.6)                                    // probability-weighted level, counted from zero
    .Refuse(q => q.State.Contains("forbidden"))
    .Throw(new TimeoutException(), request => request.Model == "slow-model");

var service = new TriageService(fake.ForModel("test-model"));
```

`Answer(match, answer)` returns any `DecisionsAnswer` you build. Out-of-range values, such as a probability of 1.5 or a choice that was not offered, are returned as given, so you can test how your code handles a bad response. `Requests` lists every request received.

## RecordingDecisionsClient

Wraps any `IDecisionsClient`, real or fake, and records each call's request, cancellation token, response, and exception:

```csharp
var recorder = new RecordingDecisionsClient(fake);
await service.TriageAsync(tickets, recorder.ForModel("test-model"));

Assert.Equal(1, recorder.Count);                       // one request, not one per ticket
Assert.All(recorder.Requests, r => Assert.Equal("test-model", r.Model));
```

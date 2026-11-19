# Recipes: semantic conditions in common .NET patterns

Short, copy-paste examples that put a `DecisionModel` verb where a .NET app already makes decisions. They need no extra DecisionsDotNet packages: each is a few lines over the core verbs. All of them are compiled against the packed packages by `eng/verify-packages.sh` (see `eng/PackageSmoke/Recipes.cs`), so they stay correct as the API changes.

Every example assumes a registered model:

```csharp
builder.Services.AddOpenRouterDecisionsClient(builder.Configuration);
builder.Services.AddDecisionModel("typesafe/jev-1.14");
```

Thresholds here are examples. Pick yours from the cost of a wrong yes against a wrong no, and check them against labelled examples.

## ASP.NET Core endpoint filter

Reject abusive input before the handler runs. This is content policy, not authorization: never let a probability decide who may access what.

```csharp
app.MapPost("/comments", (CommentRequest comment) => Results.Created($"/comments/{Guid.NewGuid()}", comment))
    .AddEndpointFilter(async (context, next) =>
    {
        var model = context.HttpContext.RequestServices.GetRequiredService<DecisionModel>();
        var comment = context.GetArgument<CommentRequest>(0);
        if (await model.MatchesAsync(comment.Text, "Is this comment abusive or harassing?",
                minProbability: 0.8, cancellationToken: context.HttpContext.RequestAborted))
            return Results.Problem("This comment breaks the community rules.", statusCode: StatusCodes.Status422UnprocessableEntity);
        return await next(context);
    });

public sealed record CommentRequest(string Text);
```

## FluentValidation async rule

FluentValidation runs async rules only through `ValidateAsync`. Avoid `DataAnnotations` for this: `ValidationAttribute.IsValid` is synchronous, so it would block on a network call.

```csharp
public sealed class CommentValidator : AbstractValidator<CommentRequest>
{
    public CommentValidator(DecisionModel model)
    {
        RuleFor(comment => comment.Text)
            .NotEmpty()
            .MustAsync(async (text, ct) => !await model.MatchesAsync(text, "Is this comment abusive or harassing?",
                minProbability: 0.8, cancellationToken: ct))
            .WithMessage("Keep comments civil.");
    }
}
```

## MediatR pipeline behavior

Moderate every request that carries user text, with a separate path for uncertain cases. The same shape works for any mediator or pipeline library.

```csharp
public interface IHasUserText
{
    string UserText { get; }
}

public sealed class ModerationBehavior<TRequest, TResponse>(DecisionModel model, IModerationQueue queue)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IHasUserText
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var verdict = await model.VerdictAsync(request.UserText, "Does this text contain threats or abuse?",
            unsureBand: (0.3, 0.7), cancellationToken: cancellationToken);
        switch (verdict.Outcome)
        {
            case VerdictOutcome.No:
                return await next();
            case VerdictOutcome.Unsure:
                await queue.HoldForReviewAsync(request, verdict, cancellationToken);
                throw new ModerationPendingException();
            default:
                throw new ModerationRejectedException(verdict);
        }
    }
}
```

MediatR 13 and later need a commercial license for some uses; check its terms. The behavior itself is the same in any version.

## Several conditions, one request

When a handler needs more than one judgment about the same text, declare them once and ask them together instead of calling three verbs:

```csharp
public sealed class RefundSignals
{
    [Noul("Is the customer asking for money back?", MinProbability = 0.75)]
    public bool WantsRefund { get; init; }

    [Noul("Is the customer threatening to cancel?", UnsureMin = 0.3, UnsureMax = 0.7)]
    public DecisionVerdict ChurnRisk { get; init; } = null!;

    [Choice("Which product is the message about?")]
    public DecisionChoice<Product> Product { get; init; } = null!;
}

var signals = await model.DecideAsync<RefundSignals>(ticket.Text, ct);
if (signals.WantsRefund && signals.ChurnRisk.Outcome == VerdictOutcome.Yes)
    await EscalateAsync(ticket, signals.Product.Value, ct);
```

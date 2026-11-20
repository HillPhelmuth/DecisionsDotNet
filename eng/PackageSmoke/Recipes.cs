// The examples from docs/recipes.md, compiled against the packed packages so they cannot drift from the API.
// Keep the bodies identical to the document.
using DecisionsDotNet;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public static class Recipes
{
    public static void MapComments(WebApplication app)
    {
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
    }

    public static async Task<bool> RefundFlowAsync(DecisionModel model, string text, CancellationToken ct)
    {
        var signals = await model.DecideAsync<RefundSignals>(text, ct);
        return signals.WantsRefund && signals.ChurnRisk.Outcome == VerdictOutcome.Yes && signals.Product.Value == Product.Plan;
    }
}

public sealed record CommentRequest(string Text);

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

public interface IHasUserText
{
    string UserText { get; }
}

public interface IModerationQueue
{
    Task HoldForReviewAsync(object request, DecisionVerdict verdict, CancellationToken cancellationToken);
}

public sealed class ModerationPendingException : Exception;

public sealed class ModerationRejectedException(DecisionVerdict verdict) : Exception
{
    public DecisionVerdict Verdict { get; } = verdict;
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

public enum Product { Plan, Hardware, Other }

public sealed class RefundSignals
{
    [Noul("Is the customer asking for money back?", MinProbability = 0.75)]
    public bool WantsRefund { get; init; }

    [Noul("Is the customer threatening to cancel?", UnsureMin = 0.3, UnsureMax = 0.7)]
    public DecisionVerdict ChurnRisk { get; init; } = null!;

    [Choice("Which product is the message about?")]
    public DecisionChoice<Product> Product { get; init; } = null!;
}

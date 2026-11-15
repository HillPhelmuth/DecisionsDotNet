namespace DecisionsDotNet;

/// <summary>Diagnostic IDs for parts of the SDK marked experimental. They may change in a minor release.</summary>
internal static class DecisionsDiagnostics
{
    /// <summary>The OpenAI Decisions client and its registrations, which are not yet verified against the live API.</summary>
    internal const string OpenAIClient = "DECISIONS001";

    internal const string UrlFormat = "https://github.com/HillPhelmuth/JevDotNet/blob/main/CHANGELOG.md#experimental-apis";
}

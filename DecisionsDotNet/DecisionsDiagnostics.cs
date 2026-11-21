namespace DecisionsDotNet;

/// <summary>Diagnostic IDs for parts of the SDK marked experimental. They may change in a minor release.</summary>
internal static class DecisionsDiagnostics
{
    /// <summary>The OpenAI Decisions client and its registrations, which are not yet verified against the live API.</summary>
    internal const string OpenAIClient = "DECISIONS001";

    /// <summary>The semantic conditional layer: <see cref="DecisionModel"/>, its verbs and result types, and the test doubles.</summary>
    internal const string Conditions = "DECISIONS002";

    /// <summary>The Microsoft Foundry and Azure OpenAI clients and their registrations, which are not yet verified against the live APIs.</summary>
    internal const string AzureClients = "DECISIONS003";

    internal const string UrlFormat = "https://github.com/HillPhelmuth/JevDotNet/blob/main/CHANGELOG.md#experimental-apis";
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

/// <summary>One extraction: the model, the fields to read, and the text or assets to read them from.</summary>
/// <param name="Model">An OpenRouter model ID that serves the Extractions route, such as one from <c>ListModelsAsync</c>.</param>
/// <param name="Fields">The fields to extract, keyed by the name each answer is returned under. At least one is required.</param>
public sealed record ExtractionRequest(string Model, IReadOnlyDictionary<string, ExtractionField> Fields)
{
    /// <summary>The text to read. It may be empty only when <see cref="Assets"/> has exactly one entry, whose key is then sent as the state.</summary>
    public string State { get; init; } = "";

    /// <summary>Images and documents to read, keyed by a name that the state or a field's instructions must reference.</summary>
    public IReadOnlyDictionary<string, ExtractionAsset>? Assets { get; init; }

    /// <summary>The date that anchors dates written without a year. Models without date fields reject it.</summary>
    public DateOnly? ReferenceDate { get; init; }

    /// <summary>OpenRouter provider routing and provider-specific options.</summary>
    public ExtractionProviderOptions? Provider { get; init; }

    /// <summary>OpenRouter's optional end-user identifier.</summary>
    public string? User { get; init; }

    /// <summary>OpenRouter's optional session identifier.</summary>
    public string? SessionId { get; init; }

    /// <summary>OpenRouter's optional trace metadata, sent as given.</summary>
    public IReadOnlyDictionary<string, object?>? Trace { get; init; }
}

/// <summary>The file format of an <see cref="ExtractionAsset"/>. Model support varies by format.</summary>
public enum ExtractionAssetFormat
{
    /// <summary>A PNG image.</summary>
    Png,
    /// <summary>A JPEG image, sent as <c>jpg</c>.</summary>
    Jpg,
    /// <summary>A JPEG image, sent as <c>jpeg</c>.</summary>
    Jpeg,
    /// <summary>A WebP image.</summary>
    Webp,
    /// <summary>A PDF document.</summary>
    Pdf
}

/// <summary>An image or document sent with an extraction request.</summary>
public sealed class ExtractionAsset
{
    /// <summary>Creates an asset from the file's raw bytes. The SDK base64-encodes them and does not resize or convert the file.</summary>
    public ExtractionAsset(ExtractionAssetFormat format, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (data.Length == 0)
            throw new ArgumentException("An extraction asset must contain data.", nameof(data));

        Format = format;
        DataBase64 = Convert.ToBase64String(data);
    }

    /// <summary>The file format.</summary>
    public ExtractionAssetFormat Format { get; }

    /// <summary>The API's asset type: <c>document</c> for PDFs and <c>image</c> otherwise.</summary>
    public string Type => Format == ExtractionAssetFormat.Pdf ? "document" : "image";

    /// <summary>The file's bytes as standard base64, without a data URL prefix.</summary>
    public string DataBase64 { get; }
}

/// <summary>OpenRouter provider routing for an extraction request.</summary>
public sealed record ExtractionProviderOptions
{
    /// <summary>Provider slugs to try, in order.</summary>
    public IReadOnlyList<string>? Order { get; init; }

    /// <summary>The only provider slugs allowed to serve the request.</summary>
    public IReadOnlyList<string>? Only { get; init; }

    /// <summary>Provider slugs that must not serve the request.</summary>
    public IReadOnlyList<string>? Ignore { get; init; }

    /// <summary>Whether OpenRouter may fall back to another provider.</summary>
    public bool? AllowFallbacks { get; init; }

    /// <summary>Provider-specific options keyed by provider slug, such as <c>cloudflare</c> with <c>return_state_text</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Options { get; init; }

    /// <summary>Additional OpenRouter provider routing properties, keyed by their JSON names.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

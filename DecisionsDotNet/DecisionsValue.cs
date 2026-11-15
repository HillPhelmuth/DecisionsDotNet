using System.Text.Json;
using System.Text.Json.Serialization;

namespace DecisionsDotNet;

/// <summary>A text, object, array, or image value accepted by the Decisions API.</summary>
[JsonConverter(typeof(DecisionsValueConverter))]
public sealed class DecisionsValue
{
    private readonly JsonElement _value;

    private DecisionsValue(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("A Decisions value must be a JSON string, object, or array.", nameof(value));
        _value = value.Clone();
    }

    /// <summary>Creates a text value. A string converts implicitly, so this is rarely needed.</summary>
    public static DecisionsValue Text(string value) => new(JsonSerializer.SerializeToElement(
        value ?? throw new ArgumentNullException(nameof(value))));
    /// <summary>Returns the text of a text value.</summary>
    /// <exception cref="InvalidOperationException">The value is an object or array.</exception>
    public string AsText() => _value.GetString() ?? throw new InvalidOperationException("The value is not a JSON string.");

    /// <summary>Creates a structured value from anything that serializes to a JSON object.</summary>
    public static DecisionsValue FromObject<T>(T value, JsonSerializerOptions? options = null) where T : notnull
        => FromSerialized(value, JsonValueKind.Object, options);

    /// <summary>Creates a structured value from anything that serializes to a JSON array.</summary>
    public static DecisionsValue FromArray<T>(T value, JsonSerializerOptions? options = null) where T : notnull
        => FromSerialized(value, JsonValueKind.Array, options);

    /// <summary>Wraps a JSON string, object, or array.</summary>
    public static DecisionsValue FromJson(JsonElement value) => new(value);

    /// <summary>Creates an inline PNG, JPEG, or WebP image. The caller prepares the image dimensions and orientation.</summary>
    public static DecisionsValue ImageFromBytes(ReadOnlyMemory<byte> data, string mediaType)
    {
        ImageInput.ValidateMediaType(mediaType);
        if (data.IsEmpty) throw new ArgumentException("Image data cannot be empty.", nameof(data));
        return ImageFromDataUrl($"data:{mediaType};base64,{Convert.ToBase64String(data.Span)}");
    }

    /// <summary>Creates an image from a base64 data URL. Remote URLs are not supported.</summary>
    public static DecisionsValue ImageFromDataUrl(string dataUrl)
    {
        ImageInput.ParseDataUrl(dataUrl);
        return FromObject(new { type = "image_url", image_url = new { url = dataUrl } });
    }

    /// <summary>Combines ordered text, structured context, and image values into a state array.</summary>
    public static DecisionsValue FromParts(params DecisionsValue[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Any(part => part is null))
            throw new ArgumentException("State parts cannot be null.", nameof(parts));
        return FromArray(parts);
    }

    /// <summary>Converts a string to a text value.</summary>
    public static implicit operator DecisionsValue(string value) => Text(value);

    /// <summary>Returns a copy of the underlying JSON.</summary>
    public JsonElement ToJsonElement() => _value.Clone();

    private static DecisionsValue FromSerialized<T>(T value, JsonValueKind expected, JsonSerializerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, options);
        if (element.ValueKind != expected)
            throw new ArgumentException($"The serialized value must be a JSON {expected.ToString().ToLowerInvariant()}.", nameof(value));
        return new DecisionsValue(element);
    }

    private sealed class DecisionsValueConverter : JsonConverter<DecisionsValue>
    {
        public override DecisionsValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(JsonElement.ParseValue(ref reader));

        public override void Write(Utf8JsonWriter writer, DecisionsValue value, JsonSerializerOptions options)
            => value._value.WriteTo(writer);
    }
}

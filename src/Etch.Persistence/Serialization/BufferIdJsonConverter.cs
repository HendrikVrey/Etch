using System.Text.Json;
using System.Text.Json.Serialization;
using Etch.Persistence.Model;

namespace Etch.Persistence.Serialization;

/// <summary>
/// Reads and writes a <see cref="BufferId"/> as a bare hex string.
/// </summary>
/// <remarks>
/// Without this the id round-trips as <c>{"value":"…"}</c>, which is noise in a file
/// people will open in an editor when something has gone wrong. The written form is
/// exactly the buffer's file name minus its extension, so a session entry can be
/// matched to a file on disk by eye.
/// </remarks>
internal sealed class BufferIdJsonConverter : JsonConverter<BufferId>
{
    /// <inheritdoc />
    public override BufferId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a buffer id string, found {reader.TokenType}.");
        }

        var text = reader.GetString();

        if (!BufferId.TryParseFileName($"{text}{BufferId.Extension}", out var id))
        {
            throw new JsonException($"'{text}' is not a valid buffer id.");
        }

        return id;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BufferId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString());
    }
}

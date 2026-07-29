using System.Text.Json.Serialization;
using Etch.Persistence.Model;

namespace Etch.Persistence.Serialization;

/// <summary>
/// Source-generated serialisation for the session index.
/// </summary>
/// <remarks>
/// Source generation rather than reflection, for two reasons that both matter here.
/// It keeps the reflection-based serialiser out of the startup path — reading
/// <c>session.json</c> happens before the first frame, and that is the budget the
/// whole project is built around. And it keeps <c>Etch.Persistence</c> honestly
/// AOT-compatible, so the analyser can prove there is nothing here that a trimmed
/// build would break.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    // The file is small and read once. Being able to read it in a text editor when
    // something has gone wrong is worth more than the bytes.
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(SessionSnapshot))]
internal sealed partial class SessionJsonContext : JsonSerializerContext
{
}

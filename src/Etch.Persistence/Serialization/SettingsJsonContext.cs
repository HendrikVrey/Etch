// System.Text.Json as well as .Serialization: JsonCommentHandling lives in the former
// while every other type named below lives in the latter. Getting this wrong is
// expensive out of all proportion: an unresolved name in a [JsonSourceGenerationOptions]
// argument makes the source generator produce nothing for the *whole* compilation, so
// every context in the assembly then fails with CS0534 for missing members it would have
// generated. One real error, four spurious ones.
using System.Text.Json;
using System.Text.Json.Serialization;
using Etch.Persistence.Model;

namespace Etch.Persistence.Serialization;

/// <summary>
/// Source-generated serialisation for <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Its own context rather than another <c>[JsonSerializable]</c> on
/// <see cref="SessionJsonContext"/>, because the two files want different options:
/// the session index writes every property so that a human debugging it sees the whole
/// shape, while settings omit nothing for the same reason but must additionally
/// <em>read</em> a file people are expected to hand-edit. Sharing one context would
/// mean one of the two silently getting the other's rules the next time either changes.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    // Hand-editing is a supported way to change these. A file where the defaults are
    // written out explicitly is one somebody can edit without first finding the docs.
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    // The file is documented as editable, so it will acquire comments and the odd
    // trailing comma. Rejecting the whole file for either would be a poor answer to
    // an invitation Etch itself extended.
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(EtchSettings))]
[JsonSerializable(typeof(UpdateState))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

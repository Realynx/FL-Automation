using System.Text.Json;

namespace FruityLink.Llm;

/// <summary>
/// The one <see cref="JsonSerializerDefaults.Web"/> options instance shared by the assembly's
/// hand-rolled (de)serialization call sites (which stay hand-rolled on purpose — the
/// System.Net.Http.Json helpers break under the FL plugin's AssemblyLoadContext; see the note in
/// <c>AccountAuthService</c>). Sharing the instance also shares its cached type metadata.
/// </summary>
internal static class LlmJson
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

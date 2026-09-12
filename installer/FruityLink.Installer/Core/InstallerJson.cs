using System.Text.Json;
using System.Text.Json.Serialization;

namespace FruityLink.Installer.Core;

/// <summary>
/// Shared serializer settings for the installer's JSON documents (compatibility list, install
/// record, manifest), so they all serialize identically.
/// </summary>
internal static class InstallerJson
{
    /// <summary>Indented, null-omitting — the compatibility list and the install record.</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Same as <see cref="Default"/> plus string enums — the manifest (PayloadKind names).</summary>
    public static readonly JsonSerializerOptions WithEnumStrings = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

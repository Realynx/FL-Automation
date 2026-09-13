using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FruityLink.Plugins.Host;

/// <summary>One atomic file per window avoids overwriting other plugins or concurrent FL sessions.</summary>
internal sealed class WindowVisibilityStore(string directory, Action<string> log)
{
    internal bool Load(string pluginId, string windowId)
    {
        try
        {
            string path = FilePath(pluginId, windowId);
            return !File.Exists(path) || JsonSerializer.Deserialize<bool>(File.ReadAllText(path));
        }
        catch (Exception error) { log("window visibility: read failed: " + error.Message); return true; }
    }

    internal void Save(string pluginId, string windowId, bool visible)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            string path = FilePath(pluginId, windowId);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(visible));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) { log("window visibility: write failed: " + error.Message); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { /* Preserve the original failure diagnostic. */ }
        }
    }

    private string FilePath(string pluginId, string windowId)
    {
        string key = JsonSerializer.Serialize(new[] { pluginId.ToUpperInvariant(), windowId });
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(directory, digest + ".json");
    }
}

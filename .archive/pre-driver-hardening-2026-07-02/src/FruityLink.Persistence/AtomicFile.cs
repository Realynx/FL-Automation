using System.Text;

namespace FruityLink.Persistence;

/// <summary>
/// Helpers for crash-safe file writes: content is written to a sibling temp file and then
/// atomically moved over the destination, so a reader never observes a half-written file.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically (temp file + move).
    /// The parent directory is created if necessary.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="contents">UTF-8 text to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, Utf8NoBom, ct).ConfigureAwait(false);
            // File.Move with overwrite is atomic on Windows for same-volume moves.
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a stray temp file is harmless.
                }
            }
        }
    }
}

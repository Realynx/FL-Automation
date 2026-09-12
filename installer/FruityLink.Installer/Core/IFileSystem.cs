using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FruityLink.Installer.Core;

/// <summary>
/// Thin file-system seam so the install engine is unit-testable and dry-run-clean. The engine
/// only ever mutates the disk through this interface; <see cref="InMemoryFileSystem"/> lets tests
/// assert backup/restore round-trips without touching a real disk.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    /// <summary>Copies a file, creating the destination directory if needed.</summary>
    void CopyFile(string source, string destination, bool overwrite);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    /// <summary>Deletes a directory only if it exists and is empty; otherwise a no-op.</summary>
    void DeleteDirectoryIfEmpty(string path);
    IEnumerable<string> EnumerateFiles(string directory, bool recursive);
    string ReadAllText(string path);
    void WriteAllText(string path, string content);
    /// <summary>True if a probe file can be created in <paramref name="directory"/>.</summary>
    bool IsDirectoryWritable(string directory);

    /// <summary>
    /// Schedules <paramref name="path"/> to be deleted on the next reboot (Win32 MoveFileEx with
    /// MOVEFILE_DELAY_UNTIL_REBOOT). Used as a last resort for a file that is still locked (loaded by
    /// a process). Returns true if the deletion was scheduled.
    /// </summary>
    bool ScheduleDeleteOnReboot(string path);

    /// <summary>
    /// Schedules a move/replace of <paramref name="source"/> onto <paramref name="destination"/> on the
    /// next reboot (MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT | MOVEFILE_REPLACE_EXISTING). Used to
    /// restore a backed-up original when the current (locked) file can't be replaced now. Returns true
    /// if the move was scheduled.
    /// </summary>
    bool ScheduleMoveOnReboot(string source, string destination);
}

/// <summary>Production implementation backed by <see cref="System.IO"/>.</summary>
public sealed class RealFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string source, string destination, bool overwrite)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(source, destination, overwrite);
    }

    public void MoveFile(string source, string destination, bool overwrite)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Move(source, destination, overwrite);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) &&
            !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, "*", option);
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    public bool IsDirectoryWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;
            var probe = Path.Combine(directory, ".flink-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Reboot-deferred file ops (for files locked by a still-loaded module) -----------------

    [Flags]
    private enum MoveFileFlags
    {
        ReplaceExisting   = 0x1,
        DelayUntilReboot  = 0x4,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    public bool ScheduleDeleteOnReboot(string path)
    {
        // A null destination + DELAY_UNTIL_REBOOT registers the path for deletion at next boot
        // (PendingFileRenameOperations under HKLM\SYSTEM — needs the admin rights the installer already has).
        return MoveFileEx(path, null, (int)MoveFileFlags.DelayUntilReboot);
    }

    public bool ScheduleMoveOnReboot(string source, string destination)
    {
        return MoveFileEx(source, destination,
            (int)(MoveFileFlags.DelayUntilReboot | MoveFileFlags.ReplaceExisting));
    }
}

/// <summary>
/// In-memory file system for tests and for the engine's own <c>--self-test</c> determinism work.
/// Paths are normalized case-insensitively with backslash separators (Windows semantics).
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>Test seam: paths recorded by <see cref="ScheduleDeleteOnReboot"/>.</summary>
    public List<string> RebootScheduledDeletes { get; } = new();

    /// <summary>Test seam: "source -> destination" entries recorded by <see cref="ScheduleMoveOnReboot"/>.</summary>
    public List<string> RebootScheduledMoves { get; } = new();

    /// <summary>Test seam: when true, reboot-scheduling "fails" (as it would for a non-admin caller).</summary>
    public bool FailRebootScheduling { get; set; }

    /// <summary>
    /// Test seam: marks a path as locked so <see cref="DeleteFile"/> / <see cref="MoveFile"/> throw
    /// (as a real loaded DLL would), exercising the retry + reboot-scheduling path in the engine.
    /// </summary>
    public void Lock(string path) => _locked.Add(Norm(path));

    public void Unlock(string path) => _locked.Remove(Norm(path));

    private static string Norm(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    private void EnsureParentDirs(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            _dirs.Add(Norm(dir));
            dir = Path.GetDirectoryName(dir);
        }
    }

    public bool FileExists(string path) => _files.ContainsKey(Norm(path));

    public bool DirectoryExists(string path) => _dirs.Contains(Norm(path));

    public void CreateDirectory(string path)
    {
        var dir = Norm(path);
        while (!string.IsNullOrEmpty(dir))
        {
            _dirs.Add(dir);
            dir = Norm(Path.GetDirectoryName(dir) ?? string.Empty);
            if (dir.Length == 0) break;
        }
    }

    public void CopyFile(string source, string destination, bool overwrite)
    {
        var src = Norm(source);
        var dst = Norm(destination);
        if (!_files.ContainsKey(src))
            throw new FileNotFoundException("InMemory source missing", source);
        if (_files.ContainsKey(dst) && !overwrite)
            throw new IOException("InMemory destination exists: " + destination);
        EnsureParentDirs(dst);
        _files[dst] = _files[src];
    }

    public void MoveFile(string source, string destination, bool overwrite)
    {
        var src = Norm(source);
        var dst = Norm(destination);
        if (!_files.ContainsKey(src))
            throw new FileNotFoundException("InMemory source missing", source);
        if (_files.ContainsKey(dst) && !overwrite)
            throw new IOException("InMemory destination exists: " + destination);
        if (_locked.Contains(src) || _locked.Contains(dst))
            throw new IOException("InMemory file locked: " + destination);
        EnsureParentDirs(dst);
        _files[dst] = _files[src];
        _files.Remove(src);
    }

    public void DeleteFile(string path)
    {
        var p = Norm(path);
        if (_locked.Contains(p))
            throw new IOException("InMemory file locked: " + path);
        _files.Remove(p);
    }

    public void DeleteDirectoryIfEmpty(string path)
    {
        var dir = Norm(path);
        var hasChildFile = _files.Keys.Any(f =>
            f.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase));
        var hasChildDir = _dirs.Any(d =>
            d.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase));
        if (!hasChildFile && !hasChildDir)
            _dirs.Remove(dir);
    }

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive)
    {
        var dir = Norm(directory) + "\\";
        foreach (var f in _files.Keys)
        {
            if (!f.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = f.Substring(dir.Length);
            if (!recursive && rest.Contains('\\')) continue;
            yield return f;
        }
    }

    public string ReadAllText(string path)
    {
        if (_files.TryGetValue(Norm(path), out var v)) return v;
        throw new FileNotFoundException("InMemory file missing", path);
    }

    public void WriteAllText(string path, string content)
    {
        var p = Norm(path);
        EnsureParentDirs(p);
        _files[p] = content;
    }

    public bool IsDirectoryWritable(string directory) => true;

    public bool ScheduleDeleteOnReboot(string path)
    {
        if (FailRebootScheduling) return false;
        // Model the OS contract: the file stays on disk until the (simulated) reboot, so we record it
        // but do NOT remove it from _files. The path stays locked-or-not as-is.
        RebootScheduledDeletes.Add(Norm(path));
        return true;
    }

    public bool ScheduleMoveOnReboot(string source, string destination)
    {
        if (FailRebootScheduling) return false;
        RebootScheduledMoves.Add(Norm(source) + " -> " + Norm(destination));
        return true;
    }
}

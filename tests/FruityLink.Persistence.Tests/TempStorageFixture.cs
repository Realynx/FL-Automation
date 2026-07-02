using FruityLink.Persistence;

namespace FruityLink.Persistence.Tests;

/// <summary>
/// Base for persistence tests: allocates a unique temp directory, builds a
/// <see cref="StoragePaths"/> over it, and recursively deletes the directory on dispose.
/// </summary>
public abstract class TempStorageFixture : IDisposable
{
    protected TempStorageFixture()
    {
        BaseDirectory = Path.Combine(Path.GetTempPath(), "FruityLinkTests", Guid.NewGuid().ToString("N"));
        Paths = new StoragePaths(BaseDirectory);
    }

    /// <summary>Root temp directory backing this test's storage.</summary>
    protected string BaseDirectory { get; }

    /// <summary>Storage layout injected into the stores under test.</summary>
    protected StoragePaths Paths { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(BaseDirectory))
            {
                Directory.Delete(BaseDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }

        GC.SuppressFinalize(this);
    }
}

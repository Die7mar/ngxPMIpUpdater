namespace ngxPMIpUpdater.Tests;

/// <summary>A unique scratch directory that is removed when the test finishes.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ngxPMIpUpdater-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

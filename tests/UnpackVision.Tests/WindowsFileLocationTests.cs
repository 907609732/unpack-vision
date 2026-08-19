using UnpackVision.App;

namespace UnpackVision.Tests;

public sealed class WindowsFileLocationTests : IDisposable
{
    private readonly string _temporaryRoot =
        Path.Combine(Path.GetTempPath(), $"UnpackVisionFileLocation-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveDistinguishesMissingConfigurationAndInvalidPath()
    {
        var missing = WindowsFileLocation.Resolve(" ");
        var invalid = WindowsFileLocation.Resolve("\0invalid");

        Assert.Equal(WindowsFileLocationState.NotConfigured, missing.State);
        Assert.Equal(WindowsFileLocationState.InvalidPath, invalid.State);
    }

    [Fact]
    public void ResolveSelectsExistingWorkbook()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var workbook = Path.Combine(_temporaryRoot, "records.xlsx");
        File.WriteAllText(workbook, "test");

        var result = WindowsFileLocation.Resolve(workbook);

        Assert.Equal(WindowsFileLocationState.FileAvailable, result.State);
        Assert.Equal(Path.GetFullPath(workbook), result.FullPath);
        Assert.Equal(Path.GetFullPath(_temporaryRoot), result.DirectoryPath);
    }

    [Fact]
    public void ResolveFallsBackToExistingDirectoryWhenWorkbookIsMissing()
    {
        Directory.CreateDirectory(_temporaryRoot);

        var result = WindowsFileLocation.Resolve(Path.Combine(_temporaryRoot, "missing.xlsx"));

        Assert.Equal(WindowsFileLocationState.DirectoryAvailable, result.State);
        Assert.Equal(Path.GetFullPath(_temporaryRoot), result.DirectoryPath);
    }

    [Fact]
    public void ResolveReportsMissingParentDirectory()
    {
        var result = WindowsFileLocation.Resolve(
            Path.Combine(_temporaryRoot, "missing", "records.xlsx"));

        Assert.Equal(WindowsFileLocationState.MissingDirectory, result.State);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, true);
        }
        GC.SuppressFinalize(this);
    }
}

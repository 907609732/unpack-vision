using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class UninstallCleanupServiceTests : IDisposable
{
    private readonly string _temporaryRoot =
        Path.Combine(Path.GetTempPath(), $"UnpackVision-Uninstall-{Guid.NewGuid():N}");

    [Fact]
    public async Task NormalUninstallPlanPreservesAllBusinessData()
    {
        var fixture = await CreateFixtureAsync();

        await UninstallCleanupService.PrepareAsync(
            fixture.Settings,
            deleteBusinessData: false,
            fixture.PlanPath,
            fixture.LocalDataRoot);
        var result = UninstallCleanupService.ExecutePendingPlan(fixture.PlanPath, fixture.LocalDataRoot);

        Assert.True(result.Completed);
        Assert.True(File.Exists(Path.Combine(fixture.LocalDataRoot, "unpackvision.db")));
        Assert.True(File.Exists(Path.Combine(fixture.RecordingRoot, "Unpacking", "sample.mp4")));
        Assert.True(File.Exists(fixture.ExcelPath));
        Assert.False(File.Exists(fixture.PlanPath));
    }

    [Fact]
    public async Task ConfirmedCleanupDeletesOnlyOwnedDataAndPreservesExcelAndOtherFiles()
    {
        var fixture = await CreateFixtureAsync(excelInsideOwnedRecordingDirectory: true);
        var unrelated = Path.Combine(fixture.RecordingRoot, "merchant-note.txt");
        await File.WriteAllTextAsync(unrelated, "keep");

        await UninstallCleanupService.PrepareAsync(
            fixture.Settings,
            deleteBusinessData: true,
            fixture.PlanPath,
            fixture.LocalDataRoot);
        var result = UninstallCleanupService.ExecutePendingPlan(fixture.PlanPath, fixture.LocalDataRoot);

        Assert.True(result.Completed, string.Join(Environment.NewLine, result.Errors));
        Assert.False(Directory.Exists(fixture.LocalDataRoot));
        Assert.False(File.Exists(Path.Combine(fixture.RecordingRoot, "Unpacking", "sample.mp4")));
        Assert.False(Directory.Exists(Path.Combine(fixture.RecordingRoot, ".unpackvision")));
        Assert.True(File.Exists(fixture.ExcelPath));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task CleanupRefusesRecordingDirectoryWithDifferentWorkspaceIdentity()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Settings.Setup.WorkspaceId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UninstallCleanupService.PrepareAsync(
                fixture.Settings,
                deleteBusinessData: true,
                fixture.PlanPath,
                fixture.LocalDataRoot));

        Assert.Contains("工作区", exception.Message);
        Assert.True(File.Exists(Path.Combine(fixture.RecordingRoot, "Unpacking", "sample.mp4")));
        Assert.False(File.Exists(fixture.PlanPath));
    }

    [Fact]
    public async Task ConfirmedCleanupPreservesWorkbookEvenWhenStoredInsideLocalDataDirectory()
    {
        var fixture = await CreateFixtureAsync(excelInsideLocalDataDirectory: true);

        await UninstallCleanupService.PrepareAsync(
            fixture.Settings,
            deleteBusinessData: true,
            fixture.PlanPath,
            fixture.LocalDataRoot);
        var result = UninstallCleanupService.ExecutePendingPlan(fixture.PlanPath, fixture.LocalDataRoot);

        Assert.True(result.Completed, string.Join(Environment.NewLine, result.Errors));
        Assert.True(File.Exists(fixture.ExcelPath));
        Assert.False(File.Exists(Path.Combine(fixture.LocalDataRoot, "unpackvision.db")));
    }

    private async Task<Fixture> CreateFixtureAsync(
        bool excelInsideOwnedRecordingDirectory = false,
        bool excelInsideLocalDataDirectory = false)
    {
        var localDataRoot = Path.Combine(_temporaryRoot, "local-data", "UnpackVision");
        var recordingRoot = Path.Combine(_temporaryRoot, "recordings", "MyWorkspace");
        var workspaceId = Guid.NewGuid();
        var unpacking = Path.Combine(recordingRoot, "Unpacking");
        var catalog = Path.Combine(recordingRoot, ".unpackvision");
        Directory.CreateDirectory(localDataRoot);
        Directory.CreateDirectory(unpacking);
        Directory.CreateDirectory(catalog);
        await File.WriteAllTextAsync(Path.Combine(localDataRoot, "unpackvision.db"), "database");
        await File.WriteAllTextAsync(Path.Combine(unpacking, "sample.mp4"), "video");
        await File.WriteAllTextAsync(
            Path.Combine(catalog, "workspace.json"),
            $$"""{"workspaceId":"{{workspaceId}}"}""");
        var excelPath = excelInsideOwnedRecordingDirectory
            ? Path.Combine(unpacking, "records.xlsx")
            : excelInsideLocalDataDirectory
                ? Path.Combine(localDataRoot, "records.xlsx")
                : Path.Combine(_temporaryRoot, "records.xlsx");
        await File.WriteAllTextAsync(excelPath, "excel");

        var settings = new LocalSettings
        {
            RecordingRoot = recordingRoot,
            ExcelWorkbookPath = excelPath,
            Setup = new SetupState
            {
                Version = SetupState.CurrentVersion,
                WorkspaceId = workspaceId,
                CompletedAt = DateTimeOffset.Now
            }
        };
        return new Fixture(
            settings,
            localDataRoot,
            recordingRoot,
            excelPath,
            Path.Combine(_temporaryRoot, "plan", "pending.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, true);
    }

    private sealed record Fixture(
        LocalSettings Settings,
        string LocalDataRoot,
        string RecordingRoot,
        string ExcelPath,
        string PlanPath);
}

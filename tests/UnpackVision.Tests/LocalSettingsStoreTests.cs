using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class LocalSettingsStoreTests : IDisposable
{
    private readonly string _temporaryRoot =
        Path.Combine(Path.GetTempPath(), $"UnpackVisionSettings-{Guid.NewGuid():N}");

    [Fact]
    public async Task LegacyDefaultRecordingTimeoutMigratesToFiveMinutesOnlyOnce()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var path = Path.Combine(_temporaryRoot, "recording-timeout.json");
        await File.WriteAllTextAsync(path, "{ \"maximumRecordingMinutes\": 30 }");

        var store = new LocalSettingsStore(path);
        var migrated = await store.LoadAsync();
        Assert.Equal(5, migrated.MaximumRecordingMinutes);
        Assert.Equal(LocalSettings.CurrentRecordingTimeoutDefaultVersion, migrated.RecordingTimeoutDefaultVersion);
        var persisted = await File.ReadAllTextAsync(path);
        Assert.Contains("\"maximumRecordingMinutes\": 5", persisted, StringComparison.Ordinal);
        Assert.Contains("\"recordingTimeoutDefaultVersion\": 1", persisted, StringComparison.Ordinal);

        migrated.MaximumRecordingMinutes = 30;
        await store.SaveAsync(migrated);
        var userChoice = await store.LoadAsync();

        Assert.Equal(30, userChoice.MaximumRecordingMinutes);
    }

    [Fact]
    public async Task NewSettingsUseFiveMinuteRecordingTimeoutByDefault()
    {
        var settings = await new LocalSettingsStore(Path.Combine(_temporaryRoot, "new-settings.json")).LoadAsync();

        Assert.Equal(5, settings.MaximumRecordingMinutes);
    }

    [Fact]
    public async Task LegacyIssueTagCatalogAddsOnlyNewTagsAndPreservesCustomDefinitions()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var path = Path.Combine(_temporaryRoot, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "issueTags": [
                {
                  "id": "DAMAGE01",
                  "name": "破损-自定义",
                  "colorHex": "#123456",
                  "barcodeValue": "CUSTOM-DAMAGE",
                  "enabled": false,
                  "sortOrder": 7
                },
                {
                  "id": "CUSTOM01",
                  "name": "客户自定义",
                  "colorHex": "#654321",
                  "barcodeValue": "CUSTOM-01",
                  "enabled": true,
                  "sortOrder": 9
                }
              ]
            }
            """);

        var settings = await new LocalSettingsStore(path).LoadAsync();

        Assert.Equal(IssueTagDefaults.CurrentCatalogVersion, settings.IssueTagCatalogVersion);
        Assert.Equal(4, settings.IssueTags.Count);
        var customized = Assert.Single(settings.IssueTags, tag => tag.Id == "DAMAGE01");
        Assert.Equal("破损-自定义", customized.Name);
        Assert.Equal("#123456", customized.ColorHex);
        Assert.Equal("CUSTOM-DAMAGE", customized.BarcodeValue);
        Assert.False(customized.Enabled);
        Assert.DoesNotContain(settings.IssueTags, tag => tag.Id == "SWAPPED1");
        Assert.Contains(settings.IssueTags, tag => tag.Id == IssueTagDefaults.MissingTagId);
        Assert.Contains(settings.IssueTags, tag => tag.Id == IssueTagDefaults.PurchaseTagId);
    }

    [Fact]
    public async Task CurrentCatalogDoesNotRestoreAUserDeletedTag()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var path = Path.Combine(_temporaryRoot, "settings.json");
        var store = new LocalSettingsStore(path);
        var settings = new LocalSettings
        {
            IssueTagCatalogVersion = IssueTagDefaults.CurrentCatalogVersion,
            IssueTags = IssueTagDefaults.Create()
                .Where(tag => tag.Id != IssueTagDefaults.PurchaseTagId)
                .ToList()
        };
        await store.SaveAsync(settings);

        var loaded = await store.LoadAsync();

        Assert.DoesNotContain(loaded.IssueTags, tag => tag.Id == IssueTagDefaults.PurchaseTagId);
    }

    [Fact]
    public async Task LegacyCatalogUpgradeIsIdempotent()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var path = Path.Combine(_temporaryRoot, "settings.json");
        var store = new LocalSettingsStore(path);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "issueTags": [
                {
                  "id": "DAMAGE01",
                  "name": "破损",
                  "colorHex": "#FF3B30",
                  "barcodeValue": "UV-TAG-DAMAGE01",
                  "enabled": true,
                  "sortOrder": 0
                },
                {
                  "id": "SWAPPED1",
                  "name": "调包",
                  "colorHex": "#AF52DE",
                  "barcodeValue": "UV-TAG-SWAPPED1",
                  "enabled": true,
                  "sortOrder": 1
                }
              ]
            }
            """);

        var first = await store.LoadAsync();
        await store.SaveAsync(first);
        var second = await store.LoadAsync();

        Assert.Equal(4, second.IssueTags.Count);
        Assert.Equal(4, second.IssueTags.Select(tag => tag.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task LegacySingleCameraMigratesToPrimaryRigAndKeepsStableIdentity()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var path = Path.Combine(_temporaryRoot, "camera-settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "camera": {
                "sourceKind": 1,
                "cameraIndex": 2,
                "windowsSymbolicLink": "device://stable-camera",
                "autoSelectBestCamera": false,
                "width": 1920,
                "height": 1080,
                "framesPerSecond": 15
              }
            }
            """);

        var settings = await new LocalSettingsStore(path).LoadAsync();

        var primary = Assert.Single(settings.CameraRig.EnabledCameras);
        Assert.True(primary.IsPrimary);
        Assert.Equal(CameraSourceType.WindowsCamera, primary.SourceType);
        Assert.Equal("device://stable-camera", primary.WindowsSymbolicLink);
        Assert.Equal(2, primary.LegacyCameraIndex);
        Assert.Equal(1920, primary.Width);
        Assert.Equal(80, primary.HikvisionHttpPort);
        Assert.Equal("device://stable-camera", settings.Camera.WindowsSymbolicLink);
        Assert.Equal(CameraRigMode.SingleCamera, settings.CameraRig.Mode);
    }

    [Fact]
    public async Task LegacyMultiCameraRigStaysMultiCameraAfterSchemaMigration()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var path = Path.Combine(_temporaryRoot, "legacy-multi-camera.json");
        await File.WriteAllTextAsync(path, """
            {
              "cameraRig": {
                "schemaVersion": 1,
                "cameras": [
                  { "id": "primary", "displayName": "Primary", "enabled": true, "isPrimary": true, "sortOrder": 0 },
                  { "id": "side", "displayName": "Side", "enabled": true, "isPrimary": false, "sortOrder": 1 }
                ]
              }
            }
            """);

        var settings = await new LocalSettingsStore(path).LoadAsync();

        Assert.Equal(CameraRigMode.MultiCamera, settings.CameraRig.Mode);
        Assert.Equal(2, settings.CameraRig.EnabledCameras.Count);
        Assert.Equal(CameraRigOptions.CurrentSchemaVersion, settings.CameraRig.SchemaVersion);
    }

    [Fact]
    public void SingleCameraModeListsSavedSourcesAndCanPromoteAnotherPrimary()
    {
        var rig = new CameraRigOptions
        {
            Mode = CameraRigMode.SingleCamera,
            Cameras =
            [
                new CameraProfile { Id = "front", DisplayName = "Front", Enabled = true, IsPrimary = true, SortOrder = 0 },
                new CameraProfile { Id = "side", DisplayName = "Side", Enabled = true, SortOrder = 1 },
                new CameraProfile { Id = "disabled", DisplayName = "Disabled", Enabled = false, SortOrder = 2 }
            ]
        };

        Assert.Equal(["front", "side"], rig.GetSelectableCameras().Select(camera => camera.Id));
        Assert.Equal("front", Assert.Single(rig.EnabledCameras).Id);

        Assert.True(rig.TrySelectSingleCamera("SIDE"));
        Assert.Equal("side", Assert.Single(rig.EnabledCameras).Id);
        Assert.Equal("side", Assert.Single(rig.Cameras, camera => camera.IsPrimary).Id);
        Assert.False(rig.TrySelectSingleCamera("disabled"));
    }

    [Fact]
    public async Task PreviewLayoutDefaultsToActiveCameraCountAndPersistsAssignments()
    {
        var path = Path.Combine(_temporaryRoot, "preview-layout.json");
        var store = new LocalSettingsStore(path);
        var settings = new LocalSettings
        {
            CameraRig = new CameraRigOptions
            {
                Mode = CameraRigMode.MultiCamera,
                Cameras =
                [
                    new CameraProfile { Id = "primary", Enabled = true, IsPrimary = true, SortOrder = 0 },
                    new CameraProfile { Id = "side", Enabled = true, SortOrder = 1 }
                ]
            },
            PreviewLayout = new CameraPreviewLayoutSettings()
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.PreviewLayout.ViewCount);
        Assert.Equal(["primary", "side"], loaded.PreviewLayout.CameraIds);

        loaded.PreviewLayout.ViewCount = 4;
        loaded.PreviewLayout.CameraIds = ["side", "primary", "", ""];
        await store.SaveAsync(loaded);
        var persisted = await store.LoadAsync();

        Assert.Equal(4, persisted.PreviewLayout.ViewCount);
        Assert.Equal(["side", "primary", "", ""], persisted.PreviewLayout.CameraIds);
    }

    [Fact]
    public async Task PreviewLayoutRemovesUnknownAndDuplicateCameraAssignments()
    {
        var path = Path.Combine(_temporaryRoot, "preview-layout-invalid.json");
        var store = new LocalSettingsStore(path);
        var settings = new LocalSettings
        {
            PreviewLayout = new CameraPreviewLayoutSettings
            {
                ViewCount = 4,
                CameraIds = ["missing", "duplicate", "duplicate", ""]
            }
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(4, loaded.PreviewLayout.ViewCount);
        Assert.Equal(1, loaded.PreviewLayout.CameraIds.Count(id => !string.IsNullOrWhiteSpace(id)));
        Assert.Equal(3, loaded.PreviewLayout.CameraIds.Count(string.IsNullOrWhiteSpace));
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

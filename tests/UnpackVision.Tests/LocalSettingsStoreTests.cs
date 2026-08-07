using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class LocalSettingsStoreTests : IDisposable
{
    private readonly string _temporaryRoot =
        Path.Combine(Path.GetTempPath(), $"UnpackVisionSettings-{Guid.NewGuid():N}");

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

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, true);
        }
        GC.SuppressFinalize(this);
    }
}

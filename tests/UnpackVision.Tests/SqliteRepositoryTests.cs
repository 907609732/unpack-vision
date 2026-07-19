using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class SqliteRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionSqlite-{Guid.NewGuid():N}");

    [Fact]
    public async Task PersistsTrackingAsTextAndClaimsDeliveryOnce()
    {
        var repository = new SqliteScanRecordRepository(new StorageOptions
        {
            DatabasePath = Path.Combine(_root, "records.db")
        });
        await repository.InitializeAsync();
        var now = DateTimeOffset.Now;
        var record = new ScanRecord
        {
            TrackingNo = "00123-ABC",
            State = RecordingState.Completed,
            ScannedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.AddAsync(record);
        await repository.EnqueueDeliveryAsync(record.Id, "excel");

        var loaded = await repository.GetAsync(record.Id);
        var delivery = Assert.Single(await repository.GetDueDeliveriesAsync(10, now.AddMinutes(1)));

        Assert.Equal("00123-ABC", loaded?.TrackingNo);
        Assert.True(await repository.TryClaimDeliveryAsync(delivery.Id));
        Assert.False(await repository.TryClaimDeliveryAsync(delivery.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
        GC.SuppressFinalize(this);
    }
}

using Microsoft.Data.Sqlite;
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
        var deliveries = await repository.GetLatestDeliveriesAsync(
            [record.Id, Guid.NewGuid()],
            "excel");

        Assert.Equal("00123-ABC", loaded?.TrackingNo);
        Assert.Equal(delivery.Id, Assert.Single(deliveries).Value.Id);
        Assert.True(await repository.TryClaimDeliveryAsync(delivery.Id));
        Assert.False(await repository.TryClaimDeliveryAsync(delivery.Id));
    }

    [Fact]
    public async Task BatchDeleteHidesRecordsCancelsDeliveriesAndKeepsImportTombstone()
    {
        var repository = new SqliteScanRecordRepository(new StorageOptions
        {
            DatabasePath = Path.Combine(_root, "delete-records.db")
        });
        await repository.InitializeAsync();
        var now = DateTimeOffset.Now;
        var deleted = new ScanRecord
        {
            TrackingNo = "DELETE-001",
            State = RecordingState.Completed,
            ScannedAt = now,
            VideoPath = Path.Combine(_root, "delete-001.mp4"),
            CreatedAt = now,
            UpdatedAt = now
        };
        var retained = new ScanRecord
        {
            TrackingNo = "KEEP-001",
            State = RecordingState.Completed,
            ScannedAt = now.AddSeconds(1),
            DuplicateOf = deleted.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.AddAsync(deleted);
        await repository.AddAsync(retained);
        await repository.EnqueueDeliveryAsync(deleted.Id, "excel");

        var affected = await repository.DeleteManyAsync([deleted.Id]);

        Assert.Equal(1, affected);
        Assert.Null(await repository.GetAsync(deleted.Id));
        Assert.Null(await repository.GetDeliveryAsync(deleted.Id, "excel"));
        Assert.Equal("KEEP-001", Assert.Single(await repository.QueryAsync()).TrackingNo);
        Assert.Null((await repository.GetAsync(retained.Id))?.DuplicateOf);
        Assert.Equal(deleted.Id, (await repository.FindByVideoPathAsync(deleted.VideoPath))?.Id);
        Assert.Equal(0, await repository.DeleteManyAsync([deleted.Id]));
    }

    [Fact]
    public async Task InitializeMigratesDatabaseWithoutDeletedAtColumn()
    {
        var databasePath = Path.Combine(_root, "legacy-records.db");
        var repository = new SqliteScanRecordRepository(new StorageOptions { DatabasePath = databasePath });
        await repository.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var removeColumn = connection.CreateCommand();
            removeColumn.CommandText = "ALTER TABLE scan_records DROP COLUMN deleted_at;";
            await removeColumn.ExecuteNonQueryAsync();
        }

        await repository.InitializeAsync();

        await using var verifyConnection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await verifyConnection.OpenAsync();
        await using var inspect = verifyConnection.CreateCommand();
        inspect.CommandText = "SELECT COUNT(*) FROM pragma_table_info('scan_records') WHERE name='deleted_at';";
        Assert.Equal(1L, (long)(await inspect.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PersistsNotesAndIdempotentTagsAndSupportsRemoval()
    {
        var repository = new SqliteScanRecordRepository(new StorageOptions { DatabasePath = Path.Combine(_root, "annotations.db") });
        await repository.InitializeAsync();
        var now = DateTimeOffset.Now;
        var record = new ScanRecord { TrackingNo = "SF1234567890", State = RecordingState.Recording, ScannedAt = now, CreatedAt = now, UpdatedAt = now };
        await repository.AddAsync(record);
        var definition = IssueTagDefaults.Create()[0];

        var first = await repository.AddTagAsync(record.Id, definition, now);
        var duplicate = await repository.AddTagAsync(record.Id, definition, now.AddSeconds(1));
        await repository.UpdateNoteAsync(record.Id, "外箱破裂", now.AddSeconds(2));

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(await repository.GetTagsAsync(record.Id));
        Assert.Equal("外箱破裂", (await repository.GetAsync(record.Id))?.Note);
        Assert.NotNull(await repository.RemoveTagAsync(record.Id, first.Id, now.AddSeconds(3)));
        Assert.Empty(await repository.GetTagsAsync(record.Id));
        Assert.Single(await repository.GetTagsAsync(record.Id, includeRemoved: true));
    }

    [Fact]
    public async Task QueryPageUsesStableOrderOffsetAndTrackingFilter()
    {
        var repository = new SqliteScanRecordRepository(new StorageOptions { DatabasePath = Path.Combine(_root, "paging.db") });
        await repository.InitializeAsync();
        var now = DateTimeOffset.Now;
        for (var index = 0; index < 5; index++)
        {
            await repository.AddAsync(new ScanRecord
            {
                TrackingNo = index == 2 ? "SF-MATCH-002" : $"YT-{index:000}",
                State = RecordingState.Completed,
                ScannedAt = now.AddSeconds(index),
                CreatedAt = now.AddSeconds(index),
                UpdatedAt = now.AddSeconds(index)
            });
        }

        var firstPage = await repository.QueryPageAsync(null, 0, 2);
        var secondPage = await repository.QueryPageAsync(null, 2, 2);
        var filtered = await repository.QueryPageAsync("MATCH", 0, 10);

        Assert.Equal(["YT-004", "YT-003"], firstPage.Select(item => item.TrackingNo));
        Assert.Equal(["SF-MATCH-002", "YT-001"], secondPage.Select(item => item.TrackingNo));
        Assert.Equal("SF-MATCH-002", Assert.Single(filtered).TrackingNo);
    }

    [Fact]
    public async Task LatestDeliveriesLoadsHistoryStatusInOneBatch()
    {
        var repository = new SqliteScanRecordRepository(new StorageOptions
        {
            DatabasePath = Path.Combine(_root, "delivery-batch.db")
        });
        await repository.InitializeAsync();
        var now = DateTimeOffset.Now;
        var records = Enumerable.Range(0, 120)
            .Select(index => new ScanRecord
            {
                TrackingNo = $"BATCH-{index:000}",
                State = RecordingState.Completed,
                ScannedAt = now.AddSeconds(index),
                CreatedAt = now.AddSeconds(index),
                UpdatedAt = now.AddSeconds(index)
            })
            .ToArray();
        foreach (var record in records)
        {
            await repository.AddAsync(record);
            await repository.EnqueueDeliveryAsync(record.Id, "excel");
        }

        var deliveries = await repository.GetLatestDeliveriesAsync(
            records.Select(record => record.Id).ToArray(),
            "excel");

        Assert.Equal(records.Length, deliveries.Count);
        Assert.All(records, record => Assert.Equal(record.Id, deliveries[record.Id].RecordId));
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

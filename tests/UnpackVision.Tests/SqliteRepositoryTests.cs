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

    [Fact]
    public async Task PersistsMultipleMediaAssetsGapsAndDefaultPlaybackAsset()
    {
        var repository = new SqliteScanRecordRepository(new StorageOptions
        {
            DatabasePath = Path.Combine(_root, "multi-media.db")
        });
        await repository.InitializeAsync();
        var now = DateTimeOffset.Now;
        var record = new ScanRecord
        {
            TrackingNo = "MULTI-001",
            State = RecordingState.Recording,
            ScannedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.AddAsync(record);
        var primary = new RecordMediaAsset
        {
            RecordId = record.Id,
            CameraId = "front",
            DisplayName = "主机位",
            Role = RecordMediaRole.Primary,
            VideoPath = Path.Combine(_root, "primary.mp4"),
            StorageTargetId = "drive-a",
            RelativeVideoPath = "Unpacking/primary.mp4",
            Codec = "H264",
            EncoderName = "qsvh264enc",
            HardwareAccelerated = true,
            WatermarkBurnedIn = true,
            Width = 3840,
            Height = 2160,
            FramesPerSecond = 15,
            Duration = TimeSpan.FromSeconds(63),
            CreatedAt = now,
            UpdatedAt = now
        };
        primary.Segments =
        [
            new RecordMediaSegment
            {
                MediaAssetId = primary.Id,
                Sequence = 0,
                StorageTargetId = primary.StorageTargetId,
                VideoPath = Path.Combine(_root, "primary-000.mp4"),
                StartOffset = TimeSpan.Zero,
                Duration = TimeSpan.FromSeconds(30),
                Recovered = true,
                CreatedAt = now
            }
        ];
        var composite = new RecordMediaAsset
        {
            RecordId = record.Id,
            CameraId = "composite",
            DisplayName = "多机位合成",
            Role = RecordMediaRole.Composite,
            VideoPath = Path.Combine(_root, "composite.mp4"),
            Width = 1920,
            Height = 1080,
            FramesPerSecond = 15,
            Integrity = MediaIntegrityStatus.Partial,
            CreatedAt = now,
            UpdatedAt = now
        };
        record.State = RecordingState.Completed;
        record.VideoPath = primary.VideoPath;
        record.CameraId = primary.CameraId;
        record.MediaIntegrity = MediaIntegrityStatus.Partial;
        record.DefaultMediaAssetId = composite.Id;
        record.MediaAssets = [primary, composite];
        record.MediaGaps = [new MediaGap
        {
            RecordId = record.Id,
            MediaAssetId = composite.Id,
            CameraId = "side",
            StartedAt = now.AddSeconds(5),
            EndedAt = now.AddSeconds(8),
            Recovered = true,
            Reason = "测试中断"
        }];
        await repository.CompleteAndEnqueueAsync(record, "excel");

        var loaded = Assert.IsType<ScanRecord>(await repository.GetAsync(record.Id));
        Assert.Equal(MediaIntegrityStatus.Partial, loaded.MediaIntegrity);
        Assert.Equal(composite.Id, loaded.DefaultMediaAssetId);
        Assert.Equal(2, loaded.MediaAssets.Count);
        Assert.Single(loaded.MediaGaps);
        var loadedPrimary = Assert.IsType<RecordMediaAsset>(
            await repository.GetMediaAssetAsync(record.Id, primary.Id));
        Assert.Equal("drive-a", loadedPrimary.StorageTargetId);
        Assert.Equal("Unpacking/primary.mp4", loadedPrimary.RelativeVideoPath);
        Assert.Equal("H264", loadedPrimary.Codec);
        Assert.Equal("qsvh264enc", loadedPrimary.EncoderName);
        Assert.True(loadedPrimary.HardwareAccelerated);
        Assert.True(loadedPrimary.WatermarkBurnedIn);
        Assert.Equal(TimeSpan.FromSeconds(63), loadedPrimary.Duration);
        var loadedSegment = Assert.Single(loadedPrimary.Segments);
        Assert.Equal(0, loadedSegment.Sequence);
        Assert.Equal(TimeSpan.FromSeconds(30), loadedSegment.Duration);
        Assert.True(loadedSegment.Recovered);
        Assert.Equal(composite.VideoPath, (await repository.GetMediaAssetAsync(record.Id, composite.Id))?.VideoPath);

        await repository.InitializeAsync();
        Assert.Equal(2, (await repository.GetMediaAssetsAsync(record.Id)).Count);
    }

    [Fact]
    public async Task InitializeIdempotentlyMigratesLegacyMediaAssetSchemaAndCreatesSegments()
    {
        var databasePath = Path.Combine(_root, "legacy-media.db");
        var repository = new SqliteScanRecordRepository(new StorageOptions { DatabasePath = databasePath });
        await repository.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            foreach (var column in new[]
                     {
                         "storage_target_id", "relative_video_path", "codec", "encoder_name",
                         "hardware_accelerated", "watermark_burned_in", "duration_ms"
                     })
            {
                await using var dropColumn = connection.CreateCommand();
                dropColumn.CommandText = $"ALTER TABLE record_media_assets DROP COLUMN {column};";
                await dropColumn.ExecuteNonQueryAsync();
            }
            await using var dropSegments = connection.CreateCommand();
            dropSegments.CommandText = "DROP TABLE record_media_segments;";
            await dropSegments.ExecuteNonQueryAsync();
        }

        await repository.InitializeAsync();
        await repository.InitializeAsync();

        await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await verify.OpenAsync();
        await using var inspect = verify.CreateCommand();
        inspect.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM pragma_table_info('record_media_assets')
                    WHERE name IN ('storage_target_id','relative_video_path','codec','encoder_name',
                                   'hardware_accelerated','watermark_burned_in','duration_ms')),
                EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='record_media_segments');
            """;
        await using var reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task RebaseOwnedPaths_UpdatesAllVerifiedMediaColumnsAtomicallyAndLeavesExternalPaths()
    {
        var databasePath = Path.Combine(_root, "migration-paths.db");
        var repository = new SqliteScanRecordRepository(new StorageOptions { DatabasePath = databasePath });
        await repository.InitializeAsync();
        var source = Path.Combine(_root, "old");
        var target = Path.Combine(_root, "new");
        var primary = Path.Combine(source, "Unpacking", "record.mp4");
        var snapshot = Path.Combine(source, "Snapshots", "photo.jpg");
        var segment = Path.Combine(source, "Unpacking", "segment.mp4");
        var recovery = Path.Combine(source, "Unpacking", "recovered.mp4");
        var external = Path.Combine(_root, "hik", "external.mp4");
        var now = DateTimeOffset.Now;
        var record = new ScanRecord
        {
            TrackingNo = "TEST-MIGRATION-001",
            State = RecordingState.Completed,
            ScannedAt = now,
            VideoPath = primary,
            Snapshots = [snapshot, external],
            CreatedAt = now,
            UpdatedAt = now
        };
        var asset = new RecordMediaAsset
        {
            RecordId = record.Id,
            CameraId = "primary",
            DisplayName = "主机位",
            Role = RecordMediaRole.Primary,
            VideoPath = primary,
            CreatedAt = now,
            UpdatedAt = now
        };
        asset.Segments =
        [
            new RecordMediaSegment
            {
                MediaAssetId = asset.Id,
                VideoPath = segment,
                CreatedAt = now
            }
        ];
        record.MediaAssets = [asset];
        record.MediaGaps =
        [
            new MediaGap
            {
                RecordId = record.Id,
                MediaAssetId = asset.Id,
                CameraId = "primary",
                StartedAt = now,
                EndedAt = now,
                Recovered = true,
                RecoveryPath = recovery,
                Reason = "test"
            }
        ];
        await repository.AddAsync(record);
        await repository.UpdateAsync(record);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [primary] = Path.Combine(target, "Unpacking", "record.mp4"),
            [snapshot] = Path.Combine(target, "Snapshots", "photo.jpg"),
            [segment] = Path.Combine(target, "Unpacking", "segment.mp4"),
            [recovery] = Path.Combine(target, "Unpacking", "recovered.mp4")
        };
        var first = await repository.RebaseOwnedPathsAsync(map);
        var second = await repository.RebaseOwnedPathsAsync(map);
        var restored = await repository.GetAsync(record.Id);

        Assert.NotNull(restored);
        Assert.True(first >= 4);
        Assert.Equal(0, second);
        Assert.Equal(map[primary], restored!.VideoPath);
        Assert.Equal(map[snapshot], restored.Snapshots[0]);
        Assert.Equal(external, restored.Snapshots[1]);
        Assert.Equal(map[primary], Assert.Single(restored.MediaAssets).VideoPath);
        Assert.Equal(map[segment], Assert.Single(restored.MediaAssets[0].Segments).VideoPath);
        Assert.Equal(map[recovery], Assert.Single(restored.MediaGaps).RecoveryPath);
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

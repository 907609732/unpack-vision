using System.Globalization;
using Microsoft.Data.Sqlite;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class SqliteScanRecordRepository : IScanRecordRepository
{
    private readonly string _connectionString;

    public SqliteScanRecordRepository(StorageOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        var fullPath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;

            CREATE TABLE IF NOT EXISTS scan_records (
                id TEXT PRIMARY KEY,
                tracking_no TEXT NOT NULL,
                workflow TEXT NOT NULL,
                state TEXT NOT NULL,
                scanned_at TEXT NOT NULL,
                recording_started_at TEXT NULL,
                recording_ended_at TEXT NULL,
                video_path TEXT NULL,
                snapshots_json TEXT NOT NULL,
                camera_id TEXT NULL,
                station_id TEXT NOT NULL,
                duplicate_of TEXT NULL,
                platform_match_status TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT '',
                note_updated_at TEXT NULL,
                failure_reason TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_scan_records_tracking_no
                ON scan_records(tracking_no);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_scan_records_video_path
                ON scan_records(video_path) WHERE video_path IS NOT NULL;

            CREATE TABLE IF NOT EXISTS sync_deliveries (
                id TEXT PRIMARY KEY,
                record_id TEXT NOT NULL,
                connector_id TEXT NOT NULL,
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                external_id TEXT NULL,
                last_error TEXT NULL,
                next_retry_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(record_id) REFERENCES scan_records(id) ON DELETE CASCADE,
                UNIQUE(record_id, connector_id)
            );

            CREATE INDEX IF NOT EXISTS ix_sync_deliveries_due
                ON sync_deliveries(status, next_retry_at);

            CREATE TABLE IF NOT EXISTS record_tags (
                id TEXT PRIMARY KEY,
                record_id TEXT NOT NULL,
                tag_id TEXT NOT NULL,
                tag_name TEXT NOT NULL,
                color_hex TEXT NOT NULL,
                tagged_at TEXT NOT NULL,
                removed_at TEXT NULL,
                source TEXT NOT NULL,
                FOREIGN KEY(record_id) REFERENCES scan_records(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_record_tags_record
                ON record_tags(record_id, tagged_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_record_tags_active
                ON record_tags(record_id, tag_id) WHERE removed_at IS NULL;

            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureDeletedAtColumnAsync(connection, cancellationToken);
        await EnsureColumnAsync(connection, "scan_records", "note", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "scan_records", "note_updated_at", "TEXT NULL", cancellationToken);
    }

    public async Task AddAsync(ScanRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateInsertCommand(connection, null, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddImportedAsync(
        ScanRecord record,
        string? connectorId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = CreateInsertCommand(connection, transaction, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(connectorId))
        {
            await InsertDeliveryAsync(connection, transaction, record.Id, connectorId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteAndEnqueueAsync(
        ScanRecord record,
        string connectorId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = CreateUpdateCommand(connection, transaction, record);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException($"扫描记录 {record.Id} 不存在");
        }
        await InsertDeliveryAsync(connection, transaction, record.Id, connectorId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static SqliteCommand CreateInsertCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ScanRecord record)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_records (
                id, tracking_no, workflow, state, scanned_at,
                recording_started_at, recording_ended_at, video_path,
                snapshots_json, camera_id, station_id, duplicate_of,
                platform_match_status, note, note_updated_at, failure_reason, created_at, updated_at)
            VALUES (
                $id, $trackingNo, $workflow, $state, $scannedAt,
                $recordingStartedAt, $recordingEndedAt, $videoPath,
                $snapshotsJson, $cameraId, $stationId, $duplicateOf,
                $platformMatchStatus, $note, $noteUpdatedAt, $failureReason, $createdAt, $updatedAt);
            """;
        BindRecord(command, record);
        return command;
    }

    public async Task UpdateAsync(ScanRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateUpdateCommand(connection, null, record);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException($"扫描记录 {record.Id} 不存在");
        }
    }

    private static SqliteCommand CreateUpdateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ScanRecord record)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE scan_records SET
                tracking_no=$trackingNo,
                workflow=$workflow,
                state=$state,
                scanned_at=$scannedAt,
                recording_started_at=$recordingStartedAt,
                recording_ended_at=$recordingEndedAt,
                video_path=$videoPath,
                snapshots_json=$snapshotsJson,
                camera_id=$cameraId,
                station_id=$stationId,
                duplicate_of=$duplicateOf,
                platform_match_status=$platformMatchStatus,
                note=$note,
                note_updated_at=$noteUpdatedAt,
                failure_reason=$failureReason,
                updated_at=$updatedAt
            WHERE id=$id AND deleted_at IS NULL;
            """;
        BindRecord(command, record);
        return command;
    }

    public async Task<ScanRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await QuerySingleAsync("WHERE id=$value AND deleted_at IS NULL", id.ToString("D"), cancellationToken);

    public async Task<ScanRecord?> FindFirstCompletedAsync(
        string trackingNo,
        CancellationToken cancellationToken = default) =>
        await QuerySingleAsync(
            "WHERE tracking_no=$value AND state IN ('Completed','Collected','Imported') AND deleted_at IS NULL ORDER BY scanned_at LIMIT 1",
            trackingNo,
            cancellationToken);

    public async Task<ScanRecord?> FindByVideoPathAsync(
        string videoPath,
        CancellationToken cancellationToken = default) =>
        await QuerySingleAsync("WHERE video_path=$value LIMIT 1", Path.GetFullPath(videoPath), cancellationToken);

    public async Task<IReadOnlyList<ScanRecord>> QueryAsync(
        string? trackingNo = null,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        await QueryPageAsync(trackingNo, 0, limit, cancellationToken);

    public async Task<IReadOnlyList<ScanRecord>> QueryPageAsync(
        string? trackingNo,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 2000);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(trackingNo)
            ? "SELECT * FROM scan_records WHERE deleted_at IS NULL ORDER BY scanned_at DESC, id DESC LIMIT $limit OFFSET $offset"
            : "SELECT * FROM scan_records WHERE deleted_at IS NULL AND tracking_no LIKE $tracking ORDER BY scanned_at DESC, id DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        if (!string.IsNullOrWhiteSpace(trackingNo))
        {
            command.Parameters.AddWithValue("$tracking", $"%{trackingNo.Trim()}%");
        }

        var records = new List<ScanRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }
        await reader.DisposeAsync();
        await AttachTagsAsync(connection, records, false, cancellationToken);
        return records;
    }

    public async Task<RecordTagAssignment> AddTagAsync(
        Guid recordId,
        IssueTagDefinition tag,
        DateTimeOffset taggedAt,
        string source = "scanner",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag.Name);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var assignment = new RecordTagAssignment
        {
            RecordId = recordId,
            TagId = tag.Id,
            TagName = tag.Name.Trim(),
            ColorHex = tag.ColorHex,
            TaggedAt = taggedAt,
            Source = string.IsNullOrWhiteSpace(source) ? "scanner" : source.Trim()
        };
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO record_tags(id, record_id, tag_id, tag_name, color_hex, tagged_at, removed_at, source)
                VALUES($id, $recordId, $tagId, $tagName, $colorHex, $taggedAt, NULL, $source)
                ON CONFLICT(record_id, tag_id) WHERE removed_at IS NULL DO NOTHING;
                """;
            BindTag(insert, assignment);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchRecordAsync(connection, transaction, recordId, taggedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var active = (await GetTagsAsync(recordId, false, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.TagId, tag.Id, StringComparison.OrdinalIgnoreCase));
        return active ?? assignment;
    }

    public async Task<RecordTagAssignment?> UndoLastTagAsync(
        Guid recordId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        RecordTagAssignment? assignment;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT * FROM record_tags
                WHERE record_id=$recordId AND removed_at IS NULL
                ORDER BY tagged_at DESC LIMIT 1;
                """;
            select.Parameters.AddWithValue("$recordId", recordId.ToString("D"));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            assignment = await reader.ReadAsync(cancellationToken) ? ReadTag(reader) : null;
        }
        if (assignment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE record_tags SET removed_at=$removedAt WHERE id=$id AND removed_at IS NULL;";
            update.Parameters.AddWithValue("$id", assignment.Id.ToString("D"));
            update.Parameters.AddWithValue("$removedAt", Format(removedAt));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        assignment.RemovedAt = removedAt;
        await TouchRecordAsync(connection, transaction, recordId, removedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return assignment;
    }

    public async Task<RecordTagAssignment?> RemoveTagAsync(
        Guid recordId,
        Guid assignmentId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        RecordTagAssignment? assignment;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT * FROM record_tags WHERE id=$id AND record_id=$recordId AND removed_at IS NULL LIMIT 1;";
            select.Parameters.AddWithValue("$id", assignmentId.ToString("D"));
            select.Parameters.AddWithValue("$recordId", recordId.ToString("D"));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            assignment = await reader.ReadAsync(cancellationToken) ? ReadTag(reader) : null;
        }
        if (assignment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE record_tags SET removed_at=$removedAt WHERE id=$id AND removed_at IS NULL;";
            update.Parameters.AddWithValue("$id", assignmentId.ToString("D"));
            update.Parameters.AddWithValue("$removedAt", Format(removedAt));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        assignment.RemovedAt = removedAt;
        await TouchRecordAsync(connection, transaction, recordId, removedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return assignment;
    }

    public async Task UpdateNoteAsync(
        Guid recordId,
        string note,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scan_records
            SET note=$note, note_updated_at=$updatedAt, updated_at=$updatedAt
            WHERE id=$id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", recordId.ToString("D"));
        command.Parameters.AddWithValue("$note", (note ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"扫描记录 {recordId} 不存在");
        }
    }

    public async Task<IReadOnlyList<RecordTagAssignment>> GetTagsAsync(
        Guid recordId,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadTagsAsync(connection, [recordId], includeRemoved, cancellationToken);
    }

    public async Task<int> DeleteManyAsync(
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken = default)
    {
        var ids = recordIds.Distinct().Take(2000).ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var parameters = string.Join(",", ids.Select((_, index) => $"$id{index}"));
        var now = Format(DateTimeOffset.Now);

        await using (var detachDuplicates = connection.CreateCommand())
        {
            detachDuplicates.Transaction = transaction;
            detachDuplicates.CommandText = $"""
                UPDATE scan_records
                SET duplicate_of=NULL, updated_at=$now
                WHERE deleted_at IS NULL AND duplicate_of IN ({parameters});
                """;
            AddIdParameters(detachDuplicates, ids);
            detachDuplicates.Parameters.AddWithValue("$now", now);
            await detachDuplicates.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteDeliveries = connection.CreateCommand())
        {
            deleteDeliveries.Transaction = transaction;
            deleteDeliveries.CommandText = $"DELETE FROM sync_deliveries WHERE record_id IN ({parameters});";
            AddIdParameters(deleteDeliveries, ids);
            await deleteDeliveries.ExecuteNonQueryAsync(cancellationToken);
        }

        int affected;
        await using (var deleteRecords = connection.CreateCommand())
        {
            deleteRecords.Transaction = transaction;
            deleteRecords.CommandText = $"""
                UPDATE scan_records
                SET deleted_at=$now, updated_at=$now
                WHERE deleted_at IS NULL AND id IN ({parameters});
                """;
            AddIdParameters(deleteRecords, ids);
            deleteRecords.Parameters.AddWithValue("$now", now);
            affected = await deleteRecords.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return affected;
    }

    public async Task EnqueueDeliveryAsync(
        Guid recordId,
        string connectorId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await InsertDeliveryAsync(connection, null, recordId, connectorId, cancellationToken);
    }

    private static async Task InsertDeliveryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid recordId,
        string connectorId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_deliveries (
                id, record_id, connector_id, status, attempt_count,
                external_id, last_error, next_retry_at, created_at, updated_at)
            VALUES ($id, $recordId, $connectorId, 'Pending', 0, NULL, NULL, $now, $now, $now)
            ON CONFLICT(record_id, connector_id) DO UPDATE SET
                status='Pending', last_error=NULL, next_retry_at=excluded.next_retry_at,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$recordId", recordId.ToString("D"));
        command.Parameters.AddWithValue("$connectorId", connectorId);
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncDelivery>> GetDueDeliveriesAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM sync_deliveries
            WHERE status IN ('Pending','Failed') AND next_retry_at <= $now
            ORDER BY next_retry_at, created_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var deliveries = new List<SyncDelivery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            deliveries.Add(ReadDelivery(reader));
        }
        return deliveries;
    }

    public async Task<SyncDelivery?> GetDeliveryAsync(
        Guid recordId,
        string connectorId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM sync_deliveries
            WHERE record_id=$recordId AND connector_id=$connectorId
            ORDER BY created_at DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$recordId", recordId.ToString("D"));
        command.Parameters.AddWithValue("$connectorId", connectorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDelivery(reader) : null;
    }

    public async Task<bool> TryClaimDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_deliveries
            SET status='Processing', attempt_count=attempt_count+1, updated_at=$now
            WHERE id=$id AND status IN ('Pending','Failed');
            """;
        command.Parameters.AddWithValue("$id", deliveryId.ToString("D"));
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.Now));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteDeliveryAsync(
        Guid deliveryId,
        string? externalId,
        CancellationToken cancellationToken = default)
    {
        await UpdateDeliveryAsync(deliveryId, "Succeeded", null, externalId, DateTimeOffset.MaxValue, cancellationToken);
    }

    public async Task FailDeliveryAsync(
        Guid deliveryId,
        string error,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken = default)
    {
        await UpdateDeliveryAsync(deliveryId, "Failed", error, null, nextRetryAt, cancellationToken);
    }

    public async Task RetryConnectorAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_deliveries
            SET status='Pending', next_retry_at=$now, last_error=NULL, updated_at=$now
            WHERE connector_id=$connectorId AND status <> 'Succeeded';
            """;
        command.Parameters.AddWithValue("$connectorId", connectorId);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.Now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task SetMetadataAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ScanRecord?> QuerySingleAsync(
        string whereClause,
        string value,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM scan_records {whereClause}";
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var record = await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        await reader.DisposeAsync();
        if (record is not null)
        {
            await AttachTagsAsync(connection, [record], false, cancellationToken);
        }
        return record;
    }

    private async Task UpdateDeliveryAsync(
        Guid deliveryId,
        string status,
        string? error,
        string? externalId,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_deliveries SET
                status=$status,
                last_error=$error,
                external_id=COALESCE($externalId, external_id),
                next_retry_at=$nextRetryAt,
                updated_at=$updatedAt
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", deliveryId.ToString("D"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$externalId", (object?)externalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextRetryAt", Format(nextRetryAt));
        command.Parameters.AddWithValue("$updatedAt", Format(DateTimeOffset.Now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureDeletedAtColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(scan_records);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        var exists = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "deleted_at", StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }
        await reader.DisposeAsync();
        if (exists)
        {
            return;
        }

        await using var migrate = connection.CreateCommand();
        migrate.CommandText = "ALTER TABLE scan_records ADD COLUMN deleted_at TEXT NULL;";
        await migrate.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        await reader.DisposeAsync();
        await using var migrate = connection.CreateCommand();
        migrate.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await migrate.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE scan_records SET updated_at=$updatedAt WHERE id=$id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", recordId.ToString("D"));
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"扫描记录 {recordId} 不存在");
        }
    }

    private static async Task AttachTagsAsync(
        SqliteConnection connection,
        IReadOnlyList<ScanRecord> records,
        bool includeRemoved,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }
        var tags = await LoadTagsAsync(connection, records.Select(item => item.Id).ToArray(), includeRemoved, cancellationToken);
        var byRecord = tags.GroupBy(item => item.RecordId).ToDictionary(group => group.Key, group => (IReadOnlyList<RecordTagAssignment>)group.ToArray());
        foreach (var record in records)
        {
            record.Tags = byRecord.GetValueOrDefault(record.Id) ?? [];
        }
    }

    private static async Task<IReadOnlyList<RecordTagAssignment>> LoadTagsAsync(
        SqliteConnection connection,
        IReadOnlyList<Guid> recordIds,
        bool includeRemoved,
        CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return [];
        }
        await using var command = connection.CreateCommand();
        var parameters = string.Join(",", recordIds.Select((_, index) => $"$tagRecord{index}"));
        command.CommandText = $"""
            SELECT * FROM record_tags
            WHERE record_id IN ({parameters}) {(includeRemoved ? string.Empty : "AND removed_at IS NULL")}
            ORDER BY tagged_at;
            """;
        for (var index = 0; index < recordIds.Count; index++)
        {
            command.Parameters.AddWithValue($"$tagRecord{index}", recordIds[index].ToString("D"));
        }
        var tags = new List<RecordTagAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(ReadTag(reader));
        }
        return tags;
    }

    private static void BindTag(SqliteCommand command, RecordTagAssignment tag)
    {
        command.Parameters.AddWithValue("$id", tag.Id.ToString("D"));
        command.Parameters.AddWithValue("$recordId", tag.RecordId.ToString("D"));
        command.Parameters.AddWithValue("$tagId", tag.TagId);
        command.Parameters.AddWithValue("$tagName", tag.TagName);
        command.Parameters.AddWithValue("$colorHex", tag.ColorHex);
        command.Parameters.AddWithValue("$taggedAt", Format(tag.TaggedAt));
        command.Parameters.AddWithValue("$source", tag.Source);
    }

    private static void AddIdParameters(SqliteCommand command, IReadOnlyList<Guid> ids)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            command.Parameters.AddWithValue($"$id{index}", ids[index].ToString("D"));
        }
    }

    private static void BindRecord(SqliteCommand command, ScanRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$trackingNo", record.TrackingNo);
        command.Parameters.AddWithValue("$workflow", record.Workflow.ToString());
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$scannedAt", Format(record.ScannedAt));
        command.Parameters.AddWithValue("$recordingStartedAt", Db(record.RecordingStartedAt));
        command.Parameters.AddWithValue("$recordingEndedAt", Db(record.RecordingEndedAt));
        command.Parameters.AddWithValue("$videoPath", Db(record.VideoPath is null ? null : Path.GetFullPath(record.VideoPath)));
        command.Parameters.AddWithValue("$snapshotsJson", record.SnapshotsJson);
        command.Parameters.AddWithValue("$cameraId", Db(record.CameraId));
        command.Parameters.AddWithValue("$stationId", record.StationId);
        command.Parameters.AddWithValue("$duplicateOf", Db(record.DuplicateOf?.ToString("D")));
        command.Parameters.AddWithValue("$platformMatchStatus", record.PlatformMatchStatus);
        command.Parameters.AddWithValue("$note", record.Note ?? string.Empty);
        command.Parameters.AddWithValue("$noteUpdatedAt", Db(record.NoteUpdatedAt));
        command.Parameters.AddWithValue("$failureReason", Db(record.FailureReason));
        command.Parameters.AddWithValue("$createdAt", Format(record.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(record.UpdatedAt));
    }

    private static ScanRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        TrackingNo = reader.GetString(reader.GetOrdinal("tracking_no")),
        Workflow = Enum.Parse<WorkflowMode>(reader.GetString(reader.GetOrdinal("workflow"))),
        State = Enum.Parse<RecordingState>(reader.GetString(reader.GetOrdinal("state"))),
        ScannedAt = Parse(reader.GetString(reader.GetOrdinal("scanned_at"))),
        RecordingStartedAt = ReadDate(reader, "recording_started_at"),
        RecordingEndedAt = ReadDate(reader, "recording_ended_at"),
        VideoPath = ReadString(reader, "video_path"),
        SnapshotsJson = reader.GetString(reader.GetOrdinal("snapshots_json")),
        CameraId = ReadString(reader, "camera_id"),
        StationId = reader.GetString(reader.GetOrdinal("station_id")),
        DuplicateOf = ReadGuid(reader, "duplicate_of"),
        PlatformMatchStatus = reader.GetString(reader.GetOrdinal("platform_match_status")),
        Note = reader.GetString(reader.GetOrdinal("note")),
        NoteUpdatedAt = ReadDate(reader, "note_updated_at"),
        FailureReason = ReadString(reader, "failure_reason"),
        CreatedAt = Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static RecordTagAssignment ReadTag(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        RecordId = Guid.Parse(reader.GetString(reader.GetOrdinal("record_id"))),
        TagId = reader.GetString(reader.GetOrdinal("tag_id")),
        TagName = reader.GetString(reader.GetOrdinal("tag_name")),
        ColorHex = reader.GetString(reader.GetOrdinal("color_hex")),
        TaggedAt = Parse(reader.GetString(reader.GetOrdinal("tagged_at"))),
        RemovedAt = ReadDate(reader, "removed_at"),
        Source = reader.GetString(reader.GetOrdinal("source"))
    };

    private static SyncDelivery ReadDelivery(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        RecordId = Guid.Parse(reader.GetString(reader.GetOrdinal("record_id"))),
        ConnectorId = reader.GetString(reader.GetOrdinal("connector_id")),
        Status = Enum.Parse<SyncStatus>(reader.GetString(reader.GetOrdinal("status"))),
        AttemptCount = reader.GetInt32(reader.GetOrdinal("attempt_count")),
        ExternalId = ReadString(reader, "external_id"),
        LastError = ReadString(reader, "last_error"),
        NextRetryAt = Parse(reader.GetString(reader.GetOrdinal("next_retry_at"))),
        CreatedAt = Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static object Db(string? value) => (object?)value ?? DBNull.Value;
    private static object Db(DateTimeOffset? value) => value is null ? DBNull.Value : Format(value.Value);
    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string? ReadString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
    private static DateTimeOffset? ReadDate(SqliteDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return value is null ? null : Parse(value);
    }
    private static Guid? ReadGuid(SqliteDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return value is null ? null : Guid.Parse(value);
    }
}

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
                failure_reason TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
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

            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                platform_match_status, failure_reason, created_at, updated_at)
            VALUES (
                $id, $trackingNo, $workflow, $state, $scannedAt,
                $recordingStartedAt, $recordingEndedAt, $videoPath,
                $snapshotsJson, $cameraId, $stationId, $duplicateOf,
                $platformMatchStatus, $failureReason, $createdAt, $updatedAt);
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
                failure_reason=$failureReason,
                updated_at=$updatedAt
            WHERE id=$id;
            """;
        BindRecord(command, record);
        return command;
    }

    public async Task<ScanRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await QuerySingleAsync("WHERE id=$value", id.ToString("D"), cancellationToken);

    public async Task<ScanRecord?> FindFirstCompletedAsync(
        string trackingNo,
        CancellationToken cancellationToken = default) =>
        await QuerySingleAsync(
            "WHERE tracking_no=$value AND state IN ('Completed','Imported') ORDER BY scanned_at LIMIT 1",
            trackingNo,
            cancellationToken);

    public async Task<ScanRecord?> FindByVideoPathAsync(
        string videoPath,
        CancellationToken cancellationToken = default) =>
        await QuerySingleAsync("WHERE video_path=$value LIMIT 1", Path.GetFullPath(videoPath), cancellationToken);

    public async Task<IReadOnlyList<ScanRecord>> QueryAsync(
        string? trackingNo = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(trackingNo)
            ? "SELECT * FROM scan_records ORDER BY scanned_at DESC LIMIT $limit"
            : "SELECT * FROM scan_records WHERE tracking_no LIKE $tracking ORDER BY scanned_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
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
        return records;
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
            ON CONFLICT(record_id, connector_id) DO NOTHING;
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
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
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
        FailureReason = ReadString(reader, "failure_reason"),
        CreatedAt = Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = Parse(reader.GetString(reader.GetOrdinal("updated_at")))
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

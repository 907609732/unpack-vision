using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class SqliteScanCommandLedger : IScanCommandLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqliteScanCommandLedger(StorageOptions options, IClock clock)
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
        _clock = clock;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;

            CREATE TABLE IF NOT EXISTS scan_command_receipts (
                idempotency_key TEXT PRIMARY KEY,
                event_id TEXT NOT NULL,
                acknowledgement_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_scan_command_receipts_created
                ON scan_command_receipts(created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ScanAcknowledgement?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT acknowledgement_json FROM scan_command_receipts WHERE idempotency_key=$key LIMIT 1;";
        command.Parameters.AddWithValue("$key", idempotencyKey.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value is null ? null : JsonSerializer.Deserialize<ScanAcknowledgement>(value, JsonOptions);
    }

    public async Task SaveAsync(
        string idempotencyKey,
        ScanAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_command_receipts(idempotency_key, event_id, acknowledgement_json, created_at)
            VALUES($key, $eventId, $json, $createdAt)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey.Trim());
        command.Parameters.AddWithValue("$eventId", acknowledgement.EventId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(acknowledgement, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", Format(_clock.Now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}

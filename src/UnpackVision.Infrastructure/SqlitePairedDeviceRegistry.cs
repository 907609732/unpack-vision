using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class SqlitePairedDeviceRegistry : IPairedDeviceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqlitePairedDeviceRegistry(StorageOptions options, IClock clock)
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

            CREATE TABLE IF NOT EXISTS paired_devices (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                roles_json TEXT NOT NULL,
                scopes_json TEXT NOT NULL,
                public_key TEXT NOT NULL,
                access_token_hash BLOB NOT NULL,
                paired_at TEXT NOT NULL,
                last_seen_at TEXT NULL,
                revoked_at TEXT NULL,
                battery_percent INTEGER NULL,
                thermal_state TEXT NOT NULL,
                network_quality TEXT NOT NULL,
                capabilities_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_paired_devices_active
                ON paired_devices(revoked_at, last_seen_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText = "DELETE FROM paired_devices WHERE revoked_at IS NOT NULL;";
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PairedDevice>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM paired_devices WHERE revoked_at IS NULL ORDER BY paired_at DESC;";
        var devices = new List<PairedDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(ReadDevice(reader));
        }
        return devices;
    }

    public async Task<DevicePairingCredential> PairAsync(
        DeviceRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.PublicKey);
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var accessToken = Convert.ToBase64String(tokenBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var now = _clock.Now;
        var device = new PairedDevice
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(registration.Name) ? "安卓设备" : registration.Name.Trim(),
            Roles = registration.Roles.Distinct(StringComparer.Ordinal).ToArray(),
            Scopes = registration.Scopes.Distinct(StringComparer.Ordinal).ToArray(),
            PublicKey = registration.PublicKey.Trim(),
            PairedAt = now,
            LastSeenAt = now
        };

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO paired_devices (
                id, name, roles_json, scopes_json, public_key, access_token_hash,
                paired_at, last_seen_at, revoked_at, battery_percent,
                thermal_state, network_quality, capabilities_json)
            VALUES (
                $id, $name, $roles, $scopes, $publicKey, $tokenHash,
                $pairedAt, $lastSeenAt, NULL, NULL,
                $thermalState, $networkQuality, $capabilities);
            """;
        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$roles", JsonSerializer.Serialize(device.Roles, JsonOptions));
        command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(device.Scopes, JsonOptions));
        command.Parameters.AddWithValue("$publicKey", device.PublicKey);
        command.Parameters.Add("$tokenHash", SqliteType.Blob).Value = SHA256.HashData(tokenBytes);
        command.Parameters.AddWithValue("$pairedAt", Format(device.PairedAt));
        command.Parameters.AddWithValue("$lastSeenAt", Format(device.LastSeenAt.Value));
        command.Parameters.AddWithValue("$thermalState", device.ThermalState.ToString());
        command.Parameters.AddWithValue("$networkQuality", device.NetworkQuality);
        command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(device.Capabilities, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new DevicePairingCredential(device, accessToken);
    }

    public async Task<PairedDevice?> AuthenticateAsync(
        string deviceId,
        string accessToken,
        string requiredScope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        byte[] supplied;
        try
        {
            supplied = DecodeToken(accessToken.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM paired_devices WHERE id=$id LIMIT 1;";
        command.Parameters.AddWithValue("$id", deviceId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var expectedHash = (byte[])reader["access_token_hash"];
        var device = ReadDevice(reader);
        if (device.IsRevoked ||
            !device.Scopes.Contains(requiredScope, StringComparer.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(supplied)))
        {
            return null;
        }
        await reader.DisposeAsync();

        device.LastSeenAt = _clock.Now;
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE paired_devices SET last_seen_at=$lastSeenAt WHERE id=$id;";
        update.Parameters.AddWithValue("$lastSeenAt", Format(device.LastSeenAt.Value));
        update.Parameters.AddWithValue("$id", device.Id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return device;
    }

    public async Task<bool> RevokeAsync(
        string deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE paired_devices
            SET revoked_at=$revokedAt
            WHERE id=$id AND revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("$revokedAt", Format(revokedAt));
        command.Parameters.AddWithValue("$id", deviceId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM paired_devices WHERE id=$id;";
        command.Parameters.AddWithValue("$id", deviceId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static PairedDevice ReadDevice(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Roles = DeserializeArray(reader.GetString(reader.GetOrdinal("roles_json"))),
        Scopes = DeserializeArray(reader.GetString(reader.GetOrdinal("scopes_json"))),
        PublicKey = reader.GetString(reader.GetOrdinal("public_key")),
        PairedAt = Parse(reader.GetString(reader.GetOrdinal("paired_at"))),
        LastSeenAt = ReadDate(reader, "last_seen_at"),
        RevokedAt = ReadDate(reader, "revoked_at"),
        BatteryPercent = reader.IsDBNull(reader.GetOrdinal("battery_percent"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("battery_percent")),
        ThermalState = Enum.TryParse<DeviceThermalState>(reader.GetString(reader.GetOrdinal("thermal_state")), out var thermal)
            ? thermal
            : DeviceThermalState.Unknown,
        NetworkQuality = reader.GetString(reader.GetOrdinal("network_quality")),
        Capabilities = JsonSerializer.Deserialize<Dictionary<string, string>>(
            reader.GetString(reader.GetOrdinal("capabilities_json")), JsonOptions)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    };

    private static IReadOnlyList<string> DeserializeArray(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
    }

    private static byte[] DecodeToken(string token)
    {
        var base64 = token.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

using System.Globalization;
using Microsoft.Data.Sqlite;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Centralizes the stable SQLite column-to-domain mapping. Schema migrations
/// and repository queries must keep these names backward compatible.
/// </summary>
internal static class SqliteRecordMapping
{
    internal static void BindTag(SqliteCommand command, RecordTagAssignment tag)
    {
        command.Parameters.AddWithValue("$id", tag.Id.ToString("D"));
        command.Parameters.AddWithValue("$recordId", tag.RecordId.ToString("D"));
        command.Parameters.AddWithValue("$tagId", tag.TagId);
        command.Parameters.AddWithValue("$tagName", tag.TagName);
        command.Parameters.AddWithValue("$colorHex", tag.ColorHex);
        command.Parameters.AddWithValue("$taggedAt", Format(tag.TaggedAt));
        command.Parameters.AddWithValue("$source", tag.Source);
    }

    internal static void AddIdParameters(SqliteCommand command, IReadOnlyList<Guid> ids)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            command.Parameters.AddWithValue($"$id{index}", ids[index].ToString("D"));
        }
    }

    internal static void BindRecord(SqliteCommand command, ScanRecord record)
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

    internal static ScanRecord ReadRecord(SqliteDataReader reader) => new()
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

    internal static RecordTagAssignment ReadTag(SqliteDataReader reader) => new()
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

    internal static SyncDelivery ReadDelivery(SqliteDataReader reader) => new()
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

    internal static object Db(string? value) => (object?)value ?? DBNull.Value;
    internal static object Db(DateTimeOffset? value) => value is null ? DBNull.Value : Format(value.Value);
    internal static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    internal static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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

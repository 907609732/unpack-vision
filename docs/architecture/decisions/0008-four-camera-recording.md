# ADR 0008: Four-camera recording keeps one business record

## Status

Accepted for 2.4.0 prerelease. Capacity, writer isolation and storage allocation are
superseded by [ADR 0009](0009-sixteen-camera-capacity-and-storage-pool.md) for 2.5.0;
the one-business-record and compatibility decisions in this ADR remain in force.

## Decision

A scan continues to create one `ScanRecord` and one `RecordingCoordinator`
state transition. The recording backend may attach up to four independent
camera assets plus one 1920×1080 composite asset. `ScanRecord.VideoPath` and
`CameraId` remain the primary-camera compatibility projection.

Each configured camera is opened once. Its capture callback replaces a bounded
latest-frame slot; the single writer loop owns every encoder, aligns frames by
monotonic capture time, writes placeholders through secondary gaps, and is the
only code allowed to finalize writers. The primary camera is required. A
secondary or composite failure marks media `Partial` without invalidating a
completed business record.

Camera configuration persists a Media Foundation symbolic link and only falls
back to the legacy OpenCV index when Windows cannot resolve that stable ID.
Multi-camera configurations must pass the real 15-second capture/encode/write
test before Settings accepts them.

## Compatibility

Old settings are migrated to a single primary profile and continue to mirror
the legacy `Camera` object. Old records and portable indexes restore their
single path as a primary media asset. The existing record video endpoint keeps
returning the primary file; new endpoints enumerate and stream other assets
without exposing local paths.

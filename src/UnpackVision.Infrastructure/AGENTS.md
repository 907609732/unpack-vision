# Infrastructure adapters

- Implement Core ports without leaking SQLite, OpenCV, OpenXML, filesystem, HTTP, or Windows types across the boundary.
- Keep schema and on-disk formats backward compatible. Migrations must be idempotent and covered by integration tests.
- Split persistence by responsibility: records, issue tags, sync deliveries, and metadata/recovery.
- Camera code must document capture-thread ownership, lock order, frame disposal, and writer finalization.
- Telemetry is consent-gated and must never collect business content, hardware fingerprints, account data, or stable device identifiers.

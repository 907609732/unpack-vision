# Critical data flows

## Desktop recording

Scanner input is normalized by the presentation adapter, routed to the recording use case, persisted through `IScanRecordRepository`, executed by `IRecordingBackend`, and queued for connectors after successful finalization. UI updates consume use-case results; they do not decide recording state.

The desktop treats device-aware Raw Input as authoritative and only runs the legacy focused-input fallback after a short arbitration window. Raw keystrokes are framed by scanner-speed inter-key timing rather than the business repeat interval, so a missing terminator cannot retain one parcel until the next scan. `RecordingCoordinator` then applies the configured repeat interval under its operation gate; concurrent copies of one physical scan are ignored, while scanning the current tracking number after that interval still stops the recording.

For a multi-camera rig, each source has one capture pipeline and a bounded latest-frame slot. Each camera has an independent writer loop, and the optional composite has its own writer loop, so a blocked secondary source cannot serialize every encoder. The completion barrier waits for all active writers before any `.partial.mp4` is renamed. The primary path remains the compatibility projection; media assets, segments and gaps are committed in the same SQLite transaction as the completed record and sync delivery.

Preview layout is independent view state. The desktop can arrange 1, 2, 3, 4, 8 or 16 tiles and assign configured cameras to those tiles without opening, closing or re-owning a recording pipeline. Rigs with one to four enabled cameras create the composite by default; five or more protect the independent evidence streams by leaving composite recording off unless the user explicitly enables it.

Before recording starts, the storage allocator reserves one healthy target for the complete business record. Every camera recording asset and segment created by that recording session stays on that target. Allocation applies the larger of the 10 GiB minimum reserve and the estimated maximum-record size plus 5 GiB; low-space targets may warn, while targets below the safety line are rejected. A later record may select the next healthy target, but an active recording session is never split between drives. The portable catalog resolves the record's actual media root and writes its sidecar index beside that order; stale copies left by the pre-recording state are removed from other configured roots after the selected catalog is durable.

The production-media admission boundary probes exactly GStreamer 1.28.5, required elements and a bounded functional H.264 encoder smoke test. FFmpeg 8.1.2 is separately inspected for recovery use and may be redistributed only when its build metadata is LGPL-compatible and contains neither GPL nor nonfree components. Probe success is a capability fact, not evidence that 8/16-camera hardware acceptance has passed.

## Mobile command

The Android client authenticates with its paired device credential and submits an idempotent command. StationHost enforces host, rate, size, scope, and device checks before the application router executes it. The command ledger returns the prior acknowledgement for a repeated idempotency key.

The visible Android issue actions use the stable tag barcodes
`UV-TAG-DAMAGE01`, `UV-TAG-SWAPPED1`, `UV-TAG-MISSING1`, and
`UV-TAG-PURCHASE`. StationHost resolves them through the configured issue-tag
catalog before updating the active recording. `UV-UNDO-TAG` remains accepted
for older clients even though the current Android UI does not expose it.

Before the desktop creates a pairing QR code, it compares the current Windows
private-network IPv4 addresses with the StationHost startup snapshot returned
by the loopback health endpoint. If Wi-Fi, Ethernet, hotspot, or tethering
changed, the desktop restarts StationHost once so Kestrel binds each current
private address individually. It never falls back to an all-interface listener.

## Sync

A completed record creates a delivery. The dispatcher claims due work, invokes a connector, and records success or a retryable failure. Excel and webhook details stay in Infrastructure.

## Workspace recovery

Workspace selection resolves storage and workbook locations. Recovery merges portable catalog entries and incomplete local records without replacing newer completed data. Portable schema version 2 retains the storage-target identifier, codec/encoder facts, watermark state, duration and recoverable segment list without persisting an absolute path or credential. All merge operations must be repeatable.

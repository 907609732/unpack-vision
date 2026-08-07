# Critical data flows

## Desktop recording

Scanner input is normalized by the presentation adapter, routed to the recording use case, persisted through `IScanRecordRepository`, executed by `IRecordingBackend`, and queued for connectors after successful finalization. UI updates consume use-case results; they do not decide recording state.

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

Workspace selection resolves storage and workbook locations. Recovery merges portable catalog entries and incomplete local records without replacing newer completed data. All merge operations must be repeatable.

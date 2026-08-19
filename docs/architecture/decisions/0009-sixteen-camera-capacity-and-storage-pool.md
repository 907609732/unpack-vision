# ADR 0009: Camera capacity is admitted per station and one order stays on one storage target

## Status

Accepted for the 2.5.0 prerelease foundation.

## Context

The previous four-camera design coupled the product limit, preview geometry and one
writer loop. Extending it by adding more hard-coded branches would make a slow stream
able to delay every encoder and would require another data-model rewrite for 32 or 64
channels. A single recording root also cannot use additional disks safely.

## Decision

The product limit is 16 simultaneously enabled camera profiles. The planner exposes
the explicit operator choices 1, 2, 3, 4, 8 and 16, but its grid and persistence models
remain generic. Preview assignments are view state only: changing a tile never changes
camera ownership or the active recording rig.

Each enabled camera owns an isolated capture/writer path with bounded latest-frame
state. The optional composite owns a separate writer path and joins the same completion
barrier. One to four cameras create a composite by default. Five to sixteen cameras do
not create one unless the user explicitly enables it, because independent evidence
streams take priority over presentation output. The primary camera remains required;
secondary failures produce media gaps and `Partial` integrity without changing the
one-record business contract.

The saved product maximum is not the measured machine capacity. Camera presets and
runtime metrics describe recommendations, while a real admission test must decide what
the current CPU, GPU, USB topology, network and disks can sustain. Future 32/64-camera
work extends the capability policy and worker topology rather than the record schema.

Recording storage is an ordered, uncapped pool. Before a record starts, the allocator
pins all recording-session media assets to one healthy target by stable volume identity. The safety reserve
is the larger of 10 GiB and the projected maximum-record size plus 5 GiB. Below 15%
free space or two estimated recording hours the target reports a warning; below the
safety reserve it cannot receive another record. Adding, removing or reordering targets
does not move existing files. The portable-catalog adapter resolves each completed
record against the configured roots, writes the sidecar beside the owning target and
removes stale pre-recording sidecars from other roots. Remote media access trusts only
the legacy compatibility root plus currently enabled pool roots; lexical traversal,
sibling-prefix paths and disabled targets remain outside the API boundary. Active-session
snapshots follow that order's allocated target, while idle snapshots use the first enabled
target. Physical multi-disk failover and volume-change behavior remain release-gate tests.

An explicit operator-requested recording-root migration is the exception to the
no-implicit-move rule. It is a three-boundary transaction: copy every application-owned
file through a temporary name and verify the complete archive, atomically persist the new
root and transactionally rebase only database paths covered by the verification receipt,
then re-hash source and destination before deleting each old copy. Missing, changed,
unverified or locked files remain at the source and are reported for an idempotent retry.
Portable paths stay relative, and external HIK recordings are never rebased or deleted.

GStreamer 1.28.5 is the required production runtime contract. Capability probing checks
the exact version, required elements, and a bounded H.264 encoder smoke test in the order
QSV, NVENC, AMF, Media Foundation and approved software fallback. FFmpeg 8.1.2 is only
eligible for redistribution when the inspected build is LGPL-compatible and contains
neither GPL nor nonfree components. A system-installed incompatible binary can support
local diagnostics but must not be copied into a release.

## Compatibility

An existing `RecordingRoot` migrates to the first storage target without moving media,
and the legacy key continues to mirror the first enabled target. `ScanRecord.VideoPath`
and the existing video endpoint still refer to the primary asset. Old single-path portable
records restore as one primary asset. Internal package ID, Android application ID,
SQLite location, tracking-number format and Excel contract do not change.

## Release evidence

Source support and automated contract tests are not sufficient to call 16-camera
recording production-ready. The prerelease must still pass the repository release gates,
the 60-second real admission test, two-hour 8/16-camera runs, mixed-source recovery,
multi-disk failover and a 100-package reconciliation. Until those results are recorded,
documentation must describe 2.5.0 as a preview and must not claim current-host GStreamer
availability or completed 16-camera hardware acceptance.

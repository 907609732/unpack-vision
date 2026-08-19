# UnpackVision architecture

UnpackVision is a modular monolith with multiple executable hosts. The desktop app, compatibility service, station host, and Android companion share product behavior but keep deployment-specific concerns at their edges.

```text
WPF App / Service / StationHost / Android
                   |
             Application use cases
                   |
        Core domain rules and capability ports
                   ^
                   |
       Infrastructure adapter implementations
```

## Reading map

- `module-map.md` defines responsibilities and allowed dependencies.
- `data-flows.md` records the recording, mobile-command, sync, and recovery flows.
- `release-gates.md` defines evidence required before publishing.
- `decisions/` records choices that should survive individual Codex tasks.

## Current media direction

The 2.5.0 prerelease foundation separates monitoring-wall layout, camera ownership,
recording capacity and storage allocation. The desktop exposes 1, 2, 3, 4, 8 and 16
preview layouts, while each enabled camera keeps an isolated writer path and the whole
business record is assigned to one storage-pool target. See
[ADR 0009](decisions/0009-sixteen-camera-capacity-and-storage-pool.md) before changing
camera limits, composite defaults, runtime selection or disk-safety rules.

GStreamer 1.28.5 and FFmpeg 8.1.2 are capability and redistribution policies, not
assumptions about a workstation. Code must probe the actual binaries and functional
encoders, reject non-whitelisted FFmpeg builds for packaging, and keep 8/16-camera
acceptance as a release gate until it has current device evidence.

Local-network camera discovery is a bounded hint, not a trusted camera configuration.
Standard ONVIF and the optional Hikvision-compatible SADP adapter are isolated behind the
same Core port; see [ADR 0010](decisions/0010-local-network-camera-discovery.md) before
changing multicast behavior, third-party discovery tooling, or result filtering.

## Context rule

For a normal change, load this index, the relevant module entry, the nearest `AGENTS.md`, the public interfaces, the implementation being changed, and its tests. Do not load every host and adapter unless the change crosses those boundaries.

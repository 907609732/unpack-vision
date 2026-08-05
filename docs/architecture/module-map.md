# Module map

| Module | Owns | Does not own | Primary verification |
|---|---|---|---|
| Recording | Recording state machine, start/stop, recovery contracts, file completion | Camera drivers or WPF controls | Coordinator and interrupted-recovery tests |
| Scanning | Barcode validation, stable scan rules, command routing and idempotency | Scanner hardware input | Router, ledger, and Android stable-gate tests |
| Issues | Issue tag definitions, barcode matching, notes and undo rules | Button layout | Issue-tag and repository tests |
| History | Record queries, bounded paging, batch delivery projection, deletion and portable catalog projection | SQLite commands or window controls | Repository and presentation tests |
| Stations | Pairing contracts, private-network address synchronization, device scopes, station state and command acknowledgements | TLS/key storage implementation | Pairing, multi-IP connection, and security tests |
| Media | Recording/preview ports, playback contracts, relay authorization | OpenCV/MediaMTX process details | Media, range, relay and camera tests |
| Sync | Delivery state, connector contracts, retry and event publication | Excel/OpenXML implementation | Sync and Excel connector tests |
| Settings | Stable configuration models and migration intent | Registry/filesystem persistence details | Settings and migration tests |
| Updates | Update manifest semantics and consent boundaries | Velopack/Android installer UI | Desktop and Android update tests |
| Workspace | Workspace selection, template intent, portable catalog and recovery rules | Dialog layout or OpenXML mechanics | Workspace and telemetry tests |
| Diagnostics | Local structured logging, retention, redaction and host logger bridge | Business payloads, credentials or remote telemetry | File creation and redaction tests |

## Dependency rules

- Core depends only on the .NET base class library.
- Application depends only on Core.
- Infrastructure depends on Core, never on a host.
- Diagnostics is an Infrastructure adapter initialized only by executable composition roots.
- Hosts may reference Application, Core, and Infrastructure; concrete adapter construction stays at their composition roots.
- Android is a separate client and communicates only through documented station contracts.

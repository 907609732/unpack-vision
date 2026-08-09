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

## Context rule

For a normal change, load this index, the relevant module entry, the nearest `AGENTS.md`, the public interfaces, the implementation being changed, and its tests. Do not load every host and adapter unless the change crosses those boundaries.

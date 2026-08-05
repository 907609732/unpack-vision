# ADR-0001: Modular monolith with explicit application layer

Status: Accepted

UnpackVision keeps its existing executable hosts while separating orchestration from domain rules and adapter implementations. This avoids distributed-system overhead and gives Codex bounded, testable module contexts.

Core contains domain rules and ports. Application contains workflows. Infrastructure implements ports. Hosts perform composition and delivery adaptation. Compatibility is preferred over a broad rewrite.

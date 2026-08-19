# ADR 0010: Network camera discovery uses bounded standard and vendor-compatible adapters

## Status

Accepted for the 2.5.0 prerelease.

## Context

An IPC configuration screen cannot treat one empty ONVIF query as proof that no camera
exists. Some deployed cameras answer only an untyped WS-Discovery probe, while Hikvision
devices can also advertise through SADP on UDP port 37020. Discovery packets are
unauthenticated local-network input and can contain malformed XML, arbitrary service URLs,
serial numbers, MAC addresses, or other fields that the recorder does not need.

## Decision

`IIpcDiscoveryService` is the host-facing port. Infrastructure combines independent,
bounded discovery adapters and returns devices plus non-fatal notices. One adapter sends
standard ONVIF WS-Discovery probes from eligible private IPv4 interfaces; when the typed
probe finds nothing it performs an untyped compatibility probe. Each datagram is parsed in
isolation with DTD and external entities disabled, a strict size limit, response correlation,
and prompt cancellation. A bad packet or one failed interface cannot terminate the other
interfaces.

Hikvision discovery uses the pinned MIT-licensed `hikvision-tooling` CLI through its SADP
discovery command. The packaged executable is the complete upstream CLI and retains other
upstream entry points; UnpackVision exposes none of them and invokes only the read-only
`discover:sadp -xml -timeout <bounded-seconds>s` argument shape. The executable is
downloaded during release staging, verified by SHA-256, invoked without a shell, bounded
by a hard timeout and output limit, and never receives credentials. Its XML is treated as
hostile input, with DTD and external entities disabled. Only the private IP address,
device kind, HTTP port, and channel count may cross the adapter boundary; serial numbers,
MAC addresses, firmware details, and raw output are discarded and never logged. No
Hikvision proprietary DLL, delivery-assistant database, or cached credential is copied or
read.

Results from both adapters are normalized and merged by private remote address. Only
same-host private HTTP or HTTPS service addresses are exposed to the UI. User information,
query strings, fragments, public or loopback hosts, mismatched responder hosts, control
characters, excessive result counts, and invalid ports are rejected. Discovery does not
verify a video stream: the operator must still supply credentials and pass the existing
camera preview before saving the profile.

## Compatibility and failure behavior

Manual RTSP entry remains available. A missing SADP helper, a blocked multicast protocol,
or an adapter failure produces a visible notice but does not discard results from another
adapter. No internet listener, remote control channel, UPnP mapping, or credential exchange
is added. The feature only performs a user-initiated, time-bounded scan on the current local
network.

## Release evidence

Automated tests cover interface selection, typed-to-untyped fallback, malformed and
oversized datagrams, cancellation, hostile service addresses, bounded SADP process output,
strict XML projection, duplicate merging, and missing-tool behavior. A release claim also
requires a current LAN scan against owned test cameras and a preview test after credentials
are entered; discovery count alone is not video compatibility evidence.

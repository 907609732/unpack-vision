# Third-party components for 2.5.3

The following components are used or fetched by the 2.5.3 prerelease branch. Their source code is not copied into this repository unless the upstream package itself is restored by the standard build tool.

| Component | Version | License | Purpose | Source |
| --- | --- | --- | --- | --- |
| ZXing-C++ Android | 3.1.0 | Apache-2.0 | Offline barcode recognition | https://github.com/zxing-cpp/zxing-cpp |
| RootEncoder | 2.8.0 | Apache-2.0 | Android hardware H.264 and RTSP/RTSPS publishing | https://github.com/pedroSG94/RootEncoder |
| MediaMTX | 1.18.2 | MIT | RTSP, WebRTC and HLS media relay | https://github.com/bluenviron/mediamtx |
| Makaretu.Dns.Multicast | 0.27.0 | MIT | Standard mDNS/DNS-SD station discovery on local and tethered networks | https://github.com/richardschneider/net-mdns |
| Velopack | 1.2.0 | MIT | Windows per-user installer and GitHub Release updates | https://github.com/velopack/velopack |
| hikvision-tooling | 1.0.43 (`afa00e3`) | MIT, with bundled dependency notices | Complete upstream CLI packaged as an optional, isolated helper; the application invokes only read-only SADP discovery | https://github.com/cameronnewman/hikvision-tooling |
| Serilog | 4.4.0 | Apache-2.0 | Structured local diagnostic logging | https://github.com/serilog/serilog |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | Size- and date-rolled diagnostic log files | https://github.com/serilog/serilog-sinks-file |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 | Microsoft ILogger integration for executable hosts | https://github.com/serilog/serilog-extensions-logging |
| Serilog.Enrichers.Sensitive | 2.1.0 | MIT | Defense-in-depth masking of named sensitive log properties | https://github.com/serilog-contrib/Serilog.Enrichers.Sensitive |
| GStreamer (audited Windows runtime subset) | 1.28.5 | LGPL-2.1-or-later plus per-plugin notices | Isolated multi-camera capture, preview, overlay and H.264 muxing | https://gstreamer.freedesktop.org/ |
| OpenH264 (inside the audited GStreamer subset) | bundled by GStreamer 1.28.5 | BSD | License-approved software H.264 fallback | https://www.openh264.org/ |
| FFmpeg (optional; not currently bundled) | 8.1.2 | LGPL-only builds only | Fragment inspection and lossless recovery | https://ffmpeg.org/ |

`scripts/fetch-mediamtx.ps1` downloads the official Windows amd64 archive for MediaMTX 1.18.2 and verifies SHA-256 `945ab46c5fc6d2802ad18e2f1d7e49245ca5609657d85e310aa6eda4cdd72eec`. The downloaded binary is ignored by Git and is intended to be included only in signed release packages together with the upstream license.

`scripts/stage-hikvision-sadp.ps1` downloads the official Windows amd64 release asset
for hikvision-tooling `v1.0.43-afa00e3`, verifies archive SHA-256
`edaf3e99ca155e640c94608bbb132ef386e11b0716fc22fb959666733424fda5`,
verifies executable SHA-256
`64d162d55671d5267a2a435c69018866090b874dde9b5d35784eca828b8c6413`,
and ships the pinned upstream and dependency licenses. The downloaded executable is the
complete upstream CLI and still contains its other command handlers. The application does
not expose those handlers: its adapter invokes only
`discover:sadp -xml -timeout <bounded-seconds>s`, without a shell or credentials.

The pinned binary's Go build information was audited as Go 1.26.5 with
`github.com/caarlos0/env/v11` 11.4.1 (MIT), `github.com/google/uuid` 1.6.0
(BSD-3-Clause), `go.uber.org/zap` 1.28.0 (MIT), and `go.uber.org/multierr` 1.11.0
(MIT). The runtime folder includes those license texts, the Go BSD-3-Clause license and
`NOTICE-hikvision-tooling.txt`. See
[`docs/operations/hikvision-sadp-redistribution.md`](docs/operations/hikvision-sadp-redistribution.md)
for the reproducible audit evidence.

`scripts/stage-media-runtimes.ps1` pins the official GStreamer 1.28.5 MSVC x86-64
installer and SHA-256 `51ee5eaec33008e8409d8cf6f6884457f22aa3bd515f8856f993a3eaab903530`.
It stages only an audited binary dependency allowlist and plugins whose runtime metadata
reports an allowed LGPL/BSD/MIT/MPL license. The complete upstream license directory is
shipped under `runtimes/gstreamer/1.28.5/share/licenses`.

FFmpeg is not copied merely because `ffmpeg.exe` exists on a developer computer. An
explicitly supplied 8.1.2 binary must pass `-version` metadata inspection and is rejected
when `--enable-gpl` or `--enable-nonfree` is present. In particular, the common Gyan
"full_build" distribution is intentionally excluded from product redistribution.

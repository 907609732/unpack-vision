# Third-party components for 2.3

The following components are used or fetched by the 2.0 development branch. Their source code is not copied into this repository unless the upstream package itself is restored by the standard build tool.

| Component | Version | License | Purpose | Source |
| --- | --- | --- | --- | --- |
| ZXing-C++ Android | 3.1.0 | Apache-2.0 | Offline barcode recognition | https://github.com/zxing-cpp/zxing-cpp |
| RootEncoder | 2.8.0 | Apache-2.0 | Android hardware H.264 and RTSP/RTSPS publishing | https://github.com/pedroSG94/RootEncoder |
| MediaMTX | 1.18.2 | MIT | RTSP, WebRTC and HLS media relay | https://github.com/bluenviron/mediamtx |
| Makaretu.Dns.Multicast | 0.27.0 | MIT | Standard mDNS/DNS-SD station discovery on local and tethered networks | https://github.com/richardschneider/net-mdns |
| Velopack | 1.2.0 | MIT | Windows per-user installer and GitHub Release updates | https://github.com/velopack/velopack |
| Serilog | 4.4.0 | Apache-2.0 | Structured local diagnostic logging | https://github.com/serilog/serilog |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | Size- and date-rolled diagnostic log files | https://github.com/serilog/serilog-sinks-file |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 | Microsoft ILogger integration for executable hosts | https://github.com/serilog/serilog-extensions-logging |
| Serilog.Enrichers.Sensitive | 2.1.0 | MIT | Defense-in-depth masking of named sensitive log properties | https://github.com/serilog-contrib/Serilog.Enrichers.Sensitive |

`scripts/fetch-mediamtx.ps1` downloads the official Windows amd64 archive for MediaMTX 1.18.2 and verifies SHA-256 `945ab46c5fc6d2802ad18e2f1d7e49245ca5609657d85e310aa6eda4cdd72eec`. The downloaded binary is ignored by Git and is intended to be included only in signed release packages together with the upstream license.

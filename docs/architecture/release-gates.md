# Release gates

## Automated

1. .NET Debug and Release build with all tests passing and no skipped tests.
2. Android Debug tests plus APK build; signed Release build, lint, R8, and signing verification for publish.
3. Service health/auth smoke, record Range/ETag smoke, StationHost security checks, and MediaMTX relay smoke.
4. Version, release-note, update-manifest, license, SHA256, secret, vulnerability, and workflow validation.
5. CodeQL, Gitleaks, OSV, Trivy, actionlint, MobSF, and OWASP ZAP have no unresolved high or critical finding.
6. The selected GStreamer runtime is exactly 1.28.5, comes from the pinned official installer, contains only the audited binary dependency/plugin allowlist, and passes the staged hash, element and license checks.
7. FFmpeg is absent by default. If an 8.1.2 binary is explicitly bundled, its metadata must omit both `--enable-gpl` and `--enable-nonfree`; the Gyan full build is not redistributable in this product package.
8. The optional Hikvision SADP helper is the exact hash-pinned 1.0.43 Windows binary. Its
   runtime directory contains only the enumerated executable, NOTICE, upstream MIT license,
   Go BSD license and audited module licenses described in the
   [redistribution audit](../operations/hikvision-sadp-redistribution.md).

## Installation and devices

1. Fresh Windows install, 2.1.x upgrade, uninstall/reinstall, and preservation of user database, recordings, and settings.
2. Fresh Android install, signed update, hash verification, pairing migration, and certificate repinning.
3. Desktop camera, scanner, mobile scanner/camera, issue tags, notes, history, Excel success/retry, restart, and recovery.
4. Two-hour mobile camera session, 100-package run, and 1/5/20/30/45-second network interruption recovery.
5. Separate two-hour 8-camera and 16-camera sessions pass the saved capacity profile: at least 90% target frame rate, less than 1% per-stream dropped frames, no more than 100 ms drift, and at least 25% CPU/GPU/disk headroom.
6. Multi-storage tests cover warning thresholds, whole-order next-target selection, every-target-insufficient refusal, target removal/read-only state, stable volume-ID recovery and legacy-root compatibility without moving old recordings.

Use [`scripts/run-stability-acceptance.ps1`](../../scripts/run-stability-acceptance.ps1)
and the [stable acceptance runbook](../operations/stability-acceptance.md) to collect
redacted, read-only evidence for gates 4 through 6. The monitor does not generate scans,
modify Excel, fill disks or replace the in-application runtime-metrics evidence.

Unsigned Windows installers may only be published as prerelease artifacts.

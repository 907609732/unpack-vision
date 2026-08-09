# Release gates

## Automated

1. .NET Debug and Release build with all tests passing and no skipped tests.
2. Android Debug tests plus APK build; signed Release build, lint, R8, and signing verification for publish.
3. Service health/auth smoke, record Range/ETag smoke, StationHost security checks, and MediaMTX relay smoke.
4. Version, release-note, update-manifest, license, SHA256, secret, vulnerability, and workflow validation.
5. CodeQL, Gitleaks, OSV, Trivy, actionlint, MobSF, and OWASP ZAP have no unresolved high or critical finding.

## Installation and devices

1. Fresh Windows install, 2.1.x upgrade, uninstall/reinstall, and preservation of user database, recordings, and settings.
2. Fresh Android install, signed update, hash verification, pairing migration, and certificate repinning.
3. Desktop camera, scanner, mobile scanner/camera, issue tags, notes, history, Excel success/retry, restart, and recovery.
4. Two-hour mobile camera session, 100-package run, and 1/5/20/30/45-second network interruption recovery.

Unsigned Windows installers may only be published as prerelease artifacts.

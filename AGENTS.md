# UnpackVision repository guidance

## Product and compatibility

- UnpackVision is a Windows-first .NET 10/WPF modular monolith with an ASP.NET Core station host, a compatibility sync service, and a Kotlin/Android companion.
- Preserve user-visible behavior, SQLite data, configuration keys, recording names, Excel formats, update manifests, and desktop/mobile wire contracts unless a task explicitly changes them.
- Treat real tracking numbers, recordings, workbook paths, device credentials, and pairing data as private. Never add them to tests, logs, fixtures, or telemetry.
- Windows packages remain prerelease-only until the installer has a trusted code signature.

## Architecture

- Dependency direction is `Hosts/UI -> Application -> Core`; Infrastructure implements Core ports and is wired only at a composition root.
- Core contains domain values, rules, and external capability interfaces. It must not depend on WPF, ASP.NET Core, SQLite, OpenCV, Excel, files, or Android.
- Application contains use-case orchestration. It may depend only on Core.
- Infrastructure contains adapters for persistence, media, workbooks, settings, networking, recovery, and telemetry.
- Keep endpoint handlers, view code-behind, and Android activities thin. Move workflows to focused services or controllers.
- Read `docs/architecture/README.md` and the nearest nested `AGENTS.md` before changing a subsystem.

## Comments and documentation

- Comments explain design reasons, concurrency/lifetime rules, compatibility constraints, security boundaries, retries, and hardware quirks.
- Do not translate obvious code into comments. Prefer clear names, small types, module documentation, XML documentation on public boundaries, and KDoc on Android boundaries.
- Update the module map or an ADR when a dependency rule or public boundary changes.

## Verification

- .NET: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Debug`
- Release: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release`
- Android: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-android.ps1 -Configuration Debug`
- Release gates additionally include the smoke/security scripts described in `docs/architecture/release-gates.md`.
- Never claim a device, installer, upgrade, signing, or external security check passed without executing it and recording the evidence.

## Worktree safety

- Preserve unrelated and pre-existing changes. Never use `git reset --hard`, `git clean`, or checkout-based file restoration.
- Keep each change bounded to one coherent module. Build and test before moving to the next high-risk module.

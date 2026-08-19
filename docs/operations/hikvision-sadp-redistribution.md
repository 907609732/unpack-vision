# Hikvision SADP helper redistribution audit

This document records the reproducible redistribution review for the optional
`hikvision-tooling` helper included by the 2.5.1 prerelease packaging flow.

## Pinned upstream artifact

- Repository: <https://github.com/cameronnewman/hikvision-tooling>
- Tag: `v1.0.43-afa00e3`
- Commit: `afa00e3db11bb47914e1f0d724c6975e0a41e218`
- Asset: `sadp-windows-amd64.exe.tar.gz`
- Archive SHA-256: `edaf3e99ca155e640c94608bbb132ef386e11b0716fc22fb959666733424fda5`
- Executable SHA-256: `64d162d55671d5267a2a435c69018866090b874dde9b5d35784eca828b8c6413`

The artifact is the complete upstream command-line program. It includes discovery,
device-information, management-command and legacy password-reset entry points. Packaging
the full executable must not be described as removing or disabling those upstream
commands. The UnpackVision adapter is the security boundary: it starts the process without
a shell and supplies only:

```text
discover:sadp -xml -timeout <1..15>s
```

It never passes a camera account or password. Process time, output size and parsed fields
remain bounded by the adapter.

## Binary module evidence

The pinned executable was inspected with the open-source
[`redress` v1.2.80](https://github.com/goretk/redress/releases/tag/v1.2.80). Its Windows
archive SHA-256 was verified as
`56a1f004cd28e2bd1da3b00755b91188c8eda87f774e56b55c2766833bd3e9eb` before use.
`redress info` reported Go 1.26.5, Windows amd64 and `CGO_ENABLED=0`.
`redress gomod` reported exactly these non-standard modules:

| Module | Version | License shipped in runtime |
| --- | --- | --- |
| `github.com/caarlos0/env/v11` | 11.4.1 | MIT (`LICENSE-caarlos0-env.txt`) |
| `github.com/google/uuid` | 1.6.0 | BSD-3-Clause (`LICENSE-google-uuid.txt`) |
| `go.uber.org/multierr` | 1.11.0 | MIT (`LICENSE-uber-multierr.txt`) |
| `go.uber.org/zap` | 1.28.0 | MIT (`LICENSE-uber-zap.txt`) |

The Go 1.26.5 BSD-3-Clause license is shipped as `LICENSE-go.txt`. The upstream program's
MIT license is shipped as `LICENSE-hikvision-tooling.txt`. Modules found only in upstream
`go.sum` tests are not listed because they do not appear in the inspected release binary's
build information.

## Release gate

`scripts/stage-hikvision-sadp.ps1` downloads every upstream license from its exact version
and verifies a fixed SHA-256. `scripts/verify-hikvision-sadp-package.ps1` rejects missing,
changed or extra files. `scripts/publish.ps1` copies only the enumerated executable,
licenses and `NOTICE-hikvision-tooling.txt`; it never recursively copies an unchecked
staging directory.

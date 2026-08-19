# Application layer

- This project contains use-case orchestration and depends only on `UnpackVision.Core`.
- Accept external capabilities through Core interfaces; never instantiate Infrastructure adapters here.
- Keep workflows deterministic except for explicit ports such as clocks, repositories, publishers, and recording backends.
- Protect recording and command workflows from concurrent duplicate execution; document lock ownership and idempotency rules.
- Tests for application workflows belong in `tests/UnpackVision.Tests`.

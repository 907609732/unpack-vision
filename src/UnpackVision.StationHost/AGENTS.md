# Station host

- `Program.cs` is the composition root and startup pipeline; endpoint behavior lives in feature endpoint modules.
- Preserve route paths, request/response JSON, authorization scopes, rate limits, loopback-only administration, HTTPS pinning, and RTSPS authorization.
- Endpoint modules may coordinate Application use cases and Core ports but must not contain persistence or media implementation details.
- Security tests and smoke tests are release gates for every endpoint refactor.

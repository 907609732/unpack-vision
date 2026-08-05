# Android companion

- `MainActivity` owns lifecycle, navigation, permission prompts, and UI composition only.
- Scanner, camera publishing, pairing, station communication, offline queueing, updates, consent, and telemetry remain separate feature components.
- Release builds must reject LAN cleartext. Debug cleartext is limited to the documented ADB loopback fallback.
- Never weaken certificate fingerprint validation, credential storage, update hash verification, or scoped station authorization.
- Use the repository build script because it maps the non-ASCII workspace to `Z:` for stable Gradle/Kotlin caches.

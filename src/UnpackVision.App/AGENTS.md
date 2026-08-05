# WPF application

- Code-behind owns view lifecycle, control events, dialogs, and UI-thread marshaling only.
- Put business workflows in Application services and reusable presentation state in focused controllers or view models.
- Infrastructure construction is allowed only in `Program`, startup registration, or another explicit composition root.
- Event subscriptions, timers, cancellation sources, images, and async resources must be released during window shutdown.
- Preserve control names and bindings covered by XAML contract tests.

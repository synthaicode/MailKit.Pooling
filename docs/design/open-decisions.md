# Open Decisions

## Confirmed by current direction

- Package name: `MailKit.Pooling`
- Primary transport boundary: SMTP via MailKit
- Initial scope: connection control library, not notification/template infrastructure

## Still open

- Final license text beyond the current repository placeholder review
- Whether the first runnable release targets `.NET 8` only or `.NET 8+`
- Whether warmup is eager, lazy, or explicit
- Whether the first retry implementation is custom or policy-library-backed
- Whether metrics are exposed through `Meter`, `EventSource`, callbacks, or a thin abstraction over multiple backends
- Whether keepalive runs on background scheduling or on-demand idle validation
- How `UnknownAfterData` is surfaced to consumers: exception, result object, or both
- Whether multi-host enters milestone `0.2` or later

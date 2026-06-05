# Contributing

This repository is in pre-implementation design mode.

Before adding SMTP pool logic:

1. Review the documents under `docs/design/`.
2. Preserve the adapter boundary so unit tests do not depend on a real SMTP server.
3. Do not add retry behavior before the error-classification contract is fixed.
4. Do not expose raw `SmtpClient` lifecycle management as the main consumer API.

# Synchronous SMTP Send with a 2-Second SLA

This sample shows the case where the application must wait for SMTP send
completion inside the request path.

The goal is:

- perform SMTP send synchronously
- return a result within 2 seconds
- treat operations that exceed 2 seconds as failures
- keep `UnknownAfterData` separate from definite pre-send failures

This is different from an outbox or queue design. The request does not return
until `ISmtpSender.SendAsync()` completes or fails.

## When This Pattern Fits

Use this pattern when the business requirement is:

- "do not return success until SMTP submission has been attempted"
- "treat slow SMTP as an application error"

Do not use this pattern if the real requirement is only:

- "return an API response within 2 seconds"

In that case, a durable outbox or queue is usually a better fit.

## DI Registration

Configure `SmtpSendTimeout` to `2` seconds. In a strict synchronous API path,
keep retry budget very small so retries do not silently consume the whole SLA.

```csharp
using PooledMailKit.DependencyInjection;
using PooledMailKit.Options;

builder.Services.AddMailKitPooling(options =>
{
    options.Hosts.Add(new SmtpHostOptions
    {
        Host = "smtp-primary.example.com",
        Port = 587,
        SecureSocketOptions = "StartTls",
        UserName = "smtp-user",
        Password = "smtp-password",
        Priority = 0,
        Weight = 1,
    });

    options.ConnectTimeout = TimeSpan.FromSeconds(2);
    options.AuthenticateTimeout = TimeSpan.FromSeconds(2);
    options.SmtpSendTimeout = TimeSpan.FromSeconds(2);

    options.AcquireTimeout = TimeSpan.FromSeconds(2);
    options.MaxRetryAttempts = 0;
    options.ReconnectCooldown = TimeSpan.FromSeconds(10);
});
```

## Multi-Host Variant

If the application uses more than one SMTP host, the synchronous pattern is
still the same: the request thread waits for `ISmtpSender.SendAsync()`.

The difference is that the pool may fail over to another host inside that same
request if the first host is unavailable.

```csharp
using PooledMailKit.DependencyInjection;
using PooledMailKit.Options;

builder.Services.AddMailKitPooling(options =>
{
    options.Hosts.Add(new SmtpHostOptions
    {
        Host = "smtp-primary.example.com",
        Port = 587,
        SecureSocketOptions = "StartTls",
        UserName = "smtp-user",
        Password = "smtp-password",
        Priority = 0,
        Weight = 3,
    });

    options.Hosts.Add(new SmtpHostOptions
    {
        Host = "smtp-secondary.example.com",
        Port = 587,
        SecureSocketOptions = "StartTls",
        UserName = "smtp-user",
        Password = "smtp-password",
        Priority = 10,
        Weight = 1,
    });

    options.ConnectTimeout = TimeSpan.FromMilliseconds(500);
    options.AuthenticateTimeout = TimeSpan.FromMilliseconds(500);
    options.SmtpSendTimeout = TimeSpan.FromSeconds(2);

    options.AcquireTimeout = TimeSpan.FromMilliseconds(250);
    options.MaxRetryAttempts = 0;
    options.ReconnectCooldown = TimeSpan.FromSeconds(10);
});
```

Important behavior:

- lower `Priority` values are preferred first
- `Weight` applies only inside the same `Priority` group
- if the first host is in cooldown or cannot be used, the pool may move to the
  next host candidate
- the request is still synchronous; failover does not create a background send

### SLA Budget Warning

With multiple hosts, the `2` second SLA is still a single end-to-end budget.

That means:

- slow timeout values on the primary host can consume most of the SLA before
  the secondary host is even tried
- synchronous failover works best when connect/authenticate budgets are short
- in strict request-path SLAs, `MaxRetryAttempts = 0` is usually safer

Practical guidance:

- if the requirement is "try the primary host only within 2 seconds", use
  longer primary timeouts and no effective failover budget
- if the requirement is "try the secondary host too within 2 seconds", keep
  primary connect/authenticate timeout values very short

The `Service Example` and `Controller Example` below do not need to change.
The sender still returns a normal success result if failover succeeded on the
secondary host.

## Service Example

The service builds a `MimeMessage`, sends it synchronously, and maps SMTP
outcomes into a narrow application result.

```csharp
using PooledMailKit.Abstractions;
using PooledMailKit.Errors;
using MimeKit;

public sealed class PasswordResetMailService
{
    private readonly ISmtpSender smtpSender;

    public PasswordResetMailService(ISmtpSender smtpSender)
    {
        this.smtpSender = smtpSender;
    }

    public async Task<SmtpSubmissionResult> SendAsync(
        string toAddress,
        string resetLink,
        CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("no-reply@example.com"));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = "Reset your password";
        message.Body = new TextPart("plain")
        {
            Text = $"Reset your password: {resetLink}",
        };

        try
        {
            var result = await smtpSender.SendAsync(message, cancellationToken)
                .ConfigureAwait(false);

            return SmtpSubmissionResult.Accepted(result.EndpointKey, result.Attempts);
        }
        catch (SmtpSendFailedException ex)
            when (ex.Classification.Kind == SmtpFailureKind.UnknownAfterData)
        {
            return SmtpSubmissionResult.Ambiguous(
                message:
                    "SMTP completion could not be confirmed within the timeout. " +
                    "Do not automatically retry without deduplication logic.",
                stage: ex.Classification.Stage.ToString(),
                attempts: ex.Attempts);
        }
        catch (SmtpSendFailedException ex)
        {
            return SmtpSubmissionResult.Rejected(
                message: "SMTP submission failed.",
                kind: ex.Classification.Kind.ToString(),
                stage: ex.Classification.Stage.ToString(),
                attempts: ex.Attempts);
        }
    }
}
```

## Result Shape

```csharp
public sealed record SmtpSubmissionResult(
    bool Accepted,
    bool Ambiguous,
    string? EndpointKey,
    string? FailureKind,
    string? Stage,
    int Attempts,
    string? Message)
{
    public static SmtpSubmissionResult Accepted(string endpointKey, int attempts) =>
        new(true, false, endpointKey, null, null, attempts, null);

    public static SmtpSubmissionResult Ambiguous(string message, string stage, int attempts) =>
        new(false, true, null, "UnknownAfterData", stage, attempts, message);

    public static SmtpSubmissionResult Rejected(
        string message,
        string kind,
        string stage,
        int attempts) =>
        new(false, false, null, kind, stage, attempts, message);
}
```

## Controller Example

This example keeps the HTTP contract explicit:

- `202 Accepted`: SMTP submission completed inside the SLA
- `504 Gateway Timeout`: SMTP did not complete inside the SLA
- `502 Bad Gateway`: SMTP submission failed before the ambiguity boundary

```csharp
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("password-reset-mails")]
public sealed class PasswordResetMailController : ControllerBase
{
    private readonly PasswordResetMailService mailService;

    public PasswordResetMailController(PasswordResetMailService mailService)
    {
        this.mailService = mailService;
    }

    [HttpPost]
    public async Task<IActionResult> Post(
        [FromBody] PasswordResetMailRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mailService.SendAsync(
            request.ToAddress,
            request.ResetLink,
            cancellationToken);

        if (result.Accepted)
        {
            return Accepted(new
            {
                accepted = true,
                endpoint = result.EndpointKey,
                attempts = result.Attempts,
            });
        }

        if (result.Ambiguous)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                accepted = false,
                ambiguous = true,
                stage = result.Stage,
                attempts = result.Attempts,
                message = result.Message,
            });
        }

        return StatusCode(StatusCodes.Status502BadGateway, new
        {
            accepted = false,
            ambiguous = false,
            kind = result.FailureKind,
            stage = result.Stage,
            attempts = result.Attempts,
            message = result.Message,
        });
    }
}

public sealed record PasswordResetMailRequest(string ToAddress, string ResetLink);
```

## Important Boundary

`SmtpSendTimeout = 2 seconds` means:

- the application stops waiting after 2 seconds
- the application treats that as a failure

It does **not** guarantee that the SMTP server definitely rejected or never
received the message body.

If timeout happens after the SMTP `DATA` ambiguity boundary, the result may be
`UnknownAfterData`. That outcome should not be blindly retried.

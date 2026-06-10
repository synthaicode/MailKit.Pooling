using System.Net.Sockets;
using System.Security.Authentication;
using MailKit;
using MailKit.Net.Smtp;
using PooledMailKit.Abstractions;

namespace PooledMailKit.Errors;

internal sealed class DefaultSmtpErrorClassifier : ISmtpErrorClassifier
{
    public SmtpFailureClassification Classify(Exception exception, SmtpSendStage stage)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is SmtpPoolExhaustedException)
        {
            return Create(
                SmtpFailureKind.PoolExhausted,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: false,
                "Pool acquisition timed out before a connection lease became available.");
        }

        if (exception is AuthenticationException or ServiceNotAuthenticatedException)
        {
            return Create(
                SmtpFailureKind.PermanentFailure,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: true,
                "SMTP authentication failed and should not be retried automatically.");
        }

        if (exception is SmtpCommandException commandException)
        {
            return Classify(commandException, stage);
        }

        if (IsTransportOrTimeoutFailure(exception))
        {
            return ClassifyTransportFailure(stage);
        }

        if (exception is SmtpProtocolException)
        {
            return ClassifyProtocolFailure(stage);
        }

        return Create(
            SmtpFailureKind.ConnectionCorrupted,
            stage,
            isRetryAllowed: false,
            shouldDiscardConnection: true,
            $"Unhandled SMTP failure type '{exception.GetType().Name}' is treated as a broken connection.");
    }

    private static SmtpFailureClassification Classify(SmtpCommandException exception, SmtpSendStage stage)
    {
        if (IsAuthenticationStatus(exception.StatusCode))
        {
            return Create(
                SmtpFailureKind.PermanentFailure,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: true,
                $"SMTP authentication was rejected with status {(int) exception.StatusCode}.");
        }

        if (exception.ErrorCode == SmtpErrorCode.MessageNotAccepted)
        {
            return ClassifyRejectedMessage(exception, stage);
        }

        if (IsRecipientOrSenderRejected(exception.ErrorCode))
        {
            return Create(
                SmtpFailureKind.PermanentFailure,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: true,
                $"SMTP envelope was rejected with status {(int) exception.StatusCode}.");
        }

        var statusCode = (int) exception.StatusCode;
        if (statusCode >= 500)
        {
            return Create(
                SmtpFailureKind.PermanentFailure,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: true,
                $"SMTP command failed with permanent status {statusCode}.");
        }

        if (statusCode >= 400)
        {
            if (IsAfterDataBoundary(stage))
            {
                return Create(
                    SmtpFailureKind.UnknownAfterData,
                    stage,
                    isRetryAllowed: false,
                    shouldDiscardConnection: true,
                    $"SMTP temporary status {statusCode} occurred after DATA and is ambiguous.");
            }

            return Create(
                SmtpFailureKind.RetryableTemporaryFailure,
                stage,
                isRetryAllowed: true,
                shouldDiscardConnection: true,
                $"SMTP command failed with temporary status {statusCode}.");
        }

        return Create(
            SmtpFailureKind.ConnectionCorrupted,
            stage,
            isRetryAllowed: false,
            shouldDiscardConnection: true,
            $"Unexpected SMTP status {statusCode} is treated as a broken session.");
    }

    private static SmtpFailureClassification ClassifyRejectedMessage(SmtpCommandException exception, SmtpSendStage stage)
    {
        // MessageNotAccepted is the server's reply to the completed DATA payload.
        // The protocol outcome is known (the message was not accepted), so this is
        // never ambiguous and the session itself remains in a clean state.
        var statusCode = (int) exception.StatusCode;
        if (statusCode >= 500)
        {
            return Create(
                SmtpFailureKind.PermanentFailure,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: false,
                $"The SMTP server permanently rejected the message with status {statusCode}.");
        }

        return Create(
            SmtpFailureKind.RetryableTemporaryFailure,
            stage,
            isRetryAllowed: true,
            shouldDiscardConnection: false,
            $"The SMTP server temporarily rejected the message with status {statusCode}.");
    }

    private static SmtpFailureClassification ClassifyTransportFailure(SmtpSendStage stage)
    {
        if (IsAfterDataBoundary(stage))
        {
            return Create(
                SmtpFailureKind.UnknownAfterData,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: true,
                "The transport failed after DATA started, so delivery may already have happened.");
        }

        return Create(
            SmtpFailureKind.RetryableBeforeSend,
            stage,
            isRetryAllowed: true,
            shouldDiscardConnection: true,
            "The transport failed before the message outcome became ambiguous.");
    }

    private static SmtpFailureClassification ClassifyProtocolFailure(SmtpSendStage stage)
    {
        if (IsAfterDataBoundary(stage))
        {
            return Create(
                SmtpFailureKind.UnknownAfterData,
                stage,
                isRetryAllowed: false,
                shouldDiscardConnection: true,
                "A protocol failure occurred after DATA started, so the message outcome is unknown.");
        }

        return Create(
            SmtpFailureKind.RetryableBeforeSend,
            stage,
            isRetryAllowed: true,
            shouldDiscardConnection: true,
            "SMTP protocol synchronization was lost before DATA, so a new connection may retry safely.");
    }

    private static bool IsRecipientOrSenderRejected(SmtpErrorCode errorCode)
    {
        return errorCode is SmtpErrorCode.RecipientNotAccepted
            or SmtpErrorCode.SenderNotAccepted;
    }

    private static bool IsAuthenticationStatus(SmtpStatusCode statusCode)
    {
        return statusCode is SmtpStatusCode.AuthenticationRequired
            or SmtpStatusCode.AuthenticationMechanismTooWeak
            or SmtpStatusCode.AuthenticationInvalidCredentials
            or SmtpStatusCode.EncryptionRequiredForAuthenticationMechanism
            or SmtpStatusCode.TemporaryAuthenticationFailure;
    }

    private static bool IsTransportOrTimeoutFailure(Exception exception)
    {
        return exception is TimeoutException
            or OperationCanceledException
            or IOException
            or SocketException
            or ServiceNotConnectedException;
    }

    private static bool IsAfterDataBoundary(SmtpSendStage stage)
    {
        return stage >= SmtpSendStage.DataStarted;
    }

    private static SmtpFailureClassification Create(
        SmtpFailureKind kind,
        SmtpSendStage stage,
        bool isRetryAllowed,
        bool shouldDiscardConnection,
        string reason)
    {
        return new SmtpFailureClassification(
            kind,
            stage,
            isRetryAllowed,
            shouldDiscardConnection,
            reason);
    }
}

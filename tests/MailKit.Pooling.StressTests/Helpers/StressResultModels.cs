namespace MailKit.Pooling.StressTests.Helpers;

internal sealed record TimeWaitDeltaResult(
    bool IsAvailable,
    int Before,
    int After,
    string Source);

internal sealed record SendRunResult(
    string Mode,
    int TotalSent,
    int SuccessCount,
    int FailureCount,
    long ElapsedMilliseconds,
    int ConnectionCreationCount,
    int? ActiveConnections,
    int? IdleConnections,
    IReadOnlyDictionary<string, int>? ErrorClassifications,
    TimeWaitDeltaResult TimeWait);

internal sealed record ComparisonScenarioResult(
    int TotalSends,
    int Concurrency,
    string ResultFilePath,
    SendRunResult Naive,
    SendRunResult Pooled);

internal sealed record ReconnectSuppressionScenarioResult(
    int WarmupSuccessCount,
    int OutageFailureCount,
    int RecoverySuccessCount,
    int ReconnectAttempts,
    int SuppressedReconnectCount,
    int RetryCount,
    int FinalSuccessCount,
    IReadOnlyDictionary<string, int> ErrorClassifications,
    string ResultFilePath);

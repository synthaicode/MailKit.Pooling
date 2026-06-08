namespace PooledMailKit.StressTests.Helpers;

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

internal sealed record LongOutageScenarioResult(
    string ScenarioName,
    int OutageDurationSeconds,
    int OutageAttemptCount,
    int OutageFailureCount,
    int ReconnectAttempts,
    int ConnectionCreateFailures,
    int SuppressedReconnectCount,
    int RecoverySuccessCount,
    int FinalSuccessCount,
    IReadOnlyDictionary<string, int> ErrorClassifications,
    string ResultFilePath);

internal sealed record FlappingScenarioResult(
    string ScenarioName,
    int CycleCount,
    int DownSecondsPerCycle,
    int UpSecondsPerCycle,
    int AttemptCount,
    int FailureCount,
    int ReconnectAttempts,
    int ConnectionCreateFailures,
    int SuppressedReconnectCount,
    int RecoverySuccessCount,
    IReadOnlyDictionary<string, int> ErrorClassifications,
    string ResultFilePath);

internal sealed record PartialOutageScenarioResult(
    string ScenarioName,
    int PrimaryOutageSeconds,
    int SecondarySuccessCountDuringPrimaryOutage,
    int PrimaryFailureCountDuringOutage,
    int SuppressedReconnectCount,
    int RecoverySuccessCount,
    IReadOnlyDictionary<string, int> ErrorClassifications,
    string ResultFilePath);

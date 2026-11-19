namespace ChaosProxy.RateLimiting;

public readonly record struct RateLimitDecision
(
    bool IsAllowed,
    int Limit,
    int Remaining,
    double RetryAfterSeconds
);

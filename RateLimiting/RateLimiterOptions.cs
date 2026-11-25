namespace ChaosProxy.RateLimiting;

public class RateLimiterOptions
{
    public const string SectionName = "RateLimiter";
    public int Capacity {get; set;} = 10;
    //refill rate
    public double TokensPerSecond {get; set;} = 5.0;
    public int BucketIdleTimeoutMinutes {get; set;} = 10;
}
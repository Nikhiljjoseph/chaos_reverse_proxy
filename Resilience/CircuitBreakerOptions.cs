namespace ChaosProxy.Resilience;

public class CircuitBreakerOptions
{
    public const string SectionName = "CircuitBreaker";
    public bool Enabled {get; set;} = true;
    public int FailureThreshold {get; set;} = 3;
    public int BreakDurationSeconds{get; set;} = 10;
}
namespace ChaosProxy.Chaos;

public class ChaosOptions
{
    public const string SectionName = "Chaos";
    public bool Enabled {get; set;} = false;

    //latency
    public double LatencyProbability {get; set;} = 0.0;
    public int LatencyMinMs {get; set;} = 500;
    public int LatencyMaxMs {get; set;} = 2000;

    //http failure
    public double FaultProbability {get; set;} = 0.0;
    public int FaultStatusCode {get; set;} = 500;

    //tcp abort
    public double AbortProbability {get; set;} = 0.0;
}
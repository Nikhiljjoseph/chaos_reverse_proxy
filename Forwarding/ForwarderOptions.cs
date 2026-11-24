namespace ChaosProxy.Forwarding;

public class ForwarderOptions
{
    public const string SectionName = "Forwarder";
    public string TargetBaseUri { get; set; } = string.Empty;
    public int RequestTimeoutMs { get; set; } = 0;
    public List<string> CustomHopByHopHeaders { get; set; } = new();
}
namespace ChaosProxy.Forwarding;

public class ForwarderOptions
{
    public const string SectionName = "Forwarder";
    public string TargetBaseUri { get; set; } = string.Empty;
    public List<string> CustomHopByHopHeaders { get; set; } = new();
}
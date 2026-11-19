using Microsoft.Extensions.Options;

namespace ChaosProxy.Chaos;

public class ChaosEngine
{
    private readonly ChaosOptions _options;
    private readonly ILogger<ChaosEngine> _logger;
    public ChaosEngine(IOptions<ChaosOptions> options, ILogger<ChaosEngine> logger)
    {
        _options = options.Value;
        _logger = logger;
    }
    public async Task<bool> TryApplyChaosAsync(HttpContext context)
    {
        if(!_options.Enabled) return false;
        if(_options.LatencyProbability>0 && Random.Shared.NextDouble() < _options.LatencyProbability)
        {
            int delayMs = Random.Shared.Next(_options.LatencyMinMs,_options.LatencyMaxMs+1);
            _logger.LogWarning("Chaos: Injecting {Delay}ms latency into {Path}", delayMs, context.Request.Path);
            await Task.Delay(delayMs, context.RequestAborted);
        }
        if(_options.FaultProbability>0 && Random.Shared.NextDouble() < _options.FaultProbability)
        {
            _logger.LogWarning("Chaos: Injecting HTTP {StatusCode} fault into {Path}", _options.FaultStatusCode, context.Request.Path);
            context.Response.StatusCode = _options.FaultStatusCode;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync($"[ChaosProxy] Injected Fault {_options.FaultStatusCode}");
            return true;
        }
        if(_options.AbortProbability>0 && Random.Shared.NextDouble() < _options.AbortProbability)
        {
            _logger.LogError("Chaos: Abruptly aborting TCP connection for {Path}", context.Request.Path);
            context.Abort();
            return true;
        }
        return false;
    }


}
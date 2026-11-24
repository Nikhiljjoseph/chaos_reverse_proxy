using ChaosProxy.Forwarding;
using ChaosProxy.Chaos;
using ChaosProxy.Resilience;
using ChaosProxy.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwarderOptions>(
    builder.Configuration.GetSection(ForwarderOptions.SectionName)
);
builder.Services.Configure<ChaosOptions>(
    builder.Configuration.GetSection(ChaosOptions.SectionName)
);
builder.Services.Configure<CircuitBreakerOptions>(
    builder.Configuration.GetSection(CircuitBreakerOptions.SectionName)
);
builder.Services.Configure<RateLimiterOptions>(
    builder.Configuration.GetSection(RateLimiterOptions.SectionName)
);

builder.Services.AddSingleton<HttpForwarder>();
builder.Services.AddSingleton<ChaosEngine>();
builder.Services.AddSingleton<CircuitBreaker>();
builder.Services.AddSingleton<RateLimiter>();

var app = builder.Build();

var forwarder = app.Services.GetRequiredService<HttpForwarder>();
var chaosEngine = app.Services.GetRequiredService<ChaosEngine>();
var circuitBreaker = app.Services.GetRequiredService<CircuitBreaker>();

app.UseMiddleware<RateLimiterMiddleware>();

app.Run(context =>
{
    if (chaosEngine.IsEnabled)
    {
        return HandleWithChaosAsync(context, forwarder, chaosEngine, circuitBreaker);
    }

    return circuitBreaker.ExecuteAsync(context, forwarder);
});

app.Run();

static async Task HandleWithChaosAsync(
    HttpContext context,
    HttpForwarder forwarder,
    ChaosEngine chaosEngine,
    CircuitBreaker circuitBreaker)
{
    if (await chaosEngine.TryApplyChaosAsync(context)) return;
    await circuitBreaker.ExecuteAsync(context, forwarder);
}
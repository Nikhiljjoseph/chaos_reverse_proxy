using System.Net;
using ChaosProxy.Forwarding;
using ChaosProxy.Chaos;
using ChaosProxy.Resilience;
using ChaosProxy.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<HttpForwarder>()
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
});

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

builder.Services.AddSingleton<ChaosEngine>();
builder.Services.AddSingleton<CircuitBreaker>();
builder.Services.AddSingleton<RateLimiter>();

var app = builder.Build();

app.UseMiddleware<RateLimiterMiddleware>();

app.MapFallback(async (HttpContext context, HttpForwarder forwarder, ChaosEngine chaosEngine, CircuitBreaker circuitBreaker) =>
{
    if(await chaosEngine.TryApplyChaosAsync(context)) return;
    await circuitBreaker.ExecuteAsync(context, async() => await forwarder.ForwardAsync(context));
});

app.Run();

using System.Globalization;

namespace ChaosProxy.RateLimiting;

public class RateLimiterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimiter _limiterService;
    public RateLimiterMiddleware(RequestDelegate next, RateLimiter limiterService)
    {
        _next = next;
        _limiterService = limiterService;
    }
    public async Task InvokeAsync(HttpContext context)
    {
        string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        RateLimitDecision decision = _limiterService.ShouldAllowRequest(clientKey);
        context.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        if(decision.IsAllowed)
        {
            await _next(context);
            return;
        }
        int retryAfterWholeSeconds = Math.Max(1,(int)Math.Ceiling(decision.RetryAfterSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfterWholeSeconds.ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsync("Rate limit exceeded. Try again later.");
    }
}
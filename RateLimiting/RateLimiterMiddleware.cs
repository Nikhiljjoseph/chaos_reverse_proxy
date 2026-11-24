using System.Globalization;
using System.Net;
using Microsoft.Extensions.Primitives;

namespace ChaosProxy.RateLimiting;

public class RateLimiterMiddleware
{
    private static readonly StringValues[] CachedIntegers = Enumerable
        .Range(0, 1025)
        .Select(static i => new StringValues(i.ToString(CultureInfo.InvariantCulture)))
        .ToArray();

    private readonly RequestDelegate _next;
    private readonly RateLimiter _limiterService;

    public RateLimiterMiddleware(RequestDelegate next, RateLimiter limiterService)
    {
        _next = next;
        _limiterService = limiterService;
    }

    public Task InvokeAsync(HttpContext context)
    {
        IPAddress clientIp = context.Connection.RemoteIpAddress ?? IPAddress.None;
        RateLimitDecision decision = _limiterService.ShouldAllowRequest(clientIp);

        var headers = context.Response.Headers;
        headers["X-RateLimit-Limit"] = FormatInt(decision.Limit);
        headers["X-RateLimit-Remaining"] = FormatInt(decision.Remaining);

        if (decision.IsAllowed)
        {
            return _next(context);
        }

        return WriteRateLimitedResponseAsync(context, decision.RetryAfterSeconds);
    }

    private static StringValues FormatInt(int value)
        => (uint)value < (uint)CachedIntegers.Length
            ? CachedIntegers[value]
            : new StringValues(value.ToString(CultureInfo.InvariantCulture));

    private static async Task WriteRateLimitedResponseAsync(HttpContext context, double retryAfterSeconds)
    {
        int retryAfterWholeSeconds = Math.Max(1, (int)Math.Ceiling(retryAfterSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = FormatInt(retryAfterWholeSeconds);
        await context.Response.WriteAsync("Rate limit exceeded. Try again later.");
    }
}
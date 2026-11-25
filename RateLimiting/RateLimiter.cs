using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;

namespace ChaosProxy.RateLimiting;

public class RateLimiter
{
    private class BucketEntry
    {
        public TokenBucket Bucket {get;}
        public long LastSeenTicks;
        public BucketEntry(int capacity, double tokensPerSecond)
        {
            Bucket = new TokenBucket(capacity,tokensPerSecond);
            LastSeenTicks = Stopwatch.GetTimestamp();
        }
    }

    private readonly ConcurrentDictionary<IPAddress, BucketEntry> _buckets = new();
    private readonly RateLimiterOptions _options;
    private readonly Timer _cleanupTimer;

    public RateLimiter(IOptions<RateLimiterOptions> options)
    {
        _options=options.Value;
        //run idle bucket cleanup every 2 mins
        _cleanupTimer = new Timer(
            CleanupIdleBuckets,
            null,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2));
    }

    public RateLimitDecision ShouldAllowRequest(IPAddress clientIp)
    {
        var entry = _buckets.GetOrAdd(
            clientIp,
            static (_, opts) => new BucketEntry(opts.Capacity, opts.TokensPerSecond),
            _options
        );
        Volatile.Write(ref entry.LastSeenTicks, Stopwatch.GetTimestamp());
        return entry.Bucket.TryConsume();
    }

    public RateLimitDecision ShouldAllowRequest(string clientKey)
        => ShouldAllowRequest(IPAddress.TryParse(clientKey, out var ip) ? ip : IPAddress.None);

    private void CleanupIdleBuckets(object? state)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        long maxIdleTicks = _options.BucketIdleTimeoutMinutes * 60 * Stopwatch.Frequency;
        foreach(var kvp in _buckets)
        {
            long lastSeen = Volatile.Read(ref kvp.Value.LastSeenTicks);
            if(nowTicks - lastSeen > maxIdleTicks)
            {
                _buckets.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();
}
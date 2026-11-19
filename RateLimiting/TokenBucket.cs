using System.Diagnostics;

namespace ChaosProxy.RateLimiting;

public class TokenBucket
{
    private const long ScaleFactor = 1000; // 1 token = 1000 millitokens
    private const long LowerMask = 0x0000_0000_FFFF_FFFFL;
    private readonly long _capacityScaled;
    private readonly int _capacity;
    private readonly double _refillTokensPerMs;
    private readonly long _startTimestamp;
    
    //__packedState stores the current state of the bucket in one long variable
    //upper 32 bits store when the last refill happened
    //lower 32 bits store current available tokens * scalefactor
    private long _packedState; 
    public TokenBucket(int capacity, double tokensPerSecond)
    {
        if(capacity<=0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        if(tokensPerSecond<=0)
            throw new ArgumentOutOfRangeException(nameof(tokensPerSecond), "Refill rate must be greater than zero.");
        
        _capacity = capacity;
        _capacityScaled = capacity*ScaleFactor;
        _refillTokensPerMs = ScaleFactor*tokensPerSecond/1000;
        _startTimestamp = Stopwatch.GetTimestamp();
        //This initializes the bucket. Start full at time 0
        _packedState = Pack(lastTimeMs:0, tokensScaled:_capacityScaled);
    }
    public RateLimitDecision TryConsume()
    {
        while (true)
        {
            long currentState = Volatile.Read(ref _packedState);
            Unpack(currentState, out long lastTimeMs, out long currentTokensScaled);
            long nowMs = GetElapsedMsSinceStart();
            long elapsedMs = Math.Max(0, nowMs-lastTimeMs);
            long refilledTokens = (long)(elapsedMs * _refillTokensPerMs);
            long availableTokens = Math.Min(_capacityScaled, currentTokensScaled+refilledTokens);
            //only update the time if at least a single token is produced.
            long newTimeMs = refilledTokens>0 ? nowMs:lastTimeMs;
            //if there is no 1 full token
            if (availableTokens < ScaleFactor)
            {
                long defecit = ScaleFactor-availableTokens;
                double retryAfterSeconds = (defecit/_refillTokensPerMs)/1000;
                return new RateLimitDecision(
                    IsAllowed: false,
                    Limit: _capacity,
                    Remaining: 0,
                    RetryAfterSeconds: retryAfterSeconds
                );
            }
            long remainingTokens = availableTokens-ScaleFactor;
            long newPacked = Pack(newTimeMs,remainingTokens);

            //change packedState only if packedState == currentState across all threads
            if(Interlocked.CompareExchange(ref _packedState, newPacked, currentState)==currentState)
            {
                int remainingWholeTokens = (int)(remainingTokens / ScaleFactor);
                return new RateLimitDecision(
                    IsAllowed: true,
                    Limit: _capacity,
                    Remaining: remainingWholeTokens,
                    RetryAfterSeconds: 0);
            }

            //if control reaches here, another thread updated the bucket before this one could. so loop around
        }
    }
    private long GetElapsedMsSinceStart()
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
        return elapsedTicks*1000/Stopwatch.Frequency;
    }
    private long Pack(long lastTimeMs, long tokensScaled)
    {
        return (lastTimeMs << 32) | (tokensScaled & LowerMask);
    }
    private void Unpack(long packedState, out long lastTimeMs, out long tokensScaled)
    {
        //ulong is to not use the first bit as a sign
        lastTimeMs = (long)((ulong)packedState >> 32);
        tokensScaled = packedState & LowerMask;
    }
}
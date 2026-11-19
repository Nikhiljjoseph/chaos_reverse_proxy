using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace ChaosProxy.Resilience;

public class CircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<CircuitBreaker> _logger;

    private int _state = (int)CircuitState.Closed;
    private int _consecutiveFailures = 0;
    private long _cooldownExpiryTimestamp = 0;
    private int _halfOpenProbeInProgress = 0;

    public CircuitBreaker(IOptions<CircuitBreakerOptions> options, ILogger<CircuitBreaker> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public CircuitState CurrentState => (CircuitState)Volatile.Read(ref _state);

    public async Task ExecuteAsync(HttpContext context, Func<Task> forwardAction)
    {
        if (!_options.Enabled)
        {
            await forwardAction();
            return;
        }
        CheckCooldownExpiry();
        var _state = CurrentState;

        //open state - fail immediately
        if(_state == CircuitState.Open)
        {
            await FastFailAsync(context);
            return;
        }

        //half open state - permit only one request
        if(_state == CircuitState.HalfOpen)
        {
            if(Interlocked.CompareExchange(ref _halfOpenProbeInProgress, 1, 0) != 0)
            {
                //if there is another request already, fail this one
                await FastFailAsync(context);
                return;
            }
            try
            {
                await ExecuteAndRecordAsync(context, forwardAction, isProbe: true);
            }
            finally
            {
                Interlocked.Exchange(ref _halfOpenProbeInProgress, 0);
            }
            return;
        }

        //closed state - normal flow
        await ExecuteAndRecordAsync(context, forwardAction, isProbe: false);
    }

    public async Task ExecuteAndRecordAsync(HttpContext context, Func<Task> forwardAction, bool isProbe)
    {
        bool isFault = false;
        try
        {
            await forwardAction();
            if(context.Response.StatusCode>=500)
                isFault = true;
        }
        catch(Exception ex) when (ex is not OperationCanceledException)
        {
            isFault = true;
            _logger.LogError(ex, "CircuitBreaker: Upstream call failed with unhandled exception.");
            throw;
        }
        finally
        {
            if (isFault)
            {
                OnFailure(isProbe);
            }
            else
            {
                OnSuccess(isProbe);
            }
        }
    }
    private void OnSuccess(bool isProbe)
    {
        if(isProbe || CurrentState == CircuitState.HalfOpen)
        {
            _logger.LogInformation("CircuitBreaker: Probe succeeded. Transitioning from Half-Open to CLOSED.");
            TransitionTo(CircuitState.Closed);
        }
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    private void OnFailure(bool isProbe)
    {
        if (isProbe)
        {
            _logger.LogWarning("CircuitBreaker: Probe failed. Transitioning from Half-Open back to OPEN.");
            TripBreaker();
            return;
        }
        int failures = Interlocked.Increment(ref _consecutiveFailures);
        _logger.LogWarning("CircuitBreaker: Failure recorded ({Current}/{Threshold})", failures, _options.FailureThreshold);
        if (failures >= _options.FailureThreshold && CurrentState == CircuitState.Closed)
        {
            TripBreaker();
        }
    }

    private void TripBreaker()
    {
        _logger.LogError("CircuitBreaker: Threshold reached. Tripping circuit to OPEN for {Duration}s.", _options.BreakDurationSeconds);
        long expiry = Stopwatch.GetTimestamp() + (_options.BreakDurationSeconds * Stopwatch.Frequency);
        Interlocked.Exchange(ref _cooldownExpiryTimestamp, expiry);
        TransitionTo(CircuitState.Open);
    }

    private void CheckCooldownExpiry()
    {
        if(CurrentState == CircuitState.Open)
        {
            long expiry = Volatile.Read(ref _cooldownExpiryTimestamp);
            if(Stopwatch.GetTimestamp() >= expiry)
            {
                if (Interlocked.CompareExchange(ref _state, (int)CircuitState.HalfOpen, (int)CircuitState.Open) == (int)CircuitState.Open)
                {
                    _logger.LogInformation("CircuitBreaker: Cooldown elapsed. Transitioning from Open to HALF-OPEN.");
                }
            }
        }
    }

    private void TransitionTo(CircuitState newState)
    {
        Interlocked.Exchange(ref _state, (int)newState);
    }

    private async Task FastFailAsync(HttpContext context)
    {
        _logger.LogWarning("CircuitBreaker: Fast-failing request (Circuit is {State})", CurrentState);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = _options.BreakDurationSeconds.ToString();
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync($"[ChaosProxy] 503 Service Unavailable - Circuit Breaker is {CurrentState}");
    }
}
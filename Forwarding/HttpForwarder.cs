using Microsoft.Extensions.Options;

namespace ChaosProxy.Forwarding;

public class HttpForwarder
{
    private readonly HttpClient _httpClient;
    private readonly string _targetBaseUri;
    private readonly HashSet<string> _hopByHopHeaders;
    //headers that must be stripped as they are used for client to proxy connection
    private static readonly string[] DefaultHopByHopHeaders =
    [
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host"
    ];
    public HttpForwarder(HttpClient httpClient, IOptions<ForwarderOptions> options)
    {
        _httpClient = httpClient;
        _targetBaseUri = options.Value.TargetBaseUri ?? throw new InvalidOperationException("Forwarder:TargetBaseUri is not configured.");
        _hopByHopHeaders = new HashSet<string>(DefaultHopByHopHeaders, StringComparer.OrdinalIgnoreCase);
        if(options.Value.CustomHopByHopHeaders is { Count: > 0 })
        {
            foreach(var customHeader in options.Value.CustomHopByHopHeaders)
            {
                _hopByHopHeaders.Add(customHeader);
            }
        }
    }
    public async Task ForwardAsync(HttpContext context)
    {
        var targetUri = BuildTargetUri(context);
        var outBoundRequest = CreateOutboundRequest(context, targetUri);
        using var upstreamResponse = await _httpClient.SendAsync(
            outBoundRequest,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted
        );
        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        CopyResponseHeaders(upstreamResponse, context);
        await upstreamResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    private Uri BuildTargetUri(HttpContext context)
    {
        var pathAndQuery = $"{context.Request.Path}{context.Request.QueryString}";
        return new Uri($"{_targetBaseUri}{pathAndQuery}");
    }
    private HttpRequestMessage CreateOutboundRequest(HttpContext context, Uri targetUri)
    {
        var outBoundRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
        outBoundRequest.Headers.Host = targetUri.Authority;
        
        if(HttpMethods.IsPost(context.Request.Method) ||
            HttpMethods.IsPut(context.Request.Method) ||
            HttpMethods.IsPatch(context.Request.Method))
        {
            outBoundRequest.Content = new StreamContent(context.Request.Body);
        }

        foreach(var header in context.Request.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key))
            {
                continue;
            }
            if(!outBoundRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                outBoundRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
        return outBoundRequest;
    }
    private void CopyResponseHeaders(HttpResponseMessage source, HttpContext destination)
    {
        foreach(var header in source.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key)) continue;
            destination.Response.Headers[header.Key] = header.Value.ToArray();
        }
        foreach(var header in source.Content.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key)) continue;
            destination.Response.Headers[header.Key] = header.Value.ToArray();
        }
    }
}
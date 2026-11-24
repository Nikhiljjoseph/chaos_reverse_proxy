using System.IO.Pipelines;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ChaosProxy.Forwarding;

public class HttpForwarder  
{
    private readonly HttpMessageInvoker _invoker;
    private readonly string _baseUriNoTrailingSlash;
    private readonly string _targetAuthority;
    private readonly TimeSpan _requestTimeout;
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
    public HttpForwarder(IOptions<ForwarderOptions> options)
    {
        string rawBaseUri = options.Value.TargetBaseUri ?? throw new InvalidOperationException("Forwarder:TargetBaseUri is not configured.");
        var baseUri = new Uri(rawBaseUri);
        _baseUriNoTrailingSlash = rawBaseUri.TrimEnd('/');
        _targetAuthority = baseUri.Authority;
        _requestTimeout = options.Value.RequestTimeoutMs > 0 ? TimeSpan.FromMilliseconds(options.Value.RequestTimeoutMs) : Timeout.InfiniteTimeSpan;
        _hopByHopHeaders = new HashSet<string>(DefaultHopByHopHeaders, StringComparer.OrdinalIgnoreCase);
        if(options.Value.CustomHopByHopHeaders is { Count: > 0 })
        {
            foreach(var customHeader in options.Value.CustomHopByHopHeaders)
            {
                _hopByHopHeaders.Add(customHeader);
            }
        }
        //Here, we configure the socket opener to behave like a forwarder and not a browser
        var handler = new SocketsHttpHandler
        {
            //we are building the proxy, so turn off the os proxy settings or connections, if any
            UseProxy = false,
            //if the server gives out a redirect message, this should get that directly to client, not follow the redirection
            AllowAutoRedirect = false,
            //if server gives out a compressed message, dont decomress it. Forward it straight to client
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            //We dont need to store anything per request, if the same forwarder is used by multiple requests
            UseCookies = false,
            //1024 concurrent connections
            MaxConnectionsPerServer = 1024,
            //opened connections are reusable for 10 mins
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            //closes idle connections after 2 mins
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true            
        };
        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
    }
    public virtual async Task ForwardAsync(HttpContext context)
    {
        var targetUri = BuildTargetUri(context.Request);
        var outBoundRequest = CreateOutboundRequest(context, targetUri);

        CancellationTokenSource? timeoutCts = null;
        CancellationToken ct = context.RequestAborted;

        if (_requestTimeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeoutCts.CancelAfter(_requestTimeout);
            ct = timeoutCts.Token;
        }

        try
        {
            using var upstreamResponse = await _invoker.SendAsync(outBoundRequest, ct);
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(upstreamResponse, context.Response.Headers);
            await CopyBodyToPipeAsync(upstreamResponse.Content, context.Response.BodyWriter, ct);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }
    private Uri BuildTargetUri(HttpRequest request)
    {
        string fullUrl = request.QueryString.HasValue
            ? string.Concat(_baseUriNoTrailingSlash, request.Path.Value, request.QueryString.Value)
            : string.Concat(_baseUriNoTrailingSlash, request.Path.Value);

        return new Uri(fullUrl, UriKind.Absolute);
    }
    private HttpRequestMessage CreateOutboundRequest(HttpContext context, Uri targetUri)
    {
        var request = context.Request;
        var method = GetStaticHttpMethod(request.Method);
        
        var outboundRequest = new HttpRequestMessage(method, targetUri)
        {
            Version = System.Net.HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        outboundRequest.Headers.Host = _targetAuthority;

        bool hasBody = HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method);

        if (hasBody)
        {
            //if the request has a payload, this streams the chunks to the server instead of holding it and sending it once finished
            outboundRequest.Content = new StreamContent(request.BodyReader.AsStream(leaveOpen: true));
        }

        foreach(var header in context.Request.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key))   continue;
            //this is done to not use .ToArray() heap allocation for a single value to improve performance
            if(header.Value.Count == 1)
            {
                string singleValue = header.Value[0];
                if(!outboundRequest.Headers.TryAddWithoutValidation(header.Key, singleValue) && hasBody)
                    outboundRequest.Content!.Headers.TryAddWithoutValidation(header.Key, singleValue);
            }
            else if(header.Value.Count > 1)
            {
                string[] values = header.Value.ToArray()!;
                if(!outboundRequest.Headers.TryAddWithoutValidation(header.Key, values) && hasBody)
                    outboundRequest.Content!.Headers.TryAddWithoutValidation(header.Key, values);

            }
        }
        return outboundRequest;
    }
    private void CopyResponseHeaders(HttpResponseMessage source, IHeaderDictionary destinationHeaders)
    {
        foreach(var header in source.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key)) continue;
            destinationHeaders[header.Key] = ToStringValues(header.Value);
        }
        foreach(var header in source.Content.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key)) continue;
            destinationHeaders[header.Key] = ToStringValues(header.Value);
        }
    }

    private static async Task CopyBodyToPipeAsync(HttpContent content, PipeWriter writer, CancellationToken cancellationToken)
    {
        //opens a stream from the srever
        await using var responseStream = await content.ReadAsStreamAsync(cancellationToken);
        while (true)
        {
            //uses 4kb slice Kestrel's pre allocated reusable memmory instead of creating a new slice every call.
            Memory<byte> buffer = writer.GetMemory(sizeHint: 4096);
            //pour upstream bytes to that slice and exit the loop when it stops
            int bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;
            //tells the writes how many bytes are read this loop 
            writer.Advance(bytesRead);
            //sends that many bytes from the memory downstream. FlushAsync have built in backpressure handling, so dont worry about that
            FlushResult flushResult = await writer.FlushAsync(cancellationToken);
            if (flushResult.IsCanceled || flushResult.IsCompleted)
                break;
        }
    }

    //This method is to efficiently convert the upstream response's IEnumerable<string> to StringValues accepted by ASP.net
    private static StringValues ToStringValues(IEnumerable<string> values)
    {
        //if already an array, we can directly convert into StringValues without heap allocations
        if (values is string[] arr)
            return new StringValues(arr);

        if (values is IList<string> list)
        {
            //if only one item in the list, we can directly convert into StringValues without heap allocations
            if (list.Count == 1)
                return new StringValues(list[0]);

            //only when the list contain multiple elements, should we allocate a string and change it to StringValues
            var copy = new string[list.Count];
            list.CopyTo(copy, 0);
            return new StringValues(copy);
        }

        // This is a fallback, if none of the above conditions are met
        return new StringValues(values.ToArray());
    }

    private static HttpMethod GetStaticHttpMethod(string method)
    {
        if (HttpMethods.IsGet(method)) return HttpMethod.Get;
        if (HttpMethods.IsPost(method)) return HttpMethod.Post;
        if (HttpMethods.IsPut(method)) return HttpMethod.Put;
        if (HttpMethods.IsDelete(method)) return HttpMethod.Delete;
        if (HttpMethods.IsPatch(method)) return HttpMethod.Patch;
        if (HttpMethods.IsHead(method)) return HttpMethod.Head;
        if (HttpMethods.IsOptions(method)) return HttpMethod.Options;
        return new HttpMethod(method);  
    }

    public void Dispose() => _invoker.Dispose();
}
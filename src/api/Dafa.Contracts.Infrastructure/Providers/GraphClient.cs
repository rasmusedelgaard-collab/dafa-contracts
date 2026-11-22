using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>Limits of the shared Microsoft Graph client (configuration section Graph:*; the defaults suit SharePoint Online).</summary>
public sealed class GraphResilienceOptions
{
    /// <summary>Tries per call, the first one included.</summary>
    public int MaxAttempts { get; set; } = 4;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>The longest single wait. A Retry-After beyond it is not waited for: the caller gets "busy" at once.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Per try, for calls that carry no file content.</summary>
    public TimeSpan MetadataTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Per try, until the response headers arrive, for uploads and downloads.</summary>
    public TimeSpan ContentTimeout { get; set; } = TimeSpan.FromSeconds(180);
    /// <summary>Calls in a row that failed after all their tries before the circuit opens.</summary>
    public int BreakerFailures { get; set; } = 5;
    public TimeSpan BreakerOpenFor { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The one place where calls to Microsoft Graph are made robust (SharePoint hardening):
/// - 429, 502, 503 and 504, timeouts and connection errors are retried a bounded number of times;
///   a Retry-After header is honoured (required by SharePoint), otherwise exponential backoff with jitter;
/// - every try has its own timeout (short for metadata, longer for file content);
/// - a simple circuit breaker: after several failed calls in a row, calls are refused for a short
///   while instead of queueing up behind a store that is down;
/// - a failure that survives the retries becomes StorageUnavailableException (503
///   storage-unavailable, "SharePoint is busy"), never a generic 500;
/// - last success, last failure and the breaker are recorded for the storage health page.
/// Request content must be re-readable (ByteArrayContent, StringContent); the stores only use those.
/// </summary>
public sealed class GraphResilienceHandler : DelegatingHandler
{
    /// <summary>Marks a request as carrying file content (longer timeout).</summary>
    public static readonly HttpRequestOptionsKey<bool> ContentCall = new("dafa.graph.content");

    // One lock per process is enough: there is one health state and a handful of calls at a time.
    private static readonly object BreakerLock = new();

    private readonly StorageHealthState _health;
    private readonly GraphResilienceOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<double> _jitter;

    public GraphResilienceHandler(StorageHealthState health, GraphResilienceOptions options)
        : this(health, options, null, null, null)
    {
    }

    /// <summary>For tests: the waiting, the clock and the jitter (0..1) can be replaced.</summary>
    public GraphResilienceHandler(
        StorageHealthState health, GraphResilienceOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay, Func<DateTime>? utcNow, Func<double>? jitter)
    {
        _health = health;
        _options = options;
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
    }

    public static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.BadGateway
        || status == HttpStatusCode.ServiceUnavailable || status == HttpStatusCode.GatewayTimeout;

    /// <summary>The Retry-After of a response as a time span (seconds or a date), or null.</summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTime utcNow)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }
        if (header.Delta is not null)
        {
            return header.Delta.Value < TimeSpan.Zero ? TimeSpan.Zero : header.Delta.Value;
        }
        if (header.Date is not null)
        {
            var wait = header.Date.Value.UtcDateTime - utcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    /// <summary>Exponential backoff with up to 25% jitter, capped at MaxDelay.</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        var seconds = _options.BaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        seconds += seconds * 0.25 * _jitter();
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaxDelay.TotalSeconds));
    }

    private void CheckBreaker()
    {
        lock (BreakerLock)
        {
            if (_health.BreakerState != "Open")
            {
                return;
            }
            var now = _utcNow();
            if (_health.BreakerOpenUntil is not null && now < _health.BreakerOpenUntil.Value)
            {
                var seconds = (int)Math.Ceiling((_health.BreakerOpenUntil.Value - now).TotalSeconds);
                throw new StorageUnavailableException("The circuit breaker is open after repeated failures.", Math.Max(1, seconds));
            }
            // The pause is over: let calls through again; the first failure re-opens it.
            _health.SetBreaker("HalfOpen", null);
        }
    }

    private void Succeeded()
    {
        lock (BreakerLock)
        {
            _health.ConsecutiveFailures = 0;
            if (_health.BreakerState != "Closed")
            {
                _health.SetBreaker("Closed", null);
            }
        }
        _health.RecordSuccess(_utcNow());
    }

    private void Failed(string error)
    {
        var now = _utcNow();
        _health.RecordFailure(now, error);
        lock (BreakerLock)
        {
            // Kept in the shared health state: handler instances are recycled by the HTTP client factory.
            _health.ConsecutiveFailures++;
            if (_health.ConsecutiveFailures >= _options.BreakerFailures || _health.BreakerState == "HalfOpen")
            {
                _health.SetBreaker("Open", now.Add(_options.BreakerOpenFor));
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CheckBreaker();
        var isContent = request.Options.TryGetValue(ContentCall, out var flag) && flag;
        var timeout = isContent ? _options.ContentTimeout : _options.MetadataTimeout;
        var attempts = Math.Max(1, _options.MaxAttempts);
        string lastError = "";
        TimeSpan? lastRetryAfter = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                var response = await base.SendAsync(request, timeoutSource.Token);
                if (!IsTransient(response.StatusCode))
                {
                    // Any definite answer, also a 4xx, means the store is reachable.
                    Succeeded();
                    return response;
                }
                retryAfter = ReadRetryAfter(response, _utcNow());
                lastError = "HTTP " + (int)response.StatusCode + " from " + request.RequestUri?.Host;
                response.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "No answer from " + request.RequestUri?.Host + " within " + (int)timeout.TotalSeconds + " s";
            }
            catch (HttpRequestException ex)
            {
                lastError = "Connection to " + request.RequestUri?.Host + " failed: " + ex.Message;
            }

            lastRetryAfter = retryAfter;
            if (attempt == attempts)
            {
                break;
            }
            var wait = retryAfter ?? BackoffFor(attempt);
            if (wait > _options.MaxDelay)
            {
                // The store asks for a longer pause than a request should hang: tell the user now.
                break;
            }
            await _delay(wait, cancellationToken);
        }

        Failed(lastError);
        throw new StorageUnavailableException(
            lastError, lastRetryAfter is null ? null : Math.Max(1, (int)Math.Ceiling(lastRetryAfter.Value.TotalSeconds)));
    }
}

/// <summary>Small helpers for the Graph REST calls of the document store, the link probe and the directory.</summary>
public static class GraphCalls
{
    public const string ClientName = "graph";
    public const string Scope = "https://graph.microsoft.com/.default";
    public const string BaseUrl = "https://graph.microsoft.com/v1.0";
    /// <summary>SharePoint gives traffic without a User-Agent a lower priority; this is the format Microsoft asks for.</summary>
    public const string UserAgent = "NONISV|DAFA|Contracts/1.0";

    public static HttpRequestMessage Request(HttpMethod method, string url, string? bearerToken, HttpContent? content = null, bool isContent = false)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        request.Options.Set(GraphResilienceHandler.ContentCall, isContent);
        return request;
    }

    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static async Task<string> BodySnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            body = body.Replace("\r", " ").Replace("\n", " ");
            return body.Length > 300 ? body.Substring(0, 300) : body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    /// <summary>
    /// Turns an unexpected answer into the right exception: 401/403 = the app has no access
    /// (configuration), 5xx = the store is failing, anything else = a programming or data error.
    /// </summary>
    public static async Task<Exception> FailureAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var snippet = await BodySnippetAsync(response, cancellationToken);
        if (status == 401 || status == 403)
        {
            return new StorageNotConfiguredException(
                "The app has no access to SharePoint for this (" + what + "). An administrator must grant the app access to the site (Sites.Selected) and check the credentials.");
        }
        if (status >= 500)
        {
            return new StorageUnavailableException("HTTP " + status + " while trying to " + what + ": " + snippet);
        }
        return new InvalidOperationException("Microsoft Graph answered HTTP " + status + " while trying to " + what + ": " + snippet);
    }

    /// <summary>host and site name of https://tenant.sharepoint.com/sites/name, or null when the address has another form.</summary>
    public static (string Host, string Name)? ParseSiteUrl(string? siteUrl)
    {
        if (!Uri.TryCreate((siteUrl ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || !string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase) || segments[1].Length == 0)
        {
            return null;
        }
        return (uri.Host, Uri.UnescapeDataString(segments[1]));
    }

    public static string SiteByUrl(string host, string name) =>
        BaseUrl + "/sites/" + host + ":/sites/" + Uri.EscapeDataString(name) + "?$select=id,webUrl,displayName";
}

/// <summary>A response body as a stream; disposing it also releases the response (streamed downloads).</summary>
public sealed class ResponseStream : Stream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _inner;

    public ResponseStream(HttpResponseMessage response, Stream inner)
    {
        _response = response;
        _inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _response.Content.Headers.ContentLength ?? throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }
}

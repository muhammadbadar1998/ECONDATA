using System.Net;
using System.Text;

namespace EconData.Tests;

/// <summary>Records requests and replays queued canned responses (or throws) for HttpClient tests.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<RecordedRequest> Requests { get; } = new();

    public StubHttpHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public StubHttpHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(_ => Json(json, status));

    public StubHttpHandler EnqueueRaw(string text, string mediaType = "text/html", HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(_ => Raw(text, mediaType, status));

    public StubHttpHandler EnqueueThrow(string message = "transient failure")
        => Enqueue(_ => throw new HttpRequestException(message));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Raw(string text, string mediaType, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));

        return _responders.Count == 0 ? Json("{}") : _responders.Dequeue()(request);
    }
}

internal sealed record RecordedRequest(string Method, string Url, string? Body, string? Authorization);

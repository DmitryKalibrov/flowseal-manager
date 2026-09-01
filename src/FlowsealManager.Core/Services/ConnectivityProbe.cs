using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using FlowsealManager.Core.Models;

namespace FlowsealManager.Core.Services;

public sealed class ConnectivityProbe
{
    private readonly HttpClient _httpClient;

    public ConnectivityProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checks = new Task<ProbeResult>[]
        {
            ProbeHttpAsync(
                "YouTube",
                ServiceKind.YouTube,
                new Uri("https://www.youtube.com/generate_204"),
                response => response.StatusCode == HttpStatusCode.NoContent,
                cancellationToken),
            ProbeHttpAsync(
                "YouTube thumbnails",
                ServiceKind.YouTube,
                new Uri("https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg"),
                response => response.IsSuccessStatusCode &&
                            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
                cancellationToken),
            ProbeHttpAsync(
                "Googlevideo edge",
                ServiceKind.YouTube,
                new Uri("https://redirector.googlevideo.com/report_mapping"),
                response => (int)response.StatusCode is >= 200 and < 500,
                cancellationToken),
            ProbeHttpBodyAsync(
                "Discord API",
                ServiceKind.Discord,
                new Uri("https://discord.com/api/v10/gateway"),
                body => body.Contains("gateway.discord.gg", StringComparison.OrdinalIgnoreCase),
                cancellationToken),
            ProbeHttpAsync(
                "Discord CDN",
                ServiceKind.Discord,
                new Uri("https://cdn.discordapp.com/embed/avatars/0.png"),
                response => response.IsSuccessStatusCode &&
                            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
                cancellationToken),
            ProbeDiscordGatewayAsync(cancellationToken)
        };

        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return new HealthReport(DateTimeOffset.UtcNow, results);
    }

    private async Task<ProbeResult> ProbeHttpAsync(
        string name,
        ServiceKind service,
        Uri uri,
        Func<HttpResponseMessage, bool> validate,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var success = validate(response);
            return new ProbeResult(
                name,
                service,
                success,
                stopwatch.Elapsed,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(name, service, false, stopwatch.Elapsed, FriendlyError(exception));
        }
    }

    private async Task<ProbeResult> ProbeHttpBodyAsync(
        string name,
        ServiceKind service,
        Uri uri,
        Func<string, bool> validate,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(
                    name,
                    service,
                    false,
                    stopwatch.Elapsed,
                    $"HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[4096];
            var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(buffer, 0, count);
            return new ProbeResult(
                name,
                service,
                validate(body),
                stopwatch.Elapsed,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(name, service, false, stopwatch.Elapsed, FriendlyError(exception));
        }
    }

    private static async Task<ProbeResult> ProbeDiscordGatewayAsync(CancellationToken cancellationToken)
    {
        const string name = "Discord Gateway WebSocket";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("User-Agent", "FlowsealManager/1.0");
            await socket.ConnectAsync(
                new Uri("wss://gateway.discord.gg/?v=10&encoding=json"),
                timeout.Token).ConfigureAwait(false);
            var buffer = new byte[4096];
            var received = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(buffer, 0, received.Count);
            var success = received.MessageType == WebSocketMessageType.Text &&
                          body.Contains("\"op\":10", StringComparison.Ordinal);
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe complete", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // A successful Hello is enough; the remote may close first.
            }

            return new ProbeResult(name, ServiceKind.Discord, success, stopwatch.Elapsed, success ? "Hello получен" : "Нет Hello");
        }
        catch (Exception exception) when (exception is WebSocketException or TaskCanceledException or HttpRequestException)
        {
            return new ProbeResult(name, ServiceKind.Discord, false, stopwatch.Elapsed, FriendlyError(exception));
        }
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        TaskCanceledException => "тайм-аут",
        HttpRequestException http when http.HttpRequestError == HttpRequestError.NameResolutionError => "ошибка DNS",
        HttpRequestException => "соединение сброшено",
        WebSocketException => "WebSocket недоступен",
        _ => exception.GetType().Name
    };
}

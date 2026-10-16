using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class NotifierTests
{
    private sealed class RecordingHandler(Func<HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static Notifier CreateNotifier(HttpMessageHandler handler, NotificationSettings settings) => new(
        new HttpClient(handler),
        Options.Create(new AppSettings { Notifications = settings }),
        NullLogger<Notifier>.Instance);

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task Send_WithoutUrl_SendsNothing()
    {
        var handler = new RecordingHandler();

        await CreateNotifier(handler, new NotificationSettings()).SendAsync(NotificationLevel.Info, "t", "m");

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Send_Ntfy_UsesPlainBodyAndHeaders()
    {
        var handler = new RecordingHandler();
        var settings = new NotificationSettings { Url = "https://ntfy.example.com/topic", Token = "tk_secret" };

        await CreateNotifier(handler, settings).SendAsync(NotificationLevel.Critical, "Locked", "Zugriff gesperrt");

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ntfy.example.com/topic", request.RequestUri!.ToString());
        Assert.Equal("Zugriff gesperrt", body);
        Assert.Equal("Locked", Header(request, "Title"));
        Assert.Equal("5", Header(request, "Priority"));
        Assert.Equal("Bearer tk_secret", request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Send_Gotify_UsesJsonAndAppKeyHeader()
    {
        var handler = new RecordingHandler();
        var settings = new NotificationSettings
        {
            Url = "https://gotify.example.com/message", Type = "Gotify", Token = "app-token"
        };

        await CreateNotifier(handler, settings).SendAsync(NotificationLevel.Warning, "Failing", "msg");

        var (request, body) = Assert.Single(handler.Requests);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("Failing", (string?)json["title"]);
        Assert.Equal("msg", (string?)json["message"]);
        Assert.Equal(8, (int?)json["priority"]);
        Assert.Equal("app-token", Header(request, "X-Gotify-Key"));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Send_Discord_PostsColoredEmbedWithoutAuthHeader()
    {
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.NoContent));
        var settings = new NotificationSettings
        {
            Url = "https://discord.com/api/webhooks/123/secret", Type = "discord", Token = "ignored"
        };

        await CreateNotifier(handler, settings).SendAsync(NotificationLevel.Critical, "Locked", "msg");

        var (request, body) = Assert.Single(handler.Requests);
        var embed = JsonNode.Parse(body)!["embeds"]![0]!;
        Assert.Equal("Locked", (string?)embed["title"]);
        Assert.Equal("msg", (string?)embed["description"]);
        Assert.Equal(0xE74C3C, (int?)embed["color"]);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Send_Json_PostsGenericPayload()
    {
        var handler = new RecordingHandler();
        var settings = new NotificationSettings { Url = "https://hooks.example.com/x", Type = "json" };

        await CreateNotifier(handler, settings).SendAsync(NotificationLevel.Info, "Updated", "msg");

        var json = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
        Assert.Equal("info", (string?)json["level"]);
        Assert.Equal("ngxPMIpUpdater", (string?)json["source"]);
    }

    [Fact]
    public async Task Send_EndpointFails_DoesNotThrow()
    {
        var settings = new NotificationSettings { Url = "https://ntfy.example.com/topic" };

        await CreateNotifier(new RecordingHandler(() => throw new HttpRequestException("down")), settings)
            .SendAsync(NotificationLevel.Info, "t", "m");
        await CreateNotifier(new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)), settings)
            .SendAsync(NotificationLevel.Info, "t", "m");
    }
}

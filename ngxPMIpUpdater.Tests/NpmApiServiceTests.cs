using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class NpmApiServiceTests
{
    private const string BaseUrl = "http://npm:81";

    /// <summary>Records every request and answers from a queue of canned responses.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Requests { get; } = [];

        public FakeHandler Respond(HttpStatusCode status, string body = "{}")
        {
            _responses.Enqueue((status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));

            var (status, responseBody) = _responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private static NpmApiService CreateService(FakeHandler handler) =>
        new(new HttpClient(handler), NullLogger<NpmApiService>.Instance);

    private static NpmSettings TokenSettings() => new() { BaseUrl = BaseUrl + "/", ApiToken = "api-token", AccessListId = 3 };

    private static NpmSettings LoginSettings() =>
        new() { BaseUrl = BaseUrl, Email = "admin@example.com", Password = "secret", AccessListId = 3 };

    private static NpmAccessList AccessList(params string[] addresses) => new()
    {
        Id = 3, Name = "Home", SatisfyAny = true, PassAuth = false,
        Items = [new NpmAuthItem { Id = 1, Username = "didi" }],
        Clients = [.. addresses.Select(a => new NpmClientEntry { Address = a })]
    };

    private static List<NpmClientEntry> Allow(params string[] addresses) =>
        [.. addresses.Select(a => new NpmClientEntry { Address = a, Directive = "allow" })];

    [Fact]
    public async Task GetAccessList_UsesTokenAndRequestsExpandedList()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, """
            {"id":3,"name":"Home","satisfy_any":true,"pass_auth":false,
             "items":[{"id":1,"username":"didi","hint":"d***"}],
             "clients":[{"address":"1.2.3.4/32","directive":"allow"}]}
            """);

        var list = await CreateService(handler).GetAccessListAsync(TokenSettings());

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{BaseUrl}/api/nginx/access-lists/3?expand=items,clients", request.Url);
        Assert.Equal("Bearer api-token", request.Auth);
        Assert.Equal("Home", list.Name);
        Assert.Equal("didi", Assert.Single(list.Items).Username);
        Assert.Equal("1.2.3.4/32", Assert.Single(list.Clients).Address);
    }

    [Fact]
    public async Task UpdateAccessList_SendsClientsButNeverItems()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK);

        await CreateService(handler).UpdateAccessListAsync(
            TokenSettings(), AccessList("1.1.1.1/32"), Allow("2.2.2.2/32", "10.0.0.0/8"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{BaseUrl}/api/nginx/access-lists/3", request.Url);

        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.False(body.ContainsKey("items"), "sending 'items' would wipe all basic-auth users");
        Assert.False(body.ContainsKey("id"));
        Assert.Equal("Home", (string?)body["name"]);
        Assert.True((bool?)body["satisfy_any"]);
        Assert.Equal(
            ["allow 2.2.2.2/32", "allow 10.0.0.0/8"],
            body["clients"]!.AsArray().Select(c => $"{c!["directive"]} {c["address"]}"));
    }

    [Fact]
    public async Task UpdateAccessList_SameIpsInDifferentOrder_SkipsPut()
    {
        var handler = new FakeHandler();

        await CreateService(handler).UpdateAccessListAsync(
            TokenSettings(), AccessList("1.1.1.1/32", "2.2.2.2/32"), Allow("2.2.2.2/32", "1.1.1.1/32"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateAccessList_CurrentListHasDuplicates_StillUpdates()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK);

        await CreateService(handler).UpdateAccessListAsync(
            TokenSettings(), AccessList("1.1.1.1/32", "1.1.1.1/32"), Allow("1.1.1.1/32"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateAccessList_DenyEntryInCurrentList_StillUpdates()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK);
        var list = AccessList("1.1.1.1/32");
        list.Clients[0].Directive = "deny";

        await CreateService(handler).UpdateAccessListAsync(TokenSettings(), list, Allow("1.1.1.1/32"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateAccessList_DenyAll_IsSentAsSchemaConformClient()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK);

        await CreateService(handler).UpdateAccessListAsync(
            TokenSettings(), AccessList("1.1.1.1/32"), [ClientRules.DenyAll]);

        var client = Assert.Single(JsonNode.Parse(handler.Requests[0].Body!)!["clients"]!.AsArray());
        Assert.Equal("deny", (string?)client!["directive"]);
        Assert.Equal("all", (string?)client["address"]);
    }

    [Fact]
    public async Task UpdateAccessList_AlreadyLockedDown_SkipsPut()
    {
        var handler = new FakeHandler();
        var list = AccessList("all");
        list.Clients[0].Directive = "deny";

        await CreateService(handler).UpdateAccessListAsync(TokenSettings(), list, [ClientRules.DenyAll]);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Login_PostsCredentialsWithoutScopeAndUsesSessionToken()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, """{"token":"session-1"}""")
            .Respond(HttpStatusCode.OK, """{"id":3,"name":"Home"}""");

        await CreateService(handler).GetAccessListAsync(LoginSettings());

        var login = handler.Requests[0];
        Assert.Equal($"{BaseUrl}/api/tokens", login.Url);
        var body = JsonSerializer.Deserialize<Dictionary<string, string>>(login.Body!)!;
        Assert.Equal(["identity", "secret"], body.Keys.Order());
        Assert.Equal("Bearer session-1", handler.Requests[1].Auth);
    }

    [Fact]
    public async Task Login_ReusesSessionTokenWithinItsLifetime()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, """{"token":"session-1"}""")
            .Respond(HttpStatusCode.OK, """{"id":3,"name":"Home"}""")
            .Respond(HttpStatusCode.OK, """{"id":3,"name":"Home"}""");
        var service = CreateService(handler);

        await service.GetAccessListAsync(LoginSettings());
        await service.GetAccessListAsync(LoginSettings());

        Assert.Single(handler.Requests, r => r.Url.EndsWith("/api/tokens"));
    }

    [Fact]
    public async Task Unauthorized_WithLogin_ReauthenticatesAndRetriesOnce()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, """{"token":"session-1"}""")
            .Respond(HttpStatusCode.Unauthorized)
            .Respond(HttpStatusCode.OK, """{"token":"session-2"}""")
            .Respond(HttpStatusCode.OK, """{"id":3,"name":"Home"}""");

        var list = await CreateService(handler).GetAccessListAsync(LoginSettings());

        Assert.Equal("Home", list.Name);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("Bearer session-2", handler.Requests[3].Auth);
    }

    [Fact]
    public async Task Unauthorized_WithApiToken_FailsWithoutRetry()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateService(handler).GetAccessListAsync(TokenSettings()));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServerError_Throws()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.BadRequest, """{"error":"data/clients must be array"}""");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateService(handler).UpdateAccessListAsync(TokenSettings(), AccessList(), Allow("1.1.1.1/32")));
    }
}

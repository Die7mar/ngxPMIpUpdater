using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class CloudflareApiServiceTests
{
    private const string AccountId = "0123456789abcdef0123456789abcdef";
    private const string GroupId = "8d3f1c2a-5b6e-4f70-9a81-2c3d4e5f6a7b";
    private const string GroupUrl = $"{CloudflareApiService.ApiBaseUrl}/accounts/{AccountId}/access/groups/{GroupId}";

    /// <summary>Records every request and answers from a queue of canned responses.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Requests { get; } = [];

        public FakeHandler Respond(HttpStatusCode status, string body)
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

    private static CloudflareApiService CreateService(FakeHandler handler) =>
        new(new HttpClient(handler), NullLogger<CloudflareApiService>.Instance);

    private static CloudflareSettings Settings() =>
        new() { AccountId = AccountId, AccessGroupId = GroupId, ApiToken = "cf-token" };

    private static string Ok(string result) => $$"""{"success":true,"errors":[],"messages":[],"result":{{result}}}""";

    private static string GroupJson(params string[] ips) => $$$"""
        {"id":"{{{GroupId}}}","name":"Home","created_at":"2026-01-01T00:00:00Z","is_default":false,
         "include":[{"email":{"email":"didi@example.com"}}{{{string.Concat(ips.Select(IpRule))}}}],
         "exclude":[{"geo":{"country_code":"KP"}}],
         "require":[]}
        """;

    private static string IpRule(string ip) => "," + new JsonObject { ["ip"] = new JsonObject { ["ip"] = ip } }.ToJsonString();

    private static CloudflareAccessGroup Group(params string[] ips) => new()
    {
        Id = GroupId, Name = "Home", Raw = JsonNode.Parse(GroupJson(ips))!.AsObject()
    };

    private static List<NpmClientEntry> Allow(params string[] addresses) =>
        [.. addresses.Select(a => new NpmClientEntry { Address = a, Directive = "allow" })];

    [Fact]
    public async Task GetAccessGroup_UsesBearerTokenAndUnwrapsResult()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Ok(GroupJson("1.2.3.4/32")));

        var group = await CreateService(handler).GetAccessGroupAsync(Settings());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GroupUrl, request.Url);
        Assert.Equal("Bearer cf-token", request.Auth);
        Assert.Equal("Home", group.Name);
        Assert.Equal(["allow 1.2.3.4/32"], CloudflareApiService.CurrentClients(group).Select(c => $"{c.Directive} {c.Address}"));
    }

    [Fact]
    public async Task UpdateAccessGroup_ReplacesIpRulesAndKeepsEverythingElse()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Ok(GroupJson()));

        var written = await CreateService(handler).UpdateAccessGroupAsync(
            Settings(), Group("1.1.1.1/32"), Allow("2.2.2.2/32", "10.0.0.0/8"));

        Assert.True(written);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(GroupUrl, request.Url);

        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.False(body.ContainsKey("id"));
        Assert.False(body.ContainsKey("created_at"));
        Assert.Equal("Home", (string?)body["name"]);
        Assert.False((bool?)body["is_default"]);
        Assert.Equal("""[{"email":{"email":"didi@example.com"}},{"ip":{"ip":"2.2.2.2/32"}},{"ip":{"ip":"10.0.0.0/8"}}]""",
            body["include"]!.ToJsonString());
        Assert.Equal("""[{"geo":{"country_code":"KP"}}]""", body["exclude"]!.ToJsonString());
        Assert.Equal("[]", body["require"]!.ToJsonString());
    }

    [Fact]
    public async Task UpdateAccessGroup_SameIpsInDifferentOrder_SkipsPut()
    {
        var handler = new FakeHandler();

        var written = await CreateService(handler).UpdateAccessGroupAsync(
            Settings(), Group("1.1.1.1/32", "2.2.2.2/32"), Allow("2.2.2.2/32", "1.1.1.1/32"));

        Assert.False(written);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateAccessGroup_CurrentGroupHasDuplicates_StillUpdates()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Ok(GroupJson()));

        await CreateService(handler).UpdateAccessGroupAsync(
            Settings(), Group("1.1.1.1/32", "1.1.1.1/32"), Allow("1.1.1.1/32"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateAccessGroup_DenyAll_WritesRuleThatMatchesNobody()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Ok(GroupJson()));

        await CreateService(handler).UpdateAccessGroupAsync(Settings(), Group("1.1.1.1/32"), [ClientRules.DenyAll]);

        var include = JsonNode.Parse(handler.Requests[0].Body!)!["include"]!.AsArray();
        Assert.Equal(CloudflareApiService.LockdownAddress, (string?)include.Last()!["ip"]!["ip"]);
        Assert.DoesNotContain(include, rule => rule!.ToJsonString().Contains("1.1.1.1"));
    }

    [Fact]
    public void CurrentClients_LockdownRule_IsReadBackAsDenyAll()
    {
        var clients = CloudflareApiService.CurrentClients(Group(CloudflareApiService.LockdownAddress));

        Assert.Equal("deny all", $"{Assert.Single(clients).Directive} {clients[0].Address}");
    }

    [Fact]
    public async Task UpdateAccessGroup_AlreadyLockedDown_SkipsPut()
    {
        var handler = new FakeHandler();

        await CreateService(handler).UpdateAccessGroupAsync(
            Settings(), Group(CloudflareApiService.LockdownAddress), [ClientRules.DenyAll]);

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}],"result":null}""")]
    [InlineData(HttpStatusCode.OK, """{"success":false,"errors":[{"code":12130,"message":"access.api.error.invalid_request"}],"result":null}""")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>")]
    public async Task Failure_Throws(HttpStatusCode status, string body)
    {
        var handler = new FakeHandler().Respond(status, body);

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler).GetAccessGroupAsync(Settings()));
    }
}

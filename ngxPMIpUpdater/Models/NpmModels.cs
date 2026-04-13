using System.Text.Json.Serialization;

namespace ngxPMIpUpdater.Models;

public class NpmTokenRequest
{
    [JsonPropertyName("identity")]
    public string Identity { get; set; } = string.Empty;

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "user";
}

public class NpmTokenResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public class NpmAccessList
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("satisfy_any")]
    public bool SatisfyAny { get; set; }

    [JsonPropertyName("pass_auth")]
    public bool PassAuth { get; set; }

    [JsonPropertyName("items")]
    public List<NpmAuthItem> Items { get; set; } = [];

    [JsonPropertyName("clients")]
    public List<NpmClientEntry> Clients { get; set; } = [];
}

public class NpmAuthItem
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public class NpmClientEntry
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("directive")]
    public string Directive { get; set; } = "allow";
}

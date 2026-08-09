using System.Text.Json.Serialization;

namespace ngxPMIpUpdater.Models;

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

/// <summary>
/// A basic-auth user of the access list. Read-only for us: we never send items back on PUT, because NPM
/// deletes and recreates all auth users whenever the "items" field is present in the request body.
/// </summary>
public class NpmAuthItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Password hint returned by NPM – the password itself is never exposed by the API.</summary>
    [JsonPropertyName("hint")]
    public string Hint { get; set; } = string.Empty;
}

public class NpmClientEntry
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("directive")]
    public string Directive { get; set; } = "allow";
}

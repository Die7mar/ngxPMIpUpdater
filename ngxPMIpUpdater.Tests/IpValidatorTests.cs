using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class IpValidatorTests
{
    private readonly IpValidator _validator = new(NullLogger<IpValidator>.Instance);

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24")]
    [InlineData("10.0.0.5", "10.0.0.5/32")]
    [InlineData("  10.0.0.5/32\r", "10.0.0.5/32")]
    [InlineData("192.168.1.5/24", "192.168.1.0/24")]   // host bits masked
    [InlineData("10.20.30.40/12", "10.16.0.0/12")]     // mask inside a byte
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    [InlineData("2001:db8:abcd:1234::1/64", "2001:db8:abcd:1234::/64")]
    public void TryNormalizeBlock_AcceptsAndNormalizes(string raw, string expected)
    {
        Assert.True(_validator.TryNormalizeBlock(raw, allowOpenBlocks: false, out var cidr));
        Assert.Equal(expected, cidr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("10.1")]                // shorthand, IPAddress.TryParse would accept it
    [InlineData("10.0.0.1/33")]
    [InlineData("10.0.0.1/-1")]
    [InlineData("10.0.0.1/abc")]
    [InlineData("10.0.0.1/24/8")]
    [InlineData("2001:db8::1/129")]
    [InlineData("mein-haus.dyndns.org")]
    public void TryNormalizeBlock_RejectsInvalid(string? raw)
    {
        Assert.False(_validator.TryNormalizeBlock(raw, allowOpenBlocks: false, out _));
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void TryNormalizeBlock_RefusesOpenBlockByDefault(string raw)
    {
        Assert.False(_validator.TryNormalizeBlock(raw, allowOpenBlocks: false, out _));
    }

    [Theory]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("::/0", "::/0")]
    public void TryNormalizeBlock_AcceptsOpenBlockWhenExplicitlyAllowed(string raw, string expected)
    {
        Assert.True(_validator.TryNormalizeBlock(raw, allowOpenBlocks: true, out var cidr));
        Assert.Equal(expected, cidr);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.0.0.0/32")]
    public void TryNormalizeBlock_RefusesUnspecifiedAddressEvenWhenOpenBlocksAllowed(string raw)
    {
        // Only the literal /0 is the intentional "allow everything" case
        Assert.False(_validator.TryNormalizeBlock(raw, allowOpenBlocks: true, out _));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("255.255.255.255")]
    [InlineData("169.254.10.20")]
    [InlineData("fe80::1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("ff02::1")]
    public void IsAcceptableDnsResult_RejectsBogusAnswers(string address)
    {
        Assert.False(_validator.IsAcceptableDnsResult(IPAddress.Parse(address), "test.example.com"));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("192.168.1.10")]    // private ranges are legitimate (e.g. split-horizon DNS)
    [InlineData("2001:db8::1")]
    public void IsAcceptableDnsResult_AcceptsRealAddresses(string address)
    {
        Assert.True(_validator.IsAcceptableDnsResult(IPAddress.Parse(address), "test.example.com"));
    }
}

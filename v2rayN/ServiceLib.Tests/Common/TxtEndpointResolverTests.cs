using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Common;

public class TxtNetworkGuard
{
    [Before(TestSession)]
    public static void DisableLiveTxtQueries()
    {
        TxtEndpointResolver.QueryOverride = _ => ([], 0);
    }
}

public class TxtEndpointResolverTests
{
    [After(Test)]
    public void ResetQuery()
    {
        TxtEndpointResolver.QueryOverride = _ => ([], 0);
    }

    [Test]
    public async Task Parse_AcceptsStrictLiterals()
    {
        await TxtEndpointResolver.Parse("192.0.2.10:10000")!.Value.Should().BeEqualTo(new TxtEndpoint("192.0.2.10", 10000));
        await TxtEndpointResolver.Parse("\"[2001:db8::10]:12345\"")!.Value.Should().BeEqualTo(new TxtEndpoint("2001:db8::10", 12345));
    }

    [Test]
    [Arguments("example.com:443")]
    [Arguments("192.0.2.10")]
    [Arguments("2001:db8::10")]
    [Arguments("192.0.2.10:0")]
    [Arguments("192.0.2.10:65536")]
    [Arguments("v=spf1 include:_spf.example.com")]
    public async Task Parse_RejectsMalformedRecords(string value)
    {
        await (TxtEndpointResolver.Parse(value) is null).Should().BeTrue();
    }

    [Test]
    public async Task PrefersTxt_IsTrueForDomainsOnly()
    {
        await TxtEndpointResolver.PrefersTxt("kr.270376.xyz").Should().BeTrue();
        await TxtEndpointResolver.PrefersTxt("192.0.2.10").Should().BeFalse();
        await TxtEndpointResolver.PrefersTxt("[2001:db8::1]").Should().BeFalse();
        await TxtEndpointResolver.PrefersTxt("").Should().BeFalse();
    }

    [Test]
    public async Task Resolve_UsesTxtThenFallsBack()
    {
        TxtEndpointResolver.QueryOverride = host => host == "kr.example.test"
            ? (["192.0.2.10:10000", "v=spf1"], 60)
            : ([], 0);

        await TxtEndpointResolver.Resolve("kr.example.test")!.Value.Should().BeEqualTo(new TxtEndpoint("192.0.2.10", 10000));
        await TxtEndpointResolver.ResolveDial("192.0.2.8", 443).Should().BeEqualTo(("192.0.2.8", 443));
        await TxtEndpointResolver.ResolveDial("none.example.test", 8443).Should().BeEqualTo(("none.example.test", 8443));
    }

    [Test]
    public async Task GenerateClientConfig_RewritesDialOnly()
    {
        TxtEndpointResolver.QueryOverride = _ => (["192.0.2.10:10000"], 60);
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        node.Address = "kr.example.test";
        node.Port = 443;
        node.StreamSecurity = Global.StreamSecurity;
        node.Sni = "s1.example.test";
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var outbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);

        await outbound.settings.address!.ToString().Should().BeEqualTo("192.0.2.10");
        await outbound.settings.port.Should().BeEqualTo(10000);
        await outbound.streamSettings.tlsSettings.serverName.Should().BeEqualTo("s1.example.test");
    }
}

using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Manager;

public class CoreTypePolicyTests
{
    [Test]
    public async Task GetCoreType_AlwaysReturnsXrayEvenWhenProfileAsksForSingBox()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);

        await AppManager.Instance.GetCoreType(node, node.ConfigType).Should().Be(ECoreType.Xray);
        await AppManager.Instance.GetCoreType(null, EConfigType.Custom).Should().Be(ECoreType.Xray);
    }

    [Test]
    public async Task GetCheckUpdateCoreTypes_OnlyV2rayNAndXray()
    {
        var types = CoreInfoManager.Instance.GetCheckUpdateCoreTypes();
        await types.Should().Contain(ECoreType.Xray);
        await types.Should().NotContain(ECoreType.sing_box);
        await types.Should().NotContain(ECoreType.mihomo);
        await types.All(t => t is ECoreType.Xray or ECoreType.v2rayN).Should().BeTrue();
    }
}

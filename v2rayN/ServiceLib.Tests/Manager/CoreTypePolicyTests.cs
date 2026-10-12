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

        await AppManager.Instance.GetCoreType(node, node.ConfigType).Should().BeEqualTo(ECoreType.Xray);
        await AppManager.Instance.GetCoreType(null, EConfigType.Custom).Should().BeEqualTo(ECoreType.Xray);
    }

    [Test]
    public async Task GetCheckUpdateCoreTypes_OnlyV2rayNAndXray()
    {
        var types = CoreInfoManager.Instance.GetCheckUpdateCoreTypes();
        await types.Contains(ECoreType.Xray).Should().BeTrue();
        await types.Contains(ECoreType.sing_box).Should().BeFalse();
        await types.Contains(ECoreType.mihomo).Should().BeFalse();
        await types.All(t => t is ECoreType.Xray or ECoreType.v2rayN).Should().BeTrue();
    }
}

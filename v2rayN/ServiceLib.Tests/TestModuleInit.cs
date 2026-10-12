using System.Runtime.CompilerServices;

namespace ServiceLib.Tests;

internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void DisableLiveTxtQueries()
    {
        TxtEndpointResolver.AllowLiveQueries = false;
    }
}

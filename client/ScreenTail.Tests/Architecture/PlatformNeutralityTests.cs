using System.Reflection;
using System.Runtime.Versioning;
using ScreenTail.Core;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Architecture;

/// <summary>ADR-0002: Core and Shared stay platform-neutral so their tests run on any OS.</summary>
public class PlatformNeutralityTests
{
    private static readonly HashSet<string> WindowsOnlyAssemblies = new(StringComparer.Ordinal)
    {
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
        "System.Drawing.Common",
        "Microsoft.Win32.Registry",
        "System.Security.Cryptography.ProtectedData",
    };

    [Theory]
    [InlineData(typeof(CoreAssembly))]
    [InlineData(typeof(IpcContract))]
    public void TargetsNoPlatform(Type anchor)
    {
        Assert.Null(anchor.Assembly.GetCustomAttribute<TargetPlatformAttribute>());
    }

    [Theory]
    [InlineData(typeof(CoreAssembly))]
    [InlineData(typeof(IpcContract))]
    public void ReferencesNoWindowsOnlyAssembly(Type anchor)
    {
        var referenced = anchor.Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(referenced, WindowsOnlyAssemblies.Contains);
    }
}

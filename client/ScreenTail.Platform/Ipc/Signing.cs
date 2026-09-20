using System.Reflection;
using System.Runtime.Versioning;

namespace ScreenTail.Platform.Ipc;

/// <summary>
/// Whether this build is one that ships (ST-012).
///
/// Asked by the checks that must be strict in a release and permissive while somebody is working: a
/// signed build refuses to run alongside a startup hook, and a development build says so and carries on,
/// because that is how a profiler is attached.
///
/// It asks the file rather than a compile-time flag. A Release build on a developer's machine is still a
/// development build — nobody signed it — and a flag would have made "is this the real thing" a question
/// about how it was compiled rather than about what it is.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Signing
{
    private static readonly Lazy<bool> Signed = new(() =>
    {
        var image = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        return image is not null && Authenticode.VerifiedSignerThumbprint(image) is not null;
    });

    /// <summary>True when nothing signed this, which is every build until ST-112's certificate exists.</summary>
    public static bool IsDevelopmentBuild => !Signed.Value;
}

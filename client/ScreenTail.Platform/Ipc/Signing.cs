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
/// development build (nobody signed it), and a flag would have made "is this the real thing" a question
/// about how it was compiled rather than about what it is.
///
/// A development build is one that carries no signature at all, the same test both peer checks use. It
/// used to be one whose signature did not verify, so a shipped build whose signature stopped verifying
/// became a development build and took the permissive rules (2026-10-12 review, S-21). Signed and not
/// accepted is not development; it is a shipped build in trouble, and it is held to the shipped rules.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Signing
{
    private static readonly Lazy<bool> Development = new(() => IsDevelopment(Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location));

    /// <summary>True when nothing signed this, which is every build until ST-112's certificate exists.</summary>
    public static bool IsDevelopmentBuild => Development.Value;

    /// <summary>
    /// Whether the file at <paramref name="image"/> carries no signature. A process that cannot say what
    /// it started from is not one: unknown is held to the strict rule.
    /// </summary>
    internal static bool IsDevelopment(string? image) => image is not null && Authenticode.IsUnsigned(image);
}

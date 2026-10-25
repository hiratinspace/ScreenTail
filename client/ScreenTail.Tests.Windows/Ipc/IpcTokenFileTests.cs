using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ScreenTail.Core.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// ADR-0003: the token file is readable by its user and nobody else.
///
/// The Mac test checks the Unix mode. The Windows half, the DACL that <see cref="IpcTokenFile.Write"/>
/// sets before moving the file into place, was asserted nowhere, so a rule widened to Users or an
/// inherited grant left in place would have passed every test there was (2026-10-12 review, T-75).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcTokenFileTests
{
    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void TheTokenFileIsReadableOnlyByItsUserOnWindows()
    {
        var me = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(me);
        var directory = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "ipc.token");
        try
        {
            IpcTokenFile.Write(path, IpcToken.Generate());

            var security = new FileInfo(path).GetAccessControl();
            var rules = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            // Inheritance off, so the temp folder's own grants (Users can read what is created under
            // it) do not reach the token.
            Assert.True(security.AreAccessRulesProtected, "the token file still inherits its folder's rules");

            // Named one by one so a failure says which door was opened.
            foreach (var outsider in Outsiders)
            {
                Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(outsider, null)));
            }

            var only = Assert.Single(rules);
            Assert.Equal(me, only.IdentityReference);
            Assert.Equal(AccessControlType.Allow, only.AccessControlType);
            Assert.False(only.IsInherited);

            // Still ours to read, after all that.
            Assert.Equal(IpcToken.Length, IpcTokenFile.Read(path).Length);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static readonly WellKnownSidType[] Outsiders =
    [
        WellKnownSidType.WorldSid,
        WellKnownSidType.AnonymousSid,
        WellKnownSidType.AuthenticatedUserSid,
        WellKnownSidType.BuiltinUsersSid,
        WellKnownSidType.InteractiveSid,
        WellKnownSidType.NetworkSid,
    ];
}

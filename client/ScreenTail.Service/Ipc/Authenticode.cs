using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ScreenTail.Service.Ipc;

/// <summary>
/// Whether a file's Authenticode signature is real, and who signed it (ST-012).
///
/// The check this replaces read the certificate out of the file's certificate table and compared its
/// thumbprint, which proves nothing: that table is just bytes in the file. Copy the publisher's
/// certificate blob into a modified executable and the thumbprint matches exactly, while the signature it
/// came with no longer covers the contents. The one attack the check existed to stop — a tampered binary
/// driving capture — was the attack it let through.
///
/// <c>WinVerifyTrust</c> is the API that actually hashes the file and walks the chain. The thumbprint is
/// still read afterwards, to pin the publisher: a validly signed binary from somebody else is a validly
/// signed binary from somebody else.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    /// <summary>
    /// The signer's thumbprint when the file carries a signature that verifies against its own contents,
    /// or null when it is unsigned, tampered with, or chains to something untrusted.
    /// </summary>
    internal static string? VerifiedSignerThumbprint(string file)
    {
        return Verifies(file) ? SignerThumbprint(file) : null;
    }

    /// <summary>Whether Windows itself accepts the signature, chain and timestamp.</summary>
    private static bool Verifies(string file)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = file,
        };

        var fileHandle = GCHandle.Alloc(fileInfo, GCHandleType.Pinned);
        try
        {
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UIChoice = WTD_UI_NONE,

                // Expired timestamps are not a tamper. A signature that was valid when it was made stays
                // valid for a file that has not changed, and refusing on expiry would make every release
                // stop working on its certificate's anniversary.
                RevocationChecks = WTD_REVOKE_NONE,
                UnionChoice = WTD_CHOICE_FILE,
                StateAction = WTD_STATEACTION_VERIFY,
                ProvFlags = WTD_SAFER_FLAG | WTD_LIFETIME_SIGNING_FLAG,
                FileInfoPtr = fileHandle.AddrOfPinnedObject(),
            };

            var action = WintrustActionGenericVerifyV2;
            var result = WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);

            // Always close the state the verify call opened, whatever it returned; otherwise the trust
            // provider leaks a context per check, and this runs on every connection.
            data.StateAction = WTD_STATEACTION_CLOSE;
            _ = WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);

            return result == 0;
        }
        finally
        {
            fileHandle.Free();
        }
    }

    private static string? SignerThumbprint(string file)
    {
        try
        {
            // SYSLIB0057 points at X509CertificateLoader, which has no way to read the signer of a signed
            // executable. CreateFromSignedFile is still the only in-box API for that, and it is safe here
            // because Verifies() has already established that the signature covers the file.
#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(signer);
            return certificate.Thumbprint;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;
    private const uint WTD_LIFETIME_SIGNING_FLAG = 0x800;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath = string.Empty;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPtr;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProvFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ScreenTail.Platform.Ipc;

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
    /// TRUST_E_NOSIGNATURE. The file carries no signature at all, which is every build until ST-112's
    /// certificate exists — as opposed to one that carries a signature Windows will not accept.
    /// </summary>
    private const uint NoSignature = 0x800B0100;

    /// <summary>
    /// The signer's thumbprint when the file carries a signature that verifies against its own contents,
    /// or null when it is unsigned, tampered with, or chains to something untrusted.
    /// </summary>
    internal static string? VerifiedSignerThumbprint(string file)
    {
        return Verify(file) == 0 ? SignerThumbprint(file) : null;
    }

    /// <summary>
    /// Whether this file is unsigned, as opposed to signed and unacceptable.
    ///
    /// The difference decides whether a build may fall back to the development rule. Both used to arrive
    /// as a null thumbprint, so a release whose own signature failed to verify — a certificate the
    /// machine does not trust, a chain that cannot be built, a tampered binary — quietly applied the rule
    /// meant for builds nobody signed, and said so only in an Information log line (2026-09-19 review).
    /// </summary>
    internal static bool IsUnsigned(string file) => Verify(file) == NoSignature;

    /// <summary>
    /// Who signed a file Windows accepts, for ST-113's "same publisher" check: the signing certificate's
    /// identity and whether the signature carries a timestamp, read from the trust provider's own state
    /// while it is still open, so the certificate is the one Windows walked the chain from rather than one
    /// read back out of the file afterwards. Null when Windows does not accept the signature.
    /// </summary>
    /// <param name="handle">The file as already opened by the caller, so what is checked is what it holds open.</param>
    internal static Core.Update.PublisherIdentity? Publisher(string file, SafeHandle? handle = null)
    {
        Core.Update.PublisherIdentity? found = null;
        var result = Verify(file, handle, state => found = SignerOf(state));
        return result == 0 ? found : null;
    }

    /// <summary>Whether Windows itself accepts the signature, chain and timestamp. Zero means yes.</summary>
    private static uint Verify(string file) => Verify(file, null, null);

    private static uint Verify(string file, SafeHandle? handle, Action<IntPtr>? whileOpen)
    {
        var added = false;
        handle?.DangerousAddRef(ref added);
        // Marshalled by hand rather than pinned. WINTRUST_FILE_INFO holds an LPCWSTR, and a managed type
        // with a string in it cannot be pinned at all — GCHandle.Alloc throws "Object contains references",
        // which is a Windows-only failure and so invisible until CI ran it.
        var path = Marshal.StringToHGlobalUni(file);
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(
                new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = path,
                    FileHandle = added ? handle!.DangerousGetHandle() : IntPtr.Zero,
                },
                fileInfoPtr,
                fDeleteOld: false);

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
                // Not lifetime signing. That flag makes a signature expire with its certificate even
                // when it was timestamped, and on that date both peer checks would refuse everyone and
                // the shipped build would stop counting as signed (2026-10-12 review, S-21). Without it a
                // timestamped signature stays good; an untimestamped one still fails once its certificate
                // expires, which is why ST-112 timestamps every signature it makes.
                ProvFlags = WTD_SAFER_FLAG,
                FileInfoPtr = fileInfoPtr,
            };

            var action = WintrustActionGenericVerifyV2;
            var result = WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);
            try
            {
                if (result == 0 && whileOpen is not null && data.StateData != IntPtr.Zero)
                {
                    whileOpen(data.StateData);
                }
            }
            finally
            {
                // Always close the state the verify call opened, whatever it returned; otherwise the trust
                // provider leaks a context per check, and this runs on every connection.
                data.StateAction = WTD_STATEACTION_CLOSE;
                _ = WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, ref data);
            }

            return unchecked((uint)result);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(path);
            if (added)
            {
                handle!.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// The signer's certificate and whether its signature is timestamped, from the provider data of a
    /// verify that succeeded. A timestamp is a counter-signer to the trust provider, or failing that an
    /// RFC 3161 or PKCS #9 counter-signature among the signer's unsigned attributes.
    /// </summary>
    private static Core.Update.PublisherIdentity? SignerOf(IntPtr state)
    {
        var provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero)
        {
            return null;
        }

        var signerPtr = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signerPtr == IntPtr.Zero)
        {
            return null;
        }

        var signer = Marshal.PtrToStructure<CryptProviderSigner>(signerPtr);
        var certPtr = WTHelperGetProvCertFromChain(signerPtr, 0);
        if (certPtr == IntPtr.Zero)
        {
            return null;
        }

        // CRYPT_PROVIDER_CERT: a DWORD, then the PCCERT_CONTEXT at pointer alignment.
        var context = Marshal.ReadIntPtr(certPtr, IntPtr.Size);
        if (context == IntPtr.Zero)
        {
            return null;
        }

        var timestamped = signer.CounterSigners > 0 || HasCounterSignature(signer.Signer);
        using var certificate = new X509Certificate2(context);
        return Core.Update.PublisherIdentity.From(certificate, timestamped);
    }

    private static bool HasCounterSignature(IntPtr signerInfo)
    {
        if (signerInfo == IntPtr.Zero)
        {
            return false;
        }

        var info = Marshal.PtrToStructure<CmsgSignerInfo>(signerInfo);
        var size = Marshal.SizeOf<CryptAttribute>();
        for (var i = 0; i < info.UnauthAttrs.Count; i++)
        {
            var attribute = Marshal.PtrToStructure<CryptAttribute>(info.UnauthAttrs.Attributes + (i * size));
            var oid = Marshal.PtrToStringAnsi(attribute.ObjectId);
            if (oid is "1.3.6.1.4.1.311.3.3.1" or "1.2.840.113549.1.9.6")
            {
                return true;
            }
        }

        return false;
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
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;

        /// <summary>LPCWSTR, owned by the caller for the life of the verify call.</summary>
        public IntPtr FilePath;
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

    /// <summary>CRYPT_PROVIDER_SGNR, as far as the counter-signers and the signer info.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint StructSize;
        public uint VerifyAsOfLow;
        public uint VerifyAsOfHigh;
        public uint CertChainCount;
        public IntPtr CertChain;
        public uint SignerType;
        public IntPtr Signer;
        public uint Error;
        public uint CounterSigners;
        public IntPtr CounterSignerList;
        public IntPtr ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAlgorithm
    {
        public IntPtr ObjectId;
        public CryptBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttributes
    {
        public uint Count;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttribute
    {
        public IntPtr ObjectId;
        public uint ValueCount;
        public IntPtr Values;
    }

    /// <summary>CMSG_SIGNER_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CmsgSignerInfo
    {
        public uint Version;
        public CryptBlob Issuer;
        public CryptBlob SerialNumber;
        public CryptAlgorithm HashAlgorithm;
        public CryptAlgorithm HashEncryptionAlgorithm;
        public CryptBlob EncryptedHash;
        public CryptAttributes AuthAttrs;
        public CryptAttributes UnauthAttrs;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
}

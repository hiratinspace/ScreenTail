using System.Runtime.Versioning;
using System.Security.Cryptography;
using ScreenTail.Core.Store;

namespace ScreenTail.Service.Store;

/// <summary>
/// The store key on Windows: 32 random bytes generated on first use and kept on disk under DPAPI with
/// <see cref="DataProtectionScope.CurrentUser"/>. Only this Windows user on this machine can unprotect
/// it, so a copied database (or key file) is useless to anyone else. Tested on the laptop runner
/// (ScreenTail.Tests.Windows, DifferentUserCannotDecrypt); the store logic itself is tested everywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProvider(string keyFilePath) : IStoreKeyProvider
{
    // Binds the protected blob to this purpose; not a secret.
    private static readonly byte[] Entropy = "ScreenTail.store.key.v1"u8.ToArray();

    public static string DefaultKeyFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail",
        "store.key");

    public byte[] GetKey()
    {
        if (!File.Exists(keyFilePath))
        {
            CreateKeyFile();
        }

        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(keyFilePath), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new StoreKeyException("The store key belongs to a different Windows user or machine.", ex);
        }
    }

    private void CreateKeyFile()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
            var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);

            // CreateNew: if another process won the race, keep its key and read that instead.
            try
            {
                using var stream = new FileStream(keyFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(protectedKey);
            }
            catch (IOException) when (File.Exists(keyFilePath))
            {
            }
        }
        finally
        {
            Array.Clear(key);
        }
    }
}

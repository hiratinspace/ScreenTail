using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenTail.Core.Net;

namespace ScreenTail.Service.Store;

/// <summary>
/// The device's refresh token on disk (ST-010), under DPAPI for this Windows user the way the store key
/// is (<see cref="DpapiKeyProvider"/>): a copied file is useless to anyone else. A file that cannot be
/// read is treated as no credential, so a profile moved between machines asks to be activated again
/// rather than failing every backend call forever.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiDeviceCredentials(string path) : IDeviceCredentials
{
    private static readonly byte[] Entropy = "ScreenTail.device.credential.v1"u8.ToArray();

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail",
        "device.cred");

    public DeviceCredential? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<DeviceCredential>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(DeviceCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sealedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credential), Entropy, DataProtectionScope.CurrentUser);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, sealedBytes);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Removes the token and the copy an interrupted <see cref="Save"/> leaves beside it, which holds the
    /// same token ("delete everything", INV-12). Both are tried even when the first will not go, and then
    /// the first failure is thrown, so the caller still knows something was left.
    /// </summary>
    public void Clear()
    {
        Exception? failed = null;
        foreach (var file in (string[])[path, path + ".tmp"])
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed ??= ex;
            }
        }

        if (failed is not null)
        {
            ExceptionDispatchInfo.Throw(failed);
        }
    }
}

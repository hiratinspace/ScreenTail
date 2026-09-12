using System.Security.Cryptography;
using System.Text;

namespace ScreenTail.Core.Ipc;

/// <summary>One pipe per user (ADR-0003). The identity is hashed so the name stays short and safe.</summary>
public static class IpcPipeNames
{
    public static string ForUser(string userIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentity);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity));
        return "ScreenTail." + Convert.ToHexString(hash.AsSpan(0, 8));
    }
}

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ScreenTail.Platform.Ipc;

/// <summary>ADR-0003 rule 1: the pipe's DACL admits only the user the service runs as.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPipeFactory
{
    public static Func<NamedPipeServerStream> ForCurrentUser(string pipeName)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Cannot determine the current Windows user.");
        var security = new PipeSecurity();
        security.SetAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return () => NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    /// <summary>The per-user pipe name from the user's SID.</summary>
    public static string PipeNameForCurrentUser()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return Core.Ipc.IpcPipeNames.ForUser(sid);
    }
}

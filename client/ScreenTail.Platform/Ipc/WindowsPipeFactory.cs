using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ScreenTail.Platform.Ipc;

/// <summary>
/// ADR-0003 rule 1: the pipe's DACL admits only the user the service runs as, and the pipe's name is the
/// service's own.
///
/// The service runs as the signed-in technician (started from the Run key in their session, never as
/// SYSTEM), and so does the UI, so both ends are always the same account.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPipeFactory
{
    /// <summary>
    /// How many refusals in a row before a held name is an error rather than a warning. A service that
    /// crashed can leave its pipe behind for as long as a UI holds its end, which clears in a few seconds
    /// once the UI notices; a name still held after that is worth an error.
    /// </summary>
    public const int HeldIsAnErrorAfter = 5;

    /// <summary>
    /// The service's pipe instances, one per call: the server listens on one and keeps one per client.
    ///
    /// <b>The first instance claims the name</b> (<see cref="PipeOptions.FirstPipeInstance"/>). Pipe names
    /// are machine-wide, and without the flag a pipe of the same name made earlier by another account
    /// would not stop the service: its instances would join that pipe, under that account's DACL, and a
    /// UI could be answered by either. With it, a name already held refuses the service's first instance.
    /// Every later instance joins the pipe the first one made, and is checked to have done so: a name lost
    /// in a gap with no instance open belongs to whoever took it, and is refused the same way.
    ///
    /// A refusal is <see cref="PipeNameHeldException"/>, an <see cref="IOException"/>, so the server goes
    /// on retrying and takes the name once it is free. <paramref name="nameHeld"/> is told each refusal,
    /// with how many there have been in a row, for the service's log; the count starts again once the
    /// name is the service's. The UI meanwhile reaches nothing it will trust and says the capture service
    /// is not running.
    /// </summary>
    public static Func<NamedPipeServerStream> ForCurrentUser(string pipeName, Action<int>? nameHeld = null)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Cannot determine the current Windows user.");
        var security = new PipeSecurity();
        security.SetAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        var gate = new object();
        string? descriptor = null;
        var refusals = 0;

        return () =>
        {
            lock (gate)
            {
                if (descriptor is null)
                {
                    NamedPipeServerStream first;
                    try
                    {
                        first = Create(pipeName, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, security);
                    }
                    catch (UnauthorizedAccessException refused)
                    {
                        // What CreateNamedPipe answers when FILE_FLAG_FIRST_PIPE_INSTANCE meets a name
                        // that exists, and when a pipe of that name will not let this account in.
                        throw Held(refused);
                    }

                    descriptor = DescriptorOf(first);
                    if (descriptor is null)
                    {
                        first.Dispose();
                        throw Held(null);
                    }

                    refusals = 0;
                    return first;
                }

                // All instances of one pipe share its security descriptor, so an instance of the pipe the
                // first one made has the first one's owner and DACL, and an instance of anybody else's has
                // theirs, even one of this account's that lets it in.
                NamedPipeServerStream next;
                try
                {
                    next = Create(pipeName, PipeOptions.Asynchronous, security);
                }
                catch (UnauthorizedAccessException refused)
                {
                    // This account always may add an instance to its own pipe, so a pipe that refuses it
                    // is somebody else's.
                    descriptor = null;
                    throw Held(refused);
                }

                if (!string.Equals(DescriptorOf(next), descriptor, StringComparison.Ordinal))
                {
                    next.Dispose();
                    descriptor = null;
                    throw Held(null);
                }

                return next;
            }
        };

        PipeNameHeldException Held(Exception? inner)
        {
            nameHeld?.Invoke(++refusals);
            return new PipeNameHeldException(PipeNameHeldException.Explanation, inner);
        }
    }

    /// <summary>The per-user pipe name from the user's SID.</summary>
    public static string PipeNameForCurrentUser()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return Core.Ipc.IpcPipeNames.ForUser(sid);
    }

    private static NamedPipeServerStream Create(string pipeName, PipeOptions options, PipeSecurity security) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            security);

    /// <summary>
    /// The pipe's owner and DACL, as text, or null when Windows will not say, which is treated as not ours.
    /// </summary>
    private static string? DescriptorOf(NamedPipeServerStream pipe)
    {
        try
        {
            return pipe.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// The capture pipe's name is held by another pipe, possibly this service's previous run still held open
/// by a UI (2026-11-15 review). An <see cref="IOException"/>, so the server's accept loop backs off and
/// tries again.
/// </summary>
public sealed class PipeNameHeldException : IOException
{
    /// <summary>What it says when nothing more specific was given. No path and no account, just the fact.</summary>
    public const string Explanation = "The capture pipe's name is held by another pipe (possibly this service's previous run), so the UI cannot reach this service until it is released.";

    public PipeNameHeldException()
        : base(Explanation)
    {
    }

    public PipeNameHeldException(string message)
        : base(message)
    {
    }

    public PipeNameHeldException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

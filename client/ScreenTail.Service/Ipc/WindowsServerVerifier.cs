using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ScreenTail.Core.Ipc;

namespace ScreenTail.Service.Ipc;

/// <summary>
/// ST-012's third acceptance criterion: a modified service binary means the UI refuses to connect.
///
/// The mirror of <see cref="WindowsClientVerifier"/>, and the half that did not exist. The service has
/// always checked who was calling it; nothing checked who was answering, so the UI connected to whatever
/// held the pipe name and wrote the session token to it. A per-user pipe needs no privilege to squat, and
/// the payoff is complete: the impostor gets the token, and then decides what the technician is told about
/// capture state. INV-4 says capture is always visibly indicated, and this is how that guarantee is
/// defeated without touching capture at all.
///
/// The rule is the same one ADR-0003 already states, read the other way round: the process on the far end
/// must be Authenticode-signed by our publisher, or — in an unsigned dev build — live in the same
/// directory as this executable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServerVerifier : IServerVerifier
{
    private readonly string _uiExecutable;
    private readonly string? _publisherThumbprint;

    /// <param name="uiExecutable">This process's own image. The dev-build fallback compares against it.</param>
    public WindowsServerVerifier(string uiExecutable)
    {
        _uiExecutable = Path.GetFullPath(uiExecutable);
        _publisherThumbprint = Authenticode.VerifiedSignerThumbprint(_uiExecutable);
    }

    public bool UiIsSigned => _publisherThumbprint is not null;

    public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not NamedPipeClientStream client)
        {
            return ValueTask.FromResult<string?>("not_a_client_pipe");
        }

        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid))
        {
            // Windows will not say who is listening. Refusing is the only safe reading: the question was
            // "is this the service", and "cannot tell" is not "yes".
            return ValueTask.FromResult<string?>("server_pid_unknown");
        }

        string? serverExecutable;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            serverExecutable = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult<string?>("server_process_unreadable");
        }

        return ValueTask.FromResult(serverExecutable is null ? "server_path_unknown" : Verify(serverExecutable));
    }

    /// <summary>The rule itself, separated from the pipe so it can be tested with plain paths.</summary>
    public string? Verify(string serverExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExecutable);
        var server = Path.GetFullPath(serverExecutable);

        if (_publisherThumbprint is not null)
        {
            var serverThumbprint = Authenticode.VerifiedSignerThumbprint(server);
            return serverThumbprint is not null && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(serverThumbprint),
                Convert.FromHexString(_publisherThumbprint))
                ? null
                : "unsigned_or_other_publisher";
        }

        var sameDirectory = string.Equals(
            Path.GetDirectoryName(server),
            Path.GetDirectoryName(_uiExecutable),
            StringComparison.OrdinalIgnoreCase);
        return sameDirectory ? null : "dev_build_outside_ui_directory";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}

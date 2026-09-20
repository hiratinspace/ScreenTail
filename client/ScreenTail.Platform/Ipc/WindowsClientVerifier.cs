using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ScreenTail.Core.Ipc;

namespace ScreenTail.Platform.Ipc;

/// <summary>
/// ADR-0003 rule 2: the connecting process's executable must be Authenticode-signed by the same publisher
/// as the service. When the service itself is unsigned (a development build), the client executable must
/// sit in the service's own directory instead. Reasons returned here go into audit rows, so they name
/// the rule, never the path.
///
/// "Signed by" means <see cref="Authenticode"/> verified the signature against the file's contents, not
/// that a matching certificate was found in it — see there for why that distinction is the whole point.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClientVerifier : IClientVerifier
{
    private readonly string _serviceExecutable;
    private readonly string? _publisherThumbprint;

    public WindowsClientVerifier(string serviceExecutable)
    {
        _serviceExecutable = Path.GetFullPath(serviceExecutable);
        _publisherThumbprint = Authenticode.VerifiedSignerThumbprint(_serviceExecutable);
    }

    public bool ServiceIsSigned => _publisherThumbprint is not null;

    public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not NamedPipeServerStream server)
        {
            return ValueTask.FromResult<string?>("not_a_server_pipe");
        }

        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid))
        {
            return ValueTask.FromResult<string?>("client_pid_unknown");
        }

        // Asked of the kernel, and held open while the answer is used. Process.MainModule reads the
        // target's own loader data, which a same-user process can rewrite to name the real signed
        // executable — so the check would go and verify a genuine file on disk while talking to an
        // impostor. And a process id is reused the moment its process exits, so the handle is what makes
        // the file we verify and the peer we are talking to the same thing (2026-09-19 review).
        using var image = ProcessImage.Of(pid);
        return ValueTask.FromResult(image is null ? "client_process_unreadable" : Verify(image.Path));
    }

    /// <summary>The rule itself, separated from the pipe so it can be tested with plain paths.</summary>
    public string? Verify(string clientExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientExecutable);
        var client = Path.GetFullPath(clientExecutable);

        if (_publisherThumbprint is not null)
        {
            var clientThumbprint = Authenticode.VerifiedSignerThumbprint(client);
            return clientThumbprint is not null && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(clientThumbprint),
                Convert.FromHexString(_publisherThumbprint))
                ? null
                : "unsigned_or_other_publisher";
        }

        // Only a build that carries no signature at all may fall back to the directory rule. Our own
        // signature failing to verify — an untrusted certificate, a chain that will not build, a
        // tampered binary — used to arrive here as the same null thumbprint, so a release quietly
        // applied the rule meant for builds nobody signed (2026-09-19 review). A release that cannot
        // vouch for itself vouches for nothing.
        if (!Authenticode.IsUnsigned(_serviceExecutable))
        {
            return "own_signature_unverifiable";
        }

        var sameDirectory = string.Equals(
            Path.GetDirectoryName(client),
            Path.GetDirectoryName(_serviceExecutable),
            StringComparison.OrdinalIgnoreCase);
        return sameDirectory ? null : "dev_build_outside_service_directory";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ScreenTail.Core.Ipc;

namespace ScreenTail.Service.Ipc;

/// <summary>
/// ADR-0003 rule 2: the connecting process's executable must be Authenticode-signed by the same publisher
/// as the service. When the service itself is unsigned (a development build), the client executable must
/// sit in the service's own directory instead. Reasons returned here go into audit rows, so they name
/// the rule, never the path.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClientVerifier : IClientVerifier
{
    private readonly string _serviceExecutable;
    private readonly string? _publisherThumbprint;

    public WindowsClientVerifier(string serviceExecutable)
    {
        _serviceExecutable = Path.GetFullPath(serviceExecutable);
        _publisherThumbprint = SignerThumbprint(_serviceExecutable);
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

        string? clientExecutable;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            clientExecutable = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult<string?>("client_process_unreadable");
        }

        return ValueTask.FromResult(clientExecutable is null ? "client_path_unknown" : Verify(clientExecutable));
    }

    /// <summary>The rule itself, separated from the pipe so it can be tested with plain paths.</summary>
    public string? Verify(string clientExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientExecutable);
        var client = Path.GetFullPath(clientExecutable);

        if (_publisherThumbprint is not null)
        {
            var clientThumbprint = SignerThumbprint(client);
            return clientThumbprint is not null && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(clientThumbprint),
                Convert.FromHexString(_publisherThumbprint))
                ? null
                : "unsigned_or_other_publisher";
        }

        var sameDirectory = string.Equals(
            Path.GetDirectoryName(client),
            Path.GetDirectoryName(_serviceExecutable),
            StringComparison.OrdinalIgnoreCase);
        return sameDirectory ? null : "dev_build_outside_service_directory";
    }

    private static string? SignerThumbprint(string executable)
    {
        try
        {
            // SYSLIB0057 points at X509CertificateLoader, which has no way to read the signer of a signed
            // executable; CreateFromSignedFile is the only in-box API for that. ST-012 replaces this thumbprint
            // match with WinVerifyTrust, which also validates the chain and timestamp.
#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(executable);
#pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(signer);
            return certificate.Thumbprint;
        }
        catch (CryptographicException)
        {
            return null; // not signed
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}

using System.Runtime.Versioning;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Service.Host;

/// <summary>
/// Where the capture service keeps its files and which pipe it serves (ST-112).
///
/// The service always runs with <see cref="Default"/>: <c>%LOCALAPPDATA%\ScreenTail</c> and the per-user
/// pipe. Nothing reads another set from the command line or the environment.
///
/// It exists so the Windows tests can run the real <see cref="CaptureHost"/>, recording, and time how long it
/// takes to stop when Windows asks, without touching the technician's data folder or the pipe a real
/// ScreenTail on the same machine serves.
///
/// <b>The secrets are always inside the data folder, under their fixed names</b> (INV-12). The store's key,
/// the device's credential and the IPC token are derived from <see cref="DataDirectory"/>, never given, so
/// "Delete everything" (<c>LocalDataEraser</c>), which removes those names from that folder, always reaches
/// them. A set that put the key somewhere else would leave it behind after an erasure.
/// </summary>
/// <param name="DataDirectory">The store, its key, the device's credential, the IPC token and the settings files. Erased by "Delete everything".</param>
/// <param name="PipeName">The pipe the UI connects to.</param>
/// <param name="ModelDirectory">Where the speech model is kept; null for the recogniser's own default.</param>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed record CaptureHostPaths(string DataDirectory, string PipeName, string? ModelDirectory)
{
    /// <summary>The store's key, under DPAPI, by the name the eraser removes first.</summary>
    public const string KeyFileName = "store.key";

    /// <summary>The device's refresh token, under DPAPI.</summary>
    public const string DeviceCredentialsFileName = "device.cred";

    /// <summary>This run's pipe token, for the UI.</summary>
    public const string IpcTokenFileName = "ipc.token";

    public string DataDirectory { get; } = Path.GetFullPath(DataDirectory);

    public string KeyFile => Path.Combine(DataDirectory, KeyFileName);

    public string DeviceCredentials => Path.Combine(DataDirectory, DeviceCredentialsFileName);

    public string IpcTokenFile => Path.Combine(DataDirectory, IpcTokenFileName);

    /// <summary>The product's: the only set the service itself ever uses.</summary>
    public static CaptureHostPaths Default() => new(CaptureHost.DataDirectory, WindowsPipeFactory.PipeNameForCurrentUser(), ModelDirectory: null);
}

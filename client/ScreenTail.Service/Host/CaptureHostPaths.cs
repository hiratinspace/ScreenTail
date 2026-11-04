using System.Runtime.Versioning;
using ScreenTail.Platform.Ipc;
using ScreenTail.Service.Store;

namespace ScreenTail.Service.Host;

/// <summary>
/// Where the capture service keeps its files and which pipe it serves (ST-112).
///
/// The service always runs with <see cref="Default"/>: <c>%LOCALAPPDATA%\ScreenTail</c>, the store's DPAPI
/// key, the device's credential and the IPC token beside it, the speech model under it, and the per-user
/// pipe. Nothing reads another set from the command line or the environment.
///
/// It exists so the Windows tests can run the real <see cref="CaptureHost"/>, recording, and time how long it
/// takes to stop when Windows asks, without touching the technician's data folder or the pipe a real
/// ScreenTail on the same machine serves.
/// </summary>
/// <param name="DataDirectory">The store and the settings files. Erased by "Delete everything".</param>
/// <param name="KeyFile">The store's key, under DPAPI.</param>
/// <param name="DeviceCredentials">The device's refresh token, under DPAPI.</param>
/// <param name="IpcTokenFile">Where this run's pipe token is written for the UI.</param>
/// <param name="PipeName">The pipe the UI connects to.</param>
/// <param name="ModelDirectory">Where the speech model is kept; null for the recogniser's own default.</param>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed record CaptureHostPaths(
    string DataDirectory,
    string KeyFile,
    string DeviceCredentials,
    string IpcTokenFile,
    string PipeName,
    string? ModelDirectory)
{
    /// <summary>The product's: the only set the service itself ever uses.</summary>
    public static CaptureHostPaths Default() => new(
        CaptureHost.DataDirectory,
        DpapiKeyProvider.DefaultKeyFilePath,
        DpapiDeviceCredentials.DefaultPath,
        Core.Ipc.IpcTokenFile.DefaultPath,
        WindowsPipeFactory.PipeNameForCurrentUser(),
        ModelDirectory: null);
}

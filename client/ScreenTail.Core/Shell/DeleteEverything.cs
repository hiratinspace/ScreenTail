using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

public sealed class DeleteEverything(
    ShellState state,
    Func<Func<int, IpcCommand>, CancellationToken, Task<ConfirmationIssued?>> request,
    Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> send)
{
    public const string WhileCapturing = "stub";

    public ShellState State { get; } = state;

    public string? BlockedBecause() => State.Snapshot.KnownCapture is null ? ConfirmedAction.NotConnected : null;

    public Task<ConfirmedResult> RunAsync(Func<string, Task<bool>> typed, CancellationToken ct = default) =>
        ConfirmedAction.RunAsync(
            (action, c) => request(id => new RequestConfirmationCommand { RequestId = id, Action = action }, c),
            "erase_everything",
            typed,
            token => id => new EraseAllLocalDataCommand { RequestId = id, Confirmation = token },
            send,
            ct);
}

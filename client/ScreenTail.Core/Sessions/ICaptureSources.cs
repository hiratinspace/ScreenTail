using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Sessions;

/// <summary>
/// What the state machine starts and stops: hooks (ST-024), screenshots (ST-025/026), speech (ST-027).
/// Sources write only through the machine's gated methods, which is how INV-6 holds regardless of what
/// a source does while paused or suppressed.
/// </summary>
public interface ICaptureSources
{
    Task StartAsync(SessionMachine machine, CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    /// <summary>Force a frame now (mark moment, ST-029), regardless of debounce.</summary>
    Task MarkMomentAsync(CancellationToken ct = default);
}

/// <summary>Produces the draft note after finalize (ST-060/063 cloud, ST-065 local).</summary>
public interface IDrafter
{
    Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default);
}

/// <param name="Reason">Plain-language reason shown in Review when <paramref name="Draft"/> is null; no content.</param>
public sealed record DraftOutcome(DraftNote? Draft, string? Reason)
{
    public bool Succeeded => Draft is not null;

    public static DraftOutcome Success(DraftNote draft) => new(draft, null);

    public static DraftOutcome Failure(string reason) => new(null, reason);
}

/// <summary>Until the capture tickets land: nothing to start, nothing to stop.</summary>
public sealed class NoCaptureSources : ICaptureSources
{
    public Task StartAsync(SessionMachine machine, CancellationToken ct = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task MarkMomentAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Until ST-060/063: every session ends in draft_failed with a clear reason.</summary>
public sealed class UnavailableDrafter : IDrafter
{
    public const string ReasonText = "Drafting isn't available yet; it arrives with the summarization tickets (ST-060, ST-063).";

    public Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(DraftOutcome.Failure(ReasonText));
}

namespace ScreenTail.Core.Sessions;

/// <summary>
/// How a discard by name ended (contract v6). Done when every session named was discarded; otherwise
/// none was, and these say which ids stood in the way and why.
/// </summary>
/// <param name="Unknown">No such session: never recorded here, or the tray already deleted it.</param>
/// <param name="AlreadyDiscarded">Discarded before, so there is nothing left of it to destroy.</param>
/// <param name="Published">Its note is in the PSA. Kept until retention (owner decision, fail closed).</param>
public sealed record NamedDiscardOutcome(
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> AlreadyDiscarded,
    IReadOnlyList<string> Published)
{
    public bool Done => Unknown.Count == 0 && AlreadyDiscarded.Count == 0 && Published.Count == 0;
}

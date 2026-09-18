namespace ScreenTail.Api.Providers;

/// <summary>
/// Why a provider call failed, in the few kinds a caller can do something different about.
///
/// The list is deliberately short. A taxonomy with thirty entries becomes thirty branches nobody writes,
/// and every one that is not handled falls through to "something went wrong", which is the message Spec
/// §4 exists to forbid. These five are the ones where the next step genuinely differs.
/// </summary>
public enum ProviderErrorKind
{
    /// <summary>The credentials are wrong, missing or expired. Settings, not retry.</summary>
    Unauthenticated,

    /// <summary>The credentials are right and this account may not do it. An administrator, not retry.</summary>
    Forbidden,

    /// <summary>The ticket, company or article is not there. A different target, not retry.</summary>
    NotFound,

    /// <summary>The provider refused what we sent. A fix here, not retry.</summary>
    Invalid,

    /// <summary>Rate limit, timeout, outage. Retry is the right answer, and only for this one.</summary>
    Unavailable,
}

/// <summary>
/// A provider failure, written the way Spec §4 requires: what happened, then what to do.
///
/// <b>The message is shown to a technician verbatim</b>, so it carries the provider's own words about
/// what it rejected and never a stack trace, a URL with a path, or a token. A message that says
/// "something went wrong" costs a support call; one that quotes a header back costs nothing and answers
/// the question.
/// </summary>
/// <param name="What">What happened, in a sentence. "ConnectWise rejected the request: clientId header missing."</param>
/// <param name="Todo">What to do about it. "Add your clientId in Settings → Integrations."</param>
/// <param name="RetryAfter">When the provider said to come back, if it did.</param>
public sealed record ProviderError(ProviderErrorKind Kind, string What, string Todo, TimeSpan? RetryAfter = null)
{
    /// <summary>
    /// Whether trying again could work. True only for <see cref="ProviderErrorKind.Unavailable"/>.
    ///
    /// Retrying anything else is how a wrong API key becomes an account lockout, and how an invalid
    /// payload becomes the same rejection several hundred times.
    /// </summary>
    public bool Retryable => Kind == ProviderErrorKind.Unavailable;

    /// <summary>Spec §4's pattern: <c>&lt;What happened&gt;. &lt;What to do&gt;.</c></summary>
    public override string ToString() => $"{What} {Todo}";
}

/// <summary>
/// What a provider call returns: the thing, or why not. Never both, never an exception for a failure the
/// caller is expected to handle.
///
/// Exceptions are for bugs. A PSA being down, a key being wrong and a ticket being closed are all
/// ordinary outcomes of a publish, and each has a different thing for the technician to do — so they
/// arrive as values that the compiler makes the caller look at.
/// </summary>
public readonly record struct ProviderResult<T>
{
    private ProviderResult(T? value, ProviderError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public ProviderError? Error { get; }

    public bool Ok => Error is null;

    internal static ProviderResult<T> From(T value) => new(value, null);

    internal static ProviderResult<T> From(ProviderError error) =>
        new(default, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>
/// Builds a <see cref="ProviderResult{T}"/>.
///
/// The factories live here rather than on the generic type because a static member on a generic type has
/// to be reached through a type argument the caller is otherwise inferring — <c>ProviderResult&lt;IReadOnlyList&lt;TicketRef&gt;&gt;.Success(x)</c>
/// rather than <c>ProviderResult.Success(x)</c>.
/// </summary>
public static class ProviderResult
{
    public static ProviderResult<T> Success<T>(T value) => ProviderResult<T>.From(value);

    public static ProviderResult<T> Failure<T>(ProviderError error) => ProviderResult<T>.From(error);
}

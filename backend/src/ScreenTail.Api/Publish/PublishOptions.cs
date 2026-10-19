namespace ScreenTail.Api.Publish;

/// <summary>
/// How long one publish may take before it answers with what it has (2026-10-12 review, S-68).
///
/// Counted from when the request arrived (<see cref="Endpoints.RequestStart"/>), as the request's own
/// timeout is, so the time the body took to upload is part of it. Shorter than that timeout (90 s in
/// Program.cs), which answers with an empty 504, and so shorter than the client's 100 s. Long enough for
/// a slow PSA to take a note and its screenshots. <c>ThePublishDeadlineEndsBeforeTheRequestDoes</c> holds
/// the two apart.
/// </summary>
public sealed class PublishOptions
{
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(75);
}

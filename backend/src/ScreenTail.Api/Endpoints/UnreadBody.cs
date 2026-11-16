namespace ScreenTail.Api.Endpoints;

/// <summary>
/// What a route refused before reading its body does with the body before it answers (2026-11-15 review).
///
/// Kestrel drains a body the application left unread for about five seconds after the answer, then
/// drops the connection. A large publish over a slow uplink is still uploading at that point, so the
/// client saw the connection reset rather than the refusal, and read it as a publish that may have
/// landed. Reading the rest into nothing first means the refusal is what arrives. Nothing is kept: the
/// bytes go to <see cref="Stream.Null"/> through the copy's one small buffer, the route's declared body
/// limit still applies while they are read, and the request's timeout still ends it.
/// </summary>
public static class UnreadBody
{
    /// <summary>
    /// Reads and discards the rest of the request body. False when it could not: the body was over the
    /// route's limit (the status is then set to the server's answer, usually 413), or the client went
    /// away or the request timed out, when nobody is left to answer.
    /// </summary>
    public static async Task<bool> DiscardAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted).ConfigureAwait(false);
            return true;
        }
        catch (BadHttpRequestException tooLarge)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = tooLarge.StatusCode;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return false;
        }
    }
}

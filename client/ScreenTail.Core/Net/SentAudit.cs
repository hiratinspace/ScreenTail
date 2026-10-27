using Microsoft.Extensions.Logging;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Store;

namespace ScreenTail.Core.Net;

/// <summary>
/// The <c>bundle_sent</c> audit row for a request the backend answered: the draft, the publish call and
/// the metric (ST-045; 2026-10-12 review, S-15). The audit export's "bytes sent, and to which host" section
/// is made of these rows. One writer for all three, so they are written the same way.
///
/// The host and the count, never the body or the path (INV-10). Written once the answer has been read,
/// and a row that cannot be written is reported by its exception's type and changes nothing: the answer
/// stands. A request that threw before an answer came back is not recorded, because whether its bytes
/// left is unknown, and the audit log does not guess.
/// </summary>
internal static partial class SentAudit
{
    /// <param name="bytes">The request body's length, read before it was sent. Null records nothing.</param>
    /// <param name="destination">
    /// Where it went: the request's own address, absolute once HttpClient has sent it. A relative one is
    /// resolved against <paramref name="baseAddress"/>, for a handler that did not keep the absolute form.
    /// </param>
    public static async Task RecordAsync(IAuditLog? audit, string sessionId, long? bytes, Uri? destination, Uri? baseAddress, ILogger? logger)
    {
        if (audit is null || bytes is not { } count)
        {
            return;
        }

        var absolute = destination is { IsAbsoluteUri: true } ? destination
            : baseAddress is { } @base && destination is { } relative ? new Uri(@base, relative)
            : null;
        if (absolute is null)
        {
            return;
        }

        try
        {
            // Not cancellable: the bytes have already left, and a shutdown in between should not leave
            // them unrecorded.
            await audit.RecordAsync(AuditTypes.BundleSent, sessionId, count, HostLabel(absolute), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The answer is in hand already; nothing about the audit write may undo that.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (logger is not null)
            {
                LogAuditFailed(logger, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The host as an audit label: an address without its brackets, an international name in its ASCII
    /// form. Anything still outside what a label may hold (a zone id, say) is recorded as
    /// <see cref="DraftSender.UnnamedHost"/>, so the bytes are still counted.
    /// </summary>
    internal static string HostLabel(Uri destination)
    {
        var host = destination.IdnHost.Trim('[', ']');
        return AuditDetail.IsValid(host) && host.Length > 0 ? host : DraftSender.UnnamedHost;
    }

    [LoggerMessage(EventId = 6402, Level = LogLevel.Warning, Message = "A bundle_sent audit row could not be written ({ExceptionType}); the backend's answer is kept.")]
    private static partial void LogAuditFailed(ILogger logger, string exceptionType);
}

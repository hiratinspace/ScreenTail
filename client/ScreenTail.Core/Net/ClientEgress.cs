using ScreenTail.Core.Speech;

namespace ScreenTail.Core.Net;

/// <summary>
/// The one egress shape the client's processes build (ST-046, INV-8).
///
/// The capture service and the <c>--transcribe</c> tool both fetch speech models through the guard, and
/// each used to name its own hosts: the service named the model hosts and the tool named none, so the
/// tool could never download a model (2026-10-12 review, T-90). One function, asked by both, and
/// <c>ClientEgressTests</c> holds it to every hop a download makes.
/// </summary>
public static class ClientEgress
{
    /// <summary>
    /// The speech model hosts, and the one backend host on both the backend list and the publish list.
    /// Publishing goes to the backend too, because the PSA's credentials live there and never here, so
    /// under local-only the purpose a sender attaches is what separates a publish from a draft (INV-8).
    /// No backend means both of those lists are empty and only a model may leave.
    /// </summary>
    public static EgressSettings Settings(Uri? backend) => EgressSettings.ForBackend(backend, SpeechModels.Hosts);
}

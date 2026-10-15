using System.IO.Pipes;
using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// The verifiers' VerifyAsync, over a real pipe to a real other process (2026-10-12 review, T-80).
///
/// Only <c>Verify(path)</c> had a test, which is the rule. What decides which path the rule is given is
/// VerifyAsync: which end of the pipe it asks Windows about, and how it turns that process into a file.
/// Ask about the wrong end and every check here verifies this process instead of the peer.
///
/// Each verifier gets two anchors, and the pair is what makes a swapped question visible whichever way
/// the build is signed. Anchored at the peer's own executable, the peer passes (same publisher, or same
/// directory for an unsigned file), and this process does not. Anchored at this process, the peer fails,
/// and this process would pass.
///
/// Once connected, the peer's DACL is cut down to the right to ask what it is. A VerifyAsync that went
/// back to <c>Process.MainModule</c> would read the same path off an ordinary peer; against this one it is
/// refused, and the peer anchored at itself stops passing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVerifierPipeTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(30);

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public async Task TheUiAsksAboutTheServerAndNotAboutItself()
    {
        Assert.SkipUnless(PipePeer.Available, "Windows PowerShell not found");
        var name = PipeName();
        using var peer = PipePeer.Serving(name);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(Soon, TestContext.Current.CancellationToken);
        Restrict(peer, nameof(TheUiAsksAboutTheServerAndNotAboutItself));

        var anchoredAtPeer = await new WindowsServerVerifier(PipePeer.Executable).VerifyAsync(pipe, TestContext.Current.CancellationToken);
        var anchoredHere = await new WindowsServerVerifier(Environment.ProcessPath!).VerifyAsync(pipe, TestContext.Current.CancellationToken);

        peer.AssertStillRunning();
        Assert.Null(anchoredAtPeer);
        Assert.NotNull(anchoredHere);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public async Task TheServiceAsksAboutTheClientAndNotAboutItself()
    {
        Assert.SkipUnless(PipePeer.Available, "Windows PowerShell not found");
        var name = PipeName();
        // The peer first, so it is disposed last: it leaves by itself once this end closes. It waits for
        // the pipe to exist before connecting.
        using var peer = PipePeer.Connecting(name);
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            waiting.CancelAfter(Soon);
            await pipe.WaitForConnectionAsync(waiting.Token);
        }

        Restrict(peer, nameof(TheServiceAsksAboutTheClientAndNotAboutItself));

        var anchoredAtPeer = await new WindowsClientVerifier(PipePeer.Executable).VerifyAsync(pipe, TestContext.Current.CancellationToken);
        var anchoredHere = await new WindowsClientVerifier(Environment.ProcessPath!).VerifyAsync(pipe, TestContext.Current.CancellationToken);

        peer.AssertStillRunning();
        Assert.Null(anchoredAtPeer);
        Assert.NotNull(anchoredHere);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public async Task EachVerifierRefusesThePipeEndItDoesNotHold()
    {
        // The service's verifier handed a client stream, or the UI's a server stream, is being asked the
        // wrong question, and says so rather than answering one it was not asked.
        var name = PipeName();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(Soon, TestContext.Current.CancellationToken);
        await accepting;

        Assert.Equal("not_a_server_pipe", await new WindowsClientVerifier(Environment.ProcessPath!).VerifyAsync(client, TestContext.Current.CancellationToken));
        Assert.Equal("not_a_client_pipe", await new WindowsServerVerifier(Environment.ProcessPath!).VerifyAsync(server, TestContext.Current.CancellationToken));
    }

    private static void Restrict(PipePeer peer, string test)
    {
        if (!peer.RestrictToQueryLimited())
        {
            Measurements.Record($"{test}: the restricted DACL did not hold on this machine, so a VerifyAsync that read MainModule would not be caught here");
        }
    }

    private static string PipeName() => IpcPipeNames.ForUser($"test-peer-{Guid.NewGuid():N}");
}

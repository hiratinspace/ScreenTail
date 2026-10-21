using System.IO.Pipes;
using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// Which file a process was started from, as both verifiers ask it (2026-10-12 review, T-80).
///
/// ProcessImage replaced <c>Process.MainModule</c>, which reads the answer out of the peer's own memory,
/// where a same-user process can rewrite it. Nothing tested the replacement, so a revert would have
/// passed: for an ordinary process both give the same path. What tells them apart without imitating the
/// attack is the access each needs. MainModule has to read the peer's memory; the kernel's record needs
/// only the right to ask what a process is. A peer that grants nothing more is named by one and not the
/// other.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessImageTests
{
    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ProcessImageNamesTheRunningFile()
    {
        using var image = ProcessImage.Of((uint)Environment.ProcessId);

        Assert.NotNull(image);
        Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), image.Path, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ProcessImageNamesAnotherProcessAndNotThisOne()
    {
        Assert.SkipUnless(PipePeer.Available, "Windows PowerShell not found");
        using var peer = PipePeer.Idling();

        using var image = ProcessImage.Of((uint)peer.Id);

        Assert.NotNull(image);
        Assert.Equal(PipePeer.Executable, image.Path, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public async Task ProcessImageNeedsOnlyTheRightToAskWhatAProcessIs()
    {
        Assert.SkipUnless(PipePeer.Available, "Windows PowerShell not found");
        var name = IpcPipeNames.ForUser($"test-image-{Guid.NewGuid():N}");
        using var peer = PipePeer.Serving(name);

        // Connected first, so the peer is past starting up before its DACL changes: PowerShell is then
        // sitting in a read on the pipe and asks nothing more of its own process.
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The peer's DACL now lets this account ask what it is and wait for it, and nothing else.
        if (!peer.RestrictToQueryLimited())
        {
            Measurements.Record("ProcessImageNeedsOnlyTheRightToAskWhatAProcessIs: the restricted DACL did not hold on this machine, so a revert to MainModule would not be caught here");
        }

        using var image = ProcessImage.Of((uint)peer.Id);

        peer.AssertStillRunning();
        Assert.NotNull(image);
        Assert.Equal(PipePeer.Executable, image.Path, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AProcessIdThatNamesNothingHasNoImage()
    {
        // "Cannot tell" is a refusal, never a path: both verifiers turn null into a reason.
        Assert.Null(ProcessImage.Of(0));
    }
}

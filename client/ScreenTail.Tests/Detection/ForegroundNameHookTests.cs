using ScreenTail.Core.Detection;

namespace ScreenTail.Tests.Detection;

/// <summary>
/// ST-031: the foreground watcher's name-change hook listens to the foreground window's thread and moves
/// with it. Hooked for every thread, each rename anywhere on the desktop woke the watcher to be thrown
/// away, and the idle service's cost followed whatever else was open.
/// </summary>
public sealed class ForegroundNameHookTests
{
    private readonly FakeHooks _hooks = new();

    [Fact]
    public void ItListensToTheForegroundThreadOnly()
    {
        using var names = _hooks.Follower();

        Assert.True(names.Follow(42));

        Assert.True(names.Scoped);
        Assert.Equal(42u, names.Thread);
        Assert.Equal([42u], _hooks.Live);
        Assert.DoesNotContain(ForegroundNameHook.EveryThread, _hooks.Asked);
    }

    [Fact]
    public void TheSameThreadInFrontAgainChangesNothing()
    {
        using var names = _hooks.Follower();
        _ = names.Follow(42);

        Assert.False(names.Follow(42));

        Assert.Single(_hooks.Asked);
    }

    [Fact]
    public void ItMovesWithTheForegroundAndTheNewHookIsInBeforeTheOldComesOut()
    {
        using var names = _hooks.Follower();
        _ = names.Follow(42);

        Assert.True(names.Follow(7));

        Assert.Equal(7u, names.Thread);
        Assert.Equal([7u], _hooks.Live);

        // Never a moment with no hook: the new one was installed while the old one was still live.
        Assert.Equal(2, _hooks.MostLiveAtOnce);
    }

    [Fact]
    public void NothingInFrontTakesTheHookOut()
    {
        using var names = _hooks.Follower();
        _ = names.Follow(42);

        Assert.True(names.Follow(0));

        Assert.False(names.Installed);
        Assert.Empty(_hooks.Live);
        Assert.False(names.Follow(0));
    }

    [Fact]
    public void AThreadWindowsWillNotHookFallsBackToEveryThreadRatherThanToThePoll()
    {
        // Never less than before: the old subscription keeps the title news coming.
        _hooks.Refuse.Add(13);
        using var names = _hooks.Follower();

        Assert.True(names.Follow(13));

        Assert.True(names.Installed);
        Assert.False(names.Scoped);
        Assert.Equal([ForegroundNameHook.EveryThread], _hooks.Live);
    }

    [Fact]
    public void TheFallbackIsKeptAcrossRefusalsAndNarrowedAgainAtTheNextHookableThread()
    {
        _hooks.Refuse.Add(13);
        _hooks.Refuse.Add(14);
        using var names = _hooks.Follower();
        _ = names.Follow(13);

        Assert.False(names.Follow(14));
        Assert.Equal([ForegroundNameHook.EveryThread], _hooks.Live);

        Assert.True(names.Follow(42));
        Assert.True(names.Scoped);
        Assert.Equal([42u], _hooks.Live);
    }

    [Fact]
    public void ARefusalEverywhereLeavesNoHookAndNothingLeaked()
    {
        using var names = _hooks.Follower();
        _ = names.Follow(42);
        _hooks.Refuse.Add(13);
        _hooks.Refuse.Add(ForegroundNameHook.EveryThread);

        Assert.True(names.Follow(13));

        Assert.False(names.Installed);
        Assert.Empty(_hooks.Live);
    }

    [Fact]
    public void DisposingRemovesTheHook()
    {
        var names = _hooks.Follower();
        _ = names.Follow(42);

        names.Dispose();

        Assert.Empty(_hooks.Live);
    }

    private sealed class FakeHooks
    {
        private readonly Dictionary<nint, uint> _live = [];
        private nint _next = 100;

        public HashSet<uint> Refuse { get; } = [];

        public List<uint> Asked { get; } = [];

        public int MostLiveAtOnce { get; private set; }

        public IReadOnlyList<uint> Live => [.. _live.Values];

        public ForegroundNameHook Follower() => new(Hook, Unhook);

        private nint Hook(uint thread)
        {
            Asked.Add(thread);
            if (Refuse.Contains(thread))
            {
                return 0;
            }

            var handle = _next++;
            _live[handle] = thread;
            MostLiveAtOnce = Math.Max(MostLiveAtOnce, _live.Count);
            return handle;
        }

        private void Unhook(nint handle) => Assert.True(_live.Remove(handle), $"hook {handle} was removed twice or never installed");
    }
}

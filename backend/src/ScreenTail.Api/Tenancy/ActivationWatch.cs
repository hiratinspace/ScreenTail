namespace ScreenTail.Api.Tenancy;

/// <summary>
/// When the operator is told that invite codes may be being guessed (ADR-0009, "Guessing invite codes").
/// The defaults are the sensitive ones: fifty refused codes in ten minutes, from every address together,
/// is far more than technicians mistyping and far fewer than a guessing run makes.
/// </summary>
public sealed class ActivationWatchOptions
{
    public const string Section = "ActivationWatch";

    /// <summary>Refused codes in one window, across all addresses, at which one warning is logged.</summary>
    public int WarnAt { get; set; } = 50;

    /// <summary>The window they are counted in.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Refuses, at startup, a setting that would warn about nothing or never.</summary>
    public void Validate()
    {
        if (WarnAt is < 1 or > 1_000_000)
        {
            throw new InvalidOperationException("ActivationWatch:WarnAt must be between 1 and 1000000.");
        }

        if (Window < TimeSpan.FromMinutes(1) || Window > TimeSpan.FromHours(24))
        {
            throw new InvalidOperationException("ActivationWatch:Window must be between 1 minute and 24 hours.");
        }
    }
}

/// <summary>
/// Counts device activations refused for their code (unknown, malformed, used or expired) across every
/// client address, and logs one warning per window once the count reaches
/// <see cref="ActivationWatchOptions.WarnAt"/>.
///
/// <b>It refuses nobody and slows nobody.</b> A cap across addresses would let anybody lock every
/// technician out of activation by guessing badly on purpose, and a delay holds the server's
/// connections while a guesser with many addresses simply sends more at once. What stops guessing is
/// the code's 99 bits (<see cref="Invites"/>); this is how the operator learns somebody is trying.
///
/// <b>The warning carries two numbers</b> (the count and the window) and nothing else: no code, no
/// address, no tenant (INV-10). The count is this instance's; more than one API instance counts apart.
/// </summary>
public sealed partial class ActivationWatch(ActivationWatchOptions options, TimeProvider time, ILogger<ActivationWatch>? logger = null)
{
    private readonly ActivationWatchOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly Lock _gate = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _refused;
    private bool _warned;

    /// <summary>Refused codes counted in the current window, for tests.</summary>
    public int RefusedInWindow
    {
        get
        {
            lock (_gate)
            {
                return _refused;
            }
        }
    }

    /// <summary>Counts one refused code; logs the window's one warning when this is the one that reaches the threshold.</summary>
    public void Refused()
    {
        int? reached = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            // A clock set back starts a new window rather than stretching the old one.
            if (now < _windowStart || now - _windowStart >= _options.Window)
            {
                _windowStart = now;
                _refused = 0;
                _warned = false;
            }

            _refused++;
            if (!_warned && _refused >= _options.WarnAt)
            {
                _warned = true;
                reached = _refused;
            }
        }

        if (reached is { } count)
        {
            Warn(_log, count, (int)Math.Round(_options.Window.TotalMinutes));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Refused} device activations were refused for their invite code within {WindowMinutes} minutes, from all addresses together: invite codes may be being guessed. Nobody is refused or slowed for this; a code has about 99 bits.")]
    private static partial void Warn(ILogger logger, int refused, int windowMinutes);
}

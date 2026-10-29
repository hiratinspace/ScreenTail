using System.Globalization;
using ScreenTail.E2E;

// ST-013: the end-to-end driver. scripts/windows/e2e/run-e2e.ps1 runs it; docs/dev/e2e.md says how.
//
//   ScreenTail.E2E.exe --backend https://localhost:5071 [--inject-clicks] [--draft-timeout 240]
//       The loop, from the pipe to the note on the demo ticket. Reads the test secret from
//       SCREENTAIL_E2E_SECRET. Exit code 0 when every step passed, 1 when any failed, 2 for bad arguments.
//
//   ScreenTail.E2E.exe --stop <pid>
//       Asks a console program to stop cleanly (Ctrl+Break into its console), for the runner's shutdown.
if (Array.IndexOf(args, "--stop") is var stopAt and >= 0)
{
    if (stopAt + 1 >= args.Length || !int.TryParse(args[stopAt + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
    {
        Console.Error.WriteLine("Usage: ScreenTail.E2E.exe --stop <pid>");
        return 2;
    }

    // The console is let go of before the event is sent, so this cannot write after it. The exit code
    // carries the answer: 0 sent; 3 no console to attach to; 4 the console is shared; 5 not sent.
    return Native.SendStop(pid);
}

var backendAt = Array.IndexOf(args, "--backend");
if (backendAt < 0 || backendAt + 1 >= args.Length || !Uri.TryCreate(args[backendAt + 1], UriKind.Absolute, out var backend))
{
    Console.Error.WriteLine("Usage: ScreenTail.E2E.exe --backend https://localhost:<port> [--inject-clicks] [--draft-timeout <seconds>]");
    return 2;
}

var secret = Environment.GetEnvironmentVariable(Loop.SecretVariable);
if (string.IsNullOrWhiteSpace(secret) || secret.Length < 8)
{
    Console.Error.WriteLine($"{Loop.SecretVariable} must hold the test's fake secret (eight characters or more), made up for this run.");
    return 2;
}

var injectClicks = args.Contains("--inject-clicks", StringComparer.Ordinal);
if (injectClicks && Environment.GetEnvironmentVariable("SCREENTAIL_ALLOW_INPUT_INJECTION") != "1")
{
    Console.Error.WriteLine("--inject-clicks takes over the mouse, so it also needs SCREENTAIL_ALLOW_INPUT_INJECTION=1, as the laptop's input job sets.");
    return 2;
}

var draftTimeout = TimeSpan.FromSeconds(240);
if (Array.IndexOf(args, "--draft-timeout") is var timeoutAt and >= 0
    && timeoutAt + 1 < args.Length
    && int.TryParse(args[timeoutAt + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
{
    draftTimeout = TimeSpan.FromSeconds(seconds);
}

var steps = new Steps();
await using (var loop = new Loop(steps, backend, secret, injectClicks, draftTimeout))
{
    await loop.RunAsync().ConfigureAwait(false);
}

Console.WriteLine(steps.Failed == 0
    ? $"PASS loop: all {steps.Passed} steps passed."
    : $"FAIL loop: {steps.Failed} step(s) failed, {steps.Passed} passed.");
return steps.Failed == 0 ? 0 : 1;

using System.Diagnostics;
using System.Globalization;

namespace ScreenTail.E2E;

/// <summary>
/// One line per step of the loop, PASS or FAIL, with what it waited for. A failure says which join of the
/// loop broke and what it saw instead, so a red run needs no rerun to be read.
///
/// The lines are text about states and counts. They never carry a window title, a screenshot, OCR text
/// beyond the test's own synthetic words, or the test secret (INV-10 holds for the harness too: its log
/// is uploaded from a public repository).
/// </summary>
internal sealed class Steps
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _number;

    public int Failed { get; private set; }

    public int Passed { get; private set; }

    /// <summary>Runs one step. A step that throws is a FAIL with the exception's type and message.</summary>
    public async Task<bool> RunAsync(string name, string waitingFor, Func<Task<string>> body)
    {
        var number = ++_number;
        var started = _clock.Elapsed;
        try
        {
            var detail = await body().ConfigureAwait(false);
            Passed++;
            Write("PASS", number, name, waitingFor, detail, _clock.Elapsed - started);
            return true;
        }
        catch (StepFailed failed)
        {
            Failed++;
            Write("FAIL", number, name, waitingFor, failed.Message, _clock.Elapsed - started);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed++;
            Write("FAIL", number, name, waitingFor, $"{ex.GetType().Name}: {ex.Message}", _clock.Elapsed - started);
            return false;
        }
    }

    /// <summary>A line that is neither a pass nor a fail: context for the next reader.</summary>
    public static void Note(string text) => Console.WriteLine($"       {text}");

    private static void Write(string verdict, int number, string name, string waitingFor, string detail, TimeSpan took)
    {
        var seconds = took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        Console.WriteLine($"{verdict} loop-{number:00} {name} ({seconds} s): waited for {waitingFor}. {detail}");
    }
}

/// <summary>A step that did not see what it waited for. The message says what it saw instead.</summary>
internal sealed class StepFailed : Exception
{
    public StepFailed()
    {
    }

    public StepFailed(string message)
        : base(message)
    {
    }

    public StepFailed(string message, Exception inner)
        : base(message, inner)
    {
    }
}

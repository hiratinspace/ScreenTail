namespace ScreenTail.Tests.Windows;

/// <summary>
/// Where a measured number goes so it can be found again.
///
/// A passing test's console output is kept by xUnit and never reaches the run log, and job summaries are
/// visible on the run page but not through the API — so a green run used to report its numbers nowhere that
/// could be read back. These go to both, plus a file the workflow uploads, which is the one a person or a
/// script can actually fetch later to see whether a budget is drifting.
/// </summary>
internal static class Measurements
{
    private static readonly Lock Gate = new();

    public static void Record(string measurement)
    {
        Console.WriteLine(measurement);
        Append(Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"), $"- {measurement}");
        Append(Environment.GetEnvironmentVariable("SCREENTAIL_MEASUREMENTS"), measurement);
    }

    /// <summary>Puts a file beside the measurements, so a run can hand back something to look at.</summary>
    public static void Save(string name, byte[] content)
    {
        var path = Environment.GetEnvironmentVariable("SCREENTAIL_MEASUREMENTS");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name), content);
        }
    }

    private static void Append(string? path, string line)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}

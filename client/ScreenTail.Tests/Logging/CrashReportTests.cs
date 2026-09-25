using ScreenTail.Shared.Logging;

namespace ScreenTail.Tests.Logging;

/// <summary>
/// ST-011 AC2: a crash becomes a report with the stack trace and nothing else, written to a local
/// queue only when the technician opted in. An exception's message is left out on purpose — it is
/// where a path, a title or a customer's name ends up — and the frames are type and method names,
/// which say where it broke without saying what it was looking at.
/// </summary>
public sealed class CrashReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheReportHasTheTypeAndTheFramesAndNotTheMessage()
    {
        var report = CrashReport.Render(Thrown(), "ScreenTail.Service", "0.4.1");

        Assert.Contains("ScreenTail.Service 0.4.1", report, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), report, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme Dental", report, StringComparison.Ordinal);
        Assert.DoesNotContain("hirat", report, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", report, StringComparison.Ordinal);
    }

    [Fact]
    public void OptedInWritesOneFileToTheQueueAndOptedOutWritesNothing()
    {
        var written = CrashReport.Write(_dir, Thrown(), "ScreenTail.UI", "0.4.1", enabled: true);
        var skipped = CrashReport.Write(_dir, Thrown(), "ScreenTail.UI", "0.4.1", enabled: false);

        Assert.NotNull(written);
        Assert.Null(skipped);
        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.EndsWith(".txt", file, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme Dental", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void TheOptInIsOffUnlessSaidSo(string? value, bool expected)
    {
        Assert.Equal(expected, CrashReport.Enabled(_ => value));
    }

    private static InvalidOperationException Thrown()
    {
        try
        {
            try
            {
                throw new IOException(@"Could not open C:\Users\hirat\AppData\Local\ScreenTail\store.db");
            }
            catch (IOException inner)
            {
                throw new InvalidOperationException("Publishing to Acme Dental failed", inner);
            }
        }
        catch (InvalidOperationException outer)
        {
            return outer;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ScreenTail.Core.Update;

namespace ScreenTail.Tests.Windows.Update;

/// <summary>
/// CI's signing job checks each manifest with installer/update-manifest.ps1 before it is uploaded, on
/// Windows PowerShell 5.1 with CNG; the client checks it with .NET. These hold the two to one format:
/// a manifest .NET signs and the client accepts, the script accepts, and a changed one it refuses.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ManifestScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void TheScriptAcceptsWhatTheClientAcceptsAndRefusesAChangedManifest()
    {
        Directory.CreateDirectory(_dir);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new UpdateKey("test-key", signer.ExportSubjectPublicKeyInfo());
        var trustPath = Path.Combine(_dir, "trust.json");
        File.WriteAllText(trustPath, new JsonObject
        {
            ["feed"] = "https://updates.screentail.example/feed/",
            ["packageHosts"] = new JsonArray("downloads.screentail.example"),
            ["keys"] = new JsonArray(new JsonObject { ["id"] = key.Id, ["spki"] = Convert.ToBase64String(key.SubjectPublicKeyInfo) }),
        }.ToJsonString());
        var signed = Encoding.UTF8.GetBytes(
            """{"format":"screentail-update-v1","channel":"stable","architecture":"x64","version":"0.1.20","minimumVersion":"0.1.0","package":{"url":"https://downloads.screentail.example/0.1.20/ScreenTail.msi","sha256":"""
            + "\"" + new string('a', 64) + "\",\"size\":4096},\"published\":\"2026-10-28T12:00:00Z\"}");
        var signature = signer.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var good = Write("good.json", signed, signature);
        var changed = (byte[])signed.Clone();
        changed[^3] ^= 0x01;
        var bad = Write("bad.json", changed, signature);

        var trust = UpdateTrust.Parse(File.ReadAllText(trustPath));
        Assert.Equal(ManifestOutcome.Offer, UpdateManifestReader.Read(File.ReadAllBytes(good), trust, UpdateChannel.Stable, "x64", new Version(0, 1, 10)).Outcome);

        Assert.Equal(0, Run(good, trustPath));
        Assert.NotEqual(0, Run(bad, trustPath));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ABuildSignsPilotOnlyAndTheChannelHasNoDefault()
    {
        // ST-113 review: a build never signs stable, and nothing picks a channel for the caller. All three
        // are refused before the package is read or the vault is asked for anything.
        Directory.CreateDirectory(_dir);
        var msi = Path.Combine(_dir, "ScreenTail.msi");
        File.WriteAllBytes(msi, new byte[1024]);
        string[] release = ["-Msi", msi, "-Version", "0.1.20", "-Out", Path.Combine(_dir, "out")];

        Assert.Contains("channel", Fails([.. release]), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("channel", Fails(["-Channel", "all", .. release]), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("promotion", Fails(["-Channel", "stable", .. release]), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_dir, "out", "stable")));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void PromotionIsRefusedOutsideTheReleaseEnvironment()
    {
        // The gate is a variable only the owner's release-stable environment holds. Without it nothing
        // is read and nothing is signed: an environment GitHub created on first use, with no reviewers,
        // does not have it.
        Directory.CreateDirectory(_dir);
        var pilot = Path.Combine(_dir, "pilot.json");
        File.WriteAllText(pilot, "{}");

        Assert.Contains("release-stable", Fails(["-Channel", "stable", "-Promote", pilot, "-Out", Path.Combine(_dir, "out")]), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Write(string name, byte[] signed, byte[] signature)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, new JsonObject
        {
            ["format"] = UpdateManifestReader.EnvelopeFormat,
            ["key"] = "test-key",
            ["payload"] = Convert.ToBase64String(signed),
            ["signature"] = Convert.ToBase64String(signature),
        }.ToJsonString());
        return path;
    }

    private static int Run(string manifest, string trust) => Script(["-Verify", manifest, "-Trust", trust]).Exit;

    /// <summary>Runs the script, which must fail, and returns what it said.</summary>
    private static string Fails(string[] arguments)
    {
        var (exit, output) = Script(arguments);
        Assert.NotEqual(0, exit);
        return output;
    }

    private static (int Exit, string Output) Script(string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script(), .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        // Nothing of a real signing configuration reaches the script from the test's environment.
        foreach (var name in (string[])["SCREENTAIL_STABLE_RELEASE", "SCREENTAIL_UPDATE_KEY", "SCREENTAIL_UPDATE_BASE", "ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_ID_TOKEN_REQUEST_TOKEN", "PROMOTE_VERSION"])
        {
            _ = start.Environment.Remove(name);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "the script finished within a minute");
        var said = output.Result + error.Result;
        TestContext.Current.SendDiagnosticMessage(said);
        return (process.ExitCode, said);
    }

    private static string Script()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "installer", "update-manifest.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("installer/update-manifest.ps1 was not found above the test binary.");
    }
}

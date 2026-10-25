using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// Every case <c>research/prompts/agreement.py</c> pinned, read by this validator as Unicode defines the
/// reading (ST-061, PR #206 review).
///
/// <c>checks.read</c> and <see cref="DraftReading"/> are two implementations of decomposition, canonical
/// ordering, composition with its exclusions and Hangul, and differential-cases.json only held them to
/// about 9,600 sentences. The file pins what every scalar value reads as on its own, and every pair and
/// triple the tables compose or reorder, Hangul in each shape, long runs of marks, 112,654 seeded random
/// sequences, and text either side of the reading's bound. The readings come from Python's own NFKC at
/// the tables' Unicode version, so neither validator wrote what it is held to.
/// <c>research/tests/test_agreement_cases.py</c> holds <c>checks.read</c> to the same digests, which is
/// what makes the two agree on every case rather than each on its own.
/// </summary>
public sealed class AgreementCasesTests
{
    public static TheoryData<string> Groups()
    {
        var data = new TheoryData<string>();
        foreach (var group in File().Groups.Keys)
        {
            data.Add(group);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void EveryCaseReadsAsUnicodeDefinesIt(string group)
    {
        var pinned = File().Groups[group];
        IReadOnlyList<string> cases = group == "scalars" ? [.. Scalars()] : pinned.Cases;
        var ours = Digests(cases.Select(DraftValidator.Read));

        Assert.Equal(pinned.Digests.Count, ours.Count);
        var wrong = Enumerable.Range(0, ours.Count).Where(i => ours[i] != pinned.Digests[i]).ToList();
        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} of {ours.Count} digests differ in {group}, the first covering cases {wrong.FirstOrDefault() * Chunk} to {((wrong.FirstOrDefault() + 1) * Chunk) - 1}"
            + $" ({Show(cases[wrong.FirstOrDefault() * Chunk])} onwards). research/prompts/agreement.py --check names them.");
    }

    [Fact]
    public void TheFileCoversWhatTheReviewAskedFor()
    {
        var groups = File().Groups;

        Assert.Equal(0x110000 - 0x800, Scalars().Count());
        Assert.True(groups["random"].Cases.Count >= 100_000, "Fewer than 100,000 random sequences.");
        Assert.Contains("\u1100\u1161\u11A8", groups["hangul"].Cases);
        Assert.Contains("a\u0323\u0302", groups["triples"].Cases);
        Assert.Contains("a\u0302\u0323", groups["triples"].Cases);
        Assert.Contains(groups["runs"].Cases, run => run.Length > 5000);
        Assert.Contains("\uFDFA\uFDFA\uFDFA\uFDFA\uFDFAaa", groups["bound"].Cases);
    }

    [Fact]
    public void TheFileWasWrittenWithTheBoundTheValidatorsShare()
    {
        // The bound is in shared-rules.json, which SharedRulesTests holds DraftRules to. A file written
        // under another bound pins the wrong side of the line.
        using var stream = Resource("shared-rules.json");
        using var rules = JsonDocument.Parse(stream);
        var file = File();

        Assert.Equal(rules.RootElement.GetProperty("read_growth").GetString(), file.Growth.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(rules.RootElement.GetProperty("read_slack").GetString(), file.Slack.ToString(CultureInfo.InvariantCulture));
    }

    private const int Chunk = 4096;

    private static IEnumerable<string> Scalars() =>
        Enumerable.Range(0, 0x110000).Where(cp => cp is < 0xD800 or > 0xDFFF).Select(char.ConvertFromUtf32);

    /// <summary>As <c>agreement.digests</c>: the first 16 hex digits of the SHA-256 of each 4,096 readings.</summary>
    private static List<string> Digests(IEnumerable<string?> readings)
    {
        var digests = new List<string>();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        Span<byte> length = stackalloc byte[4];
        foreach (var reading in readings)
        {
            if (reading is null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(length, uint.MaxValue);
                sha.AppendData(length);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)reading.Length);
                sha.AppendData(length);
                sha.AppendData(Encoding.Unicode.GetBytes(reading));
            }

            if (++count == Chunk)
            {
                digests.Add(Convert.ToHexStringLower(sha.GetHashAndReset())[..16]);
                count = 0;
            }
        }

        if (count > 0)
        {
            digests.Add(Convert.ToHexStringLower(sha.GetHashAndReset())[..16]);
        }

        return digests;
    }

    private static string Show(string text)
    {
        var shown = new StringBuilder("'");
        foreach (var c in text.Take(40))
        {
            _ = c is >= ' ' and <= '~' ? shown.Append(c) : shown.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
        }

        return shown.Append('\'').ToString();
    }

    private static readonly Lazy<AgreementFile> Loaded = new(Load);

    private static AgreementFile File() => Loaded.Value;

    private static AgreementFile Load()
    {
        using var compressed = Resource("agreement-cases.json.gz");
        using var stream = new GZipStream(compressed, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var group in root.GetProperty("groups").EnumerateObject())
        {
            groups[group.Name] = new Group(
                [.. group.Value.GetProperty("digests").EnumerateArray().Select(d => d.GetString()!)],
                group.Value.TryGetProperty("cases", out var cases) ? [.. cases.EnumerateArray().Select(c => c.GetString()!)] : []);
        }

        return root.GetProperty("chunk").GetInt32() == Chunk
            ? new AgreementFile(root.GetProperty("read_growth").GetInt32(), root.GetProperty("read_slack").GetInt32(), groups)
            : throw new InvalidOperationException("agreement-cases.json.gz digests another number of cases at a time.");
    }

    private static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream($"ScreenTail.Api.Tests.{name}")
            ?? throw new InvalidOperationException($"{name} is not embedded in this test assembly.");

    private sealed record Group(IReadOnlyList<string> Digests, IReadOnlyList<string> Cases);

    private sealed record AgreementFile(int Growth, int Slack, IReadOnlyDictionary<string, Group> Groups);
}

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Store;

namespace ScreenTail.Tests.Audit;

/// <summary>
/// The audit export as a file (2026-10-12 review, E-2 and E-4): written in the one pass that verifies it,
/// in memory that does not grow with the log, and handed to the UI without the service ever taking a
/// path from the pipe.
/// </summary>
public sealed class AuditExporterTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private SqliteSessionStore? _store;

    private string Exports => Path.Combine(_dir, AuditExporter.DirectoryName);

    [Fact]
    public async Task WhatIsWrittenIsExactlyWhatWasVerifiedEvenWhileTheLogGrows()
    {
        // The export used to read the rows, let go of the store, and verify again later: two snapshots
        // of a log that gains a row per frame, so the file and its verdict could describe different logs.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        var exporter = new AuditExporter(store.ReadAuditAsync, Exports);

        var writing = Task.Run(
            async () =>
            {
                for (var i = 0; i < 300; i++)
                {
                    await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct);
                }
            },
            ct);

        var results = new List<AuditExportResult>();
        while (!writing.IsCompleted)
        {
            results.Add(await exporter.ExportAsync(ct));
        }

        await writing;
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            Assert.True(result.Verification!.Intact, result.Verification.Describe());
            Assert.Equal(result.Rows, result.Verification.Checked + result.Verification.Unchained);
            Assert.Equal(result.Rows, await EntriesAsync(result.Path!));
        }
    }

    [Fact]
    public async Task ATamperedLogIsStillExportedInFullWithAVerdictThatSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct);
        }

        var target = (await store.GetAuditRecordsAsync(ct: ct))[2];
        store = await TamperAsync("UPDATE audit_log SET count = 99 WHERE id = " + target.Id);

        var result = await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct);

        Assert.False(result.Verification!.Intact);
        Assert.Equal(target.Id, result.Verification.BrokenAt);
        Assert.Equal(5, result.Rows);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(result.Path!, ct));
        Assert.Equal(5, parsed.RootElement.GetProperty("entries").GetArrayLength());
        Assert.False(parsed.RootElement.GetProperty("integrity").GetProperty("intact").GetBoolean());
        Assert.Equal(target.Id, parsed.RootElement.GetProperty("integrity").GetProperty("first_broken_row").GetInt64());
    }

    [Fact]
    public async Task AnEmptyLogIsAnExportOfNothingThatVerifies()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();

        var result = await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct);

        Assert.Equal(0, result.Rows);
        Assert.True(result.Verification!.Intact);
        Assert.Equal(0, await EntriesAsync(result.Path!));
    }

    [Fact]
    public async Task TheFileIsCompactAndCarriesOnlyTheRowsFields()
    {
        // Indented, a year of rows is several times the size, and the reason it was indented (a string a
        // person might read in the UI) went with the pipe.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        await store.RecordAsync(AuditTypes.FrameRedacted, "s1", 2, "Card", ct);

        var result = await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct);
        var text = await File.ReadAllTextAsync(result.Path!, ct);

        Assert.DoesNotContain('\n', text);
        using var parsed = JsonDocument.Parse(text);
        var row = Assert.Single(parsed.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(
            ["id", "at", "session_id", "type", "count", "detail", "hash"],
            row.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Card", row.GetProperty("detail").GetString());
        Assert.Equal(2, row.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task TheWriterHandsRowsOnAsItGoesRatherThanHoldingTheWholeLog()
    {
        // Bounded memory is the point of streaming. A writer that only flushed at the end would build the
        // whole export in its buffer, which is the list the old code held, under another name.
        using var sink = new MemoryStream();
        await using var writer = new AuditJsonWriter(sink, DateTimeOffset.UnixEpoch);
        var row = new AuditRecord(1, DateTimeOffset.UnixEpoch, "s1", AuditTypes.FrameCaptured, 1, "Click", null, new string('a', 64));

        var handedOn = new List<long>();
        for (var i = 1; i <= 3_000; i++)
        {
            await writer.WriteAsync(row with { Id = i }, TestContext.Current.CancellationToken);
            if (i % 1_000 == 0)
            {
                handedOn.Add(sink.Length);
            }
        }

        // Before Complete, the stream already has most of it, and more after every thousand rows.
        Assert.True(handedOn[0] > 0, "nothing reached the stream after a thousand rows");
        Assert.True(handedOn[1] > handedOn[0] && handedOn[2] > handedOn[1], string.Join(", ", handedOn));
        Assert.Equal(3_000, writer.Rows);
    }

    [Fact]
    public async Task ACancelledExportLeavesNoFileAndLetsGoOfTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        for (var i = 0; i < 50; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seen = 0;
        var exporter = new AuditExporter(
            (each, token) => store.ReadAuditAsync(
                async (row, inner) =>
                {
                    if (++seen == 10)
                    {
                        await cancel.CancelAsync();
                    }

                    await each(row, inner);
                    inner.ThrowIfCancellationRequested();
                },
                token),
            Exports);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(cancel.Token));

        Assert.Empty(Directory.EnumerateFiles(Exports));
        await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True((await store.VerifyAuditAsync(ct)).Intact);
    }

    [Fact]
    public async Task AStoreThatCannotBeReadIsARefusalNotACrash()
    {
        var exporter = new AuditExporter((_, _) => throw new SqliteException("file is not a database", 26), Exports);

        var result = await exporter.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Path);
        Assert.Equal("The audit log could not be read from the store.", result.Refusal);
        Assert.Empty(Directory.EnumerateFiles(Exports));
    }

    [Fact]
    public async Task AnExportNobodyCollectedIsSweptByTheNextOneAndAFreshOneIsLeftAlone()
    {
        // A UI that died between the reply and the copy leaves a file behind. Bounded by the next export,
        // but not so eagerly that two exports close together delete each other's.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        var exporter = new AuditExporter(store.ReadAuditAsync, Exports);
        var stale = (await exporter.ExportAsync(ct)).Path!;
        var fresh = (await exporter.ExportAsync(ct)).Path!;
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - AuditExporter.CollectWithin - TimeSpan.FromMinutes(1));

        var third = (await exporter.ExportAsync(ct)).Path!;

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(third));
        Assert.NotEqual(fresh, third);
    }

    [Fact]
    public async Task CollectingCopiesToTheChosenPlaceAndRemovesTheServicesCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct);
        var exported = (await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct)).Path!;
        var chosen = Path.Combine(_dir, "Prüfung ünd Bericht", "audit 2026.json");
        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);
        await File.WriteAllTextAsync(chosen, "an older export the technician chose to overwrite", ct);
        var expected = await File.ReadAllBytesAsync(exported, ct);

        var problem = await AuditExporter.CollectAsync(exported, chosen, Exports, ct);

        Assert.Null(problem);
        Assert.Equal(expected, await File.ReadAllBytesAsync(chosen, ct));
        Assert.False(File.Exists(exported));
    }

    [Theory]
    [InlineData("elsewhere")]
    [InlineData("traversal")]
    [InlineData("renamed")]
    [InlineData("relative")]
    public async Task APathThatIsNotOneOfTheServicesExportsIsNeitherCopiedNorDeleted(string shape)
    {
        // The reply comes from the verified service, but the UI's copy-and-delete is still only ever
        // pointed at a file the service could have written. Anything else is left exactly where it is.
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Exports);
        var name = $"audit-20261014-101500-{Guid.NewGuid():N}.json";
        var bystander = shape switch
        {
            "elsewhere" => Path.Combine(_dir, name),
            "traversal" => Path.Combine(_dir, name),
            "renamed" => Path.Combine(Exports, "store.db"),
            _ => name,
        };
        var named = shape == "traversal" ? Path.Combine(Exports, "..", name) : bystander;
        if (Path.IsPathFullyQualified(bystander))
        {
            await File.WriteAllTextAsync(bystander, "not an export", ct);
        }

        var chosen = Path.Combine(_dir, "chosen.json");

        var problem = await AuditExporter.CollectAsync(named, chosen, Exports, ct);

        Assert.Equal("The capture service named a file outside its exports folder, so nothing was copied.", problem);
        Assert.False(File.Exists(chosen));
        if (Path.IsPathFullyQualified(bystander))
        {
            Assert.True(File.Exists(bystander));
        }
    }

    [Fact]
    public async Task ADestinationInsideTheExportsFolderIsRefusedBecauseTheSweepWouldTakeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        var exported = (await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct)).Path!;

        var problem = await AuditExporter.CollectAsync(exported, exported, Exports, ct);

        Assert.Equal("Choose somewhere outside ScreenTail's own folder to keep the audit log.", problem);
        Assert.False(File.Exists(exported));
    }

    [Fact]
    public async Task ADestinationThatCannotBeWrittenLeavesNoHalfFileAndNoServiceCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        var exported = (await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct)).Path!;
        var chosen = Path.Combine(_dir, "no such folder", "audit.json");

        var problem = await AuditExporter.CollectAsync(exported, chosen, Exports, ct);

        Assert.Equal("The audit log could not be saved there.", problem);
        Assert.False(File.Exists(chosen));
        Assert.False(File.Exists(exported));
    }

    [Fact]
    public async Task AnExportThatIsAlreadyGoneSaysSoRatherThanBlamingTheDestination()
    {
        // Swept, or collected by an earlier attempt. The technician's chosen place is not the problem.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        var exported = (await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct)).Path!;
        File.Delete(exported);
        var chosen = Path.Combine(_dir, "audit.json");

        var problem = await AuditExporter.CollectAsync(exported, chosen, Exports, ct);

        Assert.Equal("The capture service's export was gone before it could be copied. Export it again.", problem);
        Assert.False(File.Exists(chosen));
    }

    [Fact]
    public async Task DeleteEverythingTakesUncollectedExportsWithIt()
    {
        // INV-12. A copy of the log is still a record of the sessions the technician asked to forget.
        // The eraser does not name the exports folder: its sweep of everything but the models takes it.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct);
        var exported = (await new AuditExporter(store.ReadAuditAsync, Exports).ExportAsync(ct)).Path!;
        await store.DisposeAsync();
        _store = null;

        var erased = LocalDataEraser.Erase(_dir);

        Assert.True(erased.Complete);
        Assert.False(File.Exists(exported));
        Assert.False(Directory.Exists(Exports));
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static async Task<int> EntriesAsync(string path)
    {
        await using var file = File.OpenRead(path);
        using var parsed = await JsonDocument.ParseAsync(file);
        return parsed.RootElement.GetProperty("entries").GetArrayLength();
    }

    private async Task<SqliteSessionStore> OpenAsync() =>
        _store ??= await SqliteSessionStore.OpenAsync(Path.Combine(_dir, "store.db"), new FixedKey(_key));

    /// <summary>Edits the table behind the store's back, the way someone with the key and a SQL client would.</summary>
    private async Task<SqliteSessionStore> TamperAsync(string sql)
    {
        await _store!.DisposeAsync();
        _store = null;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, "store.db"), Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using (var key = connection.CreateCommand())
            {
                key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(_key)}'\";";
                await key.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        return await OpenAsync();
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}

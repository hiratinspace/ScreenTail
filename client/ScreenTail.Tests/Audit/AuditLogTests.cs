using System.Security.Cryptography;
using System.Text.Json;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Audit;

/// <summary>
/// ST-045 against the real store. The log's whole value is in two claims — that it says what happened, and
/// that it can show it has not been edited since — so these check both against rows the rest of the system
/// wrote, not rows a test made up.
/// </summary>
public sealed class AuditLogTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private SqliteSessionStore? _store;
    private MachineHarness? _harness;

    [Fact]
    public async Task AChainOfRowsVerifies()
    {
        var store = await OpenAsync();

        for (var i = 0; i < 10; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        var verification = await store.VerifyAuditAsync();

        Assert.True(verification.Intact, verification.Describe());
        Assert.Equal(10, verification.Checked);
        Assert.Equal(0, verification.Unchained);
    }

    [Fact]
    public async Task EditingARowBreaksTheChainFromThere()
    {
        // The claim the export makes. Anyone with the store key can write to this table — that is not what
        // the chain prevents. What it prevents is doing so without it showing.
        var store = await OpenAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        var target = (await store.GetAuditRecordsAsync())[2];
        store = await TamperAsync("UPDATE audit_log SET count = 99 WHERE id = @id", ("@id", target.Id));

        var verification = await store.VerifyAuditAsync();

        Assert.False(verification.Intact);
        Assert.Equal(target.Id, verification.BrokenAt);
        Assert.Contains("does not verify", verification.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingARowBreaksTheChain()
    {
        // The tamper that matters most: deleting the row that says a frame was purged unredacted, or that
        // a bundle went somewhere. A log you can quietly shorten is not evidence of anything.
        var store = await OpenAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        var target = (await store.GetAuditRecordsAsync())[2];
        store = await TamperAsync("DELETE FROM audit_log WHERE id = @id", ("@id", target.Id));

        var verification = await store.VerifyAuditAsync();

        Assert.False(verification.Intact);
    }

    [Fact]
    public async Task CuttingTheLogShortFromTheEndIsNoticed()
    {
        // The tamper the chain alone cannot see, open since the September review (P1-5). Delete the last
        // rows and what is left is a shorter chain that verifies perfectly — so the export said "all N
        // audit rows verify" about a log that used to have more, which is exactly the edit somebody
        // covering a purge would make.
        //
        // The log now says separately how long it is supposed to be.
        var store = await OpenAsync();
        for (var i = 0; i < 6; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        store = await TamperAsync("DELETE FROM audit_log WHERE id IN (SELECT id FROM audit_log ORDER BY id DESC LIMIT 2)");

        var verification = await store.VerifyAuditAsync();

        Assert.False(verification.Intact);
        Assert.Equal(2, verification.Missing);
        Assert.Contains("missing from the end", verification.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyingTheLogEntirelyIsNoticed()
    {
        // The laziest version of the same thing, and the one that used to report "All 0 audit rows verify."
        var store = await OpenAsync();
        for (var i = 0; i < 4; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        store = await TamperAsync("DELETE FROM audit_log");

        var verification = await store.VerifyAuditAsync();

        Assert.False(verification.Intact);
        Assert.Equal(4, verification.Missing);
    }

    [Fact]
    public async Task ReplacingTheLastRowIsNoticedEvenWithTheCountUnchanged()
    {
        // Same number of rows, ending somewhere else. Recomputing the chain from a forged last row keeps
        // the count right, and the head hash is what disagrees.
        var store = await OpenAsync();
        for (var i = 0; i < 4; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        var records = await store.GetAuditRecordsAsync();
        var last = records[^1];
        var forged = AuditChain.Hash(last.PreviousHash, last.At, last.SessionId, last.Type, 99, last.Detail);
        store = await TamperAsync(
            "UPDATE audit_log SET count = 99, hash = @hash WHERE id = @id",
            ("@hash", forged),
            ("@id", last.Id));

        var verification = await store.VerifyAuditAsync();

        Assert.False(verification.Intact);
    }

    [Fact]
    public async Task AnUntouchedLogStillSaysSo()
    {
        // The control. A head record read wrongly would make every honest log look tampered with, which
        // is the failure that gets a verification switched off.
        var store = await OpenAsync();
        for (var i = 0; i < 6; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        var verification = await store.VerifyAuditAsync();

        Assert.True(verification.Intact, verification.Describe());
        Assert.Equal(0, verification.Missing);
        Assert.Equal(6, verification.Checked);
    }

    [Fact]
    public async Task RewritingARowAndItsHashStillBreaksTheChain()
    {
        // The obvious next move for someone editing the table: fix the row's own hash too. Every row after
        // it still points at the hash it used to have.
        var store = await OpenAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        }

        var records = await store.GetAuditRecordsAsync();
        var target = records[1];
        var forged = AuditChain.Hash(target.PreviousHash, target.At, target.SessionId, target.Type, 99, target.Detail);
        store = await TamperAsync(
            "UPDATE audit_log SET count = 99, hash = @hash WHERE id = @id",
            ("@hash", forged),
            ("@id", target.Id));

        var verification = await store.VerifyAuditAsync();

        Assert.False(verification.Intact);
        Assert.Equal(records[2].Id, verification.BrokenAt);
    }

    [Fact]
    public async Task ACompletedSessionSaysWhatWasCapturedAndWhatWasCoveredOver()
    {
        // AC1, against rows the system wrote rather than rows this test made up: frames staged by the
        // machine, a suppression the guard caused, a purge, and a bundle leaving.
        var harness = await HarnessAsync();
        var machine = harness.Machine;
        Assert.True(await machine.StartAsync(Rdp));

        Assert.True(await machine.TryStageFrameAsync(Frame("f1"), TestContext.Current.CancellationToken));
        Assert.True(await machine.TryStageFrameAsync(Frame("f2"), TestContext.Current.CancellationToken));

        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField));
        Assert.True(await machine.UnsuppressAsync());

        await harness.Store.RecordAsync(AuditTypes.FrameRedacted, machine.SessionId, 2, nameof(MaskKind.Card));
        await harness.Store.RecordAsync(AuditTypes.FramesPurgedUnredacted, machine.SessionId, 1);
        await harness.Store.RecordAsync(
            AuditTypes.BundleSent, machine.SessionId, 4096, AuditDetail.Destination(new Uri("https://api.screentail.example/v1/bundles")));

        var summary = AuditExport.Summarise(await harness.Store.GetAuditRecordsAsync(), machine.SessionId);

        Assert.Equal(2, summary.FramesCaptured);
        Assert.Equal(1, summary.FramesPurgedUnredacted);
        Assert.Equal(2, summary.RedactionsByKind[nameof(MaskKind.Card)]);
        Assert.Equal(1, summary.SuppressionsByReason[nameof(CaptureStateReason.PasswordField)].Times);
        Assert.Equal(4096, summary.BytesSent["api.screentail.example"]);
        Assert.Equal("api.screentail.example", Assert.Single(summary.Destinations));
    }

    [Fact]
    public async Task ASuppressionEndedByStoppingIsStillRecorded()
    {
        // The end a guard never sees. A session stopped while a password field still had focus would
        // otherwise leave an interval that started and never finished.
        var harness = await HarnessAsync();
        var machine = harness.Machine;
        Assert.True(await machine.StartAsync(Rdp));
        Assert.True(await machine.SuppressAsync(CaptureStateReason.SensitiveContext));

        Assert.True(await machine.StopAsync());

        var summary = AuditExport.Summarise(await harness.Store.GetAuditRecordsAsync(), machine.SessionId);
        Assert.Equal(1, summary.SuppressionsByReason[nameof(CaptureStateReason.SensitiveContext)].Times);
    }

    [Fact]
    public async Task TheExportCarriesNoContent()
    {
        // AC2, and the reason the export is built from AuditRecord alone. A session with OCR text and a
        // transcript in the store: none of it has a path into this file.
        var harness = await HarnessAsync();
        var machine = harness.Machine;
        Assert.True(await machine.StartAsync(Rdp));
        const string Secret = "the customer said her password is hunter2";

        Assert.True(await machine.TryStageFrameAsync(Frame("f1"), TestContext.Current.CancellationToken));
        Assert.True(await machine.TryAppendTranscriptAsync(
            new TranscriptSegment { Id = "t1", TsMs = 10, EndMs = 20, Speaker = Speaker.Tech, Text = Secret },
            TestContext.Current.CancellationToken));

        var records = await harness.Store.GetAuditRecordsAsync();
        var json = AuditExport.ToJson(records, await harness.Store.VerifyAuditAsync());
        var csv = AuditExport.ToCsv(records);

        Assert.DoesNotContain("hunter2", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", csv, StringComparison.OrdinalIgnoreCase);
        // The sentence, not the word. Since speech is scrubbed (2026-09-19) the export legitimately says
        // that one thing of kind "Password" was masked, which is a category and is the point of an audit
        // log. What must never appear is anything that was actually said.
        Assert.DoesNotContain("her password is", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the customer said", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, csv, StringComparison.Ordinal);
        Assert.Contains(AuditTypes.TranscriptRedacted, json, StringComparison.Ordinal);

        // And it is a real export, not an empty one that passes by having nothing in it.
        Assert.Contains(AuditTypes.FrameCaptured, json, StringComparison.Ordinal);
        Assert.Contains(AuditTypes.FrameCaptured, csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExportSaysWhetherItVerifies()
    {
        // A reader who cannot tell an intact log from a broken one is being handed a list, not evidence.
        var store = await OpenAsync();
        await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click");
        var target = (await store.GetAuditRecordsAsync())[0];
        store = await TamperAsync("UPDATE audit_log SET count = 7 WHERE id = @id", ("@id", target.Id));

        var json = AuditExport.ToJson(await store.GetAuditRecordsAsync(), await store.VerifyAuditAsync());

        using var parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.GetProperty("integrity").GetProperty("intact").GetBoolean());
    }

    [Theory]
    [InlineData("Card")]
    [InlineData("password_field")]
    [InlineData("api.screentail.example")]
    [InlineData("v1.2.3")]
    public void AShortLabelIsAllowed(string detail) => Assert.Equal(detail, AuditDetail.Require(detail));

    [Theory]
    [InlineData("the customer said her password is hunter2")]
    [InlineData("Print Spooler Properties")]
    [InlineData("a b")]
    [InlineData("")]
    public void AnythingSentenceLikeIsRefused(string detail)
    {
        // The one field in a row that is not a number or a fixed name, and therefore the only way content
        // could reach a file that promises to carry none.
        Assert.Throws<ArgumentException>(() => AuditDetail.Require(detail));
    }

    [Fact]
    public void ADetailLongerThanALabelIsRefused()
    {
        Assert.Throws<ArgumentException>(() => AuditDetail.Require(new string('a', AuditDetail.MaxLength + 1)));
    }

    [Fact]
    public void ADestinationIsAHostAndNotAUrl()
    {
        // A URL carries a path and a query, and those carry ticket numbers, subjects and customer names.
        Assert.Equal(
            "api.screentail.example",
            AuditDetail.Destination(new Uri("https://api.screentail.example/v1/bundles?ticket=Acme%20Dental%20printer")));
    }

    [Fact]
    public async Task ARowWithContentInItIsRefusedRatherThanWritten()
    {
        var store = await OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordAsync(AuditTypes.FrameRedacted, "s1", 1, "the password was hunter2"));

        Assert.Empty(await store.GetAuditRecordsAsync());
    }

    private static StagedFrame Frame(string id) => new(id, 1_000, FrameTrigger.Click, 100, 100, null, new byte[] { 0xAA });

    private async Task<SqliteSessionStore> OpenAsync() => _store ??= await SqliteSessionStore.OpenAsync(_path, _key);

    private async Task<MachineHarness> HarnessAsync() => _harness ??= await MachineHarness.StartAsync();

    /// <summary>
    /// Edits the table behind the store's back, the way someone with the key and a SQL client would, and
    /// hands back the reopened store — the old instance is disposed, and a caller holding it would fail on
    /// the semaphore rather than on the thing under test.
    /// </summary>
    private async Task<SqliteSessionStore> TamperAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await _store!.DisposeAsync();
        _store = null;

        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();

            // The same raw-key pragma the store uses; the Password keyword would run the passphrase KDF
            // over it instead and open nothing.
            await using (var key = connection.CreateCommand())
            {
                key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(_key.GetKey())}'\";";
                await key.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync();
        }

        return _store = await SqliteSessionStore.OpenAsync(_path, _key);
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Audit;

/// <summary>
/// The audit export end to end, over a real pipe and a real store (2026-10-12 review, E-2).
///
/// The log is never pruned and gains a row for every frame. Sent as one string in one message it passed
/// the pipe's 1 MiB limit after a few weeks of use, the write threw, and the server dropped the UI's only
/// connection on every attempt from then on. These seed past that point and go through the real server.
/// </summary>
public sealed class AuditExportOverThePipeTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = IpcPipeNames.ForUser("test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _token = IpcToken.Generate();
    private SqliteSessionStore? _store;
    private IpcServer? _server;

    private string Exports => Path.Combine(_dir, AuditExporter.DirectoryName);

    [Fact]
    public async Task AnAuditLogOfFiveThousandRowsStillExports()
    {
        // About two megabytes of log on the old wire, twice the frame limit.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        for (var i = 0; i < 5_000; i++)
        {
            await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct);
        }

        await using var ui = await ServeAsync(new AuditExporter(store.ReadAuditAsync, Exports), ct);

        var exported = await ui.RequestAsync<AuditExported>(id => new ExportAuditCommand { RequestId = id }, ct);

        Assert.Equal(5_000, exported.Rows);
        Assert.True(exported.Intact, exported.Note);
        Assert.Equal("All 5000 audit rows verify.", exported.Note);
        Assert.True(ui.IsConnected);
        Assert.True((await ui.SendAsync(id => new GetStateCommand { RequestId = id }, ct)).Ok);

        // The file is the whole log and says the same as the reply.
        await using (var file = File.OpenRead(exported.Path))
        {
            using var parsed = await JsonDocument.ParseAsync(file, cancellationToken: ct);
            Assert.Equal(5_000, parsed.RootElement.GetProperty("entries").GetArrayLength());
            Assert.True(parsed.RootElement.GetProperty("integrity").GetProperty("intact").GetBoolean());
        }

        // And the UI's half: the technician's copy is made, and the service's is gone.
        var kept = Path.Combine(_dir, "chosen", "screentail-audit.json");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        Assert.Null(await AuditExporter.CollectAsync(exported.Path, kept, Exports, ct));
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(exported.Path));
    }

    [Fact]
    public async Task AnExportThatCannotBeWrittenIsRefusedAndThePipeStaysUp()
    {
        // A disk that will not take the file. The answer is a reason, not a dropped connection.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync();
        await store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click", ct);
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Exports, "a file where the folder should be", ct);

        await using var ui = await ServeAsync(new AuditExporter(store.ReadAuditAsync, Exports), ct);

        var refused = await Assert.ThrowsAsync<IpcProtocolException>(
            () => ui.RequestAsync<AuditExported>(id => new ExportAuditCommand { RequestId = id }, ct));

        Assert.Equal("The audit log could not be written to disk.", refused.Message);
        Assert.True(ui.IsConnected);
        Assert.True((await ui.SendAsync(id => new GetStateCommand { RequestId = id }, ct)).Ok);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private async Task<IpcClient> ServeAsync(AuditExporter exporter, CancellationToken ct)
    {
        var commands = new SettingsCommands(
            new ClientSettingsStore(Path.Combine(_dir, "settings.json")),
            () => TenantPolicy.Default,
            _ => { },
            exporter.ExportAsync);
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, new AnyClient(), new SettingsOnly(commands), new NoAudit(), "0.0.1-test");
        _server.Start();
        return await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", ct: ct);
    }

    private async Task<SqliteSessionStore> OpenAsync() =>
        _store ??= await SqliteSessionStore.OpenAsync(Path.Combine(_dir, "store.db"), new FixedKey(RandomNumberGenerator.GetBytes(32)));

    /// <summary>The controller's chain, cut down to the one command class under test.</summary>
    private sealed class SettingsOnly(SettingsCommands settings) : IIpcCommandHandler
    {
        public CaptureStateSnapshot CurrentState => CaptureStateSnapshot.Idle;

        public CapabilitiesReported CurrentCapabilities => new AlwaysCapableProbe().Probe().ToWire();

        public async Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            await settings.HandleAsync(command, ct) ?? new CommandResult { RequestId = command.RequestId, Ok = true };

        public Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default) => settings.ReplyToAsync(command, ct);
    }

    private sealed class AnyClient : IClientVerifier
    {
        public ValueTask<string?> VerifyAsync(System.IO.Pipes.PipeStream connection, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class NoAudit : IAuditLog
    {
        public Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}

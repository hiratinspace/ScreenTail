using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Ipc;
using ScreenTail.Platform.Ipc;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// ADR-0003 rule 1 against the pipe the service really creates.
///
/// Every other IPC test builds its pipe with <see cref="IpcServer.DefaultPipeFactory"/>, which is the
/// platform's default permissions and nothing else. So the one factory that ships, the one that writes
/// the DACL, had never been run: a rule widened to Everyone or Authenticated Users, or a factory that
/// threw on first use, would have passed every test there is (2026-10-12 review, T-79).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPipeFactoryTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public async Task TheCapturePipeAdmitsOnlyItsOwnUser()
    {
        // The explicit rule set, read back off the pipe. Not AreAccessRulesProtected: a kernel object's
        // DACL is what it was created with, and the factory never sets that flag, so asserting it would
        // test nothing the factory does.
        var me = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(me);
        var create = WindowsPipeFactory.ForCurrentUser(PipeName("acl"));

        // Two instances, because the service listens on several at once and every one of them comes from
        // the same factory call.
        await using var first = create();
        await using var second = create();

        foreach (var pipe in new[] { first, second })
        {
            var rules = pipe.GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .ToList();

            // Named one by one so a failure says which door was opened.
            foreach (var outsider in Outsiders)
            {
                Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(outsider, null)));
            }

            var only = Assert.Single(rules);
            Assert.Equal(me, only.IdentityReference);
            Assert.Equal(AccessControlType.Allow, only.AccessControlType);
            Assert.False(only.IsInherited);
        }
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public async Task TheCapturePipeCarriesAFullHandshake()
    {
        // The DACL has to admit the real client as well as keep others out. This is the whole handshake,
        // with the production pieces on both ends: the ACL'd pipe, the client verifier the service runs,
        // and the server verifier the UI runs before it writes the token. Both ends are this test process,
        // which is the dev-build rule's own case: an unsigned executable talking to itself, in its own
        // directory.
        var name = PipeName("handshake");
        var token = IpcToken.Generate();
        var self = Environment.ProcessPath!;
        var handler = new IdleHandler();
        await using var server = new IpcServer(
            WindowsPipeFactory.ForCurrentUser(name),
            token,
            new WindowsClientVerifier(self),
            handler,
            new NoAudit(),
            "0.0.1-test");
        server.Start();

        await using var client = await IpcClient.ConnectAsync(
            name,
            token,
            "test-ui",
            new WindowsServerVerifier(self),
            "0.0.1",
            Soon,
            TestContext.Current.CancellationToken);
        var answered = await client.SendAsync(id => new GetStateCommand { RequestId = id }, TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStates.Idle, client.State.State);
        Assert.True(answered.Ok);
    }

    /// <summary>
    /// Everyone, Anonymous, Authenticated Users, BUILTIN\Users, Interactive and Network: each of these
    /// would let some other account on the machine, or a process that is not the technician, open the
    /// pipe and start the handshake.
    /// </summary>
    private static readonly WellKnownSidType[] Outsiders =
    [
        WellKnownSidType.WorldSid,
        WellKnownSidType.AnonymousSid,
        WellKnownSidType.AuthenticatedUserSid,
        WellKnownSidType.BuiltinUsersSid,
        WellKnownSidType.InteractiveSid,
        WellKnownSidType.NetworkSid,
    ];

    /// <summary>Never the service's own name: the laptop that runs these may have the real service running.</summary>
    private static string PipeName(string purpose) => IpcPipeNames.ForUser($"test-{purpose}-{Guid.NewGuid():N}");

    private sealed class IdleHandler : IIpcCommandHandler
    {
        public CaptureStateSnapshot CurrentState => CaptureStateSnapshot.Idle;

        public CapabilitiesReported CurrentCapabilities =>
            new CapabilityReport(DateTimeOffset.UnixEpoch, Array.Empty<CapabilityCheck>()).ToWire();

        public Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            Task.FromResult(new CommandResult { RequestId = command.RequestId, Ok = true });

        public Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            Task.FromResult<IpcEvent?>(null);
    }

    private sealed class NoAudit : IAuditLog
    {
        public Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}

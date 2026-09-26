using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenTail.Core.Net;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Net;

/// <summary>The activation step over the pipe (ST-010, Spec §5 S8 step 2): the code goes in, the tenant's name comes back.</summary>
public sealed class DeviceCommandsTests
{
    [Fact]
    public async Task ActivationAndTheDeviceQuestionAreAnsweredWithTheirEvents()
    {
        var handler = new OneReply(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { tenantName = "Acme IT", deviceId = Guid.NewGuid(), refreshToken = "r", accessToken = "a", expiresAt = DateTimeOffset.UtcNow.AddHours(1) }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        var session = new DeviceSession(new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") }, new DeviceSessionTests.InMemoryCredentials(), time: new ManualTime(DateTimeOffset.UtcNow));
        var commands = new DeviceCommands(session);

        var before = Assert.IsType<DeviceReported>(await commands.ReplyToAsync(new GetDeviceCommand { RequestId = 1 }));
        var activated = Assert.IsType<DeviceActivated>(await commands.ReplyToAsync(new ActivateDeviceCommand { RequestId = 2, Code = "KX7PM-4R2WQ", DeviceName = "TECH" }));
        var after = Assert.IsType<DeviceReported>(await commands.ReplyToAsync(new GetDeviceCommand { RequestId = 3 }));

        Assert.False(before.Activated);
        Assert.Equal("Acme IT", activated.TenantName);
        Assert.True(after.Activated);
        Assert.Equal("Acme IT", after.TenantName);
        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 4 }));
        Assert.Null(await commands.HandleAsync(new StartCommand { RequestId = 4 }));
    }

    [Fact]
    public async Task ARefusedActivationComesBackAsTheFailedResultWithTheReason()
    {
        var handler = new OneReply(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invite_expired", message = "That code has expired. Ask your admin for a new invite." }) });
        var session = new DeviceSession(new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") }, new DeviceSessionTests.InMemoryCredentials(), time: new ManualTime(DateTimeOffset.UtcNow));
        var commands = new DeviceCommands(session);

        Assert.Null(await commands.ReplyToAsync(new ActivateDeviceCommand { RequestId = 5, Code = "OLD", DeviceName = "TECH" }));
        var result = await commands.HandleAsync(new ActivateDeviceCommand { RequestId = 5, Code = "OLD", DeviceName = "TECH" });

        Assert.False(result!.Ok);
        Assert.Equal("That code has expired. Ask your admin for a new invite.", result.Error);
    }

    private sealed class OneReply(HttpResponseMessage reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply);
    }
}

using System.Security.Principal;
using ScreenTail.Service.Host;

// One capture service per user (ADR-0003). A second copy exits quietly instead of fighting over the pipe.
var userIdentity = OperatingSystem.IsWindows()
    ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
    : Environment.UserName;
using var instance = SingleInstance.TryAcquire(userIdentity);
if (instance is null)
{
    return 3;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The capture service runs on Windows only.");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CaptureHost>();
await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

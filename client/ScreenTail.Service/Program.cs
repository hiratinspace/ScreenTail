using ScreenTail.Service.Host;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CaptureHost>();
await builder.Build().RunAsync();

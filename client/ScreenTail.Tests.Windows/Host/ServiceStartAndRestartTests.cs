using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Service.Host;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// Two edges of the service's run (ST-112): a close Windows sends before the host has started is a stop and
/// not a crash, and the restart registration Windows holds is the one ApplicationRestart describes. No
/// desktop is needed, so this runs on the hosted runner.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceStartAndRestartTests
{
    [Fact]
    public async Task AStopAskedBeforeTheHostStartedEndsTheRunWithZeroAndSaysItStopped()
    {
        // What the bare host does, so this test means something: it refuses to start by throwing.
        using (var bare = Build())
        {
            bare.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bare.RunAsync(TestContext.Current.CancellationToken));
        }

        // The session-end window's WM_CLOSE or WM_ENDSESSION arriving at that moment.
        var host = Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var code = await ServiceRun.RunAsync(host, () => lifetime.ApplicationStopping.IsCancellationRequested, stopped)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.True(stopped.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AnOrdinaryStopEndsTheRunWithZeroAndSaysItStopped()
    {
        var host = Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = lifetime.ApplicationStarted.Register(lifetime.StopApplication);

        var code = await ServiceRun.RunAsync(host, () => lifetime.ApplicationStopping.IsCancellationRequested, stopped)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.True(stopped.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ACancellationNobodyAskedForIsStillAFailureButStillSaysItStopped()
    {
        var host = Build();
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServiceRun.RunAsync(host, () => false, stopped));
        Assert.True(stopped.Task.IsCompletedSuccessfully, "the session-end window would wait out its budget");
    }

    [Fact]
    public void WindowsHoldsTheRegistrationApplicationRestartDescribes()
    {
        // Registered for this test host, then taken back.
        try
        {
            Assert.Equal(0, WindowsApplicationRestart.Register(NullLogger.Instance));
            var registered = WindowsApplicationRestart.Registered();

            Assert.NotNull(registered);
            Assert.Equal(ApplicationRestart.CommandLine, registered.Value.CommandLine);
            Assert.Equal(ApplicationRestart.Flags, registered.Value.Flags);
        }
        finally
        {
            _ = WindowsApplicationRestart.Unregister();
        }

        Assert.Null(WindowsApplicationRestart.Registered());
    }

    private static IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        _ = builder.Logging.ClearProviders();
        builder.Services.AddHostedService<Idle>();
        return builder.Build();
    }

    private sealed class Idle : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}

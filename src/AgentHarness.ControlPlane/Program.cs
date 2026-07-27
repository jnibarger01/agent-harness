using AgentHarness.Domain;
using AgentHarness.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentHarness.ControlPlane;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((ctx, services) =>
            {
                var cs = ctx.Configuration["ConnectionStrings:Harness"] ?? "Data Source=harness.db";
                services.AddSingleton(new SqliteHarnessStore(cs));
                services.AddHostedService<InboxProcessorService>();
            })
            .Build();
        await host.RunAsync();
    }
}

/// <summary>
/// BackgroundService = singleton hosted service. No DI scope is created automatically per
/// iteration (Claude fix #10). We create an async scope before resolving scoped services.
/// </summary>
public sealed class InboxProcessorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    public InboxProcessorService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var inbox = scope.ServiceProvider.GetRequiredService<SqliteHarnessStore>();

            await foreach (var turn in inbox.PendingAsync(stoppingToken))
            {
                // Durable authority: dispatch creates a Run + Attempt in the store BEFORE any
                // worker is spawned. If the host crashes here, the Run remains and is reclaimed.
                var run = new Run { Id = Guid.NewGuid(), TurnId = turn.Id, CreatedAt = DateTimeOffset.UtcNow };
                run.Dispatch();
                await inbox.SaveRunAsync(run, stoppingToken);

                // (Phase 2+) lease + spawn worker via ExecutionSupervisor.
            }

            await Task.Delay(500, stoppingToken);
        }
    }
}

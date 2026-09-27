using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

public sealed class ConcurrentAuditTests
{
    [Fact]
    public async Task Parallel_provider_audits_have_independent_commit_boundaries()
    {
        var gate = new FirstWriteGate();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(gate));
        services.AddBizigoDataPlane(new ClickHouseOptions());
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var sink = scope.ServiceProvider.GetRequiredService<IAuditSink>();
        var first = sink.RecordAsync(Record("first"), TestContext.Current.CancellationToken);
        await gate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            // While one provider's commit is suspended, another must commit
            // only its own record, without saving or corrupting the first.
            await sink.RecordAsync(Record("second"), TestContext.Current.CancellationToken);
            await using var read = await sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
                .CreateDbContextAsync(TestContext.Current.CancellationToken);
            Assert.Equal("second", Assert.Single(await read.AuditLog.ToArrayAsync(TestContext.Current.CancellationToken)).Subject);
        }
        finally { gate.Release.TrySetResult(); await first; }
        await using var final = await sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "first", "second" }, await final.AuditLog.OrderBy(r => r.Subject)
            .Select(r => r.Subject).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private static AuditRecord Record(string subject) => new(subject, "telemetry.Metrics.search", "Metrics", "A", "test", 1, 0, true);

    private sealed class FirstWriteGate : SaveChangesInterceptor
    {
        private int writes;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.Single(eventData.Context!.ChangeTracker.Entries<AuditLogEntity>(), e => e.State == EntityState.Added);
            if (Interlocked.Increment(ref writes) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
}

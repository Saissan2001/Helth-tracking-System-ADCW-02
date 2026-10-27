using System.Data.Common;
using System.Diagnostics;
using System.Net;
using HealthTrack.Application;
using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace HealthTrack.Tests;
public class QueryCounter : DbCommandInterceptor
{
    public int Reads { get; set; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Reads++; return ValueTask.FromResult(result); }
}
public class InsightsHost : TestHost
{
    public QueryCounter Queries { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddDbContext<HealthTrackDbContext>(o => o.AddInterceptors(Queries)));
    }
}
[CollectionDefinition("CompletionDatabase", DisableParallelization = true)]
public class CompletionCollection : ICollectionFixture<InsightsHost>;
[Collection("CompletionDatabase")]
public class CompletionAndInsightsTests(InsightsHost host, ITestOutputHelper output)
{
    private static DateOnly Today => DateOnly.FromDateTime(TestHost.Local(DateTimeOffset.UtcNow));
    private async Task<(Actor Client, Actor Trainer, HealthPlan Plan)> Plan(PlanKind kind)
    {
        var (client, trainer) = await host.Pair();
        var saved = await host.Use((s, _) => s.SavePlan(trainer, new() { ClientId = client.Id, Kind = kind, Title = "Completion test", StartDate = Today.AddDays(-14), EndDate = Today.AddDays(14) }));
        Assert.True(saved.Success, saved.Error);
        var plan = await host.Use((s, _) => s.Plan(trainer, saved.Id!.Value));
        Assert.True((await host.Use((s, _) => s.AddItem(trainer, new() { PlanId = plan.Id, Version = TestHost.Version(plan), Day = Today.AddDays(-1).DayOfWeek, LocalTime = new(8, 0), Description = "Scheduled activity", DurationMinutes = kind == PlanKind.Workout ? 25 : null, Calories = 500 }))).Success);
        plan = await host.Use((s, _) => s.Plan(trainer, plan.Id));
        Assert.True((await host.Use((s, _) => s.ChangePlan(trainer, plan.Id, TestHost.Version(plan), PlanStatus.Published))).Success);
        return (client, trainer, await host.Use((s, _) => s.Plan(trainer, plan.Id)));
    }
    [Theory]
    [InlineData(PlanKind.Workout)]
    [InlineData(PlanKind.Diet)]
    public async Task LinkedCompletionRequiresActualLogAndDeletionRestoresUnrecordedState(PlanKind kind)
    {
        var (client, trainer, plan) = await Plan(kind); var date = Today.AddDays(-1);
        var input = await host.Use((s, _) => s.PlannedLog(client, plan.Items.Single().Id, date));
        Assert.Null(input.Calories); Assert.Null(input.DurationMinutes);
        var before = await host.Use((s, _) => s.Adherence(client, plan.Id, date, date));
        Assert.Equal(1, before.Due); Assert.Equal(0, before.Completed);
        input.DurationMinutes = kind == PlanKind.Workout ? 18 : null;
        input.LocalTime = date.ToDateTime(new(8, 15));
        var saved = await host.Use((s, _) => s.SaveLog(client, input)); Assert.True(saved.Success, saved.Error);
        var after = await host.Use((s, _) => s.Adherence(trainer, plan.Id, date, date));
        Assert.Equal(100m, after.Percent); Assert.Equal(saved.Id, after.Occurrences.Single().LogId);
        Assert.False((await host.Use((s, _) => s.SaveLog(client, input))).Success);
        var log = await host.Use((s, _) => s.Log(client, saved.Id!.Value));
        input.Id = log.Id; input.Version = TestHost.Version(log); input.LocalTime = input.LocalTime.AddDays(-1);
        var invalid = await host.Use((s, _) => s.SaveLog(client, input)); Assert.False(invalid.Success); Assert.Equal("LocalTime", invalid.Field);
        using var browser = await host.Login(client);
        var page = await browser.GetStringAsync($"/Plans/Adherence/{plan.Id}?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        Assert.Contains("Review entry", page); Assert.Contains("100%", page);
        Assert.True((await host.Use((s, _) => s.DeleteLog(client, log.Id, TestHost.Version(log)))).Success);
        Assert.Equal(0, (await host.Use((s, _) => s.Adherence(client, plan.Id, date, date))).Completed);
    }
    [Fact] public async Task CompletionRejectsOutsidersWrongDatesRevokedTrainersAndArchivedNewLogs()
    {
        var (client, trainer, plan) = await Plan(PlanKind.Workout); var other = await host.CreateActor(Roles.Client);
        var input = await host.Use((s, _) => s.PlannedLog(client, plan.Items.Single().Id, Today.AddDays(-1))); input.DurationMinutes = 20;
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.SaveLog(other, input)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.PlannedLog(client, plan.Items.Single().Id, Today.AddDays(-2))));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.Adherence(client, plan.Id, Today.AddDays(-40), Today)));
        Assert.True((await host.Use((s, _) => s.ChangePlan(trainer, plan.Id, TestHost.Version(plan), PlanStatus.Archived))).Success);
        Assert.False((await host.Use((s, _) => s.SaveLog(client, input))).Success);
        var a = (await host.Use((s, _) => s.Assignments(host.Admin))).Single(x => x.Assignment.ClientId == client.Id).Assignment;
        await host.Use((s, _) => s.Revoke(host.Admin, a.Id, TestHost.Version(a)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Adherence(trainer, plan.Id, Today, Today)));
    }
    [Fact] public async Task ArchivedPlanHistoryRemainsReadableButCannotReceiveNewCompletions()
    {
        var (client, trainer, plan) = await Plan(PlanKind.Workout); var date = Today.AddDays(-1);
        var input = await host.Use((s, _) => s.PlannedLog(client, plan.Items.Single().Id, date)); input.DurationMinutes = 30;
        var saved = await host.Use((s, _) => s.SaveLog(client, input)); Assert.True(saved.Success, saved.Error);
        Assert.True((await host.Use((s, _) => s.ChangePlan(trainer, plan.Id, TestHost.Version(plan), PlanStatus.Archived))).Success);
        foreach (var actor in new[] { client, trainer })
        {
            var history = await host.Use((s, _) => s.Adherence(actor, plan.Id, date, date));
            Assert.Equal(1, history.Completed); Assert.Equal(saved.Id, history.Occurrences.Single().LogId);
        }
        using var browser = await host.Login(client);
        var html = await browser.GetStringAsync($"/Plans/Adherence/{plan.Id}?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        Assert.Contains("Review entry", html); Assert.DoesNotContain(">Record completion</a>", html);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.PlannedLog(client, plan.Items.Single().Id, date.AddDays(-7))));
        var log = await host.Use((s, _) => s.Log(client, saved.Id!.Value));
        input.Id = log.Id; input.Version = TestHost.Version(log); input.DurationMinutes = 25;
        Assert.True((await host.Use((s, _) => s.SaveLog(client, input))).Success);
    }
    [Fact] public async Task DashboardUsesLocalWeekAndKeepsOlderLatestWeight()
    {
        var client = await host.CreateActor(Roles.Client);
        var now = DateTimeOffset.UtcNow; var local = TestHost.Local(now);
        var monday = Today.AddDays(-(((int)Today.DayOfWeek + 6) % 7));
        var dates = new[] { local.AddMinutes(-1), Today.AddDays(-6).ToDateTime(new(0, 1)), Today.AddDays(-7).ToDateTime(new(0, 1)) };
        foreach (var date in dates) Assert.True((await host.Use((s, _) => s.SaveLog(client, new() { Kind = LogKind.Workout, Description = "Walk", DurationMinutes = 10, LocalTime = date }))).Success);
        await host.Use((s, _) => s.SaveLog(client, new() { Kind = LogKind.Measurement, Description = "Older weight", WeightKg = 70, HeightCm = 175, LocalTime = local.AddDays(-45) }));
        var data = await host.Use((s, _) => s.Dashboard(client));
        Assert.Equal(2, data.SevenDayWorkouts); Assert.Equal(20, data.SevenDayMinutes);
        Assert.Equal(dates.Count(d => DateOnly.FromDateTime(d) >= monday), data.WeekWorkouts);
        Assert.Equal(70m, data.LatestWeight!.Weight); Assert.Equal(22.9m, data.LatestWeight.Bmi); Assert.Null(data.Progress!.Weight);
    }
    [Fact] public async Task ReportsPageClientsBoundQueriesAndRenderRegistrationWorkloads()
    {
        var trainer = await host.CreateActor(Roles.Trainer); var actors = new List<Actor>();
        for (var i = 0; i < 25; i++) { var actor = await host.CreateActor(Roles.Client); actors.Add(actor); await host.Use((s, _) => s.Assign(host.Admin, actor.Id, trainer.Id)); }
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        await host.Use(async (_, db) => { db.Logs.AddRange(actors.SelectMany(a => Enumerable.Range(0, 40).Select(i => new HealthLog { ClientId = a.Id, Kind = LogKind.Workout, OccurredAtUtc = at, Description = "Load fixture", DurationMinutes = 10, Routine = WorkoutRoutine.Walking }))); await db.SaveChangesAsync(); return true; });
        host.Queries.Reads = 0; var stopwatch = Stopwatch.StartNew();
        var first = await host.Use((s, _) => s.Engagement(trainer, Today.AddDays(-1), Today, 1));
        var firstReads = host.Queries.Reads; stopwatch.Stop();
        Assert.Equal(20, first.Clients.Count); Assert.Equal(25, first.EligibleClients); Assert.Equal(1000, first.WorkoutSessions); Assert.Equal(8000, first.Clients.Sum(x => x.WorkoutMinutes)); Assert.InRange(firstReads, 1, 12);
        host.Queries.Reads = 0;
        var second = await host.Use((s, _) => s.Engagement(trainer, Today.AddDays(-1), Today, 2));
        Assert.Equal(5, second.Clients.Count); Assert.Equal(firstReads, host.Queries.Reads); Assert.Empty(first.Clients.Select(x => x.ClientId).Intersect(second.Clients.Select(x => x.ClientId)));
        output.WriteLine($"25 clients / 1000 encrypted workout durations: page 1 {firstReads} SELECTs, {stopwatch.ElapsedMilliseconds} ms; page 2 {host.Queries.Reads} SELECTs. This is a local fixture, not a production latency guarantee.");
        var report = await host.Use((s, _) => s.SystemReport(host.Admin, Today.AddDays(-1), Today));
        Assert.NotNull(report.Insights); Assert.True(report.Insights.Registrations.Sum(x => x.Entries) >= 26); Assert.Contains(report.Insights.Workloads.Items, x => x.Clients == 25);
        using var staff = await host.Login(trainer);
        Assert.Contains("Page 2 of 2", await staff.GetStringAsync("/Reports?page=2"));
        var csv = await staff.GetStringAsync("/Reports/Export?page=2"); Assert.Contains("2 of 2", csv);
        using var admin = await host.Login(host.Admin);
        Assert.Contains("Account registrations", await admin.GetStringAsync("/Dashboard")); Assert.Contains("Trainer workload", await admin.GetStringAsync("/Admin/Reports"));
    }
    [Fact] public async Task OversizedReportRequestsFailClearlyInsteadOfTruncatingTotals()
    {
        var (client, trainer) = await host.Pair();
        var saved = await host.Use((s, _) => s.SaveLog(client, new() { Kind = LogKind.Workout, Description = "Bound fixture", DurationMinutes = 1, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }));
        // Clone valid ciphertext only inside the isolated test database; no sensitive value is printed.
        await host.Use(async (_, db) => await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Logs (Id, ClientId, Kind, Routine, OccurredAtUtc, Description, Notes, DurationMinutes)
            SELECT TOP (20000) NEWID(), l.ClientId, l.Kind, l.Routine, l.OccurredAtUtc, l.Description, l.Notes, l.DurationMinutes
            FROM Logs l CROSS JOIN sys.all_objects a CROSS JOIN sys.all_objects b WHERE l.Id = {0}
            """, saved.Id!.Value));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.Engagement(trainer, Today.AddDays(-1), Today)));
        Assert.Contains("20,000", error.Message);
        var empty = await host.Use((s, _) => s.Engagement(trainer, Today.AddDays(-4), Today.AddDays(-3)));
        Assert.Equal(0, empty.WorkoutSessions);
    }
    [Fact] public async Task ReminderLookupsAreBatchedAndRemainDeduplicatedBeyondOneHundredPlans()
    {
        var client = await host.CreateActor(Roles.Client); var trainer = await host.CreateActor(Roles.Trainer);
        var local = TestHost.Local(DateTimeOffset.UtcNow); var date = DateOnly.FromDateTime(local);
        await host.Use(async (_, db) =>
        {
            db.Plans.AddRange(Enumerable.Range(0, 105).Select(i => new HealthPlan { ClientId = client.Id, TrainerId = trainer.Id, Title = "Batch fixture", Status = PlanStatus.Published, StartDate = date, EndDate = date,
                Items = [new PlanItem { Day = date.DayOfWeek, LocalTime = TimeOnly.FromDateTime(local.AddMinutes(-1)), Description = "Scheduled walk", DurationMinutes = 10 }] }));
            await db.SaveChangesAsync(); return true;
        });
        host.Queries.Reads = 0;
        await host.Use(async (s, _) => { await s.GenerateReminders(); return true; });
        var reads = host.Queries.Reads;
        // Writes also use SQL reader commands for rowversions. Inspect only a second, deduplicated pass.
        host.Queries.Reads = 0;
        await host.Use(async (s, _) => { await s.GenerateReminders(); return true; });
        Assert.InRange(host.Queries.Reads, 1, 20);
        var count = await host.Use((_, db) => db.Notifications.CountAsync(x => x.RecipientId == client.Id && x.Kind == NotificationKind.Workout));
        Assert.Equal(105, count);
        output.WriteLine($"105 scheduled plans retained exactly 105 reminders; deduplicated pass used fewer than 21 SQL reader commands (first pass {reads} including inserts).");
    }
}

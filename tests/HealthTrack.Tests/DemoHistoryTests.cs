using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HealthTrack.Tests;
public class DemoHistoryFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 7, 30, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void HistoriesSpanFiveMonthsWithVarietyConsistentAmountsAndNoFutureLogs(int persona)
    {
        var data = DemoHistoryFactory.Build("client", "trainer", persona, Now, "Asia/Colombo");
        Assert.Equal(150, data.End.DayNumber - data.Start.DayNumber);
        Assert.InRange(data.Logs.Count, 1200, 2200); Assert.Equal(data.Logs.Count, data.Logs.Select(x => x.Id).Distinct().Count());
        Assert.All(data.Logs, l => { Assert.True(l.OccurredAtUtc <= Now); Assert.Equal("client", l.ClientId); });
        Assert.True((data.Logs.Max(x => x.OccurredAtUtc) - data.Logs.Min(x => x.OccurredAtUtc)).TotalDays > 149);
        Assert.True(data.Logs.Where(x => x.Kind == LogKind.Meal).Select(x => x.Description).Distinct().Count() > 20);
        Assert.True(data.Logs.Where(x => x.WeightKg.HasValue).Select(x => x.WeightKg).Distinct().Count() > 8);
        Assert.Contains(data.Logs, x => x.Kind == LogKind.Meal && x.Calories == null);
        foreach (var meal in data.Logs.Where(x => x.Kind == LogKind.Meal && x.Calories.HasValue))
            Assert.InRange(Math.Abs(meal.Calories!.Value - (4 * meal.Protein!.Value + 4 * meal.Carbs!.Value + 9 * meal.Fat!.Value)), 0, .5m);
        var sleep = data.Logs.Where(x => x.Kind == LogKind.Sleep).OrderBy(x => x.OccurredAtUtc).ToList();
        for (var i = 0; i < sleep.Count; i++)
        {
            Assert.Equal(sleep[i].DurationMinutes, (int)(sleep[i].OccurredAtUtc - sleep[i].SleepStartedAtUtc!.Value).TotalMinutes);
            if (i > 0) Assert.True(sleep[i].SleepStartedAtUtc >= sleep[i - 1].OccurredAtUtc);
        }
        var items = data.Plans.SelectMany(x => x.Items).ToDictionary(x => x.Id);
        foreach (var log in data.Logs.Where(x => x.PlanItemId.HasValue))
        {
            var item = items[log.PlanItemId!.Value]; var plan = data.Plans.Single(x => x.Id == item.PlanId);
            Assert.Equal(log.ScheduledDate!.Value.DayOfWeek, item.Day);
            Assert.InRange(log.ScheduledDate.Value, plan.StartDate, plan.EndDate);
            Assert.Equal(log.ScheduledDate, DateOnly.FromDateTime(TestHost.Local(log.OccurredAtUtc)));
            Assert.Equal(log.Kind == LogKind.Workout ? PlanKind.Workout : PlanKind.Diet, plan.Kind);
        }
        Assert.Equal(2, data.Plans.Count(x => x.Status == PlanStatus.Published));
        Assert.Equal(4, data.Plans.Count(x => x.Status == PlanStatus.Archived));
        Assert.All(data.Appointments.Where(x => x.Status == AppointmentStatus.Completed), x => Assert.True(x.EndsAtUtc < Now));
        Assert.All(data.Appointments.Where(x => x.Status is AppointmentStatus.Confirmed or AppointmentStatus.Requested), x => Assert.True(x.StartsAtUtc > Now));
        Assert.All(data.Feedback, x => Assert.True(x.CreatedAtUtc < Now));
        Assert.InRange(data.Logs.Count(x => x.IsFavorite), 4, 10);
    }
    [Fact] public void PersonasAreDistinctAndGenerationIsRepeatable()
    {
        var a = DemoHistoryFactory.Build("a", "trainer", 0, Now, "Asia/Colombo");
        var repeat = DemoHistoryFactory.Build("a", "trainer", 0, Now, "Asia/Colombo");
        var b = DemoHistoryFactory.Build("b", "trainer", 1, Now, "Asia/Colombo");
        Assert.Equal(a.Logs.Select(x => (x.Id, x.OccurredAtUtc, x.Calories, x.Steps)), repeat.Logs.Select(x => (x.Id, x.OccurredAtUtc, x.Calories, x.Steps)));
        Assert.NotEqual(a.Logs.Count, b.Logs.Count); Assert.Empty(a.Logs.Select(x => x.Id).Intersect(b.Logs.Select(x => x.Id)));
        Assert.DoesNotContain(a.Logs, x => x.Routine == WorkoutRoutine.Cycling); Assert.Contains(b.Logs, x => x.Routine == WorkoutRoutine.Cycling);
    }
}
[CollectionDefinition("RealisticSeed", DisableParallelization = true)]
public class RealisticSeedCollection : ICollectionFixture<TestHost>;
[Collection("RealisticSeed")]
public class RealisticSeedTests(TestHost host)
{
    [Fact] public async Task SeedingIsAtomicScopedEncryptedAndRepeatSafe()
    {
        using var scope = host.Services.CreateScope(); var services = scope.ServiceProvider;
        services.GetRequiredService<IConfiguration>()["Seed:Password"] = host.Password;
        await services.GetRequiredService<SeedData>().Initialize(true);
        var db = services.GetRequiredService<HealthTrackDbContext>();
        var client = await db.Users.SingleAsync(x => x.Email == "client@healthtrack.demo");
        var second = await db.Users.SingleAsync(x => x.Email == "client2@healthtrack.demo");
        var outsider = await host.CreateActor(Roles.Client);
        var manual = new HealthLog { ClientId = client.Id, Kind = LogKind.Meal, OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), Description = "My own entry", Calories = 333 };
        var untouched = new HealthLog { ClientId = outsider.Id, Kind = LogKind.Measurement, OccurredAtUtc = DateTimeOffset.UtcNow.AddDays(-2), Description = "Private outsider", WeightKg = 72 };
        db.Logs.AddRange(manual, untouched); await db.SaveChangesAsync();
        var result = await services.GetRequiredService<RealisticDemoSeeder>().Run();
        Assert.False(result.AlreadyApplied); Assert.Equal(75, result.ReplacedLegacyLogs); Assert.Equal(2, result.Clients); Assert.True(result.Logs > 2400);
        Assert.True(await db.Logs.AnyAsync(x => x.Id == manual.Id)); Assert.Equal(1, await db.Logs.CountAsync(x => x.ClientId == outsider.Id));
        Assert.Equal(2, await db.WellnessPreferences.CountAsync()); Assert.True(await db.Assignments.AnyAsync(x => x.ClientId == second.Id && x.EndedAtUtc == null));
        var count = await db.Logs.CountAsync(); var plans = await db.Plans.CountAsync();
        Assert.True((await services.GetRequiredService<RealisticDemoSeeder>().Run()).AlreadyApplied);
        Assert.Equal(count, await db.Logs.CountAsync()); Assert.Equal(plans, await db.Plans.CountAsync());
        var unprotected = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM Logs WHERE WaterMl IS NOT NULL AND WaterMl NOT LIKE 'ht1:%'").SingleAsync(); Assert.Equal(0, unprotected);
        Assert.True(await db.Logs.AnyAsync(x => x.ClientId == second.Id && x.WaterMl != null));
        using var browser = await host.Login(new(client.Id, Roles.Client));
        Assert.Contains("Your saved shortcuts", await browser.GetStringAsync("/Dashboard"));
        Assert.Contains("Morning weigh-in", await browser.GetStringAsync($"/Wellness?date={result.To!.Value.AddDays(-1):yyyy-MM-dd}"));
    }
    [Fact] public async Task ProductionEnvironmentRefusesSeedBeforeChangingAnything()
    {
        using var scope = host.Services.CreateScope(); var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>(); var prior = env.EnvironmentName;
        try { env.EnvironmentName = Environments.Production; await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<RealisticDemoSeeder>().Run()); }
        finally { env.EnvironmentName = prior; }
    }
}

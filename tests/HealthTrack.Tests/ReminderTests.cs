using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HealthTrack.Tests;
[Collection("Database")]
public class ReminderTests(TestHost host)
{
    [Theory]
    [InlineData(PlanKind.Workout, NotificationKind.Workout)]
    [InlineData(PlanKind.Diet, NotificationKind.Meal)]
    public async Task PlanRemindersArePersistedDeduplicatedAndArchiveInvalidatesPending(PlanKind kind, NotificationKind notificationKind)
    {
        var (client, trainer) = await host.Pair(); var local = TestHost.Local(DateTimeOffset.UtcNow); var today = DateOnly.FromDateTime(local);
        var result = await host.Use((s, _) => s.SavePlan(trainer, new() { ClientId = client.Id, Kind = kind, Title = "Reminder plan", StartDate = today, EndDate = today.AddDays(7) })); Assert.True(result.Success, result.Error);
        var plan = await host.Use((s, _) => s.Plan(trainer, result.Id!.Value));
        Assert.True((await host.Use((s, _) => s.AddItem(trainer, new() { PlanId = plan.Id, Version = TestHost.Version(plan), Day = today.DayOfWeek, LocalTime = TimeOnly.MinValue, Description = "Daily routine", DurationMinutes = 20 }))).Success);
        plan = await host.Use((s, _) => s.Plan(trainer, plan.Id));
        Assert.True((await host.Use((s, _) => s.AddItem(trainer, new() { PlanId = plan.Id, Version = TestHost.Version(plan), Day = today.AddDays(1).DayOfWeek, LocalTime = new(23, 0), Description = "Tomorrow routine", DurationMinutes = 20 }))).Success);
        plan = await host.Use((s, _) => s.Plan(trainer, plan.Id)); await host.Use((s, _) => s.ChangePlan(trainer, plan.Id, TestHost.Version(plan), PlanStatus.Published));
        await host.Use(async (s, _) => { await s.GenerateReminders(); return true; });
        await host.Use(async (s, _) => { await s.GenerateReminders(); return true; });
        Assert.Single((await host.Use((s, _) => s.Inbox(client, 1))).Items, x => x.Kind == notificationKind);
        var pending = await host.Use(async (_, db) => await db.Notifications.CountAsync(x => x.RecipientId == client.Id && x.Kind == notificationKind && x.DeliveredAtUtc == null && !x.Invalidated)); Assert.Equal(1, pending);
        plan = await host.Use((s, _) => s.Plan(trainer, plan.Id)); await host.Use((s, _) => s.ChangePlan(trainer, plan.Id, TestHost.Version(plan), PlanStatus.Archived));
        pending = await host.Use(async (_, db) => await db.Notifications.CountAsync(x => x.RecipientId == client.Id && x.Kind == notificationKind && x.DeliveredAtUtc == null && !x.Invalidated)); Assert.Equal(0, pending);
    }
}

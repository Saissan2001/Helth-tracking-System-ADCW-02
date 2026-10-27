using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HealthTrack.Tests;
[Collection("Database")]
public class WorkflowTests(TestHost host)
{
    [Fact] public async Task LogsPersistCorrectReportTotalsAndDenyCrossClientAccess()
    {
        var (client, trainer) = await host.Pair(); var stranger = await host.CreateActor(Roles.Client);
        var today = DateOnly.FromDateTime(TestHost.Local(DateTimeOffset.UtcNow));
        var input = new LogInput { Kind = LogKind.Workout, Description = "Walk", DurationMinutes = 30, Calories = 120, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-5)) };
        var result = await host.Use((s, _) => s.SaveLog(client, input)); Assert.True(result.Success, result.Error);
        await Assert.ThrowsAsync<MissingRecordException>(() => host.Use((s, _) => s.Log(stranger, result.Id!.Value)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Progress(host.Admin, client.Id, today, today)));
        var report = await host.Use((s, _) => s.Progress(trainer, client.Id, today, today)); Assert.Equal(120, report.Burned); Assert.Equal(1, report.WorkoutCount);
        var log = await host.Use((s, _) => s.Log(client, result.Id!.Value)); input.Id = log.Id; input.Version = TestHost.Version(log); input.Calories = 150;
        Assert.True((await host.Use((s, _) => s.SaveLog(client, input))).Success);
        Assert.False((await host.Use((s, _) => s.SaveLog(client, input))).Success); // stale edit
        log = await host.Use((s, _) => s.Log(client, log.Id));
        Assert.True((await host.Use((s, _) => s.DeleteLog(client, log.Id, TestHost.Version(log)))).Success);
        report = await host.Use((s, _) => s.Progress(client, null, today, today)); Assert.Equal(0, report.WorkoutCount); Assert.Null(report.Burned);
    }
    [Fact] public async Task MeasurementsUseEarlierHeightAndNeverLaterHeight()
    {
        var client = await host.CreateActor(Roles.Client); var now = DateTimeOffset.UtcNow;
        await host.Use(async (s, _) => { Assert.True((await s.SaveLog(client, new() { Kind = LogKind.Measurement, Description = "Height", HeightCm = 175, LocalTime = TestHost.Local(now.AddHours(-2)) })).Success); return true; });
        await host.Use((s, _) => s.SaveLog(client, new() { Kind = LogKind.Measurement, Description = "Weight", WeightKg = 70, LocalTime = TestHost.Local(now.AddHours(-1)) }));
        await host.Use((s, _) => s.SaveLog(client, new() { Kind = LogKind.Measurement, Description = "New height", HeightCm = 200, LocalTime = TestHost.Local(now.AddMinutes(-1)) }));
        var today = DateOnly.FromDateTime(TestHost.Local(now)); var r = await host.Use((s, _) => s.Progress(client, null, today.AddDays(-1), today)); Assert.Equal(22.9m, r.Bmi); Assert.Equal(22.9m, r.WeightPoints.Single().Bmi);
    }
    [Fact] public async Task TrainerPublishesScheduledPlansClientSeesOnlyPublishedAndRevocationIsImmediate()
    {
        var (client, trainer) = await host.Pair(); var otherTrainer = await host.CreateActor(Roles.Trainer);
        var result = await host.Use((s, _) => s.SavePlan(trainer, new() { ClientId = client.Id, Kind = PlanKind.Diet, Title = "Meal plan" })); Assert.True(result.Success, result.Error);
        Assert.Empty((await host.Use((s, _) => s.Plans(client, null, null, 1))).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Plan(otherTrainer, result.Id!.Value)));
        var plan = await host.Use((s, _) => s.Plan(trainer, result.Id!.Value));
        var add = await host.Use((s, _) => s.AddItem(trainer, new() { PlanId = plan.Id, Version = TestHost.Version(plan), Description = "Oats", Day = DayOfWeek.Monday, Calories = 500 })); Assert.True(add.Success, add.Error);
        plan = await host.Use((s, _) => s.Plan(trainer, plan.Id));
        Assert.True((await host.Use((s, _) => s.ChangePlan(trainer, plan.Id, TestHost.Version(plan), PlanStatus.Published))).Success);
        Assert.Single((await host.Use((s, _) => s.Plans(client, null, null, 1))).Items);
        var today = DateOnly.FromDateTime(TestHost.Local(DateTimeOffset.UtcNow)); Assert.Null((await host.Use((s, _) => s.Progress(client, null, today, today))).Eaten);
        Assert.True((await host.Use((s, _) => s.Feedback(trainer, client.Id, "Keep going"))).Success);
        Assert.Single((await host.Use((s, _) => s.Dashboard(client))).Feedback);
        var assignment = (await host.Use((s, _) => s.Assignments(host.Admin))).Single(x => x.Assignment.ClientId == client.Id).Assignment;
        Assert.True((await host.Use((s, _) => s.Revoke(host.Admin, assignment.Id, TestHost.Version(assignment)))).Success);
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Progress(trainer, client.Id, today, today)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Plan(trainer, plan.Id)));
    }
    [Fact] public async Task ConcurrentOverlappingRequestsCreateOnlyOneHold()
    {
        var (client, trainer) = await host.Pair(); var client2 = await host.CreateActor(Roles.Client);
        await host.Use((s, _) => s.Assign(host.Admin, client2.Id, trainer.Id));
        var request = new BookingInput { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddDays(2)), DurationMinutes = 30, Purpose = "Check in" };
        var outcomes = await Task.WhenAll(host.Use((s, _) => s.RequestBooking(client, request)), host.Use((s, _) => s.RequestBooking(client2, request)));
        Assert.Single(outcomes, x => x.Success); Assert.Single(outcomes, x => !x.Success);
    }
    [Fact] public async Task BookingLifecycleEnforcesProviderScopeAndInvalidatesReminderOnReschedule()
    {
        var (client, trainer) = await host.Pair(); var unrelated = await host.CreateActor(Roles.Trainer);
        var result = await host.Use((s, _) => s.RequestBooking(client, new() { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(20)), DurationMinutes = 30, Purpose = "Progress" })); Assert.True(result.Success, result.Error);
        var a = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment;
        await Assert.ThrowsAsync<MissingRecordException>(() => host.Use((s, _) => s.ChangeBooking(unrelated, new() { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Confirmed })));
        Assert.False((await host.Use((s, _) => s.ChangeBooking(client, new() { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Confirmed }))).Success);
        Assert.True((await host.Use((s, _) => s.ChangeBooking(trainer, new() { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Confirmed }))).Success);
        await host.Use(async (s, _) => { await s.GenerateReminders(); await s.GenerateReminders(); return true; });
        Assert.Single((await host.Use((s, _) => s.Inbox(client, 1))).Items, x => x.Kind == NotificationKind.Appointment);
        a = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment;
        Assert.True((await host.Use((s, _) => s.ChangeBooking(client, new() { Id = a.Id, Version = TestHost.Version(a), RescheduleLocal = TestHost.Local(DateTimeOffset.UtcNow.AddDays(2)) }))).Success);
        a = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment; Assert.Equal(AppointmentStatus.Requested, a.Status);
        Assert.True((await host.Use((s, _) => s.ChangeBooking(client, new() { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Cancelled }))).Success);
    }
    [Fact] public async Task ProfessionalsAreCoordinatedByAdministratorAndTrainerCannotReadThem()
    {
        var client = await host.CreateActor(Roles.Client); var trainer = await host.CreateActor(Roles.Trainer);
        await host.Use((s, _) => s.SaveProfessional(host.Admin, new() { DisplayName = "Test provider " + Guid.NewGuid(), Specialty = "Consultation" }));
        var provider = (await host.Use((s, _) => s.Providers(client))).First(x => x.Id.StartsWith("professional:"));
        var result = await host.Use((s, _) => s.RequestBooking(client, new() { Provider = provider.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddDays(3)), Purpose = "Advice" })); Assert.True(result.Success, result.Error);
        var a = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment;
        Assert.Empty((await host.Use((s, _) => s.Bookings(trainer, 1))).Items);
        Assert.True((await host.Use((s, _) => s.ChangeBooking(host.Admin, new() { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Confirmed }))).Success);
    }
    [Fact] public async Task NotificationsCannotBeReadByAnotherClientAndOptOutPreventsScheduleDelivery()
    {
        var (client, trainer) = await host.Pair(); var other = await host.CreateActor(Roles.Client);
        var note = (await host.Use((s, _) => s.Inbox(client, 1))).Items.First();
        await Assert.ThrowsAsync<MissingRecordException>(() => host.Use((s, _) => s.MarkRead(other, note.Id)));
        Assert.True((await host.Use((s, _) => s.MarkRead(client, note.Id))).Success);
        await host.Use((s, _) => s.UpdateProfile(client, new() { DisplayName = "Quiet client", AppointmentReminders = false, WorkoutReminders = false, MealReminders = false }));
        var request = await host.Use((s, _) => s.RequestBooking(client, new() { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(15)), Purpose = "Check in" }));
        var a = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment;
        await host.Use((s, _) => s.ChangeBooking(trainer, new() { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Confirmed }));
        await host.Use(async (s, _) => { await s.GenerateReminders(); return true; });
        Assert.DoesNotContain((await host.Use((s, _) => s.Inbox(client, 1))).Items, x => x.Kind == NotificationKind.Appointment);
    }
    [Fact] public async Task DisablingTrainerEndsAssignmentCancelsHoldsAndDeniesAccess()
    {
        var (client, trainer) = await host.Pair();
        await host.Use((s, _) => s.RequestBooking(client, new() { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddDays(4)), Purpose = "Check in" }));
        Assert.True((await host.Use((s, _) => s.SetActive(host.Admin, trainer.Id, false))).Success);
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Dashboard(trainer)));
        Assert.Equal(AppointmentStatus.Cancelled, (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment.Status);
    }
}

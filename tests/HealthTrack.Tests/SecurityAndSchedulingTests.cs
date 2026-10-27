using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HealthTrack.Tests;
[Collection("Database")]
public class SecurityAndSchedulingTests(TestHost host)
{
    [Fact] public async Task ForgedActorRoleCannotBypassServiceAuthorization()
    {
        var client = await host.CreateActor(Roles.Client);
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Members(new(client.Id, Roles.Administrator), null, null, 1)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.SaveProfessional(client, new() { DisplayName = "Forged", Specialty = "No" })));
    }
    [Fact] public async Task StaleBookingTransitionIsRejectedAndPendingReminderInvalidatedOnCancel()
    {
        var (client, trainer) = await host.Pair();
        var request = await host.Use((s, _) => s.RequestBooking(client, new() { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddDays(1)), Purpose = "Check" })); Assert.True(request.Success, request.Error);
        var a = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment;
        var confirm = new BookingChange { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Confirmed };
        Assert.True((await host.Use((s, _) => s.ChangeBooking(trainer, confirm))).Success);
        Assert.False((await host.Use((s, _) => s.ChangeBooking(trainer, confirm))).Success);
        await host.Use(async (s, _) => { await s.GenerateReminders(); return true; });
        Assert.True(await host.Use((_, db) => db.Notifications.AnyAsync(x => x.RelatedId == a.Id && !x.Invalidated && x.DeliveredAtUtc == null)));
        a = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single().Appointment;
        await host.Use((s, _) => s.ChangeBooking(client, new() { Id = a.Id, Version = TestHost.Version(a), Status = AppointmentStatus.Cancelled }));
        Assert.False(await host.Use((_, db) => db.Notifications.AnyAsync(x => x.RelatedId == a.Id && !x.Invalidated && x.DeliveredAtUtc == null)));
    }
    [Fact] public async Task TransferPreservesHistoryAndReassignsAccess()
    {
        var (client, trainer) = await host.Pair(); var next = await host.CreateActor(Roles.Trainer);
        Assert.True((await host.Use((s, _) => s.Assign(host.Admin, client.Id, next.Id))).Success);
        var history = (await host.Use((s, _) => s.Assignments(host.Admin))).Where(x => x.Assignment.ClientId == client.Id).ToList();
        Assert.Equal(2, history.Count); Assert.Single(history, x => x.Assignment.EndedAtUtc == null);
        Assert.Empty((await host.Use((s, _) => s.Members(trainer, Roles.Client, null, 1))).Items);
        Assert.Single((await host.Use((s, _) => s.Members(next, Roles.Client, null, 1))).Items);
    }
    [Fact] public async Task InvalidDatesAndOutOfRangeValuesAreRejected()
    {
        var (client, trainer) = await host.Pair();
        var invalid = await host.Use((s, _) => s.SaveLog(client, new() { Description = "Invalid", Kind = LogKind.Measurement, WeightKg = -2, LocalTime = TestHost.Local(DateTimeOffset.UtcNow) })); Assert.False(invalid.Success);
        var past = await host.Use((s, _) => s.RequestBooking(client, new() { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddDays(-1)), Purpose = "Past" })); Assert.False(past.Success);
        var today = DateOnly.FromDateTime(DateTime.Today);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.Progress(client, null, today.AddDays(1), today)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.Progress(client, null, today.AddDays(-400), today)));
    }
}

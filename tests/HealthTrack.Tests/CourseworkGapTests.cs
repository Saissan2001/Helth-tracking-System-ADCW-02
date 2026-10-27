using System.Net;
using System.Text;
using HealthTrack.Application;
using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using HealthTrack.Web.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HealthTrack.Tests;
[Collection("Database")]
public class CourseworkGapTests(TestHost host)
{
    [Fact] public async Task HealthValuesAreCiphertextInSqlAndRoundTripAcrossScopes()
    {
        var actor = await host.CreateActor(Roles.Client);
        var saved = await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Measurement, Description = "Private measurement", Notes = "Confidential note", WeightKg = 72.35m, HeightCm = 175, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }));
        Assert.True(saved.Success, saved.Error);
        var raw = await host.Use(async (_, db) => new[] {
            await db.Database.SqlQueryRaw<string>("SELECT [Notes] AS [Value] FROM [Logs] WHERE [Id]={0}", saved.Id!).SingleAsync(),
            await db.Database.SqlQueryRaw<string>("SELECT [WeightKg] AS [Value] FROM [Logs] WHERE [Id]={0}", saved.Id!).SingleAsync() });
        Assert.All(raw, value => Assert.StartsWith(SensitiveData.Prefix, value));
        Assert.DoesNotContain("Confidential note", raw[0]);
        var read = await host.Use((s, _) => s.Log(actor, saved.Id!.Value)); Assert.Equal(72.35m, read.WeightKg); Assert.Equal("Confidential note", read.Notes);
        using var scope = host.Services.CreateScope(); var cipher = scope.ServiceProvider.GetRequiredService<SensitiveData>();
        var encoded = cipher.Protect("test", "value");
        Assert.NotEqual(encoded, cipher.Protect("test", "value"));
        Assert.ThrowsAny<Exception>(() => cipher.Unprotect("wrong-purpose", encoded));
        Assert.Throws<InvalidOperationException>(() => cipher.Unprotect("test", "plaintext"));
    }
    [Fact] public async Task LegacyUpgradeEncryptsWithoutLosingValuesAndIsRepeatable()
    {
        var actor = await host.CreateActor(Roles.Client);
        var saved = await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Measurement, Description = "Legacy", WeightKg = 70, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }));
        await host.Use(async (_, db) => await db.Database.ExecuteSqlRawAsync("UPDATE [Logs] SET [Notes]={0}, [WeightKg]={1} WHERE [Id]={2}", "legacy private note", "73.50", saved.Id!));
        using (var scope = host.Services.CreateScope())
        {
            var cipher = scope.ServiceProvider.GetRequiredService<SensitiveData>(); var db = scope.ServiceProvider.GetRequiredService<HealthTrackDbContext>();
            await cipher.UpgradeExisting(db); await cipher.UpgradeExisting(db);
        }
        var read = await host.Use((s, _) => s.Log(actor, saved.Id!.Value)); Assert.Equal(73.5m, read.WeightKg); Assert.Equal("legacy private note", read.Notes);
    }
    [Fact] public async Task EngagementCountsRealCategoriesAndExcludesUnassignedClients()
    {
        var (client, trainer) = await host.Pair(); var idle = await host.CreateActor(Roles.Client); var outsider = await host.CreateActor(Roles.Client);
        await host.Use((s, _) => s.Assign(host.Admin, idle.Id, trainer.Id));
        foreach (var actor in new[] { client, outsider })
            Assert.True((await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Workout, Routine = WorkoutRoutine.Walking, Description = "Private title", DurationMinutes = 35, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }))).Success);
        var today = DateOnly.FromDateTime(TestHost.Local(DateTimeOffset.UtcNow));
        var report = await host.Use((s, _) => s.Engagement(trainer, today.AddDays(-1), today));
        Assert.Equal(2, report.EligibleClients); Assert.Equal(1, report.EngagedClients); Assert.Equal(50, report.EngagementPercent);
        Assert.Equal(35, report.Clients.Sum(c => c.WorkoutMinutes)); Assert.DoesNotContain(report.Clients, c => c.ClientId == outsider.Id);
        Assert.Equal(1, report.Routines.Single(r => r.Routine == WorkoutRoutine.Walking).LoggedSessions);
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Engagement(client, today, today)));
        var admin = await host.Use((s, _) => s.Engagement(host.Admin, today.AddDays(-1), today)); Assert.Empty(admin.Clients);
        var assignment = (await host.Use((s, _) => s.Assignments(host.Admin))).Single(a => a.Assignment.ClientId == client.Id).Assignment;
        await host.Use((s, _) => s.Revoke(host.Admin, assignment.Id, TestHost.Version(assignment)));
        Assert.DoesNotContain((await host.Use((s, _) => s.Engagement(trainer, today.AddDays(-1), today))).Clients, c => c.ClientId == client.Id);
    }
    [Fact] public async Task TrainerCanRescheduleButCannotOverlapOrUseStaleVersion()
    {
        var (client, trainer) = await host.Pair(); var start = DateTimeOffset.UtcNow.AddDays(15);
        foreach (var offset in new[] { 0, 2 }) Assert.True((await host.Use((s, _) => s.RequestBooking(client, new() { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(start.AddHours(offset)), Purpose = "Consultation" }))).Success);
        var booking = (await host.Use((s, _) => s.Bookings(client, 1))).Items.OrderBy(b => b.Appointment.StartsAtUtc).First().Appointment;
        var change = new BookingChange { Id = booking.Id, Version = TestHost.Version(booking), RescheduleLocal = TestHost.Local(start.AddHours(2)), Note = "Provider schedule changed" };
        Assert.False((await host.Use((s, _) => s.ChangeBooking(trainer, change))).Success);
        change.RescheduleLocal = TestHost.Local(start.AddHours(4)); Assert.True((await host.Use((s, _) => s.ChangeBooking(trainer, change))).Success);
        Assert.False((await host.Use((s, _) => s.ChangeBooking(trainer, change))).Success);
        var updated = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single(b => b.Appointment.Id == booking.Id).Appointment;
        Assert.Equal(AppointmentStatus.Confirmed, updated.Status); Assert.Equal("Provider schedule changed", updated.ResponseNote);
        Assert.Contains((await host.Use((s, _) => s.Inbox(client, 1))).Items, n => n.Title.Contains("rescheduled"));
    }
    [Fact] public async Task ReportRoutesExportsAndPublicPagesRenderWithCorrectAuthorization()
    {
        using var anonymous = host.Browser();
        foreach (var path in new[] { "/Home/About", "/Home/Contact", "/Home/Privacy", "/Home/Terms" }) Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(path)).StatusCode);
        var (client, trainer) = await host.Pair();
        using var staff = await host.Login(trainer);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/Reports")).StatusCode);
        var csv = await staff.GetAsync("/Reports/Export"); Assert.Equal(HttpStatusCode.OK, csv.StatusCode); Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        using var member = await host.Login(client);
        Assert.Equal(HttpStatusCode.Redirect, (await member.GetAsync("/Reports")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/Progress/Export")).StatusCode);
        var outsider = await host.CreateActor(Roles.Client);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/Progress/Export?clientId=" + outsider.Id)).StatusCode);
    }
    [Fact] public void CsvEscapesDelimitersAndNeutralizesFormulas()
    {
        var csv = Encoding.UTF8.GetString(CsvReport.Encode([new object?[] { "=1+1", "a,b", "a\"b", "normal" }]));
        Assert.Contains("\"'=1+1\"", csv); Assert.Contains("\"a,b\"", csv); Assert.Contains("\"a\"\"b\"", csv);
    }
}

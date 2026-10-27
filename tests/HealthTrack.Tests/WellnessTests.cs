using System.Net;
using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HealthTrack.Tests;
[CollectionDefinition("WellnessDatabase", DisableParallelization = true)]
public class WellnessCollection : ICollectionFixture<TestHost>;
[Collection("WellnessDatabase")]
public class WellnessTests(TestHost host)
{
    private static DateOnly Yesterday => DateOnly.FromDateTime(TestHost.Local(DateTimeOffset.UtcNow)).AddDays(-1);
    private async Task<HealthLog> Save(Actor actor, LogInput input)
    {
        var result = await host.Use((s, _) => s.SaveLog(actor, input));
        Assert.True(result.Success, result.Error);
        return await host.Use((s, _) => s.Log(actor, result.Id!.Value));
    }
    [Fact] public async Task LocalDiarySeparatesDaysUsesLatestStepsAndPreservesUnknownNutrition()
    {
        var actor = await host.CreateActor(Roles.Client); var day = Yesterday;
        await Save(actor, new() { Kind = LogKind.Hydration, Description = "Water", LocalTime = day.ToDateTime(new(0, 5)), WaterMl = 250 });
        await Save(actor, new() { Kind = LogKind.Hydration, Description = "Water", LocalTime = day.ToDateTime(new(23, 55)), WaterMl = 500 });
        await Save(actor, new() { Kind = LogKind.Hydration, Description = "Earlier", LocalTime = day.AddDays(-1).ToDateTime(new(23, 55)), WaterMl = 100 });
        await Save(actor, new() { Kind = LogKind.Measurement, Description = "Steps", LocalTime = day.ToDateTime(new(9, 0)), Steps = 1200 });
        await Save(actor, new() { Kind = LogKind.Measurement, Description = "Steps", LocalTime = day.ToDateTime(new(17, 0)), Steps = 4000 });
        await Save(actor, new() { Kind = LogKind.Meal, Description = "Unknown", LocalTime = day.ToDateTime(new(12, 0)) });
        var other = await host.CreateActor(Roles.Client);
        await Save(other, new() { Kind = LogKind.Hydration, Description = "Private", LocalTime = day.ToDateTime(new(12, 0)), WaterMl = 900 });
        var result = await host.Use((s, _) => s.Daily(actor, day));
        Assert.Equal(750, result.Totals.WaterMl); Assert.Equal(4000, result.Totals.Steps);
        Assert.Null(result.Totals.MealCalories); Assert.Null(result.Totals.Protein); Assert.Equal(1, result.Totals.MealsMissingCalories);
        Assert.Equal(5, result.Entries.Total); Assert.Equal(100, result.Days[^2].WaterMl); Assert.Null(result.Days[0].WaterMl);
        await Save(actor, new() { Kind = LogKind.Meal, Description = "Known zero", LocalTime = day.ToDateTime(new(13, 0)), Calories = 0, Protein = 0 });
        result = await host.Use((s, _) => s.Daily(actor, day));
        Assert.Equal(0m, result.Totals.MealCalories); Assert.Equal(0m, result.Totals.Protein); Assert.Null(result.Totals.Carbs);
    }
    [Fact] public async Task SleepUsesWakeDateRejectsOverlapAndSupportsCorrectionAndAdjacentSessions()
    {
        var actor = await host.CreateActor(Roles.Client); var day = Yesterday;
        var input = new LogInput { Kind = LogKind.Sleep, Description = "Rest", SleepStartedLocal = day.AddDays(-1).ToDateTime(new(23, 0)), LocalTime = day.ToDateTime(new(7, 0)) };
        var log = await Save(actor, input);
        Assert.Equal(480, log.DurationMinutes);
        var daily = await host.Use((s, _) => s.Daily(actor, day));
        Assert.Equal(480, daily.Totals.SleepMinutes); Assert.Null(daily.Days[^2].SleepMinutes);
        var overlap = await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Sleep, Description = "Overlap", SleepStartedLocal = day.ToDateTime(new(6, 0)), LocalTime = day.ToDateTime(new(8, 0)) }));
        Assert.False(overlap.Success); Assert.Equal("SleepStartedLocal", overlap.Field);
        input.Id = log.Id; input.Version = TestHost.Version(log); input.LocalTime = day.ToDateTime(new(6, 30));
        log = await Save(actor, input); Assert.Equal(450, log.DurationMinutes);
        await Save(actor, new() { Kind = LogKind.Sleep, Description = "Adjacent", SleepStartedLocal = input.LocalTime, LocalTime = day.ToDateTime(new(7, 0)) });
        Assert.Equal(480, (await host.Use((s, _) => s.Daily(actor, day))).Totals.SleepMinutes);
        var invalid = await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Sleep, Description = "Invalid", SleepStartedLocal = input.LocalTime, LocalTime = input.LocalTime }));
        Assert.False(invalid.Success); Assert.Equal("LocalTime", invalid.Field);
    }
    [Fact] public async Task SleepRejectsInvalidDaylightSavingTime()
    {
        var actor = await host.CreateActor(Roles.Client);
        Assert.True((await host.Use((s, _) => s.UpdateProfile(actor, new() { DisplayName = "DST test", TimeZoneId = "America/New_York" }))).Success);
        var result = await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Sleep, Description = "DST", SleepStartedLocal = new(2026, 3, 8, 2, 30, 0), LocalTime = new(2026, 3, 8, 7, 0, 0) }));
        Assert.False(result.Success); Assert.Equal("SleepStartedLocal", result.Field);
    }
    [Fact] public async Task PreferencesAreOptionalPrivateEncryptedAndConcurrencyProtected()
    {
        var actor = await host.CreateActor(Roles.Client);
        var settings = await host.Use((s, _) => s.WellnessSettings(actor));
        Assert.Null(settings.SetupCompletedAtUtc); Assert.Null(settings.DailyWaterMl);
        Assert.True((await host.Use((s, _) => s.SaveWellnessSettings(actor, new() { Focus = WellnessFocus.Rest, DailyWaterMl = 1750, NightlySleepMinutes = 450, DailySteps = 6000, WeeklyWorkoutMinutes = 120, ShowSteps = false }))).Success);
        settings = await host.Use((s, _) => s.WellnessSettings(actor));
        Assert.NotNull(settings.SetupCompletedAtUtc); Assert.Equal(1750, settings.DailyWaterMl); Assert.False(settings.ShowSteps);
        var raw = await host.Use((_, db) => db.Database.SqlQuery<string>($"SELECT DailyWaterMl AS Value FROM WellnessPreferences WHERE UserId = {actor.Id}").SingleAsync());
        Assert.StartsWith("ht1:", raw); Assert.NotEqual("1750", raw);
        Assert.False((await host.Use((s, _) => s.SaveWellnessSettings(actor, new() { DailyWaterMl = 3000 }))).Success);
        Assert.True((await host.Use((s, _) => s.SaveWellnessSettings(actor, new() { Version = TestHost.Version(settings), ShowWater = false }))).Success);
        settings = await host.Use((s, _) => s.WellnessSettings(actor)); Assert.Null(settings.DailyWaterMl); Assert.False(settings.ShowWater);
        var other = await host.CreateActor(Roles.Client); Assert.Null((await host.Use((s, _) => s.WellnessSettings(other))).SetupCompletedAtUtc);
        await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.Daily(host.Admin)));
    }
    [Fact] public async Task SavedCopiesRequireReviewPreserveAmountsAndNeverInheritIdentity()
    {
        var actor = await host.CreateActor(Roles.Client); var other = await host.CreateActor(Roles.Client);
        var log = await Save(actor, new() { Kind = LogKind.Meal, Description = "Lunch", LocalTime = Yesterday.ToDateTime(new(12, 0)), Calories = 500, Protein = 20 });
        Assert.True((await host.Use((s, _) => s.SetFavorite(actor, log.Id, TestHost.Version(log), true))).Success);
        Assert.Equal(1, (await host.Use((s, _) => s.Favorites(actor, 1))).Total);
        var copy = await host.Use((s, _) => s.RepeatLog(actor, log.Id));
        Assert.Null(copy.Id); Assert.Null(copy.PlanItemId); Assert.Null(copy.ScheduledDate); Assert.Empty(copy.Version); Assert.Equal(500m, copy.Calories);
        Assert.Equal(1, await host.Use((_, db) => db.Logs.CountAsync(x => x.ClientId == actor.Id)));
        await Assert.ThrowsAsync<MissingRecordException>(() => host.Use((s, _) => s.RepeatLog(other, log.Id)));
        await Assert.ThrowsAsync<MissingRecordException>(() => host.Use((s, _) => s.SetFavorite(other, log.Id, TestHost.Version(log), true)));
        var saved = await Save(actor, copy); Assert.NotEqual(log.Id, saved.Id); Assert.False(saved.IsFavorite);
        log = await host.Use((s, _) => s.Log(actor, log.Id));
        Assert.True((await host.Use((s, _) => s.SetFavorite(actor, log.Id, TestHost.Version(log), false))).Success);
        Assert.Equal(0, (await host.Use((s, _) => s.Favorites(actor, 1))).Total); Assert.Equal(2, await host.Use((_, db) => db.Logs.CountAsync(x => x.ClientId == actor.Id)));
    }
    [Fact] public async Task WaterBoundsStorageAndDeletionUpdateTotals()
    {
        var actor = await host.CreateActor(Roles.Client);
        foreach (var amount in new int?[] { null, 0, 10001 }) Assert.False((await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Hydration, Description = "Water", LocalTime = Yesterday.ToDateTime(new(12, 0)), WaterMl = amount }))).Success);
        var log = await Save(actor, new() { Kind = LogKind.Hydration, Description = "Water", LocalTime = Yesterday.ToDateTime(new(12, 0)), WaterMl = 250 });
        var raw = await host.Use((_, db) => db.Database.SqlQuery<string>($"SELECT WaterMl AS Value FROM Logs WHERE Id = {log.Id}").SingleAsync()); Assert.StartsWith("ht1:", raw);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.RepeatLog(actor, log.Id)));
        Assert.False((await host.Use((s, _) => s.SetFavorite(actor, log.Id, TestHost.Version(log), true))).Success);
        Assert.True((await host.Use((s, _) => s.DeleteLog(actor, log.Id, TestHost.Version(log)))).Success);
        Assert.Null((await host.Use((s, _) => s.Daily(actor, Yesterday))).Totals.WaterMl);
    }
    [Fact] public async Task DiaryIsPagedAndDatesAreBounded()
    {
        var actor = await host.CreateActor(Roles.Client);
        for (var i = 0; i < 21; i++) await Save(actor, new() { Kind = LogKind.Hydration, Description = "Water", LocalTime = Yesterday.ToDateTime(new(12, i)), WaterMl = 100 });
        var first = await host.Use((s, _) => s.Daily(actor, Yesterday, 1)); var last = await host.Use((s, _) => s.Daily(actor, Yesterday, 999));
        Assert.Equal(20, first.Entries.Items.Count); Assert.Single(last.Entries.Items); Assert.Equal(2, last.Entries.Number); Assert.Equal(2100, first.Totals.WaterMl);
        Assert.Empty(first.Entries.Items.Select(x => x.Id).Intersect(last.Entries.Items.Select(x => x.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Use((s, _) => s.Daily(actor, Yesterday.AddDays(2))));
    }
    [Fact] public async Task HttpPagesInlineValidationLocalRedirectAndCsvWorkEndToEnd()
    {
        var actor = await host.CreateActor(Roles.Client); using var browser = await host.Login(actor);
        foreach (var route in new[] { "/Dashboard", "/Wellness", "/Wellness/Settings", "/Wellness/Library", "/Wellness/Trends", "/Logs/Edit?kind=Sleep", "/Logs/Edit?kind=Hydration" })
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(route)).StatusCode);
        var token = await TestHost.Token(browser, "/Logs/Edit?kind=Hydration");
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Kind"] = "Hydration", ["Description"] = "Water", ["LocalTime"] = Yesterday.ToDateTime(new(12, 0)).ToString("yyyy-MM-ddTHH:mm"), ["returnUrl"] = "https://example.com/" };
        var invalid = await browser.PostAsync("/Logs/Edit", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode); var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("data-valmsg-for=\"WaterMl\"", invalidHtml); Assert.Contains("Enter the amount of water", invalidHtml);
        fields["WaterMl"] = "350";
        var success = await browser.PostAsync("/Logs/Edit", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.Redirect, success.StatusCode); Assert.Equal("/Wellness", success.Headers.Location!.ToString());
        var diary = await browser.GetStringAsync($"/Wellness?date={Yesterday:yyyy-MM-dd}"); Assert.Contains("350 ml water", diary); Assert.Contains($"date={Yesterday:yyyy-MM-dd}", diary);
        var csv = await browser.GetStringAsync($"/Wellness/Export?date={Yesterday:yyyy-MM-dd}"); Assert.Contains("Latest daily steps", csv); Assert.Contains("350", csv);
        var prefsToken = await TestHost.Token(browser, "/Wellness/Settings");
        var response = await browser.PostAsync("/Wellness/Settings", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = prefsToken, ["DailyWaterMl"] = "1500", ["ShowWater"] = "true", ["ShowSteps"] = "false", ["Focus"] = "Rest" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal(1500, (await host.Use((s, _) => s.WellnessSettings(actor))).DailyWaterMl);
        using var staff = await host.Login(host.Admin); var denied = await staff.GetAsync("/Wellness");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode); Assert.Contains("/Home/Denied", denied.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Wellness/Settings", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
    }
}

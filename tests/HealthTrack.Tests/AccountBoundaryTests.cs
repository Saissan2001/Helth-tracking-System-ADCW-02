using System.Net;
using System.Text;
using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HealthTrack.Tests;
public class RecoveryHost : TestHost
{
    public string MailDirectory { get; } = Path.Combine(Path.GetTempPath(), "HealthTrack_TestMail_" + Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("DevelopmentMail:Directory", MailDirectory);
        builder.UseSetting("DevelopmentMail:Enabled", "true");
    }
}
public class AccountBoundaryTests : IAsyncLifetime
{
    private readonly RecoveryHost host = new();
    public Task InitializeAsync() => host.InitializeAsync();
    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        if (Directory.Exists(host.MailDirectory))
        {
            // Only generated files in this test's unique, fully resolved temp directory.
            foreach (var path in Directory.GetFiles(host.MailDirectory, "*.txt")) File.Delete(path);
            Directory.Delete(host.MailDirectory);
        }
    }
    private async Task<string> Email(string id) => (await host.Use((_, db) => db.Users.Where(x => x.Id == id).Select(x => x.Email).SingleAsync()))!;
    private static async Task<HttpResponseMessage> Post(HttpClient browser, string path, Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = await TestHost.Token(browser, path);
        return await browser.PostAsync(path, new FormUrlEncodedContent(values));
    }
    [Fact] public async Task RecoverySinkResetLoginAndSingleUseTokenWorkEndToEnd()
    {
        var actor = await host.CreateActor(Roles.Client); var email = await Email(actor.Id); using var browser = host.Browser();
        var forgot = await Post(browser, "/Account/ForgotPassword", new() { ["email"] = email }); Assert.Equal(HttpStatusCode.OK, forgot.StatusCode);
        var file = Assert.Single(Directory.GetFiles(host.MailDirectory, "*.txt"));
        var link = new Uri((await File.ReadAllLinesAsync(file))[1]); var query = QueryHelpers.ParseQuery(link.Query); var token = query["token"].ToString();
        var password = "New!9" + Guid.NewGuid().ToString("N") + "aA";
        Dictionary<string, string> Reset() => new() { ["Email"] = email, ["Token"] = token, ["Password"] = password, ["ConfirmPassword"] = password };
        var reset = await Post(browser, "/Account/ResetPassword", Reset()); Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode); Assert.Equal("/Account/Login", reset.Headers.Location!.OriginalString);
        Assert.Contains("invalid or expired", await (await Post(browser, "/Account/ResetPassword", Reset())).Content.ReadAsStringAsync());
        var oldLogin = await Post(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = host.Password }); Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
        var login = await Post(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = password }); Assert.Equal(HttpStatusCode.Redirect, login.StatusCode); Assert.Equal("/Dashboard", login.Headers.Location!.OriginalString);
    }
    [Fact] public async Task ExpiredAndOtherAccountTokensCannotChangePasswords()
    {
        var actor = await host.CreateActor(Roles.Client); var other = await host.CreateActor(Roles.Client); var email = await Email(actor.Id);
        using var scope = host.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var user = (await users.FindByIdAsync(actor.Id))!;
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
        using var browser = host.Browser(); var password = "Different!9Password";
        async Task<HttpResponseMessage> Reset(string address) => await Post(browser, "/Account/ResetPassword", new() { ["Email"] = address, ["Token"] = token, ["Password"] = password, ["ConfirmPassword"] = password });
        Assert.Contains("invalid or expired", await (await Reset(await Email(other.Id))).Content.ReadAsStringAsync());
        // Exercise the real provider's expiry check deterministically without sleeping for a day.
        scope.ServiceProvider.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value.TokenLifespan = TimeSpan.FromSeconds(-1);
        Assert.Contains("invalid or expired", await (await Reset(email)).Content.ReadAsStringAsync());
        Assert.True(await users.CheckPasswordAsync(user, host.Password));
    }
    [Fact] public async Task FifthFailureLocksAccountAndExpiredLockoutAllowsLogin()
    {
        var actor = await host.CreateActor(Roles.Client); var email = await Email(actor.Id); using var browser = host.Browser();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Post(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = "Incorrect!9Password" })).StatusCode);
            using var scope = host.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.Equal(attempt == 5, await users.IsLockedOutAsync((await users.FindByIdAsync(actor.Id))!));
        }
        Assert.Equal(HttpStatusCode.OK, (await Post(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = host.Password })).StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var user = (await users.FindByIdAsync(actor.Id))!;
            Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(14));
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddSeconds(-1))).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await Post(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = host.Password })).StatusCode);
    }
    [Fact] public async Task AccountLimiterRejectsRequest31WithRetryAfterAndLeavesPublicPagesAvailable()
    {
        using var browser = host.Browser();
        for (var request = 0; request < 30; request++) Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account/Login")).StatusCode);
        var limited = await browser.GetAsync("/Account/Login"); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Home/About")).StatusCode);
    }
    [Fact] public async Task AdminReportSuppressesSmallCohortsAndNeverExposesIndividualRows()
    {
        var date = DateOnly.FromDateTime(TestHost.Local(DateTimeOffset.UtcNow));
        for (var i = 0; i < 3; i++)
        {
            var client = await host.CreateActor(Roles.Client);
            await host.Use((s, _) => s.SaveLog(client, new() { Kind = LogKind.Workout, Routine = WorkoutRoutine.Walking, Description = "Private", DurationMinutes = 20, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }));
        }
        var report = await host.Use((s, _) => s.Engagement(host.Admin, date.AddDays(-1), date));
        Assert.True(report.Suppressed); Assert.Empty(report.Clients); Assert.Empty(report.Routines);
        using var admin = await host.Login(host.Admin); var csv = await admin.GetStringAsync("/Reports/Export"); Assert.Contains("suppressed", csv); Assert.DoesNotContain("Workout minutes", csv);
        for (var i = 0; i < 2; i++)
        {
            var client = await host.CreateActor(Roles.Client);
            await host.Use((s, _) => s.SaveLog(client, new() { Kind = LogKind.Workout, Routine = WorkoutRoutine.Walking, Description = "Private", DurationMinutes = 20, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }));
        }
        report = await host.Use((s, _) => s.Engagement(host.Admin, date.AddDays(-1), date)); Assert.False(report.Suppressed); Assert.Empty(report.Clients); Assert.Equal(5, report.Routines.Single().UniqueParticipants);
        var existingId = await host.Use((_, db) => db.Profiles.Where(x => x.Role == Roles.Client).Select(x => x.UserId).FirstAsync());
        await host.Use((s, _) => s.SaveLog(new(existingId, Roles.Client), new() { Kind = LogKind.Workout, Routine = WorkoutRoutine.Yoga, Description = "Small category", DurationMinutes = 15, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }));
        report = await host.Use((s, _) => s.Engagement(host.Admin, date.AddDays(-1), date));
        Assert.False(report.Suppressed); Assert.Empty(report.Routines); // Prevent total-minus-visible-category inference.
        await host.CreateActor(Roles.Client); // A one-person inactive complement must also be hidden.
        Assert.True((await host.Use((s, _) => s.Engagement(host.Admin, date.AddDays(-1), date))).Suppressed);
    }
}

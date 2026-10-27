using System.Net;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HealthTrack.Tests;
[Collection("Database")]
public class HttpTests(TestHost host)
{
    [Fact] public async Task AnonymousIsRedirectedAndMissingCsrfIsRejected()
    {
        using var client = host.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "test@test.invalid", ["Password"] = "invalid" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }
    [Fact] public async Task AllThreeRolesRenderTheirPagesAndRejectForeignRoutes()
    {
        var (clientActor, trainerActor) = await host.Pair();
        using var client = await host.Login(clientActor);
        foreach (var path in new[] { "/Dashboard", "/Logs?kind=Workout", "/Logs?kind=Meal", "/Logs?kind=Measurement", "/Logs/Edit?kind=Measurement", "/Plans", "/Progress", "/Appointments", "/Appointments/Create", "/Notifications", "/Account/Profile", "/Account/Password" })
        { var response = await client.GetAsync(path); Assert.True(response.StatusCode == HttpStatusCode.OK, path + " returned " + response.StatusCode); }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin/Users")).StatusCode);
        using var trainer = await host.Login(trainerActor);
        foreach (var path in new[] { "/Dashboard", "/Trainer/Clients", "/Trainer/Client/" + clientActor.Id, "/Plans/Edit?clientId=" + clientActor.Id, "/Appointments", "/Plans" })
        { var response = await trainer.GetAsync(path); Assert.True(response.StatusCode == HttpStatusCode.OK, path + " returned " + response.StatusCode); }
        using var admin = await host.Login(host.Admin);
        foreach (var path in new[] { "/Dashboard", "/Admin/Users", "/Admin/Assignments", "/Admin/Professionals", "/Admin/Reports", "/Account/CreateTrainer", "/Appointments" })
        { var response = await admin.GetAsync(path); Assert.True(response.StatusCode == HttpStatusCode.OK, path + " returned " + response.StatusCode); }
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/Progress?clientId=" + clientActor.Id)).StatusCode);
    }
    [Fact] public async Task PublicRegistrationIgnoresForgedRoleAndRejectsDuplicateEmail()
    {
        using var browser = host.Browser(); var email = "signup" + Guid.NewGuid().ToString("N") + "@test.invalid";
        var token = await TestHost.Token(browser, "/Account/Register");
        var fields = new Dictionary<string, string> { ["Email"] = email, ["DisplayName"] = "New client", ["Password"] = host.Password, ["ConfirmPassword"] = host.Password, ["Role"] = "Administrator", ["IsAdmin"] = "true", ["__RequestVerificationToken"] = token };
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(fields))).StatusCode);
        var role = await host.Use(async (_, db) => await (from p in db.Profiles join u in db.Users on p.UserId equals u.Id where u.Email == email select p.Role).SingleAsync()); Assert.Equal(Roles.Client, role);
        var duplicate = await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); Assert.Contains("Unable to create this account", await duplicate.Content.ReadAsStringAsync());
    }
    [Fact] public async Task WrongPasswordCannotSignInAndExternalReturnUrlIsIgnored()
    {
        var actor = await host.CreateActor(Roles.Client); var email = await host.Use(async (_, db) => await db.Users.Where(x => x.Id == actor.Id).Select(x => x.Email).SingleAsync());
        using var browser = host.Browser(); var token = await TestHost.Token(browser, "/Account/Login");
        var form = new Dictionary<string, string> { ["Email"] = email!, ["Password"] = "WrongPassword!", ["ReturnUrl"] = "https://evil.invalid", ["__RequestVerificationToken"] = token };
        var failed = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(form)); Assert.Contains("Unable to sign in", await failed.Content.ReadAsStringAsync());
        form["Password"] = host.Password;
        var result = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(form)); Assert.Equal("/Dashboard", result.Headers.Location?.ToString());
    }
    [Fact] public async Task StoredHtmlIsEncodedAndAnotherClientsEditIsNotFound()
    {
        var actor = await host.CreateActor(Roles.Client); var other = await host.CreateActor(Roles.Client);
        var result = await host.Use((s, _) => s.SaveLog(actor, new() { Kind = LogKind.Workout, Description = "<script>alert(1)</script>", DurationMinutes = 10, LocalTime = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-1)) }));
        using var browser = await host.Login(actor); var html = await browser.GetStringAsync("/Logs?kind=Workout"); Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>alert", html);
        using var stranger = await host.Login(other); Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync("/Logs/Edit/" + result.Id)).StatusCode);
    }
    [Fact] public async Task InvalidResetTokenIsRejectedWithoutChangingPassword()
    {
        using var browser = host.Browser(); var token = await TestHost.Token(browser, "/Account/ResetPassword?email=missing@test.invalid&token=invalid");
        var result = await browser.PostAsync("/Account/ResetPassword", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "missing@test.invalid", ["Token"] = "invalid", ["Password"] = host.Password, ["ConfirmPassword"] = host.Password, ["__RequestVerificationToken"] = token }));
        Assert.Contains("invalid or expired", await result.Content.ReadAsStringAsync());
    }
}

using System.Net;
using System.Text.RegularExpressions;
using HealthTrack.Application;
using HealthTrack.Domain;
using Xunit;

namespace HealthTrack.Tests;

[CollectionDefinition("FeedbackDatabase", DisableParallelization = true)]
public class FeedbackDatabaseCollection : ICollectionFixture<TestHost>;

[Collection("FeedbackDatabase")]
public class FeedbackTests(TestHost host)
{
    private static string FieldError(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        "<span(?=[^>]*data-valmsg-for=\"" + Regex.Escape(name) + "\")(?=[^>]*class=\"field-validation-error\")[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);
    private static async Task<HttpResponseMessage> Post(HttpClient browser, string url, string tokenPage, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await TestHost.Token(browser, tokenPage);
        return await browser.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    [Fact] public async Task AccountErrorsBelongToFieldsAndGenericLoginFailureUsesToast()
    {
        using var browser = host.Browser();
        var response = await Post(browser, "/Account/Register", "/Account/Register", new()
        { ["DisplayName"] = "", ["Email"] = "not-an-email", ["Password"] = "short", ["ConfirmPassword"] = "different" });
        var html = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "DisplayName", "Email", "Password", "ConfirmPassword" }) Assert.NotEmpty(FieldError(html, field));
        Assert.DoesNotContain("validation-summary-errors", html);
        response = await Post(browser, "/Account/Register", "/Account/Register", new()
        { ["DisplayName"] = "Test signup", ["Email"] = Guid.NewGuid() + "@test.invalid", ["Password"] = "lowercaseonly", ["ConfirmPassword"] = "lowercaseonly" });
        Assert.NotEmpty(FieldError(await response.Content.ReadAsStringAsync(), "Password"));
        response = await Post(browser, "/Account/Login", "/Account/Login", new()
        { ["Email"] = "missing@test.invalid", ["Password"] = "wrong" });
        html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-toast=\"error\"", html); Assert.Contains("Unable to sign in", html);
        Assert.DoesNotContain("class=\"field-validation-error\"", html);
    }

    [Fact] public async Task LogValidationStaysInlineAndSaveToastIsConsumedOnce()
    {
        var actor = await host.CreateActor(Roles.Client);
        using var browser = await host.Login(actor);
        var response = await Post(browser, "/Logs/Edit", "/Logs/Edit?kind=Measurement", new()
        { ["Kind"] = "Measurement", ["Description"] = "Keep my description", ["LocalTime"] = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-2)).ToString("yyyy-MM-ddTHH:mm") });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter at least one health measurement", FieldError(html, "WeightKg"));
        Assert.Contains("value=\"Keep my description\"", html);
        response = await Post(browser, "/Logs/Edit", "/Logs/Edit?kind=Workout", new()
        { ["Kind"] = "Workout", ["Description"] = "Morning walk", ["DurationMinutes"] = "20", ["Routine"] = "0", ["LocalTime"] = TestHost.Local(DateTimeOffset.UtcNow.AddMinutes(-2)).ToString("yyyy-MM-ddTHH:mm") });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        html = await browser.GetStringAsync(response.Headers.Location);
        Assert.Contains("data-toast=\"success\"", html); Assert.Contains("Your entry has been saved", html);
        Assert.DoesNotContain("data-toast=\"success\"", await browser.GetStringAsync(response.Headers.Location));
    }

    [Fact] public async Task InvalidScheduleRendersDetailsWithFieldMessagesAndEnteredValues()
    {
        var (client, trainer) = await host.Pair();
        var saved = await host.Use((s, _) => s.SavePlan(trainer, new PlanInput { ClientId = client.Id, Title = "Feedback plan", Kind = PlanKind.Workout }));
        Assert.True(saved.Success);
        var plan = await host.Use((s, _) => s.Plan(trainer, saved.Id!.Value));
        using var browser = await host.Login(trainer);
        var response = await Post(browser, "/Plans/AddItem", "/Plans/Details/" + plan.Id, new()
        { ["PlanId"] = plan.Id.ToString(), ["Version"] = TestHost.Version(plan), ["Description"] = "Preserve this exercise", ["Day"] = "2", ["LocalTime"] = "09:30", ["Sets"] = "101", ["DurationMinutes"] = "20" });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.NotEmpty(FieldError(html, "Sets"));
        Assert.Contains("value=\"Preserve this exercise\"", html); Assert.Contains("value=\"09:30\"", html);
        Assert.Contains("data-attempted-value=\"2\"", html);
        Assert.Empty((await host.Use((s, _) => s.Plan(trainer, plan.Id))).Items);
    }

    [Fact] public async Task DirectoryErrorsStayInSubmittedEditorAndEncodeAttemptedText()
    {
        var saved = await host.Use((s, _) => s.SaveProfessional(host.Admin, new ProfessionalInput { DisplayName = "Original name", Specialty = "Physiotherapy" }));
        Assert.True(saved.Success);
        var person = (await host.Use((s, _) => s.Professionals(host.Admin))).Single(x => x.DisplayName == "Original name");
        using var browser = await host.Login(host.Admin);
        var response = await Post(browser, "/Admin/Professional", "/Admin/Professionals", new()
        { ["Id"] = person.Id.ToString(), ["Version"] = TestHost.Version(person), ["DisplayName"] = "<script>alert(1)</script>", ["Specialty"] = "", ["Organization"] = "Keep organization", ["IsActive"] = "false" });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(FieldError(html, "Specialty"));
        Assert.Single(Regex.Matches(html, "class=\"field-validation-error\""));
        Assert.Contains("data-invalid-form=\"true\"", html);
        Assert.DoesNotMatch("<input(?=[^>]*type=\"checkbox\")(?=[^>]*name=\"IsActive\")[^>]*checked", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html); Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Equal("Original name", (await host.Use((s, _) => s.Professionals(host.Admin))).Single(x => x.Id == person.Id).DisplayName);
    }

    [Fact] public async Task BookingTimeFailureReturnsToCorrectFormWithoutChangingBooking()
    {
        var (client, trainer) = await host.Pair();
        var saved = await host.Use((s, _) => s.RequestBooking(client, new BookingInput { Provider = "trainer:" + trainer.Id, StartsLocal = TestHost.Local(DateTimeOffset.UtcNow.AddDays(3)), Purpose = "Progress check" }));
        Assert.True(saved.Success);
        var booking = (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single(x => x.Appointment.Id == saved.Id).Appointment;
        using var browser = await host.Login(client);
        var response = await Post(browser, "/Appointments/Change", "/Appointments", new()
        { ["Id"] = booking.Id.ToString(), ["Version"] = TestHost.Version(booking), ["Status"] = "Requested", ["editTime"] = "true", ["RescheduleLocal"] = TestHost.Local(DateTimeOffset.UtcNow.AddDays(-1)).ToString("yyyy-MM-ddTHH:mm") });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.NotEmpty(FieldError(html, "RescheduleLocal"));
        Assert.Single(Regex.Matches(html, "class=\"field-validation-error\""));
        Assert.Contains("data-invalid-form=\"true\"", html);
        Assert.Equal(booking.StartsAtUtc, (await host.Use((s, _) => s.Bookings(client, 1))).Items.Single(x => x.Appointment.Id == saved.Id).Appointment.StartsAtUtc);
    }
}

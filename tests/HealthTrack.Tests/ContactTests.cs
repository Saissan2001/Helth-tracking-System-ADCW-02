using System.Net;
using HealthTrack.Application;
using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HealthTrack.Tests;

// Separate host keeps unrelated login traffic out of this feature's rate-limit window.
[CollectionDefinition("ContactDatabase", DisableParallelization = true)]
public class ContactDatabaseCollection : ICollectionFixture<TestHost>;

[Collection("ContactDatabase")]
public class ContactTests(TestHost host)
{
    private static ContactInput Valid() => new() { Name = "Contact visitor", Email = "visitor@example.invalid", Subject = "Account question", Message = "Please help me update my account details." };

    [Fact] public async Task AnonymousFormPersistsEncryptedMessageAndAdminCanReadItSafely()
    {
        using var visitor = host.Browser();
        var token = await TestHost.Token(visitor, "/Home/Contact");
        var subject = "Contact " + Guid.NewGuid().ToString("N");
        var response = await visitor.PostAsync("/Home/Contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Visitor <script>alert(1)</script>", ["Email"] = "visitor@example.invalid", ["Subject"] = subject,
            ["Message"] = "Question <script>alert(2)</script> about signing in." + new string('你', 3900), ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("#contact-form", response.Headers.Location!.ToString());
        var all = await host.Use((s, _) => s.ContactInbox(host.Admin, null, 1));
        var saved = Assert.Single(all.Items, m => m.Subject == subject);
        Assert.Equal("visitor@example.invalid", saved.Email);
        var raw = await host.Use((_, db) => db.Database.SqlQueryRaw<string>("SELECT [Message] AS [Value] FROM [ContactMessages] WHERE [Id]={0}", saved.Id).SingleAsync());
        Assert.StartsWith(SensitiveData.Prefix, raw); Assert.DoesNotContain("signing in", raw);
        Assert.Contains("Your message has been saved", await visitor.GetStringAsync("/Home/Contact"));
        await visitor.GetAsync("/Home/Contact");
        Assert.Equal(all.Total, (await host.Use((s, _) => s.ContactInbox(host.Admin, null, 1))).Total);
        using var admin = await host.Login(host.Admin);
        var html = await admin.GetStringAsync("/ContactInbox/Details/" + saved.Id);
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("Reply by email", html);
        Assert.Contains((await host.Use((s, _) => s.Inbox(host.Admin, 1))).Items, n => n.RelatedId == saved.Id);
    }

    [Fact] public async Task InvalidAndMissingCsrfSubmissionsDoNotCreateRecords()
    {
        var before = await host.Use((_, db) => db.ContactMessages.CountAsync());
        using var visitor = host.Browser();
        Assert.Equal(HttpStatusCode.BadRequest, (await visitor.PostAsync("/Home/Contact", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var token = await TestHost.Token(visitor, "/Home/Contact");
        var response = await visitor.PostAsync("/Home/Contact", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["Name"] = "Visitor", ["Email"] = "invalid-email", ["Subject"] = "Question", ["Message"] = "Too short", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invalidHtml = await response.Content.ReadAsStringAsync();
        Assert.Contains("field-validation-error", invalidHtml);
        Assert.Contains("data-valmsg-for=\"Email\"", invalidHtml);
        Assert.DoesNotContain("validation-summary-errors", invalidHtml);
        var input = Valid(); input.Message = new string('x', 4001);
        Assert.False((await host.Use((s, _) => s.SubmitContact(input))).Success);
        input = Valid(); input.Subject = "  a  ";
        Assert.False((await host.Use((s, _) => s.SubmitContact(input))).Success);
        Assert.Equal(before, await host.Use((_, db) => db.ContactMessages.CountAsync()));
    }

    [Fact] public async Task InboxIsAdminOnlyAndHandlingRejectsStaleUpdates()
    {
        var saved = await host.Use((s, _) => s.SubmitContact(Valid())); Assert.True(saved.Success, saved.Error);
        var id = saved.Id!.Value;
        foreach (var role in new[] { Roles.Client, Roles.Trainer })
        {
            var actor = await host.CreateActor(role);
            await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.ContactInbox(actor, null, 1)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.ContactDetail(actor, id)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => host.Use((s, _) => s.HandleContact(actor, id, "", true)));
            using var browser = await host.Login(actor);
            Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/ContactInbox/Details/" + id)).StatusCode);
        }
        using var anonymous = host.Browser(); Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/ContactInbox")).StatusCode);
        var original = await host.Use((s, _) => s.ContactDetail(host.Admin, id));
        using var admin = await host.Login(host.Admin);
        var token = await TestHost.Token(admin, "/ContactInbox/Details/" + id);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/ContactInbox/Handle", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["id"] = id.ToString(), ["version"] = TestHost.Version(original), ["handled"] = "true", ["__RequestVerificationToken"] = token }))).StatusCode);
        var updated = await host.Use((s, _) => s.ContactDetail(host.Admin, id)); Assert.NotNull(updated.HandledAtUtc);
        Assert.Contains((await host.Use((s, _) => s.ContactInbox(host.Admin, true, 1))).Items, m => m.Id == id);
        Assert.False((await host.Use((s, _) => s.HandleContact(host.Admin, id, TestHost.Version(original), false))).Success);
        Assert.True((await host.Use((s, _) => s.HandleContact(host.Admin, id, TestHost.Version(updated), false))).Success);
        Assert.Null((await host.Use((s, _) => s.ContactDetail(host.Admin, id))).HandledAtUtc);
    }

    [Fact] public async Task PublicFormRateLimitRejectsSixthPost()
    {
        var isolated = new TestHost();
        await isolated.InitializeAsync();
        try
        {
            using var visitor = isolated.Browser(); var token = await TestHost.Token(visitor, "/Home/Contact");
            var form = new Dictionary<string,string> { ["__RequestVerificationToken"] = token };
            for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await visitor.PostAsync("/Home/Contact", new FormUrlEncodedContent(form))).StatusCode);
            var rejected = await visitor.PostAsync("/Home/Contact", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Contains("Too many requests", await rejected.Content.ReadAsStringAsync());
            Assert.NotNull(rejected.Headers.RetryAfter);
        }
        finally { await isolated.DisposeAsync(); }
    }
}

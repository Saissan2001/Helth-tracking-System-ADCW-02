using System.Net;
using System.Text.RegularExpressions;
using HealthTrack.Application;
using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using HealthTrack.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HealthTrack.Tests;
[CollectionDefinition("Database", DisableParallelization = true)]
public class DatabaseCollection : ICollectionFixture<TestHost>;
public class TestHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DatabaseName { get; } = "HealthTrack_Test_" + Guid.NewGuid().ToString("N");
    public string Password { get; } = "Ht!9" + Guid.NewGuid().ToString("N") + "aA";
    public Actor Admin { get; private set; } = null!;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Server=(localdb)\\HealthTrack;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True");
        builder.ConfigureServices(services =>
        {
            var worker = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(ReminderWorker));
            if (worker != null) services.Remove(worker);
        });
    }
    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HealthTrackDbContext>();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<SeedData>().Initialize(false);
        Admin = await CreateActor(Roles.Administrator);
    }
    public new async Task DisposeAsync()
    {
        using (var scope = Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<HealthTrackDbContext>().Database.EnsureDeletedAsync();
        await base.DisposeAsync();
    }
    public async Task<Actor> CreateActor(string role)
    {
        using var scope = Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = role.ToLowerInvariant() + Guid.NewGuid().ToString("N") + "@test.invalid";
        var user = new ApplicationUser { UserName = email, Email = email };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<HealthTrackDbContext>(); db.Profiles.Add(new() { UserId = user.Id, DisplayName = "Test " + role, Role = role }); await db.SaveChangesAsync();
        return new(user.Id, role);
    }
    public async Task<T> Use<T>(Func<IHealthService, HealthTrackDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<IHealthService>(), scope.ServiceProvider.GetRequiredService<HealthTrackDbContext>());
    }
    public async Task<(Actor Client, Actor Trainer)> Pair()
    {
        var client = await CreateActor(Roles.Client); var trainer = await CreateActor(Roles.Trainer);
        Assert.True((await Use((s, _) => s.Assign(Admin, client.Id, trainer.Id))).Success); return (client, trainer);
    }
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    public async Task<HttpClient> Login(Actor actor)
    {
        var email = await Use(async (_, db) => await db.Users.Where(x => x.Id == actor.Id).Select(x => x.Email).SingleAsync());
        var client = Browser(); var token = await Token(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = email!, ["Password"] = Password, ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/Dashboard", response.Headers.Location!.ToString()); return client;
    }
    public static async Task<string> Token(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token missing on " + path); return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    public static DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, "Asia/Colombo").DateTime;
    public static string Version(Entity e) => Convert.ToBase64String(e.RowVersion);
}

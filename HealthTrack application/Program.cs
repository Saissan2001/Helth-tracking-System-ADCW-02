using System.Security.Claims;
using System.Threading.RateLimiting;
using HealthTrack.Application;
using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using HealthTrack.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var keyDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".local", "data-protection-keys"));
var protection = builder.Services.AddDataProtection().SetApplicationName("HealthTrack")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
else throw new PlatformNotSupportedException("This LocalDB installation requires Windows with DPAPI-protected encryption keys.");
builder.Services.AddSingleton<SensitiveData>();
var connection = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection.");
builder.Services.AddDbContext<HealthTrackDbContext>(o => o.UseSqlServer(connection));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
{
    o.User.RequireUniqueEmail = true; o.Password.RequiredLength = 10;
    o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<HealthTrackDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login"; o.AccessDeniedPath = "/Home/Denied";
    o.Cookie.Name = "HealthTrack.Auth"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Events.OnValidatePrincipal = async context =>
    {
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id == null) return;
        var db = context.HttpContext.RequestServices.GetRequiredService<HealthTrackDbContext>();
        if (!await db.Profiles.AnyAsync(x => x.UserId == id && x.IsActive))
        { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme); }
    };
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
// Only explicit [Required] fields are mandatory; optional text fields and blank Version values on new records must not fail validation.
builder.Services.AddControllersWithViews(o => { o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()); o.Filters.Add<DomainExceptionFilter>(); o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true; });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("contact", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
    o.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("Too many requests. Please wait a few minutes before trying again.", cancellationToken);
    };
    o.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton<IClock, HealthTrack.Application.SystemClock>();
builder.Services.AddScoped<IHealthService, HealthService>();
builder.Services.AddScoped<SeedData>();
builder.Services.AddScoped<RealisticDemoSeeder>();
builder.Services.AddHostedService<ReminderWorker>();
var app = builder.Build();
if (args.Contains("--migrate") || args.Contains("--seed") || args.Contains("--seed-realistic"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<HealthTrackDbContext>();
    if (args.Contains("--migrate")) { await db.Database.MigrateAsync(); await scope.ServiceProvider.GetRequiredService<SensitiveData>().UpgradeExisting(db); }
    await scope.ServiceProvider.GetRequiredService<SeedData>().Initialize(args.Contains("--seed"));
    if (args.Contains("--seed-realistic"))
    {
        var result = await scope.ServiceProvider.GetRequiredService<RealisticDemoSeeder>().Run();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    }
    return;
}
app.UseExceptionHandler("/Home/Error");
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (!context.Request.Path.StartsWithSegments("/lib") && !context.Request.Path.StartsWithSegments("/css") && !context.Request.Path.StartsWithSegments("/js")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseStaticFiles(); app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
public partial class Program;


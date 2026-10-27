using HealthTrack.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace HealthTrack.Infrastructure;
public class SeedData(HealthTrackDbContext db, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles, IConfiguration config, IHostEnvironment environment)
{
    public async Task Initialize(bool demo)
    {
        foreach (var role in new[] { Roles.Client, Roles.Trainer, Roles.Administrator })
            if (!await roles.RoleExistsAsync(role)) Check(await roles.CreateAsync(new IdentityRole(role)));
        if (!demo) return;
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Demo seed is Development-only.");
        var password = config["Seed:Password"];
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Set Seed__Password or run scripts/setup.ps1. No built-in password is provided.");
        var admin = await User("admin@healthtrack.demo", "Alex Morgan", Roles.Administrator, password);
        var trainer = await User("trainer@healthtrack.demo", "Jordan Ellis", Roles.Trainer, password);
        var client = await User("client@healthtrack.demo", "Sam Taylor", Roles.Client, password);
        await User("client2@healthtrack.demo", "Riley Chen", Roles.Client, password);
        if (await db.Logs.AnyAsync(x => x.ClientId == client)) return;
        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        if (!await db.Assignments.AnyAsync(x => x.ClientId == client && x.EndedAtUtc == null)) db.Assignments.Add(new() { ClientId = client, TrainerId = trainer, AssignedBy = admin });
        for (var day = 0; day < 30; day++)
        {
            var at = now.AddDays(-day).AddHours(-1);
            db.Logs.Add(new() { ClientId = client, Kind = LogKind.Measurement, OccurredAtUtc = at, Description = "Morning check-in", WeightKg = 71.2m + day * .08m, HeightCm = 175, Steps = 6000 + day * 90, HeartRate = 66 });
            if (day % 2 == 0) db.Logs.Add(new() { ClientId = client, Kind = LogKind.Workout, OccurredAtUtc = at, Description = day % 4 == 0 ? "Strength & mobility" : "Brisk walk", DurationMinutes = 35, Calories = 180 + day });
            db.Logs.Add(new() { ClientId = client, Kind = LogKind.Meal, OccurredAtUtc = at, Description = "Lunch · rice, lentils and vegetables", Calories = 520, Protein = 22, Carbs = 76, Fat = 14 });
        }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "Asia/Colombo").DateTime);
        var workout = new HealthPlan { ClientId = client, TrainerId = trainer, Kind = PlanKind.Workout, Title = "Build a steady rhythm", Instructions = "Move at a comfortable pace. Take breaks and record how each session feels.", StartDate = today.AddDays(-7), EndDate = today.AddDays(28), Status = PlanStatus.Published };
        var diet = new HealthPlan { ClientId = client, TrainerId = trainer, Kind = PlanKind.Diet, Title = "Everyday balanced meals", Instructions = "A flexible weekly meal schedule. Log what you actually eat separately.", StartDate = today.AddDays(-7), EndDate = today.AddDays(28), Status = PlanStatus.Published };
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            workout.Items.Add(new() { Day = day, LocalTime = new(7, 0), Description = "Brisk walk and gentle mobility", DurationMinutes = 30 });
            diet.Items.Add(new() { Day = day, LocalTime = new(8, 0), MealType = "Breakfast", Description = "Oats, yogurt and fruit", Calories = 380, Protein = 18, Carbs = 56, Fat = 10 });
            diet.Items.Add(new() { Day = day, LocalTime = new(12, 30), MealType = "Lunch", Description = "Rice, lentils and vegetables", Calories = 520, Protein = 22, Carbs = 76, Fat = 14 });
        }
        db.Plans.AddRange(workout, diet);
        db.Feedback.Add(new() { ClientId = client, TrainerId = trainer, Comment = "You have built a consistent logging habit. Keep adding notes about the activities you enjoy." });
        db.Professionals.Add(new() { DisplayName = "Dr. Avery Lee (fictional)", Specialty = "Wellness consultation", Organization = "Demo Wellness Centre" });
        db.Appointments.Add(new() { ClientId = client, TrainerId = trainer, StartsAtUtc = now.AddDays(2), EndsAtUtc = now.AddDays(2).AddMinutes(30), Purpose = "Monthly progress check-in", Status = AppointmentStatus.Requested });
        db.Notifications.Add(new() { RecipientId = client, Kind = NotificationKind.Activity, Title = "Welcome to HealthTrack. Your demo plans are ready.", ScheduledAtUtc = now, DeliveredAtUtc = now, DeduplicationKey = "demo-welcome-" + client });
        db.Audit.Add(new() { ActorId = admin, Action = "Fictional demo data seeded", ResourceId = "demo" });
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }
    private async Task<string> User(string email, string name, string role, string password)
    {
        var existing = await users.FindByEmailAsync(email); if (existing != null) return existing.Id;
        await using var tx = await db.Database.BeginTransactionAsync();
        var user = new ApplicationUser { UserName = email, Email = email };
        Check(await users.CreateAsync(user, password)); Check(await users.AddToRoleAsync(user, role));
        db.Profiles.Add(new() { UserId = user.Id, DisplayName = name, Role = role });
        await db.SaveChangesAsync(); await tx.CommitAsync(); return user.Id;
    }
    private static void Check(IdentityResult result) { if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description))); }
}

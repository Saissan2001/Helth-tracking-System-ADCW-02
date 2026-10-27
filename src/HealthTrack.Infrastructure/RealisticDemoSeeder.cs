using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace HealthTrack.Infrastructure;
public record DemoSeedResult(bool AlreadyApplied, int Clients, int Logs, int ReplacedLegacyLogs, DateOnly? From, DateOnly? To);

public sealed class RealisticDemoSeeder(HealthTrackDbContext db, IHostEnvironment environment, IClock clock)
{
    public async Task<DemoSeedResult> Run()
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Synthetic history seeding is Development-only.");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r=sp_getapplock @Resource='HealthTrack.Write',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Database is busy.',1;");
        if (await db.Audit.AnyAsync(x => x.ResourceId == DemoHistoryFactory.Version))
            return new(true, 0, 0, 0, null, null);
        var emails = new[] { "admin@healthtrack.demo", "trainer@healthtrack.demo", "client@healthtrack.demo", "client2@healthtrack.demo" };
        var accounts = await db.Users.Where(x => emails.Contains(x.Email!)).ToListAsync();
        if (accounts.Count != 4) throw new InvalidOperationException("Run the normal demo account setup first. Only the four exact @healthtrack.demo accounts are eligible.");
        var profiles = await db.Profiles.Where(x => accounts.Select(u => u.Id).Contains(x.UserId)).ToListAsync();
        var ids = emails.Select(e => accounts.Single(x => x.Email == e).Id).ToArray();
        var expectedRoles = new[] { Roles.Administrator, Roles.Trainer, Roles.Client, Roles.Client };
        for (var i = 0; i < ids.Length; i++)
            if (!profiles.Any(x => x.UserId == ids[i] && x.Role == expectedRoles[i] && x.IsActive)
                || !await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == ids[i] && r.Name == expectedRoles[i] select ur).AnyAsync())
                throw new InvalidOperationException("Demo account roles/status differ from the expected fixture. No data was changed.");
        var originalSeed = await db.Audit.AnyAsync(x => x.ResourceId == "demo" && x.Action == "Fictional demo data seeded");
        var now = clock.UtcNow; var total = 0; var removed = 0; DateOnly? from = null; DateOnly? to = null;
        for (var persona = 0; persona < 2; persona++)
        {
            var client = ids[persona + 2]; var profile = profiles.Single(x => x.UserId == client);
            var data = DemoHistoryFactory.Build(client, ids[1], persona, now, profile.TimeZoneId);
            from = data.Start; to = data.End;
            var existing = await db.Logs.Where(x => x.ClientId == client).ToListAsync();
            if (originalSeed && persona == 0)
            {
                var originalTimes = existing.Where(l => l.Kind == LogKind.Measurement && IsOriginalSeedLog(l)).Select(l => l.OccurredAtUtc).ToHashSet();
                var legacy = existing.Where(l => originalTimes.Contains(l.OccurredAtUtc) && IsOriginalSeedLog(l)).ToList();
                db.Logs.RemoveRange(legacy); removed += legacy.Count;
                existing = existing.Except(legacy).ToList();
                var oldPlans = await db.Plans.Where(x => x.ClientId == client && x.TrainerId == ids[1] && x.Status == PlanStatus.Published).ToListAsync();
                foreach (var plan in oldPlans.Where(p => p.Title == "Build a steady rhythm" && p.Instructions == "Move at a comfortable pace. Take breaks and record how each session feels." || p.Title == "Everyday balanced meals" && p.Instructions == "A flexible weekly meal schedule. Log what you actually eat separately."))
                {
                    plan.Status = PlanStatus.Archived;
                    await db.Notifications.Where(x => x.RelatedId == plan.Id && x.DeliveredAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true));
                }
            }
            var assignment = await db.Assignments.SingleOrDefaultAsync(x => x.ClientId == client && x.EndedAtUtc == null);
            if (assignment == null) db.Assignments.Add(new() { Id = DemoHistoryFactory.Key(client + ":assignment"), ClientId = client, TrainerId = ids[1], AssignedBy = ids[0], AssignedAtUtc = now.AddDays(-DemoHistoryFactory.Days - 3) });
            else if (assignment.TrainerId != ids[1]) throw new InvalidOperationException("A demo client has a different trainer assignment. No changes were committed.");
            else if (originalSeed && assignment.AssignedAtUtc > now.AddDays(-DemoHistoryFactory.Days)) assignment.AssignedAtUtc = now.AddDays(-DemoHistoryFactory.Days - 3);
            var memberSince = now.AddDays(-DemoHistoryFactory.Days - 7);
            if (profile.CreatedAtUtc > memberSince) profile.CreatedAtUtc = memberSince;
            if (string.IsNullOrWhiteSpace(profile.Bio)) profile.Bio = persona == 0
                ? "Fictional demo persona: desk-based work, strength sessions after work and weekend walks. Prefers meals prepared at home; travel sometimes interrupts logging."
                : "Fictional demo persona: a flexible work schedule, easy runs, weekend cycling and yoga. Focuses on a sustainable routine and varied meals.";
            db.Plans.AddRange(data.Plans);
            // Preserve user-entered sessions: skip a generated sleep interval if it would overlap one.
            var logs = data.Logs.Where(l => l.Kind != LogKind.Sleep || !existing.Any(e => e.Kind == LogKind.Sleep && e.SleepStartedAtUtc < l.OccurredAtUtc && e.OccurredAtUtc > l.SleepStartedAtUtc)).ToList();
            db.Logs.AddRange(logs); total += logs.Count;
            if (!await db.WellnessPreferences.AnyAsync(x => x.UserId == client)) db.WellnessPreferences.Add(data.Preferences);
            db.Feedback.AddRange(data.Feedback);
            var bookings = await db.Appointments.Where(x => (x.TrainerId == ids[1] || x.ClientId == client) && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.Declined).ToListAsync();
            foreach (var visit in data.Appointments)
                if (!bookings.Any(x => HealthMath.Overlaps(x.StartsAtUtc, x.EndsAtUtc, visit.StartsAtUtc, visit.EndsAtUtc))) { db.Appointments.Add(visit); bookings.Add(visit); }
            // A small, useful inbox; don't generate months of stale reminder messages.
            foreach (var feedback in data.Feedback.TakeLast(2)) db.Notifications.Add(new() { Id = DemoHistoryFactory.Key(client + ":notice:" + feedback.Id), RecipientId = client, Kind = NotificationKind.Activity, RelatedId = feedback.Id, Title = "Jordan shared feedback after your check-in.", DeduplicationKey = DemoHistoryFactory.Version + ":" + feedback.Id, ScheduledAtUtc = feedback.CreatedAtUtc, DeliveredAtUtc = feedback.CreatedAtUtc, ReadAtUtc = feedback.CreatedAtUtc.AddHours(2) });
        }
        foreach (var staff in profiles.Where(x => x.Role != Roles.Client))
            if (staff.CreatedAtUtc > now.AddDays(-180)) staff.CreatedAtUtc = now.AddDays(-180);
        db.Audit.Add(new() { Id = DemoHistoryFactory.Key("audit"), ActorId = ids[0], Action = "150-day synthetic demo histories seeded", ResourceId = DemoHistoryFactory.Version, OccurredAtUtc = now });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return new(false, 2, total, removed, from, to);
    }
    // Match the original fixed fixture, not arbitrary records sharing a title. Edited/link/favorite records survive.
    private static bool IsOriginalSeedLog(HealthLog l)
    {
        if (l.PlanItemId != null || l.IsFavorite || !string.IsNullOrEmpty(l.Notes)) return false;
        if (l.Kind == LogKind.Measurement && l.Description == "Morning check-in" && l.HeightCm == 175 && l.HeartRate == 66 && l.WeightKg.HasValue && l.Steps.HasValue)
        {
            var offset = (l.WeightKg.Value - 71.2m) / .08m;
            return offset >= 0 && offset < 30 && offset == decimal.Truncate(offset) && l.Steps == 6000 + offset * 90;
        }
        return l.Kind == LogKind.Meal && l.Description == "Lunch · rice, lentils and vegetables" && l.Calories == 520 && l.Protein == 22 && l.Carbs == 76 && l.Fat == 14
            || l.Kind == LogKind.Workout && (l.Description == "Strength & mobility" || l.Description == "Brisk walk") && l.DurationMinutes == 35 && l.Calories >= 180 && l.Calories <= 209 && l.Routine == WorkoutRoutine.Other;
    }
}

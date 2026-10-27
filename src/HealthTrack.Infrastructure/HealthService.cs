using System.ComponentModel.DataAnnotations;
using System.Data;
using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;

// Authorization lives here as well as in MVC. Every caller supplies an authenticated, server-derived Actor.
public partial class HealthService(HealthTrackDbContext db, IClock clock, UserManager<ApplicationUser> users) : IHealthService
{
    private async Task<MemberProfile> Require(Actor actor, params string[] roles)
    {
        var p = await db.Profiles.SingleOrDefaultAsync(x => x.UserId == actor.Id && x.IsActive);
        if (p == null || p.Role != actor.Role || roles.Length > 0 && !roles.Contains(p.Role)) throw new AccessDeniedException();
        return p;
    }
    private async Task<string> ClientScope(Actor actor, string? clientId)
    {
        await Require(actor, Roles.Client, Roles.Trainer);
        if (actor.Role == Roles.Client)
        {
            if (clientId != null && clientId != actor.Id) throw new AccessDeniedException();
            return actor.Id;
        }
        if (clientId == null || !await db.Assignments.AnyAsync(x => x.TrainerId == actor.Id && x.ClientId == clientId && x.EndedAtUtc == null)
            || !await db.Profiles.AnyAsync(x => x.UserId == clientId && x.IsActive)) throw new AccessDeniedException();
        return clientId;
    }
    private static string? Validate(object input)
    {
        var errors = new List<ValidationResult>();
        return Validator.TryValidateObject(input, new ValidationContext(input), errors, true) ? null : string.Join(" ", errors.Select(x => x.ErrorMessage));
    }
    private static void Version(Entity entity, string version)
    {
        if (string.IsNullOrEmpty(version) || Convert.ToBase64String(entity.RowVersion) != version)
            throw new DbUpdateConcurrencyException();
    }
    private void Audit(Actor actor, string action, object id) => db.Audit.Add(new() { ActorId = actor.Id, Action = action, ResourceId = id.ToString()!, OccurredAtUtc = clock.UtcNow });
    // One database-owned transaction lock serializes short writes for this small coursework system.
    // Unlike an in-process semaphore, it also works across web instances and reminder workers.
    private async Task Lock(CancellationToken ct = default) => await db.Database.ExecuteSqlRawAsync(
        "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'HealthTrack.Write', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; IF @r < 0 THROW 51000, 'Schedule is busy. Please retry.', 1;", ct);
    private async Task<Outcome> Write(Actor actor, Func<Task<Outcome>> action)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await Lock();
            await Require(actor);
            var outcome = await action();
            if (!outcome.Success) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return outcome; }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return outcome;
        }
        catch (AccessDeniedException) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); throw; }
        catch (MissingRecordException) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); throw; }
        catch (DbUpdateConcurrencyException) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return Outcome.Fail("This record changed. Refresh the page and try again."); }
        catch (ArgumentException e) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return Outcome.Fail(e.Message); }
        catch (SqlException e) when (e.Number is 1205 or 51000) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return Outcome.Fail("The schedule is busy. Please try again."); }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 }) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return Outcome.Fail("This record already exists. Refresh and try again."); }
    }
    private static DateTimeOffset ToUtc(DateTime local, string zone)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified) || tz.IsAmbiguousTime(unspecified)) throw new ArgumentException("This local time is invalid or ambiguous due to daylight saving. Choose another time.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, tz));
    }
    private static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly from, DateOnly to, string zone)
    {
        if (to < from || to.DayNumber - from.DayNumber > 366) throw new ArgumentException("Choose an ordered date range of at most one year.");
        return (ToUtc(from.ToDateTime(TimeOnly.MinValue), zone), ToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
    }
    private static async Task<Page<T>> Paged<T>(IQueryable<T> query, int page)
    {
        page = Math.Clamp(page, 1, 100000);
        return new(await query.Skip((page - 1) * 20).Take(20).ToListAsync(), await query.CountAsync(), page);
    }
    public Task<MemberProfile> Profile(Actor actor) => Require(actor);
    public Task<Outcome> UpdateProfile(Actor actor, ProfileInput input) => Write(actor, async () =>
    {
        if (Validate(input) is { } error) return Outcome.Fail(error);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(input.TimeZoneId, out _)) return Outcome.Fail("Enter a recognized time zone, such as Asia/Colombo or Europe/London.", "TimeZoneId");
        var p = await Require(actor);
        p.DisplayName = input.DisplayName.Trim(); p.Bio = input.Bio ?? ""; p.TimeZoneId = input.TimeZoneId;
        p.WorkoutReminders = input.WorkoutReminders; p.MealReminders = input.MealReminders;
        p.AppointmentReminders = input.AppointmentReminders; p.ReminderMinutes = input.ReminderMinutes;
        await db.Notifications.Where(x => x.RecipientId == actor.Id && x.DeliveredAtUtc == null && x.Kind != NotificationKind.Activity).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true));
        Audit(actor, "Profile updated", p.Id);
        return Outcome.Ok();
    });
    public async Task<Page<MemberProfile>> Members(Actor actor, string? role, string? search, int page)
    {
        await Require(actor, Roles.Administrator, Roles.Trainer);
        var q = db.Profiles.AsNoTracking();
        if (actor.Role == Roles.Trainer) q = q.Where(p => p.IsActive && db.Assignments.Any(a => a.ClientId == p.UserId && a.TrainerId == actor.Id && a.EndedAtUtc == null));
        if (!string.IsNullOrWhiteSpace(role)) q = q.Where(x => x.Role == role);
        if (!string.IsNullOrWhiteSpace(search)) { search = search.Trim(); q = q.Where(x => x.DisplayName.Contains(search)); }
        return await Paged(q.OrderBy(x => x.DisplayName), page);
    }
    public async Task<Page<HealthLog>> Logs(Actor actor, string? clientId, LogKind kind, DateOnly from, DateOnly to, int page)
    {
        var id = await ClientScope(actor, clientId);
        var p = await Require(actor); var (start, end) = Range(from, to, p.TimeZoneId);
        return await Paged(db.Logs.AsNoTracking().Where(x => x.ClientId == id && x.Kind == kind && x.OccurredAtUtc >= start && x.OccurredAtUtc < end).OrderByDescending(x => x.OccurredAtUtc), page);
    }
    public async Task<HealthLog> Log(Actor actor, Guid id)
    {
        await Require(actor, Roles.Client);
        return await db.Logs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ClientId == actor.Id) ?? throw new MissingRecordException();
    }
    public Task<Outcome> SaveLog(Actor actor, LogInput input) => Write(actor, async () =>
    {
        var p = await Require(actor, Roles.Client);
        if (Validate(input) is { } error) return Outcome.Fail(error);
        DateTimeOffset occurred;
        try { occurred = ToUtc(input.LocalTime, p.TimeZoneId); }
        catch (ArgumentException timeError) { return Outcome.Fail(timeError.Message, nameof(input.LocalTime)); }
        if (occurred > clock.UtcNow.AddMinutes(5) || occurred < clock.UtcNow.AddYears(-10)) return Outcome.Fail("Log a time within the past ten years, not in the future.", "LocalTime");
        var log = input.Id.HasValue ? await db.Logs.SingleOrDefaultAsync(x => x.Id == input.Id && x.ClientId == actor.Id) ?? throw new MissingRecordException() : new HealthLog { ClientId = actor.Id };
        if (input.Id.HasValue) { Version(log, input.Version); if (log.Kind != input.Kind) return Outcome.Fail("The log type cannot be changed."); }
        else db.Logs.Add(log);
        if (input.Id.HasValue && (log.PlanItemId != input.PlanItemId || log.ScheduledDate != input.ScheduledDate))
            return Outcome.Fail("The linked plan occurrence cannot be changed. Delete this entry and record a new one if needed.");
        if (input.PlanItemId.HasValue)
        {
            var item = await db.PlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.PlanItemId) ?? throw new MissingRecordException();
            var plan = await db.Plans.AsNoTracking().SingleAsync(x => x.Id == item.PlanId);
            if (plan.ClientId != actor.Id) throw new AccessDeniedException();
            if ((!input.Id.HasValue && plan.Status != PlanStatus.Published) || plan.Status == PlanStatus.Draft)
                return Outcome.Fail("Only published plan occurrences can be recorded.");
            if (input.ScheduledDate is not { } date || date < plan.StartDate || date > plan.EndDate || date.DayOfWeek != item.Day)
                return Outcome.Fail("Choose a scheduled date within this plan.");
            if (DateOnly.FromDateTime(input.LocalTime) != date)
                return Outcome.Fail("Record the actual time on the linked scheduled date.", nameof(input.LocalTime));
            if (input.Kind != (plan.Kind == PlanKind.Workout ? LogKind.Workout : LogKind.Meal))
                return Outcome.Fail("The entry must match the plan type.");
            if (await db.Logs.AnyAsync(x => x.PlanItemId == item.Id && x.ScheduledDate == date && x.Id != log.Id))
                return Outcome.Fail("This scheduled item already has an entry. Edit the existing entry instead.");
            log.PlanItemId = item.Id; log.ScheduledDate = date;
        }
        else if (input.ScheduledDate.HasValue) return Outcome.Fail("Choose a plan item for this scheduled date.");
        log.Kind = input.Kind; log.Description = input.Description.Trim(); log.Notes = input.Notes ?? ""; log.OccurredAtUtc = occurred;
        if (log.Kind == LogKind.Measurement)
        { log.WeightKg = input.WeightKg; log.HeightCm = input.HeightCm; log.HeartRate = input.HeartRate; log.Systolic = input.Systolic; log.Diastolic = input.Diastolic; log.Steps = input.Steps; }
        if (log.Kind == LogKind.Workout) { log.DurationMinutes = input.DurationMinutes; log.Calories = input.Calories; log.Routine = input.Routine; }
        if (log.Kind == LogKind.Meal) { log.Calories = input.Calories; log.Protein = input.Protein; log.Carbs = input.Carbs; log.Fat = input.Fat; }
        if (log.Kind == LogKind.Hydration) log.WaterMl = input.WaterMl;
        if (log.Kind == LogKind.Sleep)
        {
            DateTimeOffset sleepStart;
            try { sleepStart = ToUtc(input.SleepStartedLocal!.Value, p.TimeZoneId); }
            catch (ArgumentException timeError) { return Outcome.Fail(timeError.Message, nameof(input.SleepStartedLocal)); }
            var duration = (occurred - sleepStart).TotalMinutes;
            if (duration < 1 || duration > 1440) return Outcome.Fail("Sleep end must be after the start and within 24 hours.", nameof(input.LocalTime));
            if (await db.Logs.AnyAsync(x => x.ClientId == actor.Id && x.Kind == LogKind.Sleep && x.Id != log.Id && x.SleepStartedAtUtc < occurred && x.OccurredAtUtc > sleepStart))
                return Outcome.Fail("This sleep session overlaps an existing entry. Edit that session instead.", nameof(input.SleepStartedLocal));
            log.SleepStartedAtUtc = sleepStart; log.DurationMinutes = (int)duration;
        }
        Audit(actor, input.Id.HasValue ? "Log corrected" : "Log recorded", log.Id);
        return Outcome.Ok(log.Id);
    });
    public Task<Outcome> DeleteLog(Actor actor, Guid id, string version) => Write(actor, async () =>
    {
        await Require(actor, Roles.Client);
        var log = await db.Logs.SingleOrDefaultAsync(x => x.Id == id && x.ClientId == actor.Id) ?? throw new MissingRecordException();
        Version(log, version); db.Logs.Remove(log); Audit(actor, "Log deleted", id); return Outcome.Ok();
    });
    public async Task<ProgressReport> Progress(Actor actor, string? clientId, DateOnly from, DateOnly to)
    {
        var id = await ClientScope(actor, clientId); var p = await Require(actor); var (start, end) = Range(from, to, p.TimeZoneId);
        // Scope and date filtering happen in SQL before encrypted values are materialized.
        // Arithmetic is deliberately in memory: SQL cannot aggregate randomized ciphertext.
        var rows = await db.Logs.AsNoTracking().Where(x => x.ClientId == id && x.OccurredAtUtc >= start && x.OccurredAtUtc < end)
            .OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).ToListAsync();
        var historicalHeight = await db.Logs.AsNoTracking().Where(x => x.ClientId == id && x.Kind == LogKind.Measurement && x.HeightCm != null && x.OccurredAtUtc < start)
            .OrderByDescending(x => x.OccurredAtUtc).Select(x => x.HeightCm).FirstOrDefaultAsync();
        var readings = new List<WeightPoint>();
        foreach (var row in rows.Where(x => x.Kind == LogKind.Measurement))
        {
            if (row.HeightCm.HasValue) historicalHeight = row.HeightCm;
            if (row.WeightKg.HasValue) readings.Add(new(row.OccurredAtUtc, row.WeightKg.Value, HealthMath.Bmi(row.WeightKg, historicalHeight)));
        }
        var workouts = rows.Where(x => x.Kind == LogKind.Workout).ToList();
        var meals = rows.Where(x => x.Kind == LogKind.Meal).ToList();
        var daily = rows.Where(x => x.Kind == LogKind.Workout || x.Kind == LogKind.Meal).GroupBy(x => x.OccurredAtUtc.UtcDateTime.Date)
            .Select(g => new DayTotal(DateOnly.FromDateTime(g.Key), g.Count(x => x.Kind == LogKind.Workout),
                g.Where(x => x.Kind == LogKind.Workout).Sum(x => x.Calories ?? 0), g.Where(x => x.Kind == LogKind.Meal).Sum(x => x.Calories ?? 0),
                g.Where(x => x.Kind == LogKind.Workout).Sum(x => x.DurationMinutes ?? 0))).OrderBy(x => x.Day).ToList();
        return new()
        {
            ClientId = id, Name = await db.Profiles.Where(x => x.UserId == id).Select(x => x.DisplayName).SingleAsync(), From = from, To = to,
            WorkoutCount = workouts.Count, WorkoutMinutes = workouts.Sum(x => x.DurationMinutes ?? 0),
            Burned = workouts.Any(x => x.Calories != null) ? workouts.Sum(x => x.Calories) : null,
            Eaten = meals.Any(x => x.Calories != null) ? meals.Sum(x => x.Calories) : null,
            Weight = readings.LastOrDefault()?.Weight, Bmi = readings.LastOrDefault()?.Bmi,
            WeightPoints = readings.TakeLast(366).ToList(), Days = daily,
            Weeks = daily.GroupBy(x => x.Day.AddDays(-(((int)x.Day.DayOfWeek + 6) % 7))).OrderBy(g => g.Key)
                .Select(g => new WeekTotal(g.Key, g.Sum(x => x.Workouts), g.Sum(x => x.Minutes))).ToList()
        };
    }
    public async Task<Page<HealthPlan>> Plans(Actor actor, string? clientId, PlanKind? kind, int page)
    {
        await Require(actor, Roles.Client, Roles.Trainer);
        var q = db.Plans.AsNoTracking().Include(x => x.Items).AsQueryable();
        if (actor.Role == Roles.Client) { await ClientScope(actor, clientId); q = q.Where(x => x.ClientId == actor.Id && x.Status != PlanStatus.Draft); }
        else
        {
            q = q.Where(x => x.TrainerId == actor.Id && db.Assignments.Any(a => a.ClientId == x.ClientId && a.TrainerId == actor.Id && a.EndedAtUtc == null));
            if (clientId != null) { await ClientScope(actor, clientId); q = q.Where(x => x.ClientId == clientId); }
        }
        if (kind.HasValue) q = q.Where(x => x.Kind == kind);
        return await Paged(q.OrderByDescending(x => x.StartDate), page);
    }
    public async Task<HealthPlan> Plan(Actor actor, Guid id)
    {
        await Require(actor, Roles.Client, Roles.Trainer);
        var plan = await db.Plans.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id) ?? throw new MissingRecordException();
        await ClientScope(actor, plan.ClientId);
        if (actor.Role == Roles.Client && plan.Status == PlanStatus.Draft || actor.Role == Roles.Trainer && plan.TrainerId != actor.Id) throw new AccessDeniedException();
        return plan;
    }
    public Task<Outcome> SavePlan(Actor actor, PlanInput input) => Write(actor, async () =>
    {
        await Require(actor, Roles.Trainer); await ClientScope(actor, input.ClientId);
        if (Validate(input) is { } error) return Outcome.Fail(error);
        var plan = input.Id.HasValue ? await Plan(actor, input.Id.Value) : new HealthPlan { ClientId = input.ClientId, TrainerId = actor.Id };
        if (input.Id.HasValue) { Version(plan, input.Version); if (plan.Status != PlanStatus.Draft || plan.ClientId != input.ClientId || plan.Kind != input.Kind) return Outcome.Fail("Only draft plans can be edited; their client and type stay fixed."); }
        else db.Plans.Add(plan);
        plan.Kind = input.Kind; plan.Title = input.Title.Trim(); plan.Instructions = input.Instructions ?? ""; plan.StartDate = input.StartDate; plan.EndDate = input.EndDate;
        Audit(actor, "Plan draft saved", plan.Id); return Outcome.Ok(plan.Id);
    });
    public Task<Outcome> AddItem(Actor actor, ItemInput input) => Write(actor, async () =>
    {
        await Require(actor, Roles.Trainer);
        if (Validate(input) is { } error) return Outcome.Fail(error);
        var plan = await Plan(actor, input.PlanId); Version(plan, input.Version);
        if (plan.Status != PlanStatus.Draft) return Outcome.Fail("Only a draft plan can have its schedule changed.");
        if (plan.Items.Count >= 50) return Outcome.Fail("A plan can contain up to 50 scheduled items.");
        if (plan.Kind == PlanKind.Workout && input.DurationMinutes == null && (input.Sets == null || input.Repetitions == null)) return Outcome.Fail("Enter a duration or both sets and repetitions.", "DurationMinutes");
        var item = new PlanItem { PlanId = plan.Id, Routine = plan.Kind == PlanKind.Workout ? input.Routine : WorkoutRoutine.Other, Day = input.Day, LocalTime = input.LocalTime, Description = input.Description.Trim(), MealType = plan.Kind == PlanKind.Diet ? input.MealType ?? "Meal" : "", Sets = input.Sets, Repetitions = input.Repetitions, DurationMinutes = input.DurationMinutes, Calories = input.Calories, Protein = input.Protein, Carbs = input.Carbs, Fat = input.Fat };
        db.PlanItems.Add(item); // Explicit Added state: the new item already has a generated Guid key.
        plan.Items.Add(item);
        db.Entry(plan).Property(x => x.Title).IsModified = true; // Bump the aggregate rowversion for a child edit.
        Audit(actor, "Plan item added", plan.Id); return Outcome.Ok(plan.Id);
    });
    public Task<Outcome> RemoveItem(Actor actor, Guid id, string version) => Write(actor, async () =>
    {
        await Require(actor, Roles.Trainer);
        var item = await db.PlanItems.FindAsync(id) ?? throw new MissingRecordException();
        var plan = await Plan(actor, item.PlanId); Version(plan, version);
        if (plan.Status != PlanStatus.Draft) return Outcome.Fail("Only draft items can be removed.");
        db.PlanItems.Remove(item); db.Entry(plan).Property(x => x.Title).IsModified = true;
        Audit(actor, "Plan item removed", plan.Id); return Outcome.Ok(plan.Id);
    });
    public Task<Outcome> ChangePlan(Actor actor, Guid id, string version, PlanStatus status) => Write(actor, async () =>
    {
        await Require(actor, Roles.Trainer); var plan = await Plan(actor, id); Version(plan, version); plan.Transition(status);
        if (status == PlanStatus.Archived) await db.Notifications.Where(x => x.RelatedId != null && plan.Items.Select(i => i.Id).Contains(x.RelatedId.Value) && x.DeliveredAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true));
        Notify(plan.ClientId, "Your " + plan.Kind.ToString().ToLowerInvariant() + " plan is " + status.ToString().ToLowerInvariant(), plan.Id);
        Audit(actor, "Plan " + status, id); return Outcome.Ok(id);
    });
    public Task<Outcome> Feedback(Actor actor, string clientId, string comment) => Write(actor, async () =>
    {
        await Require(actor, Roles.Trainer); await ClientScope(actor, clientId);
        if (string.IsNullOrWhiteSpace(comment) || comment.Length > 2000) return Outcome.Fail("Feedback must contain between 1 and 2,000 characters.", "comment");
        var feedback = new TrainerFeedback { ClientId = clientId, TrainerId = actor.Id, Comment = comment.Trim(), CreatedAtUtc = clock.UtcNow };
        db.Feedback.Add(feedback); Notify(clientId, "Your trainer shared new feedback", feedback.Id); Audit(actor, "Feedback added", feedback.Id); return Outcome.Ok();
    });
    private void Notify(string recipient, string title, Guid related) => db.Notifications.Add(new() { RecipientId = recipient, Title = title, Kind = NotificationKind.Activity, RelatedId = related, ScheduledAtUtc = clock.UtcNow, DeliveredAtUtc = clock.UtcNow, DeduplicationKey = Guid.NewGuid().ToString("N") });
}

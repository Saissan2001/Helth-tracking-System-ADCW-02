using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;
public partial class HealthService
{
    public async Task<WellnessPreferences> WellnessSettings(Actor actor)
    {
        await Require(actor, Roles.Client);
        return await db.WellnessPreferences.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == actor.Id) ?? new() { UserId = actor.Id };
    }
    public Task<Outcome> SaveWellnessSettings(Actor actor, WellnessSettingsInput input) => Write(actor, async () =>
    {
        await Require(actor, Roles.Client);
        if (Validate(input) is { } error) return Outcome.Fail(error);
        var settings = await db.WellnessPreferences.SingleOrDefaultAsync(x => x.UserId == actor.Id);
        if (settings == null)
        {
            if (!string.IsNullOrEmpty(input.Version)) return Outcome.Fail("Settings changed. Refresh and try again.");
            settings = new() { UserId = actor.Id }; db.WellnessPreferences.Add(settings);
        }
        else Version(settings, input.Version);
        settings.Focus = input.Focus; settings.WeeklyWorkoutMinutes = input.WeeklyWorkoutMinutes;
        settings.DailyWaterMl = input.DailyWaterMl; settings.NightlySleepMinutes = input.NightlySleepMinutes; settings.DailySteps = input.DailySteps;
        settings.ShowWater = input.ShowWater; settings.ShowSleep = input.ShowSleep; settings.ShowNutrition = input.ShowNutrition; settings.ShowSteps = input.ShowSteps;
        settings.SetupCompletedAtUtc ??= clock.UtcNow;
        Audit(actor, "Wellness preferences saved", settings.Id); return Outcome.Ok();
    });
    public async Task<Page<HealthLog>> Favorites(Actor actor, int page)
    {
        await Require(actor, Roles.Client);
        return await Paged(db.Logs.AsNoTracking().Where(x => x.ClientId == actor.Id && x.IsFavorite && (x.Kind == LogKind.Workout || x.Kind == LogKind.Meal))
            .OrderByDescending(x => x.OccurredAtUtc).ThenBy(x => x.Id), page);
    }
    public Task<Outcome> SetFavorite(Actor actor, Guid id, string version, bool favorite) => Write(actor, async () =>
    {
        await Require(actor, Roles.Client);
        var log = await db.Logs.SingleOrDefaultAsync(x => x.Id == id && x.ClientId == actor.Id) ?? throw new MissingRecordException();
        Version(log, version);
        if (log.Kind is not (LogKind.Workout or LogKind.Meal)) return Outcome.Fail("Save workouts or meals for reuse.");
        if (favorite && !log.IsFavorite && await db.Logs.CountAsync(x => x.ClientId == actor.Id && x.IsFavorite) >= 100)
            return Outcome.Fail("Your saved library holds up to 100 entries. Remove an older saved item first.");
        log.IsFavorite = favorite; Audit(actor, favorite ? "Entry saved to library" : "Entry removed from library", log.Id); return Outcome.Ok();
    });
    public async Task<LogInput> RepeatLog(Actor actor, Guid id)
    {
        var profile = await Require(actor, Roles.Client);
        var log = await Log(actor, id);
        if (log.Kind is not (LogKind.Workout or LogKind.Meal)) throw new ArgumentException("Only workouts and meals can be reused. Record a fresh measurement, sleep session or water amount.");
        return new() { Kind = log.Kind, Description = log.Description, Notes = log.Notes, Routine = log.Routine,
            DurationMinutes = log.DurationMinutes, Calories = log.Calories, Protein = log.Protein, Carbs = log.Carbs, Fat = log.Fat,
            LocalTime = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, profile.TimeZoneId).DateTime };
    }
    public async Task<DailyOverview> Daily(Actor actor, DateOnly? date = null, int page = 1)
    {
        var profile = await Require(actor, Roles.Client);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
        var selected = date ?? today;
        if (selected > today || selected < today.AddYears(-10)) throw new ArgumentException("Choose today or a date within the past ten years.");
        var (start, end) = Range(selected.AddDays(-13), selected, profile.TimeZoneId);
        var scoped = db.Logs.AsNoTracking().Where(x => x.ClientId == actor.Id && x.OccurredAtUtc >= start && x.OccurredAtUtc < end);
        // Only amounts/timestamps needed for the overview are decrypted; descriptions/notes stay in the paged diary query.
        var rows = await scoped.OrderBy(x => x.OccurredAtUtc).Select(x => new HealthLog { Id = x.Id, Kind = x.Kind, OccurredAtUtc = x.OccurredAtUtc,
            DurationMinutes = x.DurationMinutes, WaterMl = x.WaterMl, Steps = x.Steps, Calories = x.Calories, Protein = x.Protein,
            Carbs = x.Carbs, Fat = x.Fat, PlanItemId = x.PlanItemId, ScheduledDate = x.ScheduledDate }).Take(10001).ToListAsync();
        if (rows.Count > 10000) throw new ArgumentException("This two-week overview exceeds 10,000 entries. Use the paged log lists to review this data.");
        var grouped = rows.ToLookup(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.OccurredAtUtc, zone).DateTime));
        static decimal? Known(IEnumerable<decimal?> values) { var known = values.Where(x => x.HasValue).ToList(); return known.Count == 0 ? null : known.Sum(); }
        WellnessDay Totals(DateOnly day)
        {
            var entries = grouped[day].ToList(); var meals = entries.Where(x => x.Kind == LogKind.Meal).ToList();
            var water = entries.Where(x => x.Kind == LogKind.Hydration).ToList(); var sleep = entries.Where(x => x.Kind == LogKind.Sleep).ToList();
            var steps = entries.Where(x => x.Kind == LogKind.Measurement && x.Steps.HasValue).OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).FirstOrDefault()?.Steps;
            return new(day, entries.Count, entries.Where(x => x.Kind == LogKind.Workout).Sum(x => x.DurationMinutes ?? 0), entries.Count(x => x.Kind == LogKind.Workout),
                water.Count == 0 ? null : water.Sum(x => x.WaterMl ?? 0), sleep.Count == 0 ? null : sleep.Sum(x => x.DurationMinutes ?? 0), steps,
                Known(meals.Select(x => x.Calories)), Known(meals.Select(x => x.Protein)), Known(meals.Select(x => x.Carbs)), Known(meals.Select(x => x.Fat)), meals.Count(x => !x.Calories.HasValue));
        }
        var week = selected.AddDays(-(((int)selected.DayOfWeek + 6) % 7));
        var allDays = Enumerable.Range(0, 14).Select(i => Totals(selected.AddDays(i - 13))).ToList();
        var selectedStart = ToUtc(selected.ToDateTime(TimeOnly.MinValue), profile.TimeZoneId);
        var diary = scoped.Where(x => x.OccurredAtUtc >= selectedStart);
        var count = await diary.CountAsync(); page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / 20d)));
        var plans = db.Plans.AsNoTracking().Where(x => x.ClientId == actor.Id && x.Status == PlanStatus.Published && x.StartDate <= selected && x.EndDate >= selected);
        var scheduledQuery = from item in db.PlanItems.AsNoTracking() join plan in plans on item.PlanId equals plan.Id
                             where item.Day == selected.DayOfWeek
                             select new { plan.Id, plan.Title, plan.Kind, ItemId = item.Id, item.Description, item.LocalTime };
        var scheduledCount = await scheduledQuery.CountAsync();
        var scheduled = await scheduledQuery.OrderBy(x => x.LocalTime).ThenBy(x => x.ItemId).Take(12).ToListAsync();
        var completions = rows.Where(x => x.PlanItemId.HasValue && x.ScheduledDate == selected).ToDictionary(x => x.PlanItemId!.Value, x => x.Id);
        return new() { Profile = profile, Preferences = await WellnessSettings(actor), Date = selected, Today = today, WeekStart = week,
            WeekWorkoutMinutes = allDays.Where(x => x.Date >= week).Sum(x => x.WorkoutMinutes), Totals = Totals(selected), Days = allDays.Skip(7).ToList(),
            PreviousSevenMinutes = allDays.Take(7).Sum(x => x.WorkoutMinutes), PreviousSevenRecordedDays = allDays.Take(7).Count(x => x.Entries > 0),
            Entries = await Paged(diary.OrderByDescending(x => x.OccurredAtUtc).ThenBy(x => x.Id), page),
            Favorites = await db.Logs.AsNoTracking().Where(x => x.ClientId == actor.Id && x.IsFavorite && (x.Kind == LogKind.Workout || x.Kind == LogKind.Meal)).OrderByDescending(x => x.OccurredAtUtc).Take(4).ToListAsync(),
            ScheduledCount = scheduledCount, Schedule = scheduled.Select(x => new TodayPlanItem(x.Id, x.ItemId, x.Title, x.Description, x.LocalTime, x.Kind,
                completions.TryGetValue(x.ItemId, out var logId) ? logId : null)).ToList() };
    }
}

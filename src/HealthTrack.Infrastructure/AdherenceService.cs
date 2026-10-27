using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;
public partial class HealthService
{
    public async Task<LogInput> PlannedLog(Actor actor, Guid itemId, DateOnly date)
    {
        var profile = await Require(actor, Roles.Client);
        var item = await db.PlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId) ?? throw new MissingRecordException();
        var plan = await Plan(actor, item.PlanId);
        var localNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, profile.TimeZoneId).DateTime;
        var today = DateOnly.FromDateTime(localNow);
        if (plan.Status != PlanStatus.Published) throw new ArgumentException("Only published plans can receive new completion entries.");
        if (date < plan.StartDate || date > plan.EndDate || date > today || date.DayOfWeek != item.Day)
            throw new ArgumentException("Choose a scheduled date on or before today within this plan.");
        if (await db.Logs.AnyAsync(x => x.PlanItemId == itemId && x.ScheduledDate == date))
            throw new ArgumentException("This occurrence already has a log. Open its existing entry from plan progress.");
        // Actual amounts stay blank: a prescription is not evidence that it was performed or eaten.
        return new() { PlanItemId = itemId, ScheduledDate = date, Kind = plan.Kind == PlanKind.Workout ? LogKind.Workout : LogKind.Meal,
            Description = item.Description[..Math.Min(150, item.Description.Length)], Routine = item.Routine,
            LocalTime = date.ToDateTime(item.LocalTime) <= localNow ? date.ToDateTime(item.LocalTime) : localNow };
    }

    public async Task<AdherenceReport> Adherence(Actor actor, Guid planId, DateOnly from, DateOnly to)
    {
        var plan = await Plan(actor, planId);
        if (to < from || to.DayNumber - from.DayNumber > 30) throw new ArgumentException("Choose up to 31 days for plan progress.");
        var zone = await db.Profiles.Where(x => x.UserId == plan.ClientId).Select(x => x.TimeZoneId).SingleAsync();
        var localNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, zone).DateTime;
        var items = plan.Items.Select(x => x.Id).ToList();
        var logs = await db.Logs.AsNoTracking().Where(x => x.ClientId == plan.ClientId && x.PlanItemId != null && items.Contains(x.PlanItemId.Value)
            && x.ScheduledDate >= from && x.ScheduledDate <= to).Select(x => new { x.PlanItemId, x.ScheduledDate, x.Id }).ToListAsync();
        var linked = logs.ToDictionary(x => (x.PlanItemId!.Value, x.ScheduledDate!.Value), x => x.Id);
        var occurrences = new List<PlanOccurrence>();
        for (var offset = 0; offset <= to.DayNumber - from.DayNumber; offset++)
        {
            var date = from.AddDays(offset);
            if (date < plan.StartDate || date > plan.EndDate) continue;
            foreach (var item in plan.Items.Where(x => x.Day == date.DayOfWeek).OrderBy(x => x.LocalTime).ThenBy(x => x.Id))
                occurrences.Add(new(item.Id, date, item.LocalTime, item.Description,
                    linked.TryGetValue((item.Id, date), out var logId) ? logId : null, date.ToDateTime(item.LocalTime) <= localNow));
        }
        return new(plan, from, to, zone, occurrences);
    }
}

using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;
public partial class HealthService
{
    public async Task<EngagementReport> Engagement(Actor actor, DateOnly from, DateOnly to, int page = 1)
    {
        var viewer = await Require(actor, Roles.Trainer, Roles.Administrator);
        var admin = actor.Role == Roles.Administrator;
        var (start, end) = Range(from, to, viewer.TimeZoneId);
        var profiles = db.Profiles.AsNoTracking().Where(p => p.IsActive && p.Role == Roles.Client);
        if (!admin) profiles = profiles.Where(p => db.Assignments.Any(a => a.ClientId == p.UserId && a.TrainerId == actor.Id && a.EndedAtUtc == null));
        var ids = profiles.Select(p => p.UserId);
        var logs = db.Logs.AsNoTracking().Where(l => ids.Contains(l.ClientId) && l.OccurredAtUtc >= start && l.OccurredAtUtc < end);
        var eligible = await profiles.CountAsync();
        var engaged = await logs.Select(l => l.ClientId).Distinct().CountAsync();
        // Suppress complementary small groups too: eligible minus engaged must not reveal a group of 1–4.
        var suppressed = admin && (engaged < 5 || eligible - engaged is > 0 and < 5);
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(eligible / 20d)));
        if (suppressed) return new(from, to, eligible, 0, [], []) { Suppressed = true, PageNumber = page };
        var sessions = await logs.CountAsync(l => l.Kind == LogKind.Workout);
        var clients = new List<ClientPerformance>();
        if (!admin)
        {
            var members = await profiles.OrderBy(p => p.DisplayName).ThenBy(p => p.UserId).Skip((page - 1) * 20).Take(20)
                .Select(p => new { p.UserId, p.DisplayName }).ToListAsync();
            var pageIds = members.Select(p => p.UserId).ToList();
            var pageLogs = logs.Where(l => pageIds.Contains(l.ClientId));
            var counts = await pageLogs.GroupBy(l => new { l.ClientId, l.Kind }).Select(g => new { g.Key.ClientId, g.Key.Kind, Count = g.Count() }).ToListAsync();
            var activeDays = await pageLogs.Select(l => new { l.ClientId, Day = l.OccurredAtUtc.Date }).Distinct()
                .GroupBy(l => l.ClientId).Select(g => new { ClientId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.ClientId, x => x.Count);
            var workoutRows = pageLogs.Where(l => l.Kind == LogKind.Workout);
            // Encrypted durations cannot be summed in SQL. Bound decryption and memory explicitly.
            if (await workoutRows.Take(20001).CountAsync() > 20000)
                throw new ArgumentException("This page exceeds 20,000 workout entries. Choose a shorter reporting date range.");
            var minutes = new Dictionary<string, int>();
            await foreach (var row in workoutRows.Select(l => new { l.ClientId, l.DurationMinutes }).AsAsyncEnumerable())
                minutes[row.ClientId] = minutes.GetValueOrDefault(row.ClientId) + (row.DurationMinutes ?? 0);
            foreach (var member in members)
            {
                int Count(LogKind kind) => counts.Where(x => x.ClientId == member.UserId && x.Kind == kind).Select(x => x.Count).FirstOrDefault();
                clients.Add(new(member.UserId, member.DisplayName, Count(LogKind.Workout), minutes.GetValueOrDefault(member.UserId), Count(LogKind.Meal), Count(LogKind.Measurement), activeDays.GetValueOrDefault(member.UserId)));
            }
        }
        var plans = db.Plans.Where(p => ids.Contains(p.ClientId) && p.Kind == PlanKind.Workout && p.Status == PlanStatus.Published && p.StartDate <= to && p.EndDate >= from);
        if (!admin) plans = plans.Where(p => p.TrainerId == actor.Id);
        var prescribed = await (from item in db.PlanItems join plan in plans on item.PlanId equals plan.Id select new { item.Routine, plan.ClientId })
            .GroupBy(x => x.Routine).Select(g => new { Routine = g.Key, Count = g.Count(), Participants = g.Select(x => x.ClientId).Distinct().Count() }).ToListAsync();
        var performed = await logs.Where(l => l.Kind == LogKind.Workout).GroupBy(l => l.Routine)
            .Select(g => new { Routine = g.Key, Count = g.Count(), Participants = g.Select(x => x.ClientId).Distinct().Count() }).ToListAsync();
        // Suppress the whole breakdown if one category is small, so subtracting visible categories
        // from the session total cannot recover a withheld category's count.
        var hideBreakdown = admin && (performed.Any(x => x.Participants < 5) || prescribed.Any(x => x.Participants < 5));
        var routines = new List<RoutinePopularity>();
        foreach (var routine in Enum.GetValues<WorkoutRoutine>())
        {
            var actual = performed.SingleOrDefault(x => x.Routine == routine);
            var scheduled = prescribed.SingleOrDefault(x => x.Routine == routine);
            // Admin sees only categories where both nonempty populations meet the threshold.
            if (hideBreakdown) continue;
            if (actual != null || scheduled != null) routines.Add(new(routine, actual?.Count ?? 0, actual?.Participants ?? 0, scheduled?.Count ?? 0));
        }
        return new(from, to, eligible, engaged, clients, routines.OrderByDescending(r => r.LoggedSessions).ThenBy(r => r.Routine).ToList())
            { PageNumber = page, WorkoutSessions = sessions };
    }
}

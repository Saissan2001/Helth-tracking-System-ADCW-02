using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;

public partial class HealthService
{
    public async Task<DashboardData> Dashboard(Actor actor)
    {
        var p = await Require(actor); var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId)).DateTime);
        var data = new DashboardData
        {
            Profile = p,
            Unread = await db.Notifications.CountAsync(x => x.RecipientId == actor.Id && !x.Invalidated && x.DeliveredAtUtc != null && x.ReadAtUtc == null && x.ScheduledAtUtc <= clock.UtcNow),
            PendingBookings = await ScopedBookings(actor).CountAsync(x => x.Status == AppointmentStatus.Requested),
            Upcoming = await BookingRows(ScopedBookings(actor).AsNoTracking().Where(x => x.StartsAtUtc > clock.UtcNow && (x.Status == AppointmentStatus.Confirmed || x.Status == AppointmentStatus.Requested)).OrderBy(x => x.StartsAtUtc).Take(4)).ToListAsync()
        };
        if (actor.Role == Roles.Client)
        {
            data.Daily = await Daily(actor);
            data.Progress = await Progress(actor, null, today.AddDays(-29), today);
            data.WeekStarting = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var (sevenStart, sevenEnd) = Range(today.AddDays(-6), today, p.TimeZoneId);
            var weekStart = ToUtc(data.WeekStarting.ToDateTime(TimeOnly.MinValue), p.TimeZoneId);
            var recent = await db.Logs.AsNoTracking().Where(x => x.ClientId == actor.Id && x.Kind == LogKind.Workout && x.OccurredAtUtc >= sevenStart && x.OccurredAtUtc < sevenEnd)
                .Select(x => new { x.OccurredAtUtc, x.DurationMinutes }).ToListAsync();
            data.SevenDayWorkouts = recent.Count; data.SevenDayMinutes = recent.Sum(x => x.DurationMinutes ?? 0);
            data.WeekWorkouts = recent.Count(x => x.OccurredAtUtc >= weekStart);
            data.WeekMinutes = recent.Where(x => x.OccurredAtUtc >= weekStart).Sum(x => x.DurationMinutes ?? 0);
            var latest = await db.Logs.AsNoTracking().Where(x => x.ClientId == actor.Id && x.Kind == LogKind.Measurement && x.WeightKg != null && x.OccurredAtUtc <= clock.UtcNow)
                .OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Select(x => new { x.OccurredAtUtc, x.WeightKg }).FirstOrDefaultAsync();
            if (latest != null)
            {
                var height = await db.Logs.AsNoTracking().Where(x => x.ClientId == actor.Id && x.Kind == LogKind.Measurement && x.HeightCm != null && x.OccurredAtUtc <= latest.OccurredAtUtc)
                    .OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Select(x => x.HeightCm).FirstOrDefaultAsync();
                data.LatestWeight = new(latest.OccurredAtUtc, latest.WeightKg!.Value, HealthMath.Bmi(latest.WeightKg, height));
            }
            data.CurrentPlans = await db.Plans.AsNoTracking().Include(x => x.Items).Where(x => x.ClientId == actor.Id && x.Status == PlanStatus.Published && x.StartDate <= today && x.EndDate >= today).OrderByDescending(x => x.StartDate).Take(4).ToListAsync();
            data.Feedback = await db.Feedback.AsNoTracking().Where(x => x.ClientId == actor.Id).OrderByDescending(x => x.CreatedAtUtc).Take(4).ToListAsync();
        }
        else if (actor.Role == Roles.Trainer)
        {
            data.Clients = await db.Assignments.CountAsync(x => x.TrainerId == actor.Id && x.EndedAtUtc == null);
            data.Plans = await db.Plans.CountAsync(x => x.TrainerId == actor.Id && x.Status == PlanStatus.Published && db.Assignments.Any(a => a.ClientId == x.ClientId && a.TrainerId == actor.Id && a.EndedAtUtc == null));
        }
        else
        {
            data.Clients = await db.Profiles.CountAsync(x => x.Role == Roles.Client && x.IsActive);
            data.Trainers = await db.Profiles.CountAsync(x => x.Role == Roles.Trainer && x.IsActive);
            data.Plans = await db.Plans.CountAsync(x => x.Status == PlanStatus.Published);
            data.Insights = await Insights(p, today.AddDays(-29), today, 1);
        }
        return data;
    }
    public async Task<IReadOnlyList<AssignmentRow>> Assignments(Actor actor)
    {
        await Require(actor, Roles.Administrator);
        return await db.Assignments.AsNoTracking().OrderByDescending(x => x.AssignedAtUtc).Take(200).Select(x => new AssignmentRow(x,
            db.Profiles.Where(p => p.UserId == x.ClientId).Select(p => p.DisplayName).First(), db.Profiles.Where(p => p.UserId == x.TrainerId).Select(p => p.DisplayName).First())).ToListAsync();
    }
    public Task<Outcome> Assign(Actor actor, string clientId, string trainerId) => Write(actor, async () =>
    {
        await Require(actor, Roles.Administrator);
        if (!await db.Profiles.AnyAsync(x => x.UserId == clientId && x.Role == Roles.Client && x.IsActive)) return Outcome.Fail("Choose an active client.", "clientId");
        if (!await db.Profiles.AnyAsync(x => x.UserId == trainerId && x.Role == Roles.Trainer && x.IsActive)) return Outcome.Fail("Choose an active trainer.", "trainerId");
        var old = await db.Assignments.SingleOrDefaultAsync(x => x.ClientId == clientId && x.EndedAtUtc == null);
        if (old?.TrainerId == trainerId) return Outcome.Fail("This trainer is already assigned.", "trainerId");
        if (old != null) { old.EndedAtUtc = clock.UtcNow; await CancelTrainerBookings(old.ClientId, old.TrainerId); await db.SaveChangesAsync(); }
        var assignment = new TrainerAssignment { ClientId = clientId, TrainerId = trainerId, AssignedBy = actor.Id, AssignedAtUtc = clock.UtcNow };
        db.Assignments.Add(assignment); Notify(clientId, "Your trainer assignment has changed", assignment.Id); Notify(trainerId, "A client has been assigned to you", assignment.Id);
        Audit(actor, "Trainer assigned / transferred", assignment.Id); return Outcome.Ok();
    });
    private async Task CancelTrainerBookings(string clientId, string trainerId)
    {
        var bookings = await db.Appointments.Where(x => x.ClientId == clientId && x.TrainerId == trainerId && (x.Status == AppointmentStatus.Requested || x.Status == AppointmentStatus.Confirmed)).ToListAsync();
        foreach (var a in bookings)
        {
            a.Status = AppointmentStatus.Cancelled; a.ResponseNote = "Trainer assignment ended.";
            Notify(a.ClientId, "Consultation cancelled after assignment change", a.Id); Notify(trainerId, "Consultation cancelled after assignment change", a.Id);
            await db.Notifications.Where(x => x.RelatedId == a.Id && x.Kind == NotificationKind.Appointment && x.DeliveredAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true));
        }
    }
    public Task<Outcome> Revoke(Actor actor, Guid id, string version) => Write(actor, async () =>
    {
        await Require(actor, Roles.Administrator); var a = await db.Assignments.FindAsync(id) ?? throw new MissingRecordException(); Version(a, version);
        if (a.EndedAtUtc != null) return Outcome.Fail("This assignment has already ended.");
        a.EndedAtUtc = clock.UtcNow; await CancelTrainerBookings(a.ClientId, a.TrainerId); Audit(actor, "Assignment revoked", a.Id); Notify(a.ClientId, "Your trainer assignment ended", a.Id); return Outcome.Ok();
    });
    public async Task<IReadOnlyList<HealthcareProfessional>> Professionals(Actor actor)
    {
        await Require(actor, Roles.Administrator); return await db.Professionals.AsNoTracking().OrderBy(x => x.DisplayName).Take(200).ToListAsync();
    }
    public Task<Outcome> SaveProfessional(Actor actor, ProfessionalInput input) => Write(actor, async () =>
    {
        await Require(actor, Roles.Administrator); if (Validate(input) is { } error) return Outcome.Fail(error);
        var p = input.Id.HasValue ? await db.Professionals.FindAsync(input.Id.Value) ?? throw new MissingRecordException() : new HealthcareProfessional();
        if (input.Id.HasValue) Version(p, input.Version); else db.Professionals.Add(p);
        p.DisplayName = input.DisplayName.Trim(); p.Specialty = input.Specialty.Trim(); p.Organization = input.Organization ?? ""; p.IsActive = input.IsActive;
        if (!p.IsActive)
        {
            foreach (var a in await db.Appointments.Where(x => x.ProfessionalId == p.Id && (x.Status == AppointmentStatus.Requested || x.Status == AppointmentStatus.Confirmed)).ToListAsync())
            {
                a.Status = AppointmentStatus.Cancelled; Notify(a.ClientId, "Consultation cancelled: provider unavailable", a.Id);
                await db.Notifications.Where(x => x.RelatedId == a.Id && x.DeliveredAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true));
            }
        }
        Audit(actor, "Professional directory updated", p.Id); return Outcome.Ok();
    });
    public Task<Outcome> SetActive(Actor actor, string userId, bool active) => Write(actor, async () =>
    {
        await Require(actor, Roles.Administrator);
        var p = await db.Profiles.SingleOrDefaultAsync(x => x.UserId == userId) ?? throw new MissingRecordException();
        if (p.Role == Roles.Administrator) return Outcome.Fail("Administrator accounts cannot be disabled through this screen.");
        p.IsActive = active;
        var user = await users.FindByIdAsync(userId) ?? throw new MissingRecordException(); await users.UpdateSecurityStampAsync(user);
        if (!active)
        {
            foreach (var assignment in await db.Assignments.Where(x => (x.ClientId == userId || x.TrainerId == userId) && x.EndedAtUtc == null).ToListAsync())
            { assignment.EndedAtUtc = clock.UtcNow; await CancelTrainerBookings(assignment.ClientId, assignment.TrainerId); }
            foreach (var a in await db.Appointments.Where(x => x.ClientId == userId && x.ProfessionalId != null && (x.Status == AppointmentStatus.Requested || x.Status == AppointmentStatus.Confirmed)).ToListAsync()) a.Status = AppointmentStatus.Cancelled;
            await db.Notifications.Where(x => x.RecipientId == userId && x.DeliveredAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true));
        }
        Audit(actor, active ? "Account activated" : "Account disabled", userId); return Outcome.Ok();
    });
    public async Task<AdminReport> SystemReport(Actor actor, DateOnly from, DateOnly to, int page = 1)
    {
        var p = await Require(actor, Roles.Administrator); var (start, end) = Range(from, to, p.TimeZoneId);
        var statuses = await db.Appointments.Where(x => x.StartsAtUtc >= start && x.StartsAtUtc < end).GroupBy(x => x.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count);
        return new(await db.Profiles.CountAsync(x => x.IsActive), await db.Profiles.CountAsync(x => x.Role == Roles.Client), await db.Profiles.CountAsync(x => x.Role == Roles.Trainer), await db.Assignments.CountAsync(x => x.EndedAtUtc == null),
            await db.Logs.CountAsync(x => x.OccurredAtUtc >= start && x.OccurredAtUtc < end), await db.Plans.CountAsync(), statuses,
            await db.Logs.Where(x => x.OccurredAtUtc >= start && x.OccurredAtUtc < end).GroupBy(x => x.OccurredAtUtc.Date).OrderBy(g => g.Key).Select(g => new UsageDay(g.Key, g.Count())).ToListAsync(),
            await db.Audit.AsNoTracking().Where(x => x.OccurredAtUtc >= start && x.OccurredAtUtc < end).OrderByDescending(x => x.OccurredAtUtc).Take(100).ToListAsync()) { Insights = await Insights(p, from, to, page) };
    }
    private async Task<AdminInsights> Insights(MemberProfile viewer, DateOnly from, DateOnly to, int page)
    {
        var (start, end) = Range(from, to, viewer.TimeZoneId);
        var counts = await db.Profiles.AsNoTracking().Where(x => x.CreatedAtUtc >= start && x.CreatedAtUtc < end)
            .GroupBy(x => x.CreatedAtUtc.Date).Select(g => new UsageDay(g.Key, g.Count())).ToListAsync();
        var byDay = counts.ToDictionary(x => x.Day, x => x.Entries);
        var days = Enumerable.Range(0, (end.UtcDateTime.Date - start.UtcDateTime.Date).Days + 1)
            .Select(i => start.UtcDateTime.Date.AddDays(i)).Where(d => d < end.UtcDateTime)
            .Select(d => new UsageDay(d, byDay.GetValueOrDefault(d))).ToList();
        var trainers = db.Profiles.AsNoTracking().Where(x => x.Role == Roles.Trainer && x.IsActive);
        var total = await trainers.CountAsync();
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / 20d)));
        var workloads = await trainers.Select(p => new { p.DisplayName, p.UserId,
            Clients = db.Assignments.Count(a => a.TrainerId == p.UserId && a.EndedAtUtc == null) })
            .OrderByDescending(x => x.Clients).ThenBy(x => x.DisplayName).ThenBy(x => x.UserId)
            .Skip((page - 1) * 20).Take(20).Select(x => new TrainerWorkload(x.DisplayName, x.Clients)).ToListAsync();
        return new(days, new(workloads, total, page));
    }

}

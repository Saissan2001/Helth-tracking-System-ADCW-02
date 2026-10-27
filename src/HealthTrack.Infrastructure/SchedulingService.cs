using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;

public partial class HealthService
{
    public async Task<int> UnreadCount(Actor actor)
    {
        await Require(actor);
        return await db.Notifications.CountAsync(x => x.RecipientId == actor.Id && !x.Invalidated && x.DeliveredAtUtc != null && x.ReadAtUtc == null && x.ScheduledAtUtc <= clock.UtcNow);
    }
    public async Task<IReadOnlyList<(string Id, string Name)>> Providers(Actor actor)
    {
        await Require(actor, Roles.Client);
        var trainers = await db.Profiles.AsNoTracking().Where(p => p.IsActive && p.Role == Roles.Trainer && db.Assignments.Any(a => a.ClientId == actor.Id && a.TrainerId == p.UserId && a.EndedAtUtc == null)).Select(p => new { p.UserId, p.DisplayName }).ToListAsync();
        var professionals = await db.Professionals.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DisplayName).Take(200).ToListAsync();
        return trainers.Select(x => ("trainer:" + x.UserId, x.DisplayName + " · Trainer")).Concat(professionals.Select(x => ("professional:" + x.Id, x.DisplayName + " · " + x.Specialty))).ToList();
    }
    private IQueryable<Appointment> ScopedBookings(Actor actor) => actor.Role switch
    {
        Roles.Client => db.Appointments.Where(x => x.ClientId == actor.Id),
        Roles.Trainer => db.Appointments.Where(x => x.TrainerId == actor.Id && db.Assignments.Any(a => a.TrainerId == actor.Id && a.ClientId == x.ClientId && a.EndedAtUtc == null)),
        Roles.Administrator => db.Appointments.Where(x => x.ProfessionalId != null),
        _ => throw new AccessDeniedException()
    };
    private IQueryable<BookingRow> BookingRows(IQueryable<Appointment> q) => q.Select(a => new BookingRow(a,
        db.Profiles.Where(p => p.UserId == a.ClientId).Select(p => p.DisplayName).First(),
        a.TrainerId != null ? db.Profiles.Where(p => p.UserId == a.TrainerId).Select(p => p.DisplayName).First() : db.Professionals.Where(p => p.Id == a.ProfessionalId).Select(p => p.DisplayName).First()));
    public async Task<Page<BookingRow>> Bookings(Actor actor, int page)
    {
        await Require(actor);
        return await Paged(BookingRows(ScopedBookings(actor).AsNoTracking().OrderByDescending(x => x.StartsAtUtc)), page);
    }
    private Task<bool> Conflict(Appointment a) => db.Appointments.AnyAsync(x => x.Id != a.Id && (x.Status == AppointmentStatus.Requested || x.Status == AppointmentStatus.Confirmed)
        && (x.ClientId == a.ClientId || a.TrainerId != null && x.TrainerId == a.TrainerId || a.ProfessionalId != null && x.ProfessionalId == a.ProfessionalId)
        && x.StartsAtUtc < a.EndsAtUtc && a.StartsAtUtc < x.EndsAtUtc);
    public Task<Outcome> RequestBooking(Actor actor, BookingInput input) => Write(actor, async () =>
    {
        var p = await Require(actor, Roles.Client);
        if (Validate(input) is { } error) return Outcome.Fail(error);
        var providers = await Providers(actor);
        if (!providers.Any(x => x.Id == input.Provider)) return Outcome.Fail("Choose an active assigned trainer or directory professional.", "Provider");
        DateTimeOffset start;
        try { start = ToUtc(input.StartsLocal, p.TimeZoneId); }
        catch (ArgumentException timeError) { return Outcome.Fail(timeError.Message, nameof(input.StartsLocal)); }
        if (start <= clock.UtcNow || start > clock.UtcNow.AddDays(180)) return Outcome.Fail("Choose a future start within the next 180 days.", "StartsLocal");
        var a = new Appointment { ClientId = actor.Id, StartsAtUtc = start, EndsAtUtc = start.AddMinutes(input.DurationMinutes), Purpose = input.Purpose.Trim() };
        if (input.Provider.StartsWith("trainer:")) a.TrainerId = input.Provider[8..]; else a.ProfessionalId = Guid.Parse(input.Provider[13..]);
        if (await Conflict(a)) return Outcome.Fail("This time overlaps an existing request or confirmed booking for you or the provider.", "StartsLocal");
        db.Appointments.Add(a); Notify(actor.Id, "Appointment requested", a.Id);
        if (a.TrainerId != null) Notify(a.TrainerId, "New consultation request", a.Id);
        else foreach (var admin in await db.Profiles.Where(x => x.Role == Roles.Administrator && x.IsActive).Select(x => x.UserId).ToListAsync()) Notify(admin, "New professional consultation request", a.Id);
        Audit(actor, "Appointment requested", a.Id); return Outcome.Ok(a.Id);
    });
    public Task<Outcome> ChangeBooking(Actor actor, BookingChange input) => Write(actor, async () =>
    {
        var p = await Require(actor);
        if (Validate(input) is { } error) return Outcome.Fail(error);
        var a = await ScopedBookings(actor).SingleOrDefaultAsync(x => x.Id == input.Id) ?? throw new MissingRecordException();
        Version(a, input.Version);
        if (input.RescheduleLocal.HasValue)
        {
            // Clients request a new slot; the responsible provider/coordinator can directly update it.
            // ScopedBookings plus the transaction lock retain participant and overlap protection.
            if (a.Status is not (AppointmentStatus.Requested or AppointmentStatus.Confirmed)) return Outcome.Fail("Only pending or confirmed appointments can be rescheduled.");
            DateTimeOffset start;
            try { start = ToUtc(input.RescheduleLocal.Value, p.TimeZoneId); }
            catch (ArgumentException timeError) { return Outcome.Fail(timeError.Message, nameof(input.RescheduleLocal)); }
            if (start <= clock.UtcNow || start > clock.UtcNow.AddDays(180)) return Outcome.Fail("Choose a future date within 180 days.", "RescheduleLocal");
            if (!(await Providers(new Actor(a.ClientId, Roles.Client))).Any(x => x.Id == (a.TrainerId != null ? "trainer:" + a.TrainerId : "professional:" + a.ProfessionalId))) return Outcome.Fail("This provider is no longer available.");
            if (actor.Role != Roles.Client && string.IsNullOrWhiteSpace(input.Note)) return Outcome.Fail("Explain the schedule change for the client.", "Note");
            var duration = a.EndsAtUtc - a.StartsAtUtc; a.StartsAtUtc = start; a.EndsAtUtc = start + duration;
            a.Status = actor.Role == Roles.Client ? AppointmentStatus.Requested : AppointmentStatus.Confirmed;
        }
        else a.Transition(input.Status, actor.Role == Roles.Client, clock.UtcNow);
        if (a.Status is AppointmentStatus.Confirmed or AppointmentStatus.Requested && await Conflict(a)) return Outcome.Fail("This time is already reserved. Choose a different time.", "RescheduleLocal");
        a.ResponseNote = input.Note ?? "";
        await db.Notifications.Where(x => x.RelatedId == a.Id && x.Kind == NotificationKind.Appointment && x.DeliveredAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true));
        Notify(a.ClientId, input.RescheduleLocal.HasValue ? "Appointment rescheduled — check the new time" : "Appointment " + a.Status.ToString().ToLowerInvariant(), a.Id);
        if (a.TrainerId != null && a.TrainerId != actor.Id) Notify(a.TrainerId, "Appointment changed", a.Id);
        Audit(actor, input.RescheduleLocal.HasValue ? "Appointment rescheduled" : "Appointment " + a.Status, a.Id);
        return Outcome.Ok(a.Id);
    });
    public async Task<Page<Notification>> Inbox(Actor actor, int page)
    {
        await Require(actor);
        return await Paged(db.Notifications.AsNoTracking().Where(x => x.RecipientId == actor.Id && !x.Invalidated && x.ScheduledAtUtc <= clock.UtcNow && x.DeliveredAtUtc != null).OrderByDescending(x => x.ScheduledAtUtc), page);
    }
    public Task<Outcome> MarkRead(Actor actor, Guid? id) => Write(actor, async () =>
    {
        var q = db.Notifications.Where(x => x.RecipientId == actor.Id && !x.Invalidated && x.DeliveredAtUtc != null && x.ScheduledAtUtc <= clock.UtcNow);
        if (id.HasValue) { q = q.Where(x => x.Id == id); if (!await q.AnyAsync()) throw new MissingRecordException(); }
        await q.Where(x => x.ReadAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAtUtc, clock.UtcNow)); return Outcome.Ok();
    });
    public async Task GenerateReminders(CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear(); // The worker owns this unit of work; bulk reconciliation changes rowversions.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await Lock(cancellationToken);
        var now = clock.UtcNow;
        // Reconcile pending rows before delivery, including preferences and plan/appointment invalidation.
        await db.Notifications.Where(x => x.DeliveredAtUtc == null && x.Kind != NotificationKind.Activity).ExecuteUpdateAsync(s => s.SetProperty(x => x.Invalidated, true), cancellationToken);
        Guid? appointmentAfter = null;
        while (true)
        {
            var query = db.Appointments.AsNoTracking().Where(x => x.Status == AppointmentStatus.Confirmed && x.StartsAtUtc > now && x.StartsAtUtc < now.AddDays(2));
            if (appointmentAfter.HasValue) query = query.Where(x => x.Id.CompareTo(appointmentAfter.Value) > 0);
            var appointments = await query.OrderBy(x => x.Id).Take(100).ToListAsync(cancellationToken);
            if (appointments.Count == 0) break;
            var recipients = appointments.SelectMany(a => new[] { a.ClientId, a.TrainerId }).Where(x => x != null).Distinct().ToList();
            var profiles = await db.Profiles.AsNoTracking().Where(p => recipients.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancellationToken);
            var reminders = new List<Notification>();
            foreach (var a in appointments)
                foreach (var recipient in new[] { a.ClientId, a.TrainerId }.Where(x => x != null))
                    if (profiles.TryGetValue(recipient!, out var p) && p.IsActive && p.AppointmentReminders)
                        reminders.Add(Reminder(p.UserId, NotificationKind.Appointment, a.Id, a.StartsAtUtc, a.StartsAtUtc.AddMinutes(-p.ReminderMinutes), "Upcoming consultation"));
            await UpsertReminders(reminders, cancellationToken);
            appointmentAfter = appointments[^1].Id;
        }
        // Small batches bound worker memory; keyset paging also visits plans beyond the first page.
        Guid? after = null;
        while (true)
        {
            var q = db.Plans.AsNoTracking().Include(x => x.Items).Where(x => x.Status == PlanStatus.Published && x.EndDate >= DateOnly.FromDateTime(now.UtcDateTime.AddDays(-1)) && x.StartDate <= DateOnly.FromDateTime(now.UtcDateTime.AddDays(2)));
            if (after.HasValue) q = q.Where(x => x.Id.CompareTo(after.Value) > 0);
            var plans = await q.OrderBy(x => x.Id).Take(100).ToListAsync(cancellationToken);
            if (plans.Count == 0) break;
            var clientIds = plans.Select(x => x.ClientId).Distinct().ToList();
            var profiles = await db.Profiles.AsNoTracking().Where(x => clientIds.Contains(x.UserId)).ToDictionaryAsync(x => x.UserId, cancellationToken);
            var reminders = new List<Notification>();
            foreach (var plan in plans)
            {
                var p = profiles[plan.ClientId];
                if (!p.IsActive || plan.Kind == PlanKind.Workout && !p.WorkoutReminders || plan.Kind == PlanKind.Diet && !p.MealReminders) continue;
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId)).DateTime);
                foreach (var day in new[] { today, today.AddDays(1) })
                {
                    if (day < plan.StartDate || day > plan.EndDate) continue;
                    foreach (var item in plan.Items.Where(x => x.Day == day.DayOfWeek))
                    {
                        DateTimeOffset due;
                        try { due = ToUtc(day.ToDateTime(item.LocalTime), p.TimeZoneId); } catch (ArgumentException) { continue; }
                        reminders.Add(Reminder(p.UserId, plan.Kind == PlanKind.Workout ? NotificationKind.Workout : NotificationKind.Meal, item.Id, due, due, plan.Kind == PlanKind.Workout ? "Your scheduled workout" : "Your scheduled meal"));
                    }
                }
            }
            await UpsertReminders(reminders, cancellationToken);
            after = plans[^1].Id;
        }
        await db.SaveChangesAsync(cancellationToken);
        await db.Notifications.Where(x => !x.Invalidated && x.DeliveredAtUtc == null && x.ScheduledAtUtc <= now).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveredAtUtc, now), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }
    private static Notification Reminder(string user, NotificationKind kind, Guid related, DateTimeOffset occurrence, DateTimeOffset due, string title) =>
        new() { RecipientId = user, Kind = kind, RelatedId = related, DeduplicationKey = HealthMath.ReminderKey(user, kind, related, occurrence), ScheduledAtUtc = due, Title = title };
    private async Task UpsertReminders(List<Notification> reminders, CancellationToken ct)
    {
        var wanted = reminders.DistinctBy(x => x.DeduplicationKey).ToList();
        if (wanted.Count == 0) return;
        var keys = wanted.Select(x => x.DeduplicationKey).ToList();
        var existing = await db.Notifications.Where(x => keys.Contains(x.DeduplicationKey)).ToDictionaryAsync(x => x.DeduplicationKey, ct);
        foreach (var reminder in wanted)
        {
            if (!existing.TryGetValue(reminder.DeduplicationKey, out var row)) db.Notifications.Add(reminder);
            else if (row.DeliveredAtUtc == null) { row.Invalidated = false; row.ScheduledAtUtc = reminder.ScheduledAtUtc; }
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear(); // Keep tracked rows bounded to one batch within the shared transaction.
    }
}

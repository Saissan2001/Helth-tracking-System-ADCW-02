using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Domain;

public static class Roles
{
    public const string Client = "Client", Trainer = "Trainer", Administrator = "Administrator";
}
public enum PlanKind { Workout, Diet }
public enum PlanStatus { Draft, Published, Archived }
public enum LogKind { Measurement, Workout, Meal, Hydration, Sleep }
public enum WorkoutRoutine { Other, Walking, Running, Cycling, StrengthTraining, Yoga, Mobility, Swimming, IntervalTraining }
public enum AppointmentStatus { Requested, Confirmed, Declined, Cancelled, Completed }
public enum NotificationKind { Activity, Workout, Meal, Appointment }

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}
public class MemberProfile : Entity
{
    [MaxLength(450)] public string UserId { get; set; } = "";
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    [MaxLength(30)] public string Role { get; set; } = Roles.Client;
    public bool IsActive { get; set; } = true;
    [MaxLength(100)] public string TimeZoneId { get; set; } = "Asia/Colombo";
    [MaxLength(500)] public string Bio { get; set; } = "";
    public bool WorkoutReminders { get; set; } = true;
    public bool MealReminders { get; set; } = true;
    public bool AppointmentReminders { get; set; } = true;
    public int ReminderMinutes { get; set; } = 30;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public class TrainerAssignment : Entity
{
    [MaxLength(450)] public string TrainerId { get; set; } = "";
    [MaxLength(450)] public string ClientId { get; set; } = "";
    [MaxLength(450)] public string AssignedBy { get; set; } = "";
    public DateTimeOffset AssignedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
}
public class HealthLog : Entity
{
    public int? WaterMl { get; set; }
    public DateTimeOffset? SleepStartedAtUtc { get; set; }
    public bool IsFavorite { get; set; }
    public Guid? PlanItemId { get; set; }
    public DateOnly? ScheduledDate { get; set; }
    public WorkoutRoutine Routine { get; set; }
    [MaxLength(450)] public string ClientId { get; set; } = "";
    public LogKind Kind { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    [MaxLength(150)] public string Description { get; set; } = "";
    [MaxLength(1000)] public string Notes { get; set; } = "";
    public decimal? WeightKg { get; set; }
    public decimal? HeightCm { get; set; }
    public int? HeartRate { get; set; }
    public int? Systolic { get; set; }
    public int? Diastolic { get; set; }
    public int? Steps { get; set; }
    public int? DurationMinutes { get; set; }
    public decimal? Calories { get; set; }
    public decimal? Protein { get; set; }
    public decimal? Carbs { get; set; }
    public decimal? Fat { get; set; }
}
public class HealthPlan : Entity
{
    [MaxLength(450)] public string ClientId { get; set; } = "";
    [MaxLength(450)] public string TrainerId { get; set; } = "";
    public PlanKind Kind { get; set; }
    [MaxLength(120)] public string Title { get; set; } = "";
    [MaxLength(2000)] public string Instructions { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PlanStatus Status { get; set; }
    public List<PlanItem> Items { get; set; } = [];
    public void Transition(PlanStatus next)
    {
        if (!(Status == PlanStatus.Draft && next == PlanStatus.Published || Status == PlanStatus.Published && next == PlanStatus.Archived))
            throw new ArgumentException("Only draft plans can be published and published plans archived.");
        if (next == PlanStatus.Published && Items.Count == 0) throw new ArgumentException("Add at least one scheduled item before publishing.");
        Status = next;
    }
}
public class PlanItem : Entity
{
    public WorkoutRoutine Routine { get; set; }
    public Guid PlanId { get; set; }
    public DayOfWeek Day { get; set; }
    public TimeOnly LocalTime { get; set; } = new(8, 0);
    [MaxLength(250)] public string Description { get; set; } = "";
    [MaxLength(40)] public string MealType { get; set; } = "";
    public int? Sets { get; set; }
    public int? Repetitions { get; set; }
    public int? DurationMinutes { get; set; }
    public decimal? Calories { get; set; }
    public decimal? Protein { get; set; }
    public decimal? Carbs { get; set; }
    public decimal? Fat { get; set; }
}
public class HealthcareProfessional : Entity
{
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    [MaxLength(100)] public string Specialty { get; set; } = "";
    [MaxLength(150)] public string Organization { get; set; } = "";
    public bool IsActive { get; set; } = true;
}
public class Appointment : Entity
{
    [MaxLength(450)] public string ClientId { get; set; } = "";
    [MaxLength(450)] public string? TrainerId { get; set; }
    public Guid? ProfessionalId { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    [MaxLength(500)] public string Purpose { get; set; } = "";
    [MaxLength(500)] public string ResponseNote { get; set; } = "";
    public AppointmentStatus Status { get; set; }
    public void Transition(AppointmentStatus next, bool isClient, DateTimeOffset now)
    {
        var allowed = isClient
            ? next == AppointmentStatus.Cancelled && Status is AppointmentStatus.Requested or AppointmentStatus.Confirmed
            : (Status, next) switch
            {
                (AppointmentStatus.Requested, AppointmentStatus.Confirmed or AppointmentStatus.Declined or AppointmentStatus.Cancelled) => true,
                (AppointmentStatus.Confirmed, AppointmentStatus.Cancelled) => true,
                (AppointmentStatus.Confirmed, AppointmentStatus.Completed) => EndsAtUtc <= now,
                _ => false
            };
        if (!allowed) throw new ArgumentException("This appointment change is not allowed. Completed visits must have ended.");
        if (next == AppointmentStatus.Confirmed && StartsAtUtc <= now) throw new ArgumentException("A past appointment cannot be confirmed.");
        Status = next;
    }
}
public class TrainerFeedback : Entity
{
    [MaxLength(450)] public string TrainerId { get; set; } = "";
    [MaxLength(450)] public string ClientId { get; set; } = "";
    [MaxLength(2000)] public string Comment { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public class Notification : Entity
{
    [MaxLength(450)] public string RecipientId { get; set; } = "";
    public NotificationKind Kind { get; set; }
    [MaxLength(150)] public string Title { get; set; } = "";
    public Guid? RelatedId { get; set; }
    [MaxLength(300)] public string DeduplicationKey { get; set; } = "";
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
    public bool Invalidated { get; set; }
}
public class ActivityAudit : Entity
{
    [MaxLength(450)] public string ActorId { get; set; } = "";
    [MaxLength(100)] public string Action { get; set; } = "";
    [MaxLength(100)] public string ResourceId { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public static class HealthMath
{
    public static decimal? Bmi(decimal? weightKg, decimal? heightCm) => weightKg > 0 && heightCm > 0
        ? Math.Round(weightKg.Value / ((heightCm.Value / 100m) * (heightCm.Value / 100m)), 1) : null;
    public static bool Overlaps(DateTimeOffset start, DateTimeOffset end, DateTimeOffset otherStart, DateTimeOffset otherEnd) => start < otherEnd && otherStart < end;
    public static string ReminderKey(string user, NotificationKind kind, Guid related, DateTimeOffset occurrence) => $"{user}:{kind}:{related:N}:{occurrence.UtcTicks}";
}

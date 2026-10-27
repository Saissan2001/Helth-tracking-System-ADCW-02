using System.ComponentModel.DataAnnotations;
using HealthTrack.Domain;

namespace HealthTrack.Application;

public record Actor(string Id, string Role);
public record Outcome(bool Success, string? Error = null, Guid? Id = null, string? Field = null)
{
    public static Outcome Ok(Guid? id = null) => new(true, null, id);
    public static Outcome Fail(string error, string? field = null) => new(false, error, null, field);
}
public record Page<T>(IReadOnlyList<T> Items, int Total, int Number, int Size = 20)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)Size));
}
public class LogInput : IValidatableObject
{
    [Range(1, 10000)] public int? WaterMl { get; set; }
    public DateTime? SleepStartedLocal { get; set; }
    public Guid? PlanItemId { get; set; }
    public DateOnly? ScheduledDate { get; set; }
    [EnumDataType(typeof(WorkoutRoutine))] public WorkoutRoutine Routine { get; set; }
    public Guid? Id { get; set; }
    public string Version { get; set; } = "";
    public LogKind Kind { get; set; }
    public DateTime LocalTime { get; set; } = DateTime.Now;
    [Required, StringLength(150)] public string Description { get; set; } = "";
    [StringLength(1000)] public string Notes { get; set; } = "";
    [Range(1, 600)] public decimal? WeightKg { get; set; }
    [Range(30, 300)] public decimal? HeightCm { get; set; }
    [Range(20, 250)] public int? HeartRate { get; set; }
    [Range(40, 300)] public int? Systolic { get; set; }
    [Range(20, 200)] public int? Diastolic { get; set; }
    [Range(0, 200000)] public int? Steps { get; set; }
    [Range(1, 1440)] public int? DurationMinutes { get; set; }
    [Range(0, 30000)] public decimal? Calories { get; set; }
    [Range(0, 5000)] public decimal? Protein { get; set; }
    [Range(0, 5000)] public decimal? Carbs { get; set; }
    [Range(0, 5000)] public decimal? Fat { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!Enum.IsDefined(Kind)) yield return new("Choose a valid log type.");
        if (Kind == LogKind.Hydration && WaterMl == null) yield return new("Enter the amount of water in millilitres.", [nameof(WaterMl)]);
        if (Kind == LogKind.Sleep && SleepStartedLocal == null) yield return new("Enter when your sleep started.", [nameof(SleepStartedLocal)]);
        if (Kind == LogKind.Measurement && WeightKg == null && HeightCm == null && HeartRate == null && Systolic == null && Diastolic == null && Steps == null)
            yield return new("Enter at least one health measurement.", [nameof(WeightKg)]);
        if (Kind == LogKind.Workout && DurationMinutes == null) yield return new("Workout duration is required.", [nameof(DurationMinutes)]);
        if (Systolic.HasValue != Diastolic.HasValue || Systolic <= Diastolic) yield return new("Enter both blood pressure values, with systolic above diastolic.", [nameof(Systolic), nameof(Diastolic)]);
    }
}
public class PlanInput : IValidatableObject
{
    public Guid? Id { get; set; }
    public string Version { get; set; } = "";
    [Required] public string ClientId { get; set; } = "";
    public PlanKind Kind { get; set; }
    [Required, StringLength(120)] public string Title { get; set; } = "";
    [StringLength(2000)] public string Instructions { get; set; } = "";
    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly EndDate { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddDays(28));
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!Enum.IsDefined(Kind)) yield return new("Choose a valid plan type.", [nameof(Kind)]);
        if (StartDate > EndDate || EndDate.DayNumber - StartDate.DayNumber > 366) yield return new("Plan dates must be ordered and span at most one year.", [nameof(EndDate)]);
    }
}
public class ItemInput
{
    [EnumDataType(typeof(WorkoutRoutine))] public WorkoutRoutine Routine { get; set; }
    public Guid PlanId { get; set; }
    public string Version { get; set; } = "";
    [EnumDataType(typeof(DayOfWeek))] public DayOfWeek Day { get; set; }
    public TimeOnly LocalTime { get; set; } = new(8, 0);
    [Required, StringLength(250)] public string Description { get; set; } = "";
    [StringLength(40)] public string MealType { get; set; } = "Breakfast";
    [Range(1, 100)] public int? Sets { get; set; }
    [Range(1, 1000)] public int? Repetitions { get; set; }
    [Range(1, 1440)] public int? DurationMinutes { get; set; }
    [Range(0, 30000)] public decimal? Calories { get; set; }
    [Range(0, 5000)] public decimal? Protein { get; set; }
    [Range(0, 5000)] public decimal? Carbs { get; set; }
    [Range(0, 5000)] public decimal? Fat { get; set; }
}
public class BookingInput
{
    [Required] public string Provider { get; set; } = "";
    public DateTime StartsLocal { get; set; } = DateTime.Today.AddDays(1).AddHours(9);
    [Range(15, 180)] public int DurationMinutes { get; set; } = 30;
    [Required, StringLength(500)] public string Purpose { get; set; } = "";
}
public class BookingChange
{
    public Guid Id { get; set; }
    [Required] public string Version { get; set; } = "";
    [EnumDataType(typeof(AppointmentStatus))] public AppointmentStatus Status { get; set; }
    [StringLength(500)] public string Note { get; set; } = "";
    public DateTime? RescheduleLocal { get; set; }
}
public class ProfileInput
{
    [Required, StringLength(100)] public string DisplayName { get; set; } = "";
    [StringLength(500)] public string Bio { get; set; } = "";
    [Required, StringLength(100)] public string TimeZoneId { get; set; } = "Asia/Colombo";
    public bool WorkoutReminders { get; set; }
    public bool MealReminders { get; set; }
    public bool AppointmentReminders { get; set; }
    [Range(0, 1440)] public int ReminderMinutes { get; set; } = 30;
}
public class ProfessionalInput
{
    public Guid? Id { get; set; }
    public string Version { get; set; } = "";
    [Required, StringLength(100)] public string DisplayName { get; set; } = "";
    [Required, StringLength(100)] public string Specialty { get; set; } = "";
    [StringLength(150)] public string Organization { get; set; } = "";
    public bool IsActive { get; set; } = true;
}
public record BookingRow(Appointment Appointment, string Client, string Provider);
public record WeightPoint(DateTimeOffset At, decimal Weight, decimal? Bmi = null);
public record DayTotal(DateOnly Day, int Workouts, decimal Burned, decimal Eaten, int Minutes = 0);
public record WeekTotal(DateOnly Starting, int Workouts, int Minutes);
public class ProgressReport
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public int WorkoutCount { get; set; }
    public int WorkoutMinutes { get; set; }
    public decimal? Burned { get; set; }
    public decimal? Eaten { get; set; }
    public decimal? Weight { get; set; }
    public decimal? Bmi { get; set; }
    public List<WeightPoint> WeightPoints { get; set; } = [];
    public List<DayTotal> Days { get; set; } = [];
    public List<WeekTotal> Weeks { get; set; } = [];
}
public class DashboardData
{
    public MemberProfile Profile { get; set; } = new();
    public ProgressReport? Progress { get; set; }
    public WeightPoint? LatestWeight { get; set; }
    public int WeekWorkouts { get; set; }
    public int WeekMinutes { get; set; }
    public int SevenDayWorkouts { get; set; }
    public int SevenDayMinutes { get; set; }
    public DateOnly WeekStarting { get; set; }
    public AdminInsights? Insights { get; set; }
    public DailyOverview? Daily { get; set; }
    public int Clients { get; set; }
    public int Trainers { get; set; }
    public int Plans { get; set; }
    public int PendingBookings { get; set; }
    public int Unread { get; set; }
    public IReadOnlyList<BookingRow> Upcoming { get; set; } = [];
    public IReadOnlyList<HealthPlan> CurrentPlans { get; set; } = [];
    public IReadOnlyList<TrainerFeedback> Feedback { get; set; } = [];
}
public record AssignmentRow(TrainerAssignment Assignment, string Client, string Trainer);
public record UsageDay(DateTime Day, int Entries);
public record ClientPerformance(string? ClientId, string Name, int WorkoutSessions, int WorkoutMinutes, int Meals, int MeasurementEntries, int ActiveDays);
public record RoutinePopularity(WorkoutRoutine Routine, int LoggedSessions, int UniqueParticipants, int ScheduledItems);
public record EngagementReport(DateOnly From, DateOnly To, int EligibleClients, int EngagedClients, IReadOnlyList<ClientPerformance> Clients, IReadOnlyList<RoutinePopularity> Routines)
{
    public int PageNumber { get; init; } = 1;
    public int Pages => Math.Max(1, (int)Math.Ceiling(EligibleClients / 20d));
    public int WorkoutSessions { get; init; }
    public bool Suppressed { get; init; }
    public decimal EngagementPercent => EligibleClients == 0 ? 0 : Math.Round(100m * EngagedClients / EligibleClients, 1);
}
public record PlanOccurrence(Guid ItemId, DateOnly Date, TimeOnly Time, string Description, Guid? LogId, bool Due);
public record AdherenceReport(HealthPlan Plan, DateOnly From, DateOnly To, string TimeZoneId, IReadOnlyList<PlanOccurrence> Occurrences)
{
    public int Due => Occurrences.Count(x => x.Due);
    public int Completed => Occurrences.Count(x => x.Due && x.LogId.HasValue);
    public decimal? Percent => Due == 0 ? null : Math.Round(100m * Completed / Due, 1);
}
public record TrainerWorkload(string Name, int Clients);
public record AdminInsights(IReadOnlyList<UsageDay> Registrations, Page<TrainerWorkload> Workloads);
public record AdminReport(int ActiveUsers, int Clients, int Trainers, int ActiveAssignments, int Logs, int Plans, IReadOnlyDictionary<AppointmentStatus, int> Appointments, IReadOnlyList<UsageDay> Usage, IReadOnlyList<ActivityAudit> Audit)
{
    public AdminInsights? Insights { get; init; }
}

public interface IHealthService
{
    Task<WellnessPreferences> WellnessSettings(Actor actor);
    Task<Outcome> SaveWellnessSettings(Actor actor, WellnessSettingsInput input);
    Task<DailyOverview> Daily(Actor actor, DateOnly? date = null, int page = 1);
    Task<LogInput> RepeatLog(Actor actor, Guid id);
    Task<Page<HealthLog>> Favorites(Actor actor, int page);
    Task<Outcome> SetFavorite(Actor actor, Guid id, string version, bool favorite);
    Task<Outcome> SubmitContact(ContactInput input, CancellationToken cancellationToken = default);
    Task<Page<ContactMessage>> ContactInbox(Actor actor, bool? handled, int page);
    Task<ContactMessage> ContactDetail(Actor actor, Guid id);
    Task<Outcome> HandleContact(Actor actor, Guid id, string version, bool handled);
    Task<MemberProfile> Profile(Actor actor);
    Task<int> UnreadCount(Actor actor);
    Task<Outcome> UpdateProfile(Actor actor, ProfileInput input);
    Task<DashboardData> Dashboard(Actor actor);
    Task<Page<MemberProfile>> Members(Actor actor, string? role, string? search, int page);
    Task<Page<HealthLog>> Logs(Actor actor, string? clientId, LogKind kind, DateOnly from, DateOnly to, int page);
    Task<LogInput> PlannedLog(Actor actor, Guid itemId, DateOnly date);
    Task<AdherenceReport> Adherence(Actor actor, Guid planId, DateOnly from, DateOnly to);
    Task<HealthLog> Log(Actor actor, Guid id);
    Task<Outcome> SaveLog(Actor actor, LogInput input);
    Task<Outcome> DeleteLog(Actor actor, Guid id, string version);
    Task<ProgressReport> Progress(Actor actor, string? clientId, DateOnly from, DateOnly to);
    Task<Page<HealthPlan>> Plans(Actor actor, string? clientId, PlanKind? kind, int page);
    Task<HealthPlan> Plan(Actor actor, Guid id);
    Task<Outcome> SavePlan(Actor actor, PlanInput input);
    Task<Outcome> AddItem(Actor actor, ItemInput input);
    Task<Outcome> RemoveItem(Actor actor, Guid id, string version);
    Task<Outcome> ChangePlan(Actor actor, Guid id, string version, PlanStatus status);
    Task<Outcome> Feedback(Actor actor, string clientId, string comment);
    Task<IReadOnlyList<(string Id, string Name)>> Providers(Actor actor);
    Task<Page<BookingRow>> Bookings(Actor actor, int page);
    Task<Outcome> RequestBooking(Actor actor, BookingInput input);
    Task<Outcome> ChangeBooking(Actor actor, BookingChange input);
    Task<Page<Notification>> Inbox(Actor actor, int page);
    Task<Outcome> MarkRead(Actor actor, Guid? id);
    Task<IReadOnlyList<AssignmentRow>> Assignments(Actor actor);
    Task<Outcome> Assign(Actor actor, string clientId, string trainerId);
    Task<Outcome> Revoke(Actor actor, Guid id, string version);
    Task<IReadOnlyList<HealthcareProfessional>> Professionals(Actor actor);
    Task<Outcome> SaveProfessional(Actor actor, ProfessionalInput input);
    Task<Outcome> SetActive(Actor actor, string userId, bool active);
    Task<AdminReport> SystemReport(Actor actor, DateOnly from, DateOnly to, int page = 1);
    Task<EngagementReport> Engagement(Actor actor, DateOnly from, DateOnly to, int page = 1);
    Task GenerateReminders(CancellationToken cancellationToken = default);
}

public class AccessDeniedException : Exception;
public class MissingRecordException : Exception;
public interface IClock { DateTimeOffset UtcNow { get; } }
public class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

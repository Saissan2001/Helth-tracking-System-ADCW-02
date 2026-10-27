using System.ComponentModel.DataAnnotations;
using HealthTrack.Domain;

namespace HealthTrack.Application;
public class WellnessSettingsInput
{
    public string Version { get; set; } = "";
    [EnumDataType(typeof(WellnessFocus))] public WellnessFocus Focus { get; set; }
    [Range(1, 10080)] public int? WeeklyWorkoutMinutes { get; set; }
    [Range(1, 10000)] public int? DailyWaterMl { get; set; }
    [Range(1, 1440)] public int? NightlySleepMinutes { get; set; }
    [Range(1, 200000)] public int? DailySteps { get; set; }
    public bool ShowWater { get; set; } = true;
    public bool ShowSleep { get; set; } = true;
    public bool ShowNutrition { get; set; } = true;
    public bool ShowSteps { get; set; } = true;
}
public record WellnessDay(DateOnly Date, int Entries, int WorkoutMinutes, int WorkoutCount, int? WaterMl, int? SleepMinutes, int? Steps, decimal? MealCalories, decimal? Protein, decimal? Carbs, decimal? Fat, int MealsMissingCalories);
public record TodayPlanItem(Guid PlanId, Guid ItemId, string Title, string Description, TimeOnly Time, PlanKind Kind, Guid? LogId);
public class DailyOverview
{
    public MemberProfile Profile { get; set; } = new();
    public WellnessPreferences Preferences { get; set; } = new();
    public DateOnly Date { get; set; }
    public DateOnly Today { get; set; }
    public DateOnly WeekStart { get; set; }
    public int WeekWorkoutMinutes { get; set; }
    public WellnessDay Totals { get; set; } = null!;
    public Page<HealthLog> Entries { get; set; } = new([], 0, 1);
    public IReadOnlyList<WellnessDay> Days { get; set; } = [];
    public int PreviousSevenMinutes { get; set; }
    public int PreviousSevenRecordedDays { get; set; }
    public IReadOnlyList<HealthLog> Favorites { get; set; } = [];
    public IReadOnlyList<TodayPlanItem> Schedule { get; set; } = [];
    public int ScheduledCount { get; set; }
    public int RecordedDays => Days.Count(x => x.Entries > 0);
    public int SevenWorkoutMinutes => Days.Sum(x => x.WorkoutMinutes);
}

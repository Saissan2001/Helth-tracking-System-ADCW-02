using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Domain;
public enum WellnessFocus { Balance, Movement, Nutrition, Rest }
public class WellnessPreferences : Entity
{
    [MaxLength(450)] public string UserId { get; set; } = "";
    public WellnessFocus Focus { get; set; }
    public int? WeeklyWorkoutMinutes { get; set; }
    public int? DailyWaterMl { get; set; }
    public int? NightlySleepMinutes { get; set; }
    public int? DailySteps { get; set; }
    public bool ShowWater { get; set; } = true;
    public bool ShowSleep { get; set; } = true;
    public bool ShowNutrition { get; set; } = true;
    public bool ShowSteps { get; set; } = true;
    public DateTimeOffset? SetupCompletedAtUtc { get; set; }
}

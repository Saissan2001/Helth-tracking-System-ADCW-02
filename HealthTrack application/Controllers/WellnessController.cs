using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HealthTrack.Web.Controllers;
[Authorize(Roles = Roles.Client)]
public class WellnessController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index(DateOnly? date, int page = 1) => View(await Health.Daily(Actor, date, page));
    public async Task<IActionResult> Trends(DateOnly? date) => View(await Health.Daily(Actor, date));
    [HttpGet] public async Task<IActionResult> Settings()
    {
        var p = await Health.WellnessSettings(Actor);
        ViewData["FirstRun"] = p.SetupCompletedAtUtc == null;
        return View(new WellnessSettingsInput { Version = Display.Version(p), Focus = p.Focus, WeeklyWorkoutMinutes = p.WeeklyWorkoutMinutes,
            DailyWaterMl = p.DailyWaterMl, NightlySleepMinutes = p.NightlySleepMinutes, DailySteps = p.DailySteps,
            ShowWater = p.ShowWater, ShowSleep = p.ShowSleep, ShowNutrition = p.ShowNutrition, ShowSteps = p.ShowSteps });
    }
    [HttpPost] public async Task<IActionResult> Settings(WellnessSettingsInput input)
    {
        if (ModelState.IsValid)
        {
            var result = await Health.SaveWellnessSettings(Actor, input);
            if (result.Success) { Flash(result, "Your goals and Today view are ready."); return RedirectToAction("Index", "Dashboard"); }
            ModelState.AddModelError(result.Field ?? "", result.Error!);
        }
        ViewData["FirstRun"] = (await Health.WellnessSettings(Actor)).SetupCompletedAtUtc == null;
        return View(input);
    }
    public async Task<IActionResult> Library(int page = 1) => View(await Health.Favorites(Actor, page));
    [HttpPost] public async Task<IActionResult> Favorite(Guid id, string version, bool favorite, string? returnUrl)
    {
        Flash(await Health.SetFavorite(Actor, id, version, favorite), favorite ? "Saved to your reusable entries." : "Removed from your saved entries. The original log is still in your diary.");
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Library));
    }
    public async Task<IActionResult> Export(DateOnly? date)
    {
        var data = await Health.Daily(Actor, date);
        var rows = new List<object?[]> { new object?[] { "Local date", "Timezone", "Entries", "Workout minutes", "Workout sessions", "Water ml", "Sleep minutes by wake date", "Latest daily steps", "Known meal kcal", "Protein g", "Carbs g", "Fat g", "Meals missing kcal" } };
        rows.AddRange(data.Days.Select(d => new object?[] { d.Date, data.Profile.TimeZoneId, d.Entries, d.WorkoutMinutes, d.WorkoutCount, d.WaterMl, d.SleepMinutes, d.Steps, d.MealCalories, d.Protein, d.Carbs, d.Fat, d.MealsMissingCalories }));
        return File(CsvReport.Encode(rows), "text/csv; charset=utf-8", $"healthtrack-wellness-{data.Date:yyyy-MM-dd}.csv");
    }
}

using System.Security.Claims;
using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HealthTrack.Web.Controllers;
public abstract class HealthController(IHealthService service) : Controller
{
    protected readonly IHealthService Health = service;
    protected Actor Actor => new(User.FindFirstValue(ClaimTypes.NameIdentifier)!, User.FindFirstValue(ClaimTypes.Role)!);
    protected void Flash(Outcome result, string success) => TempData[result.Success ? "Success" : "Error"] = result.Success ? success : result.Error;
    protected async Task<(DateOnly From, DateOnly To)> Dates(DateOnly? from, DateOnly? to)
    {
        var p = await Health.Profile(Actor); ViewData["Zone"] = p.TimeZoneId;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId)).DateTime);
        return (from ?? today.AddDays(-29), to ?? today);
    }
}
public class DashboardController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index() => View(await Health.Dashboard(Actor));
}
[Authorize(Roles = Roles.Client)]
public class LogsController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index(LogKind kind = LogKind.Workout, DateOnly? from = null, DateOnly? to = null, int page = 1)
    {
        var dates = await Dates(from, to); ViewData["Kind"] = kind; ViewData["From"] = dates.From; ViewData["To"] = dates.To;
        return View(await Health.Logs(Actor, null, kind, dates.From, dates.To, page));
    }
    [HttpGet] public async Task<IActionResult> Edit(Guid? id, LogKind kind = LogKind.Workout, Guid? itemId = null, DateOnly? date = null, Guid? repeatId = null, string? returnUrl = null)
    {
        var p = await Health.Profile(Actor); ViewData["Zone"] = p.TimeZoneId;
        ViewData["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (!id.HasValue && repeatId.HasValue)
        {
            ViewData["Repeated"] = true; var draft = await Health.RepeatLog(Actor, repeatId.Value);
            if (date.HasValue) draft.LocalTime = date.Value.ToDateTime(TimeOnly.FromDateTime(draft.LocalTime));
            return View(draft);
        }
        if (!id.HasValue && itemId.HasValue && date.HasValue) return View(await Health.PlannedLog(Actor, itemId.Value, date.Value));
        if (!id.HasValue)
        {
            var now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, p.TimeZoneId).DateTime;
            return View(new LogInput { Kind = kind, Description = kind == LogKind.Hydration ? "Water" : kind == LogKind.Sleep ? "Sleep session" : "", LocalTime = date.HasValue ? date.Value.ToDateTime(TimeOnly.FromDateTime(now)) : now });
        }
        var l = await Health.Log(Actor, id.Value);
        return View(new LogInput { Id = l.Id, WaterMl = l.WaterMl, SleepStartedLocal = l.SleepStartedAtUtc.HasValue ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(l.SleepStartedAtUtc.Value, p.TimeZoneId).DateTime : null, PlanItemId = l.PlanItemId, ScheduledDate = l.ScheduledDate, Version = Display.Version(l), Kind = l.Kind, LocalTime = TimeZoneInfo.ConvertTime(l.OccurredAtUtc, TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId)).DateTime, Description = l.Description, Notes = l.Notes, Routine = l.Routine, WeightKg = l.WeightKg, HeightCm = l.HeightCm, HeartRate = l.HeartRate, Systolic = l.Systolic, Diastolic = l.Diastolic, Steps = l.Steps, DurationMinutes = l.DurationMinutes, Calories = l.Calories, Protein = l.Protein, Carbs = l.Carbs, Fat = l.Fat });
    }
    [HttpPost] public async Task<IActionResult> Edit(LogInput input, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        ViewData["Zone"] = (await Health.Profile(Actor)).TimeZoneId;
        if (!ModelState.IsValid) return View(input);
        var result = await Health.SaveLog(Actor, input);
        if (!result.Success) { ModelState.AddModelError(result.Field ?? "", result.Error!); return View(input); }
        Flash(result, "Your entry has been saved.");
        if (Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl!);
        if (input.Kind is LogKind.Hydration or LogKind.Sleep) return RedirectToAction("Index", "Wellness");
        return RedirectToAction(nameof(Index), new { kind = input.Kind });
    }
    [HttpPost] public async Task<IActionResult> Delete(Guid id, string version, LogKind kind, string? returnUrl = null) { Flash(await Health.DeleteLog(Actor, id, version), "Entry deleted. Your totals have been updated."); return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index), new { kind }); }
}
[Authorize(Roles = Roles.Client + "," + Roles.Trainer)]
public class ProgressController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Export(string? clientId, DateOnly? from, DateOnly? to)
    {
        var d = await Dates(from, to); var report = await Health.Progress(Actor, clientId, d.From, d.To);
        var rows = new List<object?[]> { new object?[] { "Record", "Date (UTC)", "Weight (kg)", "BMI", "Workouts", "Workout minutes", "Recorded calories burned", "Recorded energy consumed" } };
        rows.AddRange(report.WeightPoints.Select(p => new object?[] { "Measurement", p.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm"), p.Weight, p.Bmi, null, null, null, null }));
        rows.AddRange(report.Days.Select(p => new object?[] { "Daily activity", p.Day, null, null, p.Workouts, p.Minutes, p.Burned, p.Eaten }));
        return File(CsvReport.Encode(rows), "text/csv; charset=utf-8", $"healthtrack-progress-{d.From:yyyy-MM-dd}-{d.To:yyyy-MM-dd}.csv");
    }
    public async Task<IActionResult> Index(string? clientId, DateOnly? from, DateOnly? to)
    {
        if (Actor.Role == Roles.Trainer && clientId == null) return RedirectToAction("Clients", "Trainer");
        var d = await Dates(from, to); return View(await Health.Progress(Actor, clientId, d.From, d.To));
    }
}
[Authorize(Roles = Roles.Client + "," + Roles.Trainer)]
public class PlansController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index(string? clientId, PlanKind? kind, int page = 1) { ViewData["ClientId"] = clientId; ViewData["Kind"] = kind; return View(await Health.Plans(Actor, clientId, kind, page)); }
    public async Task<IActionResult> Details(Guid id) => View(await Health.Plan(Actor, id));
    public async Task<IActionResult> Adherence(Guid id, DateOnly? from, DateOnly? to)
    {
        var plan = await Health.Plan(Actor, id);
        var profile = await Health.Profile(Actor);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, profile.TimeZoneId).DateTime);
        var end = to ?? (plan.EndDate < today ? plan.EndDate : today);
        return View(await Health.Adherence(Actor, id, from ?? end.AddDays(-6), end));
    }
    [Authorize(Roles = Roles.Trainer), HttpGet] public async Task<IActionResult> Edit(string clientId, Guid? id, PlanKind kind = PlanKind.Workout)
    {
        if (!id.HasValue) { await Health.Progress(Actor, clientId, DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today)); return View(new PlanInput { ClientId = clientId, Kind = kind }); }
        var p = await Health.Plan(Actor, id.Value); return View(new PlanInput { Id = p.Id, Version = Display.Version(p), ClientId = p.ClientId, Kind = p.Kind, Title = p.Title, Instructions = p.Instructions, StartDate = p.StartDate, EndDate = p.EndDate });
    }
    [Authorize(Roles = Roles.Trainer), HttpPost] public async Task<IActionResult> Edit(PlanInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var result = await Health.SavePlan(Actor, input);
        if (!result.Success) { ModelState.AddModelError(result.Field ?? "", result.Error!); return View(input); }
        Flash(result, "Draft saved. Add scheduled items, then publish the plan."); return RedirectToAction(nameof(Details), new { id = result.Id });
    }
    [Authorize(Roles = Roles.Trainer), HttpPost] public async Task<IActionResult> AddItem(ItemInput input)
    {
        if (ModelState.IsValid)
        {
            var result = await Health.AddItem(Actor, input);
            if (result.Success) { Flash(result, "Schedule item added."); return RedirectToAction(nameof(Details), new { id = input.PlanId }); }
            ModelState.AddModelError(result.Field ?? "", result.Error!);
        }
        return View("Details", await Health.Plan(Actor, input.PlanId));
    }
    [Authorize(Roles = Roles.Trainer), HttpPost] public async Task<IActionResult> RemoveItem(Guid id, Guid planId, string version) { Flash(await Health.RemoveItem(Actor, id, version), "Schedule item removed."); return RedirectToAction(nameof(Details), new { id = planId }); }
    [Authorize(Roles = Roles.Trainer), HttpPost] public async Task<IActionResult> Change(Guid id, string version, PlanStatus status) { Flash(await Health.ChangePlan(Actor, id, version, status), "Plan " + status.ToString().ToLowerInvariant() + "."); return RedirectToAction(nameof(Details), new { id }); }
}
[Authorize(Roles = Roles.Trainer)]
public class TrainerController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Clients(string? search, int page = 1) { ViewData["Search"] = search; return View(await Health.Members(Actor, Roles.Client, search, page)); }
    public async Task<IActionResult> Client(string id)
    {
        var d = await Dates(null, null); return View(await Health.Progress(Actor, id, d.From, d.To));
    }
    [HttpPost] public async Task<IActionResult> Feedback(string clientId, string comment)
    {
        var result = await Health.Feedback(Actor, clientId, comment);
        if (result.Success) { Flash(result, "Feedback shared with your client."); return RedirectToAction(nameof(Client), new { id = clientId }); }
        ModelState.AddModelError(result.Field ?? "", result.Error!);
        var view = (ViewResult)await Client(clientId); view.ViewName = "Client"; return view;
    }
}
public class AppointmentsController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index(int page = 1) { await Dates(null, null); return View(await Health.Bookings(Actor, page)); }
    [Authorize(Roles = Roles.Client), HttpGet] public async Task<IActionResult> Create() { ViewData["Providers"] = await Health.Providers(Actor); await Dates(null, null); return View(new BookingInput()); }
    [Authorize(Roles = Roles.Client), HttpPost] public async Task<IActionResult> Create(BookingInput input)
    {
        ViewData["Providers"] = await Health.Providers(Actor); await Dates(null, null);
        if (!ModelState.IsValid) return View(input);
        var result = await Health.RequestBooking(Actor, input);
        if (!result.Success) { ModelState.AddModelError(result.Field ?? "", result.Error!); return View(input); }
        Flash(result, "Request sent. Your provider or coordinator will review it."); return RedirectToAction(nameof(Index));
    }
    [HttpPost] public async Task<IActionResult> Change(BookingChange input, int page = 1, bool editTime = false)
    {
        if (editTime && !input.RescheduleLocal.HasValue) ModelState.AddModelError(nameof(input.RescheduleLocal), "Choose a new appointment time.");
        if (ModelState.IsValid)
        {
            var result = await Health.ChangeBooking(Actor, input);
            if (result.Success) { Flash(result, "Appointment updated."); return RedirectToAction(nameof(Index), new { page }); }
            ModelState.AddModelError(result.Field ?? "", result.Error!);
        }
        ViewData["FeedbackScope"] = $"booking-{input.Id}-{(editTime || input.RescheduleLocal.HasValue ? "time" : "manage")}";
        var view = (ViewResult)await Index(page); view.ViewName = "Index"; return view;
    }
}
public class NotificationsController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index(int page = 1) { await Dates(null, null); return View(await Health.Inbox(Actor, page)); }
    [HttpPost] public async Task<IActionResult> Read(Guid? id) { Flash(await Health.MarkRead(Actor, id), "Notifications marked as read."); return RedirectToAction(nameof(Index)); }
}
[Authorize(Roles = Roles.Administrator)]
public class AdminController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Users(string? role, string? search, int page = 1) { ViewData["Search"] = search; ViewData["Role"] = role; return View(await Health.Members(Actor, role, search, page)); }
    [HttpPost] public async Task<IActionResult> SetActive(string userId, bool active) { Flash(await Health.SetActive(Actor, userId, active), "Account status updated."); return RedirectToAction(nameof(Users)); }
    public async Task<IActionResult> Assignments(string? search, string? trainerSearch, int page = 1, int trainerPage = 1)
    {
        ViewData["Clients"] = await Health.Members(Actor, Roles.Client, search, page);
        ViewData["Trainers"] = await Health.Members(Actor, Roles.Trainer, trainerSearch, trainerPage);
        ViewData["TrainerSearch"] = trainerSearch;
        ViewData["Search"] = search;
        return View(await Health.Assignments(Actor));
    }
    [HttpPost] public async Task<IActionResult> Assign(string clientId, string trainerId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) ModelState.AddModelError(nameof(clientId), "Choose a client.");
        if (string.IsNullOrWhiteSpace(trainerId)) ModelState.AddModelError(nameof(trainerId), "Choose a trainer.");
        if (ModelState.IsValid)
        {
            var result = await Health.Assign(Actor, clientId, trainerId);
            if (result.Success) { Flash(result, "Trainer assignment saved."); return RedirectToAction(nameof(Assignments)); }
            ModelState.AddModelError(result.Field ?? "", result.Error!);
        }
        var view = (ViewResult)await Assignments(null, null); view.ViewName = "Assignments"; return view;
    }
    [HttpPost] public async Task<IActionResult> Revoke(Guid id, string version) { Flash(await Health.Revoke(Actor, id, version), "Assignment ended. Trainer access has been revoked."); return RedirectToAction(nameof(Assignments)); }
    public async Task<IActionResult> Professionals() => View(await Health.Professionals(Actor));
    [HttpPost] public async Task<IActionResult> Professional(ProfessionalInput input)
    {
        if (ModelState.IsValid)
        {
            var result = await Health.SaveProfessional(Actor, input);
            if (result.Success) { Flash(result, "Directory updated."); return RedirectToAction(nameof(Professionals)); }
            ModelState.AddModelError(result.Field ?? "", result.Error!);
        }
        ViewData["FeedbackScope"] = "professional-" + (input.Id?.ToString() ?? "new");
        return View("Professionals", await Health.Professionals(Actor));
    }
    public async Task<IActionResult> Reports(DateOnly? from, DateOnly? to, int page = 1) { var d = await Dates(from, to); ViewData["From"] = d.From; ViewData["To"] = d.To; return View(await Health.SystemReport(Actor, d.From, d.To, page)); }
}


using System.Globalization;
using System.Text;
using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HealthTrack.Web.Controllers;
[Authorize(Roles = Roles.Trainer + "," + Roles.Administrator)]
public class ReportsController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index(DateOnly? from, DateOnly? to, int page = 1)
    {
        var dates = await Dates(from, to);
        ViewData["From"] = dates.From; ViewData["To"] = dates.To;
        return View(await Health.Engagement(Actor, dates.From, dates.To, page));
    }
    public async Task<IActionResult> Export(DateOnly? from, DateOnly? to, int page = 1)
    {
        var dates = await Dates(from, to);
        var report = await Health.Engagement(Actor, dates.From, dates.To, page);
        var rows = new List<object?[]> { new object?[] { "Dataset", "Label", "Metric", "Value", "From", "To" } };
        void Add(string dataset, string label, string metric, object value) => rows.Add([dataset, label, metric, value, report.From, report.To]);
        Add("Summary", "Current eligible clients", "Count", report.EligibleClients);
        if (report.Suppressed) Add("Privacy", "Small cohort", "Status", "Activity figures suppressed; minimum group size is 5, including complementary groups.");
        else
        {
            Add("Summary", "Clients with at least one log", "Count", report.EngagedClients);
            Add("Summary", "Engagement", "Percent", report.EngagementPercent);
            Add("Summary", "Workout sessions", "Count", report.WorkoutSessions);
        }
        if (Actor.Role == Roles.Trainer) Add("Scope", "Client page", "Page", $"{report.PageNumber} of {report.Pages}; up to 20 clients; summary and routines cover the full assigned cohort");
        foreach (var c in report.Clients)
        {
            Add("Performance", c.Name, "Workout sessions", c.WorkoutSessions); Add("Performance", c.Name, "Workout minutes", c.WorkoutMinutes);
            Add("Performance", c.Name, "Meal entries", c.Meals); Add("Performance", c.Name, "Measurement entries", c.MeasurementEntries); Add("Performance", c.Name, "Active UTC days", c.ActiveDays);
        }
        foreach (var r in report.Routines)
        {
            Add("Routines", r.Routine.ToString(), "Logged sessions", r.LoggedSessions); Add("Routines", r.Routine.ToString(), "Unique participants", r.UniqueParticipants);
            Add("Routines", r.Routine.ToString(), "Published schedule items", r.ScheduledItems);
        }
        return File(CsvReport.Encode(rows), "text/csv; charset=utf-8", $"healthtrack-performance-{report.From:yyyy-MM-dd}-{report.To:yyyy-MM-dd}.csv");
    }
}
public static class CsvReport
{
    // Quotes handle separators/newlines; prefix neutralizes spreadsheet formula injection.
    public static byte[] Encode(IEnumerable<object?[]> rows)
    {
        static string Cell(object? value)
        {
            var text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
            if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@') || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(Cell)))) + "\r\n")).ToArray();
    }
}

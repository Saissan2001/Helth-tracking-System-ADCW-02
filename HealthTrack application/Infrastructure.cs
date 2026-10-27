using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HealthTrack.Web;
public class DomainExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext c)
    {
        var status = c.Exception switch { AccessDeniedException => 403, MissingRecordException => 404, ArgumentException => 400, _ => 0 };
        if (status == 0) return;
        c.Result = new ViewResult { ViewName = "~/Views/Shared/Problem.cshtml", StatusCode = status, ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), c.ModelState) { ["Status"] = status, ["Message"] = status == 400 ? c.Exception.Message : status == 403 ? "You do not have access to this information." : "This record could not be found." } };
        c.ExceptionHandled = true;
    }
}
public class ReminderWorker(IServiceScopeFactory scopes, ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<IHealthService>().GenerateReminders(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Reminder processing failed; retrying on the next interval."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
public static class Display
{
    public static string Local(DateTimeOffset time, string zone = "Asia/Colombo") => TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString("dd MMM yyyy, HH:mm");
    public static string Version(Entity entity) => Convert.ToBase64String(entity.RowVersion);
    public static string Number(decimal? value, string suffix = "") => value.HasValue ? value.Value.ToString("0.#") + suffix : "Not recorded";
}

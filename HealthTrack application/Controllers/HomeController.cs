using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using HealthTrack.Application;
namespace HealthTrack.Web.Controllers;
public class HomeController(IHealthService health) : Controller
{
    [AllowAnonymous] public IActionResult Index() => User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Dashboard") : View();
    [AllowAnonymous] public IActionResult Privacy() => View();
    [AllowAnonymous] public IActionResult About() => View();
    [AllowAnonymous, HttpGet] public IActionResult Contact() => View(new ContactInput());
    [AllowAnonymous, HttpPost, EnableRateLimiting("contact"), RequestSizeLimit(65536)]
    public async Task<IActionResult> Contact(ContactInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(input);
        var result = await health.SubmitContact(input, cancellationToken);
        if (!result.Success) { ModelState.AddModelError(result.Field ?? "", result.Error!); return View(input); }
        TempData["ContactSuccess"] = "Thank you. Your message has been saved for the HealthTrack support team to review.";
        return RedirectToAction(nameof(Contact), "Home", null, "contact-form");
    }
    [AllowAnonymous] public IActionResult Terms() => View();
    [AllowAnonymous] public IActionResult Denied() { Response.StatusCode = 403; ViewData["Status"] = 403; ViewData["Message"] = "Your account does not have access to this page."; return View("~/Views/Shared/Problem.cshtml"); }
    [AllowAnonymous] public IActionResult Error() { Response.StatusCode = 500; ViewData["Status"] = 500; ViewData["Message"] = "We could not complete that request. Please retry. If it continues, contact the operator with reference " + HttpContext.TraceIdentifier; return View("~/Views/Shared/Problem.cshtml"); }
}

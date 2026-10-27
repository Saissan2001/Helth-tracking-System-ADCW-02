using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HealthTrack.Web.Controllers;

[Authorize(Roles = Roles.Administrator)]
public class ContactInboxController(IHealthService health) : HealthController(health)
{
    public async Task<IActionResult> Index(bool? handled, int page = 1)
    {
        ViewData["Handled"] = handled;
        ViewData["Zone"] = (await Health.Profile(Actor)).TimeZoneId;
        return View(await Health.ContactInbox(Actor, handled, page));
    }
    public async Task<IActionResult> Details(Guid id)
    {
        ViewData["Zone"] = (await Health.Profile(Actor)).TimeZoneId;
        return View(await Health.ContactDetail(Actor, id));
    }
    [HttpPost] public async Task<IActionResult> Handle(Guid id, string version, bool handled)
    {
        Flash(await Health.HandleContact(Actor, id, version, handled), handled ? "Enquiry marked as handled." : "Enquiry reopened.");
        return RedirectToAction(nameof(Details), new { id });
    }
}

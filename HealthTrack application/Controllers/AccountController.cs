using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using HealthTrack.Application;
using HealthTrack.Domain;
using HealthTrack.Infrastructure;
using HealthTrack.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Web.Controllers;
[EnableRateLimiting("account")]
public class AccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, HealthTrackDbContext db, IHealthService health, IWebHostEnvironment env, IConfiguration config) : Controller
{
    private Actor Actor => new(User.FindFirstValue(ClaimTypes.NameIdentifier)!, User.FindFirstValue(ClaimTypes.Role)!);
    [AllowAnonymous, HttpGet] public IActionResult Login(string? returnUrl) => View(new LoginInput { ReturnUrl = returnUrl });
    [AllowAnonymous, HttpPost] public async Task<IActionResult> Login(LoginInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var user = await users.FindByEmailAsync(input.Email);
        if (user != null && await db.Profiles.AnyAsync(x => x.UserId == user.Id && x.IsActive))
        {
            var result = await signIn.PasswordSignInAsync(user, input.Password, input.RememberMe, true);
            if (result.Succeeded) return Url.IsLocalUrl(input.ReturnUrl) ? LocalRedirect(input.ReturnUrl!) : RedirectToAction("Index", "Dashboard");
        }
        ModelState.AddModelError("", "Unable to sign in. Check your details or try again later if the account is locked."); return View(input);
    }
    [AllowAnonymous, HttpGet] public IActionResult Register() => View(new RegisterInput());
    [AllowAnonymous, HttpPost] public async Task<IActionResult> Register(RegisterInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var result = await CreateAccount(input, Roles.Client);
        if (!result.Succeeded) { AddIdentityErrors(result, nameof(input.Password), privateRegistration: true); return View(input); }
        TempData["Success"] = "Your client account is ready. Sign in to get started."; return RedirectToAction(nameof(Login));
    }
    private void AddIdentityErrors(IdentityResult result, string passwordField, bool privateRegistration = false)
    {
        foreach (var error in result.Errors)
        {
            var field = error.Code == "PasswordMismatch" ? "CurrentPassword" : error.Code.StartsWith("Password") ? passwordField
                : error.Code.Contains("Email") || error.Code.Contains("UserName") ? "Email" : "";
            var message = privateRegistration && error.Code.Contains("Duplicate")
                ? "Unable to create this account. Try signing in or resetting your password." : error.Description;
            ModelState.AddModelError(field, message);
        }
    }
    private async Task<IdentityResult> CreateAccount(RegisterInput input, string role)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = new ApplicationUser { UserName = input.Email.Trim(), Email = input.Email.Trim() };
        var result = await users.CreateAsync(user, input.Password); if (!result.Succeeded) return result;
        result = await users.AddToRoleAsync(user, role); if (!result.Succeeded) return result;
        db.Profiles.Add(new() { UserId = user.Id, DisplayName = input.DisplayName.Trim(), Role = role });
        db.Audit.Add(new() { ActorId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Id, Action = role + " account created", ResourceId = user.Id });
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return IdentityResult.Success;
    }
    [Authorize(Roles = Roles.Administrator), HttpGet] public IActionResult CreateTrainer() => View("CreateTrainer", new RegisterInput());
    [Authorize(Roles = Roles.Administrator), HttpPost] public async Task<IActionResult> CreateTrainer(RegisterInput input)
    {
        if (!ModelState.IsValid) return View("CreateTrainer", input);
        var result = await CreateAccount(input, Roles.Trainer);
        if (!result.Succeeded) { AddIdentityErrors(result, nameof(input.Password)); return View("CreateTrainer", input); }
        TempData["Success"] = "Trainer created. Share their initial credentials privately."; return RedirectToAction("Users", "Admin");
    }
    [HttpPost] public async Task<IActionResult> Logout() { await signIn.SignOutAsync(); return RedirectToAction("Index", "Home"); }
    [HttpGet] public async Task<IActionResult> Profile()
    {
        var p = await health.Profile(Actor);
        return View(new ProfileInput { DisplayName = p.DisplayName, Bio = p.Bio, TimeZoneId = p.TimeZoneId, WorkoutReminders = p.WorkoutReminders, MealReminders = p.MealReminders, AppointmentReminders = p.AppointmentReminders, ReminderMinutes = p.ReminderMinutes });
    }
    [HttpPost] public async Task<IActionResult> Profile(ProfileInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var result = await health.UpdateProfile(Actor, input);
        if (!result.Success) { ModelState.AddModelError(result.Field ?? "", result.Error!); return View(input); }
        TempData["Success"] = "Profile and reminder preferences saved."; return RedirectToAction(nameof(Profile));
    }
    [HttpGet] public IActionResult Password() => View(new PasswordInput());
    [HttpPost] public async Task<IActionResult> Password(PasswordInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var user = (await users.GetUserAsync(User))!; var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
        if (!result.Succeeded) { AddIdentityErrors(result, nameof(input.NewPassword)); return View(input); }
        await signIn.RefreshSignInAsync(user); TempData["Success"] = "Password changed."; return RedirectToAction(nameof(Profile));
    }
    [AllowAnonymous, HttpGet] public IActionResult ForgotPassword() => View();
    [AllowAnonymous, HttpPost] public async Task<IActionResult> ForgotPassword([Required(ErrorMessage = "Enter your email address."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(256, ErrorMessage = "Email addresses must be 256 characters or fewer.")] string email)
    {
        if (!ModelState.IsValid) return View();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var user = await users.FindByEmailAsync(email);
            if (user != null && env.IsDevelopment() && config.GetValue<bool>("DevelopmentMail:Enabled") && await db.Profiles.AnyAsync(x => x.UserId == user.Id && x.IsActive))
            {
                var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
                var directory = Path.GetFullPath(config["DevelopmentMail:Directory"] ?? Path.Combine(env.ContentRootPath, "..", ".local", "mail")); Directory.CreateDirectory(directory);
                var link = "https://localhost:7000/Account/ResetPassword?email=" + Uri.EscapeDataString(email) + "&token=" + token;
                await System.IO.File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid() + ".txt"), "Development password reset (not an email delivery)\n" + link);
            }
        }
        ViewData["Sent"] = true; return View();
    }
    [AllowAnonymous, HttpGet] public IActionResult ResetPassword(string email, string token) => View(new ResetInput { Email = email, Token = token });
    [AllowAnonymous, HttpPost] public async Task<IActionResult> ResetPassword(ResetInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var user = await users.FindByEmailAsync(input.Email); IdentityResult? result = null;
        try { if (user != null) result = await users.ResetPasswordAsync(user, Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(input.Token)), input.Password); } catch (FormatException) { }
        if (result?.Succeeded != true)
        {
            if (result?.Errors.Any(e => e.Code.StartsWith("Password")) == true) AddIdentityErrors(result, nameof(input.Password));
            else ModelState.AddModelError("", "The reset link is invalid or expired, or the password does not meet the requirements.");
            return View(input);
        }
        TempData["Success"] = "Password reset. You can now sign in."; return RedirectToAction(nameof(Login));
    }
}

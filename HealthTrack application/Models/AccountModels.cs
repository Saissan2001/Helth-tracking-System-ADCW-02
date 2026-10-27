using System.ComponentModel.DataAnnotations;
namespace HealthTrack.Web.Models;
public class LoginInput
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}
public class RegisterInput
{
    [Required, StringLength(100)] public string DisplayName { get; set; } = "";
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public class ResetInput
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    public string Token { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public class PasswordInput
{
    [Required, DataType(DataType.Password)] public string CurrentPassword { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
    [Compare(nameof(NewPassword)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}

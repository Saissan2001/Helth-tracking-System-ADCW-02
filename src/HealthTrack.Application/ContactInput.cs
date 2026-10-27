using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Application;

public class ContactInput
{
    [Required, StringLength(100), Display(Name = "Your name")]
    public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(256), Display(Name = "Email address")]
    public string Email { get; set; } = "";
    [Required, StringLength(150, MinimumLength = 3)]
    public string Subject { get; set; } = "";
    [Required, StringLength(4000, MinimumLength = 10)]
    public string Message { get; set; } = "";
}

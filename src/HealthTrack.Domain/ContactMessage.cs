using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Domain;

public class ContactMessage : Entity
{
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(150)] public string Subject { get; set; } = "";
    [MaxLength(4000)] public string Message { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? HandledAtUtc { get; set; }
}

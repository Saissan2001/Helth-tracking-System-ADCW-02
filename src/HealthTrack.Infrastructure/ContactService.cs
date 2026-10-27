using System.Net.Mail;
using HealthTrack.Application;
using HealthTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;

public partial class HealthService
{
    // The only anonymous write use case; MVC adds anti-forgery and contact-specific rate limiting.
    public async Task<Outcome> SubmitContact(ContactInput input, CancellationToken cancellationToken = default)
    {
        if (Validate(input) is { } error) return Outcome.Fail(error);
        if (!MailAddress.TryCreate(input.Email.Trim(), out var address) || address.Address != input.Email.Trim())
            return Outcome.Fail("Enter one valid email address so the support team can reach you.", "Email");
        var message = new ContactMessage
        {
            Name = input.Name.Trim(), Email = address.Address, Subject = input.Subject.Trim(),
            Message = input.Message.Trim(), CreatedAtUtc = clock.UtcNow
        };
        if (message.Subject.Length < 3) return Outcome.Fail("Enter a subject of at least 3 characters.", nameof(input.Subject));
        if (message.Message.Length < 10) return Outcome.Fail("Enter a message of at least 10 characters.", nameof(input.Message));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await Lock(cancellationToken);
        db.ContactMessages.Add(message);
        // Notification titles contain no sender identity or submitted content.
        foreach (var id in await db.Profiles.Where(p => p.IsActive && p.Role == Roles.Administrator).Select(p => p.UserId).ToListAsync(cancellationToken))
            Notify(id, "New contact enquiry — open Contact inbox", message.Id);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Ok(message.Id);
    }

    public async Task<Page<ContactMessage>> ContactInbox(Actor actor, bool? handled, int page)
    {
        await Require(actor, Roles.Administrator);
        var query = db.ContactMessages.AsNoTracking();
        if (handled.HasValue) query = query.Where(m => m.HandledAtUtc.HasValue == handled.Value);
        return await Paged(query.OrderByDescending(m => m.CreatedAtUtc).ThenBy(m => m.Id), page);
    }

    public async Task<ContactMessage> ContactDetail(Actor actor, Guid id)
    {
        await Require(actor, Roles.Administrator);
        return await db.ContactMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id) ?? throw new MissingRecordException();
    }

    public Task<Outcome> HandleContact(Actor actor, Guid id, string version, bool handled) => Write(actor, async () =>
    {
        await Require(actor, Roles.Administrator);
        var message = await db.ContactMessages.FindAsync(id) ?? throw new MissingRecordException();
        Version(message, version);
        message.HandledAtUtc = handled ? clock.UtcNow : null;
        Audit(actor, handled ? "Contact enquiry handled" : "Contact enquiry reopened", id);
        return Outcome.Ok();
    });
}

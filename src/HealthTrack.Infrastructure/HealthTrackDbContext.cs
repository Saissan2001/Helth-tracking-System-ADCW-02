using HealthTrack.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Infrastructure;

public class ApplicationUser : IdentityUser;
public class HealthTrackDbContext(DbContextOptions<HealthTrackDbContext> options, SensitiveData sensitiveData) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<WellnessPreferences> WellnessPreferences => Set<WellnessPreferences>();
    public DbSet<MemberProfile> Profiles => Set<MemberProfile>();
    public DbSet<TrainerAssignment> Assignments => Set<TrainerAssignment>();
    public DbSet<HealthLog> Logs => Set<HealthLog>();
    public DbSet<HealthPlan> Plans => Set<HealthPlan>();
    public DbSet<PlanItem> PlanItems => Set<PlanItem>();
    public DbSet<HealthcareProfessional> Professionals => Set<HealthcareProfessional>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<TrainerFeedback> Feedback => Set<TrainerFeedback>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ActivityAudit> Audit => Set<ActivityAudit>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<WellnessPreferences>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<WellnessPreferences>().HasOne<ApplicationUser>().WithOne().HasForeignKey<WellnessPreferences>(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<HealthLog>().HasIndex(x => new { x.ClientId, x.IsFavorite, x.Kind });
        b.Entity<ContactMessage>().HasIndex(x => new { x.HandledAtUtc, x.CreatedAtUtc });
        b.Entity<ApplicationUser>().HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
        b.Entity<MemberProfile>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<MemberProfile>().HasOne<ApplicationUser>().WithOne().HasForeignKey<MemberProfile>(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<TrainerAssignment>().HasIndex(x => x.ClientId).IsUnique().HasFilter("[EndedAtUtc] IS NULL");
        b.Entity<TrainerAssignment>().HasIndex(x => new { x.TrainerId, x.EndedAtUtc });
        b.Entity<TrainerAssignment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<TrainerAssignment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.TrainerId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<HealthLog>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<HealthLog>().HasIndex(x => new { x.ClientId, x.Kind, x.OccurredAtUtc });
        b.Entity<HealthLog>().HasOne<PlanItem>().WithMany().HasForeignKey(x => x.PlanItemId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<HealthLog>().HasIndex(x => new { x.PlanItemId, x.ScheduledDate }).IsUnique().HasFilter("[PlanItemId] IS NOT NULL");
        b.Entity<HealthLog>().ToTable(t => t.HasCheckConstraint("CK_Log_PlanOccurrence", "([PlanItemId] IS NULL AND [ScheduledDate] IS NULL) OR ([PlanItemId] IS NOT NULL AND [ScheduledDate] IS NOT NULL)"));
        b.Entity<MemberProfile>().HasIndex(x => x.CreatedAtUtc);
        b.Entity<HealthPlan>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<HealthPlan>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.TrainerId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<HealthPlan>().HasIndex(x => new { x.ClientId, x.Status });
        b.Entity<HealthPlan>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Appointment>().ToTable(t => {
            t.HasCheckConstraint("CK_Appointment_Provider", "([TrainerId] IS NOT NULL AND [ProfessionalId] IS NULL) OR ([TrainerId] IS NULL AND [ProfessionalId] IS NOT NULL)");
            t.HasCheckConstraint("CK_Appointment_Interval", "[EndsAtUtc] > [StartsAtUtc]");
        });
        b.Entity<Appointment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<Appointment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.TrainerId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<Appointment>().HasOne<HealthcareProfessional>().WithMany().HasForeignKey(x => x.ProfessionalId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<Appointment>().HasIndex(x => new { x.TrainerId, x.StartsAtUtc });
        b.Entity<Appointment>().HasIndex(x => new { x.ProfessionalId, x.StartsAtUtc });
        b.Entity<Appointment>().HasIndex(x => new { x.ClientId, x.StartsAtUtc });
        b.Entity<TrainerFeedback>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<TrainerFeedback>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.TrainerId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<Notification>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<Notification>().HasIndex(x => x.DeduplicationKey).IsUnique();
        b.Entity<Notification>().HasIndex(x => new { x.RecipientId, x.ScheduledAtUtc });
        foreach (var type in b.Model.GetEntityTypes())
            foreach (var p in type.GetProperties().Where(x => x.ClrType == typeof(decimal) || x.ClrType == typeof(decimal?)))
            { p.SetPrecision(10); p.SetScale(2); }
        sensitiveData.Configure(b);
    }
}

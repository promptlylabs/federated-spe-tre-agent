using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Submission.Api.Repositories.DbContexts
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
          : base(options)
        {
           
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Project> Projects { get; set; }
        
        
        public DbSet<Tre> Tres { get; set; }

        public DbSet<FiveSafesTes.Core.Models.Submission> Submissions { get; set; }
        public DbSet<HistoricStatus> HistoricStatuses { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<SubmissionFile> SubmissionFiles { get; set; }

        public DbSet<ProjectTreDecision> ProjectTreDecisions { get; set; }

        public DbSet<MembershipTreDecision> MembershipTreDecisions { get; set; }

        public DbSet<UsedOnboardingJti> UsedOnboardingJtis { get; set; }

        public DbSet<FederatedTask> FederatedTasks { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<UsedOnboardingJti>(b =>
            {
                b.HasIndex(x => x.Jti).IsUnique();
            });
            modelBuilder.Entity<FederatedTask>(b =>
            {
                b.HasIndex(x => x.TaskId).IsUnique();
                b.HasIndex(x => new { x.TreId, x.Phase });
                b.Property(x => x.Phase).HasConversion<string>();
                // Every phase change is guarded by the current phase; the token makes a stale or concurrent change fail.
                b.Property(x => x.Phase).IsConcurrencyToken();
                b.HasMany(x => x.PhaseChanges).WithOne().HasForeignKey(x => x.FederatedTaskId).OnDelete(DeleteBehavior.Cascade);
                // Task history is the audit record of released results; deleting a TRE must not erase it.
                b.HasOne(x => x.Tre).WithMany().HasForeignKey(x => x.TreId).OnDelete(DeleteBehavior.Restrict);
                // The stored task holds the code a hospital runs: once saved, EF refuses to change it (ANZE-6045).
                b.Property(x => x.TesTaskJson).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            });
            modelBuilder.Entity<FederatedTaskPhaseChange>(b =>
            {
                b.ToTable("FederatedTaskPhaseChanges");
                b.Property(x => x.Phase).HasConversion<string>();
                b.HasIndex(x => x.FederatedTaskId);
            });
        }
    }
}

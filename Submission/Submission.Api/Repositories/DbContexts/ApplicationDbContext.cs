using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.EntityFrameworkCore;

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
                b.HasIndex(x => new { x.TreId, x.Step });
                b.Property(x => x.Step).HasConversion<string>();
                // Every step change is guarded by the current step; the token makes a stale or concurrent change fail.
                b.Property(x => x.Step).IsConcurrencyToken();
                b.HasMany(x => x.StepChanges).WithOne().HasForeignKey(x => x.FederatedTaskId).OnDelete(DeleteBehavior.Cascade);
                // Task history is the audit record of released results; deleting a TRE must not erase it.
                b.HasOne(x => x.Tre).WithMany().HasForeignKey(x => x.TreId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<FederatedTaskStepChange>(b =>
            {
                b.ToTable("FederatedTaskStepChanges");
                b.Property(x => x.Step).HasConversion<string>();
                b.HasIndex(x => x.FederatedTaskId);
            });
        }
    }
}

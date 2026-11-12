using FiveSafesTes.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Data_Egress_API.Repositories.DbContexts
{
    public class ApplicationDbContext : DbContext
    {
        //protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        //{
        //    optionsBuilder.UseNpgsql("DefaultConnection")
        //    //.UseUtcDateTime();
        //}
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
          : base(options)
        {

        }
        public DbSet<KeycloakCredentials> KeycloakCredentials { get; set; }
        public DbSet<EgressSubmission> EgressSubmissions { get; set; }
        public DbSet<EgressFile> EgressFiles { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // The federated study (and its data permit) is stored as one JSON column on the egress row.
            modelBuilder.Entity<EgressSubmission>().OwnsOne(x => x.Study, study =>
            {
                study.ToJson();
                study.OwnsOne(x => x.DataPermit);
            });
        }


    }
}

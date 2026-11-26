using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.EntityFrameworkCore;
using Submission.Api.Repositories.DbContexts;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedStudyVersionTests
    {
        [Theory]
        [InlineData(false, FederatedStudyVersionState.Ended, FederatedTaskPhase.Released, FederatedTaskPhase.Failed)]
        [InlineData(false, FederatedStudyVersionState.Open, FederatedTaskPhase.Released, FederatedTaskPhase.Running)]
        [InlineData(true, FederatedStudyVersionState.Canceling, FederatedTaskPhase.Canceled, FederatedTaskPhase.Canceling)]
        [InlineData(true, FederatedStudyVersionState.Ended, FederatedTaskPhase.Canceled, FederatedTaskPhase.Released)]
        public void State_FollowsPartsAndCancel(bool canceled, FederatedStudyVersionState expected, params FederatedTaskPhase[] phases)
        {
            var version = new FederatedStudyVersion
            {
                CanceledAt = canceled ? DateTime.UtcNow : null,
                Parts = phases.Select(p => new FederatedTask { Phase = p }).ToList()
            };

            Assert.Equal(expected, version.State);
        }

        [Fact]
        public void TesTaskJson_CannotChangeAfterSave()
        {
            using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var version = new FederatedStudyVersion { VersionId = Guid.NewGuid(), StudyId = "s", Version = 1, TesTaskJson = "{}" };
            db.FederatedStudyVersions.Add(version);
            db.SaveChanges();

            db.Entry(version).Property(x => x.TesTaskJson).CurrentValue = "{\"x\":1}";

            var error = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            Assert.Contains("TesTaskJson", error.Message);
        }
    }
}

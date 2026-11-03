using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.EntityFrameworkCore;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedTaskRenamesTests
    {
        private static ApplicationDbContext NewDb()
        {
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.Tres.Add(new Tre { Id = 1, Name = "tre-a", AdminUsername = "a", About = "", FormData = "" });
            db.Tres.Add(new Tre { Id = 2, Name = "tre-b", AdminUsername = "b", About = "", FormData = "" });
            db.SaveChanges();
            return db;
        }

        private static FederatedTask Seed(ApplicationDbContext db, int treId, string treName, TesState state)
        {
            var task = new FederatedTask
            {
                TaskId = Guid.NewGuid(), TreId = treId, TreName = treName, StudyId = "s", Operation = "count",
                Dataset = "d", TesTaskJson = "{}", State = state, SubmittedAt = DateTime.UtcNow
            };
            db.FederatedTasks.Add(task);
            db.SaveChanges();
            return task;
        }

        [Fact]
        public void FailQueuedUnderOtherNames_FailsOnlyThisTresQueuedTasksUnderAnotherName()
        {
            using var db = NewDb();
            var oldName = Seed(db, 1, "tre-a", TesState.QUEUEDEnum);
            var olderName = Seed(db, 1, "tre-x", TesState.QUEUEDEnum);
            var current = Seed(db, 1, "tre-a-2", TesState.QUEUEDEnum);
            var done = Seed(db, 1, "tre-a", TesState.COMPLETEEnum);
            var otherTre = Seed(db, 2, "tre-b", TesState.QUEUEDEnum);

            var failed = FederatedTaskRenames.FailQueuedUnderOtherNames(db, 1, "Tre-A-2 ");
            db.SaveChanges();

            Assert.Equal(2, failed);
            var rows = db.FederatedTasks.AsNoTracking().ToDictionary(x => x.TaskId);
            foreach (var renamed in new[] { oldName, olderName })
            {
                var row = rows[renamed.TaskId];
                Assert.Equal(TesState.SYSTEMERROREnum, row.State);
                Assert.Equal(FederatedTaskErrorCodes.TreRenamed, row.ErrorCode);
                Assert.Contains("tre-a-2", row.ResultErrorMessage);
                Assert.NotNull(row.CompletedAt);
            }
            Assert.Equal(TesState.QUEUEDEnum, rows[current.TaskId].State);
            Assert.Equal(TesState.COMPLETEEnum, rows[done.TaskId].State);
            Assert.Null(rows[done.TaskId].ErrorCode);
            Assert.Equal(TesState.QUEUEDEnum, rows[otherTre.TaskId].State);
        }

        [Fact]
        public void FailQueuedUnderOtherNames_CaseOnlyRename_FailsNothing()
        {
            using var db = NewDb();
            var task = Seed(db, 1, "tre-a", TesState.QUEUEDEnum);

            var failed = FederatedTaskRenames.FailQueuedUnderOtherNames(db, 1, "TRE-A");
            db.SaveChanges();

            Assert.Equal(0, failed);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }
    }
}

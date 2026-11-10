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
                Dataset = "d", TesTaskJson = "{}", State = state, SubmittedAt = DateTime.UtcNow,
                Step = state == TesState.COMPLETEEnum ? FederatedTaskStep.Released : FederatedTaskStep.Queued
            };
            db.FederatedTasks.Add(task);
            db.SaveChanges();
            return task;
        }

        [Fact]
        public void FailUnfinishedUnderOtherNames_FailsOnlyThisTresQueuedTasksUnderAnotherName()
        {
            using var db = NewDb();
            var oldName = Seed(db, 1, "tre-a", TesState.QUEUEDEnum);
            var olderName = Seed(db, 1, "tre-x", TesState.QUEUEDEnum);
            var current = Seed(db, 1, "tre-a-2", TesState.QUEUEDEnum);
            var done = Seed(db, 1, "tre-a", TesState.COMPLETEEnum);
            var otherTre = Seed(db, 2, "tre-b", TesState.QUEUEDEnum);

            var failed = FederatedTaskRenames.FailUnfinishedUnderOtherNames(db, 1, "Tre-A-2 ");
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

        [Theory]
        [InlineData(FederatedTaskStep.Running)]
        [InlineData(FederatedTaskStep.WaitingForOutputApproval)]
        public void Rename_fails_running_and_waiting_tasks_under_old_name(FederatedTaskStep step)
        {
            using var db = NewDb();
            var task = Seed(db, 1, "tre-a", TesState.QUEUEDEnum);
            task.MoveTo(FederatedTaskStep.Queued, DateTime.UtcNow);
            task.MoveTo(step, DateTime.UtcNow);
            db.SaveChanges();

            var failed = FederatedTaskRenames.FailUnfinishedUnderOtherNames(db, 1, "tre-a-2");
            db.SaveChanges();

            Assert.Equal(1, failed);
            var row = db.FederatedTasks.AsNoTracking().Include(x => x.StepChanges).Single();
            Assert.Equal(FederatedTaskStep.Failed, row.Step);
            Assert.Equal(TesState.SYSTEMERROREnum, row.State);
            Assert.Equal(FederatedTaskErrorCodes.TreRenamed, row.ErrorCode);
            var last = row.StepChanges.OrderBy(c => c.Id).Last();
            Assert.Equal(FederatedTaskStep.Failed, last.Step);
            Assert.Equal(FederatedTaskErrorCodes.TreRenamed, last.ErrorCode);
        }

        [Fact]
        public void FailUnfinishedUnderOtherNames_CaseOnlyRename_FailsNothing()
        {
            using var db = NewDb();
            var task = Seed(db, 1, "tre-a", TesState.QUEUEDEnum);

            var failed = FederatedTaskRenames.FailUnfinishedUnderOtherNames(db, 1, "TRE-A");
            db.SaveChanges();

            Assert.Equal(0, failed);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }
    
        private static ApplicationDbContext OpenDb(string name, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).AddInterceptors(interceptors).Options);

        private static (string Name, Guid TaskId) SeedRunningUnderOldName()
        {
            var name = Guid.NewGuid().ToString();
            using var db = OpenDb(name);
            db.Tres.Add(new Tre { Id = 1, Name = "tre-a", AdminUsername = "a", About = "", FormData = "" });
            var task = new FederatedTask
            {
                TaskId = Guid.NewGuid(), TreId = 1, TreName = "tre-a", StudyId = "s", Operation = "count",
                Dataset = "d", TesTaskJson = "{}", State = TesState.QUEUEDEnum, SubmittedAt = DateTime.UtcNow,
                Step = FederatedTaskStep.Queued
            };
            task.MoveTo(FederatedTaskStep.Running, DateTime.UtcNow);
            db.FederatedTasks.Add(task);
            db.SaveChanges();
            return (name, task.TaskId);
        }

        /// <summary>An Agent report in a second context, just before the rename's first save.</summary>
        private static BeforeFirstSave AgentReports(string dbName, Guid taskId, FederatedTaskStep step) => new(() =>
        {
            using var other = OpenDb(dbName);
            other.FederatedTasks.Single(x => x.TaskId == taskId).MoveTo(step, DateTime.UtcNow);
            other.SaveChanges();
        });

        private static void Rename(ApplicationDbContext db, string newName) => db.Tres.Single(x => x.Id == 1).Name = newName;

        [Fact]
        public async Task SaveWithSweep_report_racing_the_sweep_is_redone_and_the_task_still_fails()
        {
            var (name, taskId) = SeedRunningUnderOldName();
            await using var db = OpenDb(name, AgentReports(name, taskId, FederatedTaskStep.WaitingForOutputApproval));
            Rename(db, "tre-a-2");

            var failed = await FederatedTaskRenames.SaveWithSweepAsync(db, 1, "tre-a-2");

            Assert.Equal(1, failed);
            await using var check = OpenDb(name);
            Assert.Equal("tre-a-2", check.Tres.Single().Name);
            var row = check.FederatedTasks.Include(x => x.StepChanges).Single();
            Assert.Equal(FederatedTaskStep.Failed, row.Step);
            Assert.Equal(FederatedTaskErrorCodes.TreRenamed, row.ErrorCode);
            // The Agent's report is kept in the history, then the rename's failure. Only the order is checked: the
            // in-memory provider does not roll back the failed first save, Postgres does (one transaction per save).
            var steps = row.StepChanges.OrderBy(c => c.Id).Select(c => c.Step).ToList();
            Assert.Equal([FederatedTaskStep.Running, FederatedTaskStep.WaitingForOutputApproval], steps.Take(2));
            Assert.Equal(FederatedTaskStep.Failed, steps.Last());
        }

        [Fact]
        public async Task SaveWithSweep_task_released_during_the_sweep_stays_released()
        {
            var (name, taskId) = SeedRunningUnderOldName();
            await using var db = OpenDb(name, AgentReports(name, taskId, FederatedTaskStep.Released));
            Rename(db, "tre-a-2");

            var failed = await FederatedTaskRenames.SaveWithSweepAsync(db, 1, "tre-a-2");

            Assert.Equal(0, failed);
            await using var check = OpenDb(name);
            Assert.Equal("tre-a-2", check.Tres.Single().Name);
            var row = check.FederatedTasks.Single();
            Assert.Equal(FederatedTaskStep.Released, row.Step);
            Assert.Null(row.ErrorCode);
        }

        /// <summary>Runs a competing write once, just before the first save of the context it is attached to.</summary>
        private sealed class BeforeFirstSave(Action competingWrite) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
        {
            private bool _done;

            public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
                Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (!_done)
                {
                    _done = true;
                    competingWrite();
                }

                return ValueTask.FromResult(result);
            }
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Submission.Api.Migrations;
using Xunit;

namespace Submission.Api.Tests
{
    public class WaitingStateMigrationTests
    {
        [Fact]
        public void Up_moves_waiting_tasks_from_paused_to_running()
        {
            var sql = Assert.Single(new ReportWaitingAsRunning().UpOperations.OfType<SqlOperation>()).Sql;

            Assert.Contains("SET \"State\" = 4", sql);
            Assert.Contains("\"Step\" = 'WaitingForOutputApproval'", sql);
            Assert.Contains("\"State\" = 5", sql);
        }

        [Fact]
        public void Down_moves_them_back_to_paused()
        {
            var sql = Assert.Single(new ReportWaitingAsRunning().DownOperations.OfType<SqlOperation>()).Sql;

            Assert.Contains("SET \"State\" = 5", sql);
            Assert.Contains("\"Step\" = 'WaitingForOutputApproval'", sql);
        }
    }
}

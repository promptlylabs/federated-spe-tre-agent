using Agent.Api.FederatedTasks;
using FiveSafesTes.Core.Models.FederatedTasks;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class RunErrorMessagesTests
{
    [Theory]
    [InlineData("dataset_not_found", "the runner found no dataset file")]
    [InlineData("ambiguous_dataset", "the runner found more than one dataset file")]
    [InlineData("dataset_too_large", "the dataset is larger than the runner's 256 MiB limit")]
    [InlineData("invalid_dataset", "the runner could not read the dataset as CSV")]
    [InlineData("schema_not_found", "the runner found no schema.json for the dataset")]
    [InlineData("invalid_schema", "the dataset's schema.json is not valid")]
    [InlineData("schema_mismatch", "the dataset does not match its schema.json")]
    [InlineData("code_error", "the code could not be imported, has no site_analysis, or site_analysis raised an exception")]
    [InlineData("invalid_result", "site_analysis did not return a valid result")]
    [InlineData("result_too_large", "the result returned by site_analysis is larger than 61,440 bytes")]
    public void ForRunnerCode_ReturnsTheFixedSentence(string code, string expected) =>
        Assert.Equal(expected, RunErrorMessages.ForRunnerCode(code));

    [Fact]
    public void ForRunnerCode_UnknownCodeIsNull() => Assert.Null(RunErrorMessages.ForRunnerCode("bogus"));

    [Fact]
    public void FixedMessages()
    {
        Assert.Equal("the run produced a result file the TRE cannot accept", RunErrorMessages.InvalidResultFile);
        Assert.Equal("the run ended without a result", RunErrorMessages.RunFailed);
        Assert.Equal("the code ran out of memory", RunErrorMessages.OutOfMemory);
        Assert.Equal("the code ran longer than the TRE's limit of 30 minutes", RunErrorMessages.Timeout(30));
    }

    [Fact]
    public void NewCodes()
    {
        Assert.Equal("schema_not_found", FederatedTaskErrorCodes.SchemaNotFound);
        Assert.Equal("timeout", FederatedTaskErrorCodes.Timeout);
        Assert.Equal("out_of_memory", FederatedTaskErrorCodes.OutOfMemory);
        Assert.Equal("run_failed", FederatedTaskErrorCodes.RunFailed);
        Assert.Contains(FederatedTaskErrorCodes.SchemaNotFound, FederatedTaskErrorCodes.InputCheckCodes);
    }
}

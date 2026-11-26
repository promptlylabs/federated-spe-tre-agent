using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedInputChecksTests
{
    private const string RunnerImage = "python-runner:1";
    private const string Code = "def site_analysis(df):\n    return {}\n";

    /// <summary>A dataset with its data file and the schema.json its uploader puts next to it.</summary>
    private static FakeDatasetStore Store() =>
        StoreWithoutSchema().Add("dataset/cohort_2024/schema.json", "{}");

    private static FakeDatasetStore StoreWithoutSchema() =>
        new FakeDatasetStore().Add("dataset/cohort_2024/cohort.csv", "person_id,age\n1,34\n");

    private static TesTask Task(string dataset = "cohort_2024") =>
        FederatedTesProfile.BuildCodeTask(dataset, Code, "study-1", "tre-a");

    private static FederatedTaskSpec Spec(TesTask task) => FederatedTesProfile.Parse(task).Spec!;

    private static FederatedInputChecks Checks(IDatasetReader reader, params string[] approved) =>
        new(reader, new FederatedTaskSettings { ApprovedImages = approved });

    private static FederatedInputChecks Checks(FakeDatasetStore store, params string[] approved) =>
        Checks(new DatasetReader(store, new FederatedTaskSettings()), approved);

    private static Task<InputCheckOutcome> Run(FederatedInputChecks checks, TesTask task) =>
        checks.RunAsync(task, Spec(task), CancellationToken.None);

    private sealed class ThrowingResolveReader : IDatasetReader
    {
        public Task<DatasetResolveResult> ResolveAsync(string datasetName, CancellationToken ct) =>
            throw new DatasetStoreException("bucket down", new IOException("down"));
    }

    [Fact]
    public async Task Approved_image_and_existing_dataset_passes_with_the_file_key()
    {
        var outcome = await Run(Checks(Store(), RunnerImage), Task());

        Assert.Equal(InputCheckKind.Passed, outcome.Kind);
        Assert.Equal("dataset/cohort_2024/cohort.csv", outcome.FileKey);
    }

    [Fact]
    public async Task Unapproved_image_is_rejected_without_listing_the_bucket()
    {
        var store = Store();

        var outcome = await Run(Checks(store, "other:1"), Task());

        Assert.Equal(InputCheckKind.Rejected, outcome.Kind);
        Assert.Equal("image_not_approved", outcome.ErrorCode);
        Assert.Equal("image 'python-runner:1' is not approved at this TRE", outcome.ErrorMessage);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task Image_match_is_case_sensitive()
    {
        var outcome = await Run(Checks(Store(), "Python-Runner:1"), Task());

        Assert.Equal(InputCheckKind.Rejected, outcome.Kind);
        Assert.Equal("image_not_approved", outcome.ErrorCode);
    }

    [Fact]
    public async Task Missing_dataset_is_rejected_with_dataset_not_found()
    {
        var outcome = await Run(Checks(Store(), RunnerImage), Task("nope"));

        Assert.Equal(InputCheckKind.Rejected, outcome.Kind);
        Assert.Equal("dataset_not_found", outcome.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(outcome.ErrorMessage));
    }

    [Fact]
    public async Task Empty_approved_list_holds_without_listing_the_bucket()
    {
        var store = Store();

        var outcome = await Run(Checks(store), Task());

        Assert.Equal(InputCheckKind.Held, outcome.Kind);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task Store_failure_while_resolving_throws()
    {
        var checks = Checks(new ThrowingResolveReader(), RunnerImage);

        await Assert.ThrowsAsync<DatasetStoreException>(() => Run(checks, Task()));
    }

    [Fact]
    public async Task Without_schema_is_rejected_with_schema_not_found()
    {
        var outcome = await Run(Checks(StoreWithoutSchema(), RunnerImage), Task());

        Assert.Equal(InputCheckKind.Rejected, outcome.Kind);
        Assert.Equal("schema_not_found", outcome.ErrorCode);
        Assert.Equal("no schema.json found for 'cohort_2024'", outcome.ErrorMessage);
    }
}

using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedInputChecksTests
{
    private static FakeDatasetStore Store() =>
        new FakeDatasetStore().Add("dataset/cohort_2024/cohort.csv", "person_id,age\n1,34\n");

    private static TesTask Task(string dataset = "cohort_2024") =>
        FederatedTesProfile.Build(new FederatedTaskSpec("count", dataset, null, [], "study-1", "tre-a"));

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

        public Task<DatasetReadResult> ReadAsync(string fileKey, CancellationToken ct) =>
            throw new InvalidOperationException("reader must not be called");
    }

    [Fact]
    public async Task Approved_image_and_existing_dataset_passes_with_the_file_key()
    {
        var outcome = await Run(Checks(Store(), FederatedTesProfile.ExecutorImage), Task());

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
        Assert.Equal("image '5s-tes/federated-ops:1' is not approved at this TRE", outcome.ErrorMessage);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task Image_match_is_case_sensitive()
    {
        var outcome = await Run(Checks(Store(), "5S-TES/federated-ops:1"), Task());

        Assert.Equal(InputCheckKind.Rejected, outcome.Kind);
        Assert.Equal("image_not_approved", outcome.ErrorCode);
    }

    [Fact]
    public async Task Missing_dataset_is_rejected_with_dataset_not_found()
    {
        var outcome = await Run(Checks(Store(), FederatedTesProfile.ExecutorImage), Task("nope"));

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
        var checks = Checks(new ThrowingResolveReader(), FederatedTesProfile.ExecutorImage);

        await Assert.ThrowsAsync<DatasetStoreException>(() => Run(checks, Task()));
    }
}

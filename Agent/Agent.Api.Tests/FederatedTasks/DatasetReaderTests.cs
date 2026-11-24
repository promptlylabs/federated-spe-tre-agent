using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class DatasetReaderTests
{
    private static DatasetReader Reader(FakeDatasetStore store) => new(store, new FederatedTaskSettings());

    private static Task<DatasetResolveResult> Resolve(IDatasetReader reader, string name) =>
        reader.ResolveAsync(name, CancellationToken.None);

    [Fact]
    public async Task Resolve_ValidCsv_ReturnsFileKey()
    {
        var store = new FakeDatasetStore().Add("dataset/cohort_2024/cohort.csv", "person_id,age,sex\n1,34,F\n2,51,M\n");

        var result = await Resolve(Reader(store), "cohort_2024");

        Assert.True(result.IsSuccess);
        Assert.Equal("dataset/cohort_2024/cohort.csv", result.FileKey);
    }

    [Fact]
    public async Task Resolve_FindsOnlyCsv_WhateverTheUploadExtensions()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort_2024/cohort.csv", "x\n1\n")
            .Add("dataset/cohort_2024/cohort.parquet", "not csv");
        var reader = new DatasetReader(store, new FederatedTaskSettings { AllowedExtensions = ["csv", "parquet"] });

        var result = await Resolve(reader, "cohort_2024");

        Assert.True(result.IsSuccess);
        Assert.Equal("dataset/cohort_2024/cohort.csv", result.FileKey);
    }

    [Fact]
    public async Task Resolve_NoFile_ReturnsDatasetNotFound()
    {
        var result = await Resolve(Reader(new FakeDatasetStore()), "cohort_2024");

        Assert.Equal("dataset_not_found", result.ErrorCode);
    }

    [Theory]
    [InlineData("dataset/cohort_2024/readme.txt")]
    [InlineData("dataset/cohort_2024/cohort.CSV")]
    public async Task Resolve_NoMatchingExtension_ReturnsDatasetNotFound(string key)
    {
        var store = new FakeDatasetStore().Add(key, "a,b\n1,2\n");

        var result = await Resolve(Reader(store), "cohort_2024");

        Assert.Equal("dataset_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Resolve_TwoCsvFiles_ReturnsAmbiguousDataset()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort_2024/a.csv", "a\n1\n")
            .Add("dataset/cohort_2024/b.csv", "a\n2\n");

        var result = await Resolve(Reader(store), "cohort_2024");

        Assert.Equal("ambiguous_dataset", result.ErrorCode);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("Cohort")]
    [InlineData("")]
    public async Task Resolve_InvalidName_ReturnsInvalidDatasetName_AndDoesNotList(string name)
    {
        var store = new FakeDatasetStore();

        var result = await Resolve(Reader(store), name);

        Assert.Equal("invalid_dataset_name", result.ErrorCode);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task Resolve_DoesNotMatchSiblingWithSamePrefix()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort/a.csv", "x\n1\n")
            .Add("dataset/cohort_2024/b.csv", "y\n2\n");

        var result = await Resolve(Reader(store), "cohort");

        Assert.True(result.IsSuccess);
        Assert.Equal("dataset/cohort/a.csv", result.FileKey);
    }

    [Fact]
    public async Task Resolve_IgnoresSubfolders()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort_2024/archive/old.csv", "z\n9\n")
            .Add("dataset/cohort_2024/cohort.csv", "x\n1\n");

        var result = await Resolve(Reader(store), "cohort_2024");

        Assert.True(result.IsSuccess);
        Assert.Equal("dataset/cohort_2024/cohort.csv", result.FileKey);
    }

    [Fact]
    public async Task Resolve_OnlySubfolderFile_ReturnsDatasetNotFound()
    {
        var store = new FakeDatasetStore().Add("dataset/x/sub/a.csv", "a\n1\n");

        var result = await Resolve(Reader(store), "x");

        Assert.Equal("dataset_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Resolve_store_failure_throws_DatasetStoreException()
    {
        var store = new FakeDatasetStore { ThrowOnList = true };

        var ex = await Assert.ThrowsAsync<DatasetStoreException>(() => Reader(store).ResolveAsync("cohort_2024", CancellationToken.None));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task Resolve_ReturnsSchemaKey_WhenPresent()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/x/x.csv", "a\n1\n")
            .Add("dataset/x/schema.json", "{}");

        var resolved = await Reader(store).ResolveAsync("x", CancellationToken.None);

        Assert.True(resolved.IsSuccess);
        Assert.Equal("dataset/x/schema.json", resolved.SchemaKey);
    }

    [Fact]
    public async Task Resolve_SchemaKeyNull_WhenAbsent()
    {
        var store = new FakeDatasetStore().Add("dataset/x/x.csv", "a\n1\n");

        var resolved = await Reader(store).ResolveAsync("x", CancellationToken.None);

        Assert.True(resolved.IsSuccess);
        Assert.Null(resolved.SchemaKey);
    }

    [Fact]
    public async Task Resolve_IgnoresNestedSchemaJson()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/x/x.csv", "a\n1\n")
            .Add("dataset/x/sub/schema.json", "{}");

        var resolved = await Reader(store).ResolveAsync("x", CancellationToken.None);

        Assert.True(resolved.IsSuccess);
        Assert.Null(resolved.SchemaKey);
    }
}

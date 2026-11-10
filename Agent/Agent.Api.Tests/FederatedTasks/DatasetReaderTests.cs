using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using System.Security.Cryptography;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class DatasetReaderTests
{
    private static DatasetReader Reader(FakeDatasetStore store) => new(store, new FederatedTaskSettings());

    /// <summary>Finds then reads, the way the Agent does; a finding error is returned as a read error.</summary>
    private static async Task<DatasetReadResult> ReadByName(IDatasetReader reader, string name)
    {
        var resolved = await reader.ResolveAsync(name, CancellationToken.None);
        return resolved.IsSuccess
            ? await reader.ReadAsync(resolved.FileKey!, CancellationToken.None)
            : DatasetReadResult.Fail(resolved.ErrorCode!, resolved.ErrorMessage!);
    }

    [Fact]
    public async Task Read_ValidCsv_ReturnsTable()
    {
        var store = new FakeDatasetStore().Add("dataset/cohort_2024/cohort.csv", "person_id,age,sex\n1,34,F\n2,51,M\n");

        var result = await ReadByName(Reader(store), "cohort_2024");

        Assert.True(result.IsSuccess);
        Assert.Equal(["person_id", "age", "sex"], result.Table!.Columns);
        Assert.Equal(2, result.Table.Rows.Count);
        Assert.Equal(["2", "51", "M"], result.Table.Rows[1]);
    }

    [Fact]
    public async Task Read_ValidCsv_ReturnsSha256OfFileBytes()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("person_id,age\n1,34\n2,51\n");
        var store = new FakeDatasetStore().Add("dataset/cohort_2024/cohort.csv", bytes);

        var result = await ReadByName(Reader(store), "cohort_2024");

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
    }

    [Fact]
    public async Task Read_MalformedCsv_ReturnsInvalidDatasetWithSha256()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("id,name\n1,O\"Brien\n");
        var store = new FakeDatasetStore().Add("dataset/cohort_2024/cohort.csv", bytes);

        var result = await ReadByName(Reader(store), "cohort_2024");

        Assert.Equal("invalid_dataset", result.ErrorCode);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
    }

    [Fact]
    public async Task Read_ReadsOnlyCsv_WhateverTheUploadExtensions()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort_2024/cohort.csv", "x\n1\n")
            .Add("dataset/cohort_2024/cohort.parquet", "not csv");
        var reader = new DatasetReader(store, new FederatedTaskSettings { AllowedExtensions = ["csv", "parquet"] });

        var result = await ReadByName(reader, "cohort_2024");

        Assert.True(result.IsSuccess);
        Assert.Equal(["x"], result.Table!.Columns);
    }

    [Fact]
    public async Task Read_NoFile_ReturnsDatasetNotFound()
    {
        var result = await ReadByName(Reader(new FakeDatasetStore()), "cohort_2024");

        Assert.Equal("dataset_not_found", result.ErrorCode);
    }

    [Theory]
    [InlineData("dataset/cohort_2024/readme.txt")]
    [InlineData("dataset/cohort_2024/cohort.CSV")]
    public async Task Read_NoMatchingExtension_ReturnsDatasetNotFound(string key)
    {
        var store = new FakeDatasetStore().Add(key, "a,b\n1,2\n");

        var result = await ReadByName(Reader(store), "cohort_2024");

        Assert.Equal("dataset_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Read_TwoCsvFiles_ReturnsAmbiguousDataset()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort_2024/a.csv", "a\n1\n")
            .Add("dataset/cohort_2024/b.csv", "a\n2\n");

        var result = await ReadByName(Reader(store), "cohort_2024");

        Assert.Equal("ambiguous_dataset", result.ErrorCode);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("Cohort")]
    [InlineData("")]
    public async Task Read_InvalidName_ReturnsInvalidDatasetName_AndDoesNotList(string name)
    {
        var store = new FakeDatasetStore();

        var result = await ReadByName(Reader(store), name);

        Assert.Equal("invalid_dataset_name", result.ErrorCode);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task Read_DoesNotMatchSiblingWithSamePrefix()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort/a.csv", "x\n1\n")
            .Add("dataset/cohort_2024/b.csv", "y\n2\n");

        var result = await ReadByName(Reader(store), "cohort");

        Assert.True(result.IsSuccess);
        Assert.Equal(["x"], result.Table!.Columns);
    }

    [Fact]
    public async Task Read_IgnoresSubfolders()
    {
        var store = new FakeDatasetStore()
            .Add("dataset/cohort_2024/archive/old.csv", "z\n9\n")
            .Add("dataset/cohort_2024/cohort.csv", "x\n1\n");

        var result = await ReadByName(Reader(store), "cohort_2024");

        Assert.True(result.IsSuccess);
        Assert.Equal(["x"], result.Table!.Columns);
    }

    [Fact]
    public async Task Read_OnlySubfolderFile_ReturnsDatasetNotFound()
    {
        var store = new FakeDatasetStore().Add("dataset/x/sub/a.csv", "a\n1\n");

        var result = await ReadByName(Reader(store), "x");

        Assert.Equal("dataset_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Read_store_failure_throws_DatasetStoreException()
    {
        var store = new FakeDatasetStore().Add("dataset/x/a.csv", "a\n1\n");
        store.ThrowOnOpenKey = "dataset/x/a.csv";

        var ex = await Assert.ThrowsAsync<DatasetStoreException>(() => Reader(store).ReadAsync("dataset/x/a.csv", CancellationToken.None));
        Assert.IsType<IOException>(ex.InnerException);
    }
}

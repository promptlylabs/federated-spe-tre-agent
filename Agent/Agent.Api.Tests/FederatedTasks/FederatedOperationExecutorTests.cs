using Agent.Api.FederatedTasks;
using FiveSafesTes.Core.Models.FederatedTasks;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedOperationExecutorTests
{
    private static DatasetTable Table(params string?[][] rows) =>
        new(["person_id", "age", "sex"], rows);

    private static DatasetTable Cohort() =>
        Table(["1", "34", "F"], ["2", "51", "M"], ["3", "47", "F"]);

    private static FederatedTaskSpec Spec(string op, string? column = null, params TaskFilter[] filters) =>
        new(op, "cohort_2024", column, filters, "s1", "tre-a");

    [Fact]
    public void Count_NoFilters()
    {
        var result = FederatedOperationExecutor.Execute(Spec("count"), Cohort());

        Assert.True(result.IsSuccess);
        Assert.Equal(3m, result.Value);
        Assert.Equal(3, result.RowCount);
    }

    [Fact]
    public void Mean_WithEqFilter()
    {
        var result = FederatedOperationExecutor.Execute(
            Spec("mean", "age", new TaskFilter("sex", "eq", "F")), Cohort());

        Assert.True(result.IsSuccess);
        Assert.Equal(40.5m, result.Value);
        Assert.Equal(2, result.RowCount);
    }

    [Theory]
    [InlineData("gte", "47", 2)]
    [InlineData("gt", "47", 1)]
    [InlineData("lt", "40", 1)]
    [InlineData("lte", "34", 1)]
    public void Filters_NumericComparison(string op, string value, int expected)
    {
        var result = FederatedOperationExecutor.Execute(
            Spec("count", null, new TaskFilter("age", op, value)), Cohort());

        Assert.Equal(expected, result.RowCount);
        Assert.Equal((decimal)expected, result.Value);
    }

    [Fact]
    public void Mean_EmptyAfterFilter()
    {
        var result = FederatedOperationExecutor.Execute(
            Spec("mean", "age", new TaskFilter("sex", "eq", "X")), Cohort());

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(0, result.RowCount);
    }

    [Fact]
    public void MissingColumn()
    {
        var mean = FederatedOperationExecutor.Execute(Spec("mean", "height"), Cohort());
        var filter = FederatedOperationExecutor.Execute(
            Spec("count", null, new TaskFilter("height", "eq", "1")), Cohort());

        Assert.Equal(FederatedTaskErrorCodes.ColumnNotFound, mean.ErrorCode);
        Assert.Equal(FederatedTaskErrorCodes.ColumnNotFound, filter.ErrorCode);
    }

    [Fact]
    public void Mean_NonNumericValue()
    {
        var result = FederatedOperationExecutor.Execute(Spec("mean", "sex"), Cohort());

        Assert.Equal(FederatedTaskErrorCodes.NonNumericValue, result.ErrorCode);
    }

    [Fact]
    public void Count_WithColumn_ExcludesEmpty()
    {
        var table = Table(["1", "34", "F"], ["2", "", "M"], ["3", null, "F"], ["4", "47", "F"]);

        var result = FederatedOperationExecutor.Execute(Spec("count", "age"), table);

        Assert.Equal(2, result.RowCount);
        Assert.Equal(2m, result.Value);
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("5-")]
    [InlineData("1e3")]
    public void Mean_NonStrictNumber_IsNonNumeric(string value)
    {
        var table = Table(["1", "34", "F"], ["2", value, "M"]);

        var result = FederatedOperationExecutor.Execute(Spec("mean", "age"), table);

        Assert.Equal(FederatedTaskErrorCodes.NonNumericValue, result.ErrorCode);
    }

    [Fact]
    public void Filter_ThousandsSeparatorValue_IsNonNumeric()
    {
        var table = Table(["1", "1,000", "F"], ["2", "7", "M"]);

        var cellSide = FederatedOperationExecutor.Execute(
            Spec("count", null, new TaskFilter("age", "gt", "5")), table);
        var operandSide = FederatedOperationExecutor.Execute(
            Spec("count", null, new TaskFilter("age", "gt", "1,000")), Cohort());

        Assert.Equal(1, cellSide.RowCount);
        Assert.Equal(0, operandSide.RowCount);
    }

    [Fact]
    public void Mean_NegativeDecimal_Parses()
    {
        var table = Table(["1", "-2.5", "F"], ["2", " 1.5 ", "M"]);

        var result = FederatedOperationExecutor.Execute(Spec("mean", "age"), table);

        Assert.True(result.IsSuccess);
        Assert.Equal(-0.5m, result.Value);
    }

    [Fact]
    public void WhitespaceOnlyCells_TreatedAsEmpty()
    {
        var table = Table(["1", "34", "F"], ["2", "  ", "M"], ["3", "\t", "F"]);

        var count = FederatedOperationExecutor.Execute(Spec("count", "age"), table);
        var mean = FederatedOperationExecutor.Execute(Spec("mean", "age"), table);

        Assert.Equal(1, count.RowCount);
        Assert.True(mean.IsSuccess);
        Assert.Equal(34m, mean.Value);
        Assert.Equal(1, mean.RowCount);
    }
}

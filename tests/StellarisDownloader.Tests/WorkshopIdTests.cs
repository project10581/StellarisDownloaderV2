using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class WorkshopIdTests
{
    [Theory]
    [InlineData("123", true)]
    [InlineData("00123", true)]
    [InlineData("0", true)]
    [InlineData("١٢٣", false)]
    [InlineData("１２３", false)]
    [InlineData("12A", false)]
    [InlineData("-123", false)]
    [InlineData("123 ", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void OnlyNonEmptyAsciiDigitsAreValid(string? value, bool expected) =>
        Assert.Equal(expected, WorkshopId.IsValid(value));
}

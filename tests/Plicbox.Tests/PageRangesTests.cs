using Plicbox.Core;

namespace Plicbox.Tests;

public sealed class PageRangesTests
{
    [Theory]
    [InlineData("1-3, 5", 10, "1,2,3,5")]
    [InlineData("5, 1-2; 2", 10, "1,2,5")]   // any order, overlaps, ';' too
    [InlineData("8-", 10, "8,9,10")]        // open end: to the last page
    [InlineData("3–4", 10, "3,4")]          // en dash, as autocorrect types it
    [InlineData("2-99", 4, "2,3,4")]        // a range may run past the end
    [InlineData(" 7 ", 7, "7")]
    public void Reads_the_pages_typed(string text, int count, string expected) =>
        Assert.Equal(expected, string.Join(",", PageRanges.Parse(text, count)!));

    [Theory]
    [InlineData("", 5)]
    [InlineData("0", 5)]
    [InlineData("6", 5)]       // starts after the last page
    [InlineData("4-2", 5)]     // backwards
    [InlineData("2-x", 5)]
    [InlineData("1,,a", 5)]
    public void Rejects_what_isnt_a_page_list(string text, int count) => Assert.Null(PageRanges.Parse(text, count));

    [Fact]
    public void Writes_pages_as_runs_for_the_printer()
    {
        Assert.Equal("1-3,5,7-8", PageRanges.Format([1, 2, 3, 5, 7, 8]));
        Assert.Equal("4", PageRanges.Format([4]));
    }
}

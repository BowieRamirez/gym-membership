using GymMembership.Core.Services;
using Xunit;

// Owner, 2026-11-15: every table shows 10 rows at a time with Previous and Next.
public class TablePagingTests
{
    [Fact]
    public void Thirty_four_rows_make_four_pages_of_ten_and_the_last_has_four()
    {
        var first = PageWindow.Of(34, 0);
        Assert.Equal((0, 4, 0, "1–10 of 34", false, true), (first.Page, first.Pages, first.Skip, first.Text, first.HasPrevious, first.HasNext));
        var last = PageWindow.Of(34, 3);
        Assert.Equal((30, "31–34 of 34", true, false), (last.Skip, last.Text, last.HasPrevious, last.HasNext));
    }

    [Fact]
    public void Ten_or_fewer_rows_need_no_pager()
    {
        Assert.False(PageWindow.Of(10, 0).Needed);
        Assert.False(PageWindow.Of(0, 0).Needed);
        Assert.True(PageWindow.Of(11, 0).Needed);
    }

    [Fact]
    public void A_page_past_the_end_after_rows_go_away_moves_back_to_the_last_page()
    {
        Assert.Equal(1, PageWindow.Of(15, 3).Page);   // was on page 4 of a longer list; 15 rows have 2 pages
        Assert.Equal(0, PageWindow.Of(0, 2).Page);
        Assert.Equal(0, PageWindow.Of(15, -1).Page);
    }

    [Fact]
    public void The_archive_reads_ten_at_a_time() => Assert.Equal(10, ArchiveService.PageSize);
}

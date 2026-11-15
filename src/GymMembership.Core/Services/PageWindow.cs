namespace GymMembership.Core.Services;

/// <summary>Owner, 2026-11-15: every table shows <see cref="Size"/> rows at a time. Which rows a page holds and what the pager
/// says; a page past the end (rows went away on a refresh) moves back to the last page.</summary>
public readonly record struct PageWindow(int Page, int Pages, int Skip, int Count)
{
    public const int Size = 10;

    public static PageWindow Of(int count, int page)
    {
        var pages = Math.Max(1, (count + Size - 1) / Size);
        page = Math.Clamp(page, 0, pages - 1);
        return new(page, pages, page * Size, count);
    }

    public bool Needed => Count > Size;
    public bool HasPrevious => Page > 0;
    public bool HasNext => Page < Pages - 1;
    public string Text => $"{Skip + 1}–{Math.Min(Skip + Size, Count)} of {Count}";
}

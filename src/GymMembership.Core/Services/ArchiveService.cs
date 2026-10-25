using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public sealed record ArchiveFilter(string? Type = null, string? Status = null, DateOnly? From = null, DateOnly? To = null, string? Reason = null);
public sealed record ArchivePage(IReadOnlyList<ArchiveRecord> Rows, bool HasMore);

public interface IArchiveService
{
    Task<AppResult<ArchivePage>> ListAsync(ArchiveFilter filter, int page);
    Task<AppResult<bool>> RestoreAsync(ArchiveRecord record);
}

public sealed class ArchiveService(ISupabaseGateway gw) : IArchiveService
{
    public const int PageSize = 30;

    public Task<AppResult<ArchivePage>> ListAsync(ArchiveFilter filter, int page)
    {
        if (page < 0 || page > int.MaxValue / PageSize || filter.To < filter.From)
            return Task.FromResult(AppResult<ArchivePage>.Fail(AppErrorKind.Validation, "bad_archive_filter"));
        return Safe.RunAsync(async () =>
        {
            var rows = await gw.RpcAsync<List<ArchiveRecord>>("archive_records", new()
            {
                ["p_type"] = filter.Type ?? "", ["p_status"] = filter.Status ?? "",
                ["p_from"] = filter.From?.ToString("yyyy-MM-dd")!, ["p_to"] = filter.To?.ToString("yyyy-MM-dd")!,
                ["p_reason"] = filter.Reason?.Trim() ?? "", ["p_offset"] = page * PageSize, ["p_limit"] = PageSize + 1
            }) ?? [];
            return new ArchivePage(rows.Take(PageSize).ToList(), rows.Count > PageSize);
        });
    }

    public Task<AppResult<bool>> RestoreAsync(ArchiveRecord record) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("restore_archived_record", new() { ["p_type"] = record.Type, ["p_id"] = record.RecordId });
        return true;
    });
}

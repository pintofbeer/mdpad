namespace MdPad.Models;

public sealed record Note(
    string Id,
    string Title,
    string Content,
    DateOnly NoteDate,
    bool IsFile,
    string? FilePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FileSavedAt,
    IReadOnlyList<string> Tags);

public sealed record NoteSummary(
    string Id,
    string Title,
    DateOnly NoteDate,
    bool IsFile,
    string? FilePath,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Tags,
    string Preview);

public sealed record SidebarModel(
    DateOnly Today,
    IReadOnlyList<DateBucket> Dates,
    IReadOnlyList<TagBucket> Tags,
    IReadOnlyList<NoteSummary> Recent);

public sealed record DateBucket(DateOnly Date, int Count, IReadOnlyList<NoteSummary> Notes);

public sealed record TagBucket(string Name, int Count);

using System.Text.Json;

namespace MdPad;

public sealed record BridgeMessage(string Id, string Type, JsonElement? Payload);

public sealed record BridgeResponse(string Id, bool Ok, object? Data = null, string? Error = null);

public sealed record SaveContentRequest(string Id, string Content);

public sealed record MetadataRequest(string Id, string Title, string NoteDate, IReadOnlyList<string> Tags);

public sealed record CreateNoteRequest(string NoteDate, string Title);

public sealed record IdRequest(string Id);

public sealed record SearchRequest(string Query);

public sealed record DateRequest(string Date);

public sealed record TagRequest(string Tag);

public sealed record ThemeRequest(string Theme);

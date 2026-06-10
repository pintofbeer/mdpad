using Microsoft.Data.Sqlite;
using MdPad.Models;

namespace MdPad.Data;

public sealed class NoteRepository
{
    private readonly string _connectionString;

    public NoteRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var connection = OpenConnection();
        await ExecuteAsync(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                note_date TEXT NOT NULL,
                is_file INTEGER NOT NULL DEFAULT 0,
                file_path TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                file_saved_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE IF NOT EXISTS note_tags (
                note_id TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (note_id, tag_id)
            );
            """);

        await EnsureScratchAsync(DateOnly.FromDateTime(DateTime.Today));
    }

    public async Task<SidebarModel> GetSidebarAsync()
    {
        var notes = await GetSummariesAsync();
        var dates = notes
            .GroupBy(note => note.NoteDate)
            .OrderByDescending(group => group.Key)
            .Select(group => new DateBucket(group.Key, group.Count(), group.OrderByDescending(x => x.UpdatedAt).ToList()))
            .ToList();

        var tags = notes
            .SelectMany(note => note.Tags)
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .Select(group => new TagBucket(group.Key, group.Count()))
            .ToList();

        return new SidebarModel(
            DateOnly.FromDateTime(DateTime.Today),
            dates,
            tags,
            notes.OrderByDescending(note => note.UpdatedAt).Take(20).ToList());
    }

    public async Task<IReadOnlyList<NoteSummary>> SearchAsync(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return Array.Empty<NoteSummary>();
        }

        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.title, n.content, n.note_date, n.is_file, n.file_path, n.updated_at,
                   GROUP_CONCAT(t.name, '|') AS tags
            FROM notes n
            LEFT JOIN note_tags nt ON nt.note_id = n.id
            LEFT JOIN tags t ON t.id = nt.tag_id
            WHERE n.title LIKE $query OR n.content LIKE $query OR t.name LIKE $query OR n.note_date LIKE $query
            GROUP BY n.id
            ORDER BY n.updated_at DESC
            LIMIT 100;
            """;
        command.Parameters.AddWithValue("$query", $"%{query}%");
        return await ReadSummariesAsync(command);
    }

    public async Task<IReadOnlyList<NoteSummary>> GetByDateAsync(DateOnly date)
    {
        var summaries = await GetSummariesAsync();
        return summaries.Where(note => note.NoteDate == date).OrderBy(note => note.Title).ToList();
    }

    public async Task<IReadOnlyList<NoteSummary>> GetByTagAsync(string tag)
    {
        var summaries = await GetSummariesAsync();
        return summaries.Where(note => note.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public async Task<Note> EnsureScratchAsync(DateOnly date)
    {
        await using var connection = OpenConnection();
        var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT id FROM notes
            WHERE note_date = $date AND title = 'scratch' AND is_file = 0
            LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$date", ToDbDate(date));
        var id = (string?)await existing.ExecuteScalarAsync();
        if (id is not null)
        {
            return await GetNoteAsync(id) ?? throw new InvalidOperationException("Scratch note vanished after lookup.");
        }

        return await CreateNoteAsync(date, "scratch", "", false, null);
    }

    public async Task<Note> CreateNoteAsync(DateOnly date, string title, string content, bool isFile, string? filePath)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note(Guid.NewGuid().ToString("N"), CleanTitle(title), content, date, isFile, filePath, now, now, null, Array.Empty<string>());
        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, title, content, note_date, is_file, file_path, created_at, updated_at, file_saved_at)
            VALUES ($id, $title, $content, $date, $isFile, $filePath, $createdAt, $updatedAt, $fileSavedAt);
            """;
        BindNote(command, note);
        await command.ExecuteNonQueryAsync();
        return note;
    }

    public async Task<Note?> GetNoteAsync(string id)
    {
        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.title, n.content, n.note_date, n.is_file, n.file_path, n.created_at, n.updated_at, n.file_saved_at,
                   GROUP_CONCAT(t.name, '|') AS tags
            FROM notes n
            LEFT JOIN note_tags nt ON nt.note_id = n.id
            LEFT JOIN tags t ON t.id = nt.tag_id
            WHERE n.id = $id
            GROUP BY n.id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadNote(reader) : null;
    }

    public async Task<Note> UpdateContentAsync(string id, string content)
    {
        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes
            SET content = $content, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return await GetNoteAsync(id) ?? throw new InvalidOperationException($"Note {id} does not exist.");
    }

    public async Task<Note> UpdateMetadataAsync(string id, string title, DateOnly noteDate, IReadOnlyList<string> tags)
    {
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE notes
            SET title = $title, note_date = $date, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", CleanTitle(title));
        command.Parameters.AddWithValue("$date", ToDbDate(noteDate));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();

        var clear = connection.CreateCommand();
        clear.Transaction = (SqliteTransaction)transaction;
        clear.CommandText = "DELETE FROM note_tags WHERE note_id = $id;";
        clear.Parameters.AddWithValue("$id", id);
        await clear.ExecuteNonQueryAsync();

        foreach (var tag in tags.Select(CleanTag).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await UpsertTagAsync(connection, (SqliteTransaction)transaction, id, tag);
        }

        await transaction.CommitAsync();
        return await GetNoteAsync(id) ?? throw new InvalidOperationException($"Note {id} does not exist.");
    }

    public async Task<Note> MarkFileSavedAsync(string id)
    {
        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes
            SET file_saved_at = $savedAt, updated_at = $savedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$savedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return await GetNoteAsync(id) ?? throw new InvalidOperationException($"Note {id} does not exist.");
    }

    public async Task<Note> SaveAsAsync(string id, string filePath)
    {
        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes
            SET is_file = 1, file_path = $filePath, file_saved_at = $savedAt, updated_at = $savedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$filePath", filePath);
        command.Parameters.AddWithValue("$savedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return await GetNoteAsync(id) ?? throw new InvalidOperationException($"Note {id} does not exist.");
    }

    public async Task DeleteNoteAsync(string id)
    {
        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<NoteSummary>> GetSummariesAsync()
    {
        await using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.title, n.content, n.note_date, n.is_file, n.file_path, n.updated_at,
                   GROUP_CONCAT(t.name, '|') AS tags
            FROM notes n
            LEFT JOIN note_tags nt ON nt.note_id = n.id
            LEFT JOIN tags t ON t.id = nt.tag_id
            GROUP BY n.id
            ORDER BY n.note_date DESC, n.updated_at DESC;
            """;
        return await ReadSummariesAsync(command);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpsertTagAsync(SqliteConnection connection, SqliteTransaction transaction, string noteId, string tag)
    {
        var insertTag = connection.CreateCommand();
        insertTag.Transaction = transaction;
        insertTag.CommandText = "INSERT OR IGNORE INTO tags (name) VALUES ($name);";
        insertTag.Parameters.AddWithValue("$name", tag);
        await insertTag.ExecuteNonQueryAsync();

        var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText = """
            INSERT OR IGNORE INTO note_tags (note_id, tag_id)
            SELECT $noteId, id FROM tags WHERE name = $name COLLATE NOCASE;
            """;
        link.Parameters.AddWithValue("$noteId", noteId);
        link.Parameters.AddWithValue("$name", tag);
        await link.ExecuteNonQueryAsync();
    }

    private static void BindNote(SqliteCommand command, Note note)
    {
        command.Parameters.AddWithValue("$id", note.Id);
        command.Parameters.AddWithValue("$title", note.Title);
        command.Parameters.AddWithValue("$content", note.Content);
        command.Parameters.AddWithValue("$date", ToDbDate(note.NoteDate));
        command.Parameters.AddWithValue("$isFile", note.IsFile ? 1 : 0);
        command.Parameters.AddWithValue("$filePath", (object?)note.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", note.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", note.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$fileSavedAt", note.FileSavedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static async Task<IReadOnlyList<NoteSummary>> ReadSummariesAsync(SqliteCommand command)
    {
        var notes = new List<NoteSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var content = reader.GetString(2);
            notes.Add(new NoteSummary(
                reader.GetString(0),
                reader.GetString(1),
                DateOnly.Parse(reader.GetString(3)),
                reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                ReadTags(reader, 7),
                MakePreview(content)));
        }

        return notes;
    }

    private static Note ReadNote(SqliteDataReader reader)
    {
        return new Note(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateOnly.Parse(reader.GetString(3)),
            reader.GetInt32(4) == 1,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            ReadTags(reader, 9));
    }

    private static IReadOnlyList<string> ReadTags(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return Array.Empty<string>();
        }

        return reader.GetString(ordinal).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string MakePreview(string content)
    {
        var singleLine = string.Join(" ", content.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 120 ? singleLine : singleLine[..120] + "...";
    }

    private static string CleanTitle(string value)
    {
        var title = value.Trim();
        return title.Length == 0 ? "untitled" : title;
    }

    private static string CleanTag(string value)
    {
        return value.Trim().TrimStart('#');
    }

    private static string ToDbDate(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd");
    }
}

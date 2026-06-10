using System.IO;
using System.Text.Json;
using System.Windows;
using MdPad.Data;
using MdPad.Models;
using MdPad.Services;

namespace MdPad;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly NoteRepository _notes = new(Paths.DatabasePath);
    private readonly SettingsStore _settings = new();
    private readonly DialogService _dialogs = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _notes.InitializeAsync();
        await Browser.EnsureCoreWebView2Async();
        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
        var index = Path.Combine(webRoot, "index.html");
        if (!File.Exists(index))
        {
            MessageBox.Show("The mdpad web assets were not found. Build src/MdPad.Web before running the app.", "mdpad");
            return;
        }

        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.mdpad.local",
            webRoot,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        Browser.Source = new Uri("https://app.mdpad.local/index.html");
    }

    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        BridgeMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(e.WebMessageAsJson, JsonOptions);
            if (message is null)
            {
                return;
            }

            var data = await HandleMessageAsync(message);
            Post(new BridgeResponse(message.Id, true, data));
        }
        catch (Exception ex)
        {
            if (message is not null)
            {
                Post(new BridgeResponse(message.Id, false, null, ex.Message));
            }
        }
    }

    private async Task<object?> HandleMessageAsync(BridgeMessage message)
    {
        return message.Type switch
        {
            "init" => await InitAsync(),
            "sidebar" => await _notes.GetSidebarAsync(),
            "openTodayScratch" => await _notes.EnsureScratchAsync(DateOnly.FromDateTime(DateTime.Today)),
            "createNote" => await CreateNoteAsync(Read<CreateNoteRequest>(message)),
            "getNote" => await RequireNoteAsync(Read<IdRequest>(message).Id),
            "saveContent" => await SaveContentAsync(Read<SaveContentRequest>(message)),
            "saveMetadata" => await SaveMetadataAsync(Read<MetadataRequest>(message)),
            "closeNote" => await CloseNoteAsync(Read<IdRequest>(message).Id),
            "search" => await _notes.SearchAsync(Read<SearchRequest>(message).Query),
            "notesByDate" => await _notes.GetByDateAsync(ParseDate(Read<DateRequest>(message).Date)),
            "notesByTag" => await _notes.GetByTagAsync(Read<TagRequest>(message).Tag),
            "openFile" => await OpenFileAsync(),
            "saveFile" => await SaveFileAsync(Read<IdRequest>(message).Id),
            "saveAs" => await SaveAsAsync(Read<IdRequest>(message).Id),
            "setTheme" => await SetThemeAsync(Read<ThemeRequest>(message).Theme),
            _ => throw new InvalidOperationException($"Unknown message type: {message.Type}")
        };
    }

    private async Task<object> InitAsync()
    {
        var settings = await _settings.LoadAsync();
        var scratch = await _notes.EnsureScratchAsync(DateOnly.FromDateTime(DateTime.Today));
        var sidebar = await _notes.GetSidebarAsync();
        return new { settings, scratch, sidebar };
    }

    private async Task<Note> CreateNoteAsync(CreateNoteRequest request)
    {
        return await _notes.CreateNoteAsync(ParseDate(request.NoteDate), request.Title, "", false, null);
    }

    private async Task<Note> SaveContentAsync(SaveContentRequest request)
    {
        return await _notes.UpdateContentAsync(request.Id, request.Content);
    }

    private async Task<Note> SaveMetadataAsync(MetadataRequest request)
    {
        return await _notes.UpdateMetadataAsync(request.Id, request.Title, ParseDate(request.NoteDate), request.Tags);
    }

    private async Task<object> CloseNoteAsync(string id)
    {
        await _notes.DeleteNoteAsync(id);
        return new { id };
    }

    private async Task<Note> OpenFileAsync()
    {
        var path = _dialogs.PickOpenFile();
        if (path is null)
        {
            throw new OperationCanceledException("Open cancelled.");
        }

        var content = await File.ReadAllTextAsync(path);
        return await _notes.CreateNoteAsync(DateOnly.FromDateTime(DateTime.Today), Path.GetFileName(path), content, true, path);
    }

    private async Task<Note> SaveFileAsync(string id)
    {
        var note = await RequireNoteAsync(id);
        if (!note.IsFile || string.IsNullOrWhiteSpace(note.FilePath))
        {
            return await SaveAsAsync(id);
        }

        await File.WriteAllTextAsync(note.FilePath, note.Content);
        return await _notes.MarkFileSavedAsync(id);
    }

    private async Task<Note> SaveAsAsync(string id)
    {
        var note = await RequireNoteAsync(id);
        var extension = Path.GetExtension(note.FilePath ?? note.Title);
        var suggested = string.IsNullOrWhiteSpace(extension) ? $"{note.Title}.md" : note.Title;
        var path = _dialogs.PickSaveFile(suggested);
        if (path is null)
        {
            throw new OperationCanceledException("Save cancelled.");
        }

        await File.WriteAllTextAsync(path, note.Content);
        return await _notes.SaveAsAsync(id, path);
    }

    private async Task<AppSettings> SetThemeAsync(string theme)
    {
        var normalized = theme is "light" or "dark" ? theme : "system";
        var settings = new AppSettings(normalized);
        await _settings.SaveAsync(settings);
        return settings;
    }

    private async Task<Note> RequireNoteAsync(string id)
    {
        return await _notes.GetNoteAsync(id) ?? throw new InvalidOperationException($"Note {id} was not found.");
    }

    private T Read<T>(BridgeMessage message)
    {
        if (message.Payload is null)
        {
            throw new InvalidOperationException("Message payload is required.");
        }

        return message.Payload.Value.Deserialize<T>(JsonOptions) ?? throw new InvalidOperationException("Message payload was invalid.");
    }

    private void Post(BridgeResponse response)
    {
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static DateOnly ParseDate(string value)
    {
        return DateOnly.Parse(value);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new DateOnlyJsonConverter());
        return options;
    }
}

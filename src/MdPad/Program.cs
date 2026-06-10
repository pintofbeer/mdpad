using System.Text.Json;
using MdPad.Data;
using MdPad.Models;
using MdPad.Services;
using Photino.NET;

namespace MdPad;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            var app = new MdPadApp();
            app.Run();
        }
        catch (DllNotFoundException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("libwebkit2gtk", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("mdpad requires WebKitGTK on Linux. Install it with: sudo apt-get install libwebkit2gtk-4.1-0");
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
        catch (ApplicationException ex) when (OperatingSystem.IsLinux() && ex.ToString().Contains("libwebkit2gtk", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("mdpad requires WebKitGTK on Linux. Install it with: sudo apt-get install libwebkit2gtk-4.1-0");
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new DateOnlyJsonConverter());
        return options;
    }

    private sealed class MdPadApp
    {
        private readonly NoteRepository _notes = new(Paths.DatabasePath);
        private readonly SettingsStore _settings = new();
        private PhotinoWindow? _window;
        private DialogService? _dialogs;

        public void Run()
        {
            _notes.InitializeAsync().GetAwaiter().GetResult();

            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            var index = Path.Combine(webRoot, "index.html");

            _window = new PhotinoWindow()
                .SetLogVerbosity(0)
                .SetTitle("mdpad")
                .SetSize(1280, 820)
                .SetMinSize(900, 560)
                .SetResizable(true)
                .SetContextMenuEnabled(false)
                .SetDevToolsEnabled(true)
                .RegisterWebMessageReceivedHandler(OnWebMessageReceived);

            _dialogs = new DialogService(_window);

            if (!File.Exists(index))
            {
                _window.ShowMessage("mdpad", "The mdpad web assets were not found. Build src/MdPad.Web before running the app.", PhotinoDialogButtons.Ok, PhotinoDialogIcon.Error);
            }
            else
            {
                _window.Load(index);
            }

            _window.WaitForClose();
        }

        private async void OnWebMessageReceived(object? sender, string messageText)
        {
            BridgeMessage? message = null;
            try
            {
                message = JsonSerializer.Deserialize<BridgeMessage>(messageText, JsonOptions);
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
            var path = await Dialogs.PickOpenFileAsync();
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
            var path = await Dialogs.PickSaveFileAsync(suggested);
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
            Window.SendWebMessage(JsonSerializer.Serialize(response, JsonOptions));
        }

        private PhotinoWindow Window => _window ?? throw new InvalidOperationException("Window has not been created.");

        private DialogService Dialogs => _dialogs ?? throw new InvalidOperationException("Dialogs have not been created.");

        private static DateOnly ParseDate(string value)
        {
            return DateOnly.Parse(value);
        }
    }
}

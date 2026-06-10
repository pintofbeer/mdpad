using Photino.NET;

namespace MdPad.Services;

public sealed class DialogService
{
    private static readonly (string Name, string[] Extensions)[] TextFilters =
    [
        ("Text and Markdown", ["*.txt", "*.md", "*.markdown"]),
        ("All files", ["*.*"])
    ];

    private static readonly (string Name, string[] Extensions)[] SaveFilters =
    [
        ("Markdown", ["*.md"]),
        ("Text", ["*.txt"]),
        ("All files", ["*.*"])
    ];

    private readonly PhotinoWindow _window;

    public DialogService(PhotinoWindow window)
    {
        _window = window;
    }

    public async Task<string?> PickOpenFileAsync()
    {
        var paths = await _window.ShowOpenFileAsync("Open in mdpad", "", false, TextFilters);
        return paths.FirstOrDefault();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        return await _window.ShowSaveFileAsync("Save as", suggestedName, SaveFilters);
    }
}

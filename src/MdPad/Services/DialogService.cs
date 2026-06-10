using Microsoft.Win32;

namespace MdPad.Services;

public sealed class DialogService
{
    public string? PickOpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open in mdpad",
            Filter = "Text and Markdown|*.txt;*.md;*.markdown|All files|*.*",
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save as",
            FileName = suggestedName,
            Filter = "Markdown|*.md|Text|*.txt|All files|*.*",
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

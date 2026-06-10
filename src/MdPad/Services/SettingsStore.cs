using System.Text.Json;
using MdPad.Models;

namespace MdPad.Services;

public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(Paths.AppDataDirectory, "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings("system");
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings("system");
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
    }
}

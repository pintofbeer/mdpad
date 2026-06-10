namespace MdPad.Services;

public static class Paths
{
    public static string AppDataDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(root, "mdpad");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DatabasePath => Path.Combine(AppDataDirectory, "mdpad.db");
}

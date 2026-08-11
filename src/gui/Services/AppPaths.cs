using System;
using System.IO;
using System.Linq;

namespace GameTranslator.Gui.Services;

internal static class AppPaths
{
    public static string RootDirectory { get; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string DataDirectory { get; } = Path.Combine(RootDirectory, "Data");
    public static string CacheDirectory { get; } = Path.Combine(RootDirectory, "Cache");

    public static void Ensure(bool migrateLegacy = false)
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        if (!migrateLegacy) return;
        MigrateLegacy(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameTranslator"), DataDirectory, CacheDirectory);
    }

    internal static void MigrateLegacy(string legacy, string dataDirectory, string cacheDirectory)
    {
        if (string.Equals(legacy, dataDirectory, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy)) return;

        foreach (var name in new[] { "config.json", "game-path.txt", "bridge-token.txt", "crash.log" })
            MoveIfMissing(Path.Combine(legacy, name), Path.Combine(dataDirectory, name));

        var sourceDatabase = Path.Combine(legacy, "translations.db");
        var destinationDatabase = Path.Combine(cacheDirectory, "translations.db");
        var suffixes = new[] { "", "-wal", "-shm" };
        if (File.Exists(sourceDatabase) && !suffixes.Any(suffix => File.Exists(destinationDatabase + suffix)))
        {
            foreach (var suffix in suffixes)
                MoveIfMissing(sourceDatabase + suffix, destinationDatabase + suffix);
        }

        foreach (var name in new[] { "debug.log", "diagnose.log", "icon_extract.log" })
            File.Delete(Path.Combine(legacy, name));
        if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
    }

    private static void MoveIfMissing(string source, string destination)
    {
        if (!File.Exists(source)) return;
        if (!File.Exists(destination)) File.Move(source, destination);
    }
}

using System.IO;
using System.Text.Json;
using PatchHealthCheck.Models;

namespace PatchHealthCheck.Persistence;

public static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(ServerSnapshot snapshot, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, JsonSerializer.Serialize(snapshot, Options));
    }

    public static ServerSnapshot? Load(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<ServerSnapshot>(json, Options);
    }

    public static string BuildFileName(string baseDir, string serverName, string label)
    {
        var safeServer = string.Join("_", serverName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(baseDir, $"{safeServer}_{label}.json");
    }
}

using Cabinet.Diagnostic.Core;

namespace Cabinet.Diagnostic.Windows;

/// <summary>Vérifie que le dossier Documents existe, accepte l'écriture et n'est pas synchronisé en ligne.</summary>
public sealed class WindowsFolderProbe : IFolderProbe
{
    public FolderInfo InspectDocuments()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var exists = !string.IsNullOrEmpty(path) && Directory.Exists(path);
        if (!exists)
        {
            return new FolderInfo(false, false, CloudSync.None);
        }

        var environment = new Dictionary<string, string?>
        {
            ["OneDrive"] = Environment.GetEnvironmentVariable("OneDrive"),
            ["OneDriveCommercial"] = Environment.GetEnvironmentVariable("OneDriveCommercial"),
            ["OneDriveConsumer"] = Environment.GetEnvironmentVariable("OneDriveConsumer"),
        };

        return new FolderInfo(true, CanWrite(path), CloudSyncDetector.Detect(path, environment));
    }

    private static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, $"cabinet-diagnostic-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

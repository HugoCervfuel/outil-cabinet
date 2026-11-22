namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Détermine si un dossier est synchronisé avec un service en ligne.
/// </summary>
public static class CloudSyncDetector
{
    private static readonly string[] OneDriveVariables = { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" };

    private static readonly string[] OtherServiceSegments = { "Dropbox", "Google Drive", "Mon Drive", "My Drive", "iCloudDrive" };

    public static CloudSync Detect(string folderPath, IReadOnlyDictionary<string, string?> environment)
    {
        var path = Normalize(folderPath);

        foreach (var variable in OneDriveVariables)
        {
            if (!environment.TryGetValue(variable, out var root) || string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = Normalize(root);
            if (path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return CloudSync.OneDrive;
            }
        }

        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)))
        {
            return CloudSync.OneDrive;
        }

        if (segments.Any(s => OtherServiceSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return CloudSync.OtherService;
        }

        return CloudSync.None;
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');
}

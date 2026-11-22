using System.Diagnostics;
using Cabinet.Core;

namespace Cabinet.Platform;

/// <summary>Une clé qu'on peut supprimer (tests et désinstallation).</summary>
public interface IDisposableKeyStore : IKeyStore
{
    void DeleteKey();
}

/// <summary>Choisit les implémentations propres au système (macOS, Windows).</summary>
public static class PlatformServices
{
    public const string ProductFolder = "OutilCabinet";

    public static IActivityProbe CreateActivityProbe()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacActivityProbe();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsActivityProbe();
        }

        return new UnsupportedActivityProbe();
    }

    public static IKeyStore CreateKeyStore(string dataFolder, string service = ProductFolder)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainKeyStore(service);
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiKeyStore(Path.Combine(dataFolder, "cle.dpapi"));
        }

        return new FileKeyStore(Path.Combine(dataFolder, "cle.bin"));
    }

    /// <summary>Dossier des données, dans le profil de l'utilisateur.</summary>
    public static string DataFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", ProductFolder);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);
        }

        return Path.Combine(home, ".local", "share", ProductFolder);
    }

    /// <summary>Sur Mac, la lecture du titre des fenêtres demande l'autorisation « Accessibilité ».</summary>
    public static bool NeedsAccessibilityPermission() => OperatingSystem.IsMacOS() && !MacActivityProbe.HasAccessibilityPermission();

    public static void OpenAccessibilitySettings()
    {
        if (OperatingSystem.IsMacOS())
        {
            MacActivityProbe.RequestAccessibilityPermission();
            Process.Start(new ProcessStartInfo("/usr/bin/open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
            {
                UseShellExecute = false,
            })?.Dispose();
        }
    }
}

/// <summary>Systèmes non pris en charge : aucune activité n'est relevée.</summary>
public sealed class UnsupportedActivityProbe : IActivityProbe
{
    public ActiveWindow? ReadActiveWindow() => null;

    public TimeSpan? IdleTime() => null;
}

/// <summary>Clé dans un fichier lisible par l'utilisateur seul. Réservé au développement sous Linux.</summary>
public sealed class FileKeyStore(string path) : IDisposableKeyStore
{
    public byte[] GetOrCreateKey()
    {
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }

    public void DeleteKey() => File.Delete(path);
}

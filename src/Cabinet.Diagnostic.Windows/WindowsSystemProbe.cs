using System.Management;
using Cabinet.Diagnostic.Core;
using Microsoft.Win32;

namespace Cabinet.Diagnostic.Windows;

/// <summary>Lit la version de Windows et l'état de l'antivirus déclaré au Centre de sécurité.</summary>
public sealed class WindowsSystemProbe : ISystemProbe
{
    public SystemInfo Read()
    {
        var (antivirusName, antivirus) = ReadAntivirus();
        return new SystemInfo(ReadWindowsVersion(), Environment.Is64BitOperatingSystem, antivirusName, antivirus);
    }

    private static SafeText ReadWindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuild") as string;
        var displayVersion = key?.GetValue("DisplayVersion") as string;
        var installationType = key?.GetValue("InstallationType") as string;

        string name;
        if (string.Equals(installationType, "Server", StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows Server";
        }
        else
        {
            name = int.TryParse(build, out var number) && number >= 22000 ? "Windows 11" : "Windows 10";
        }

        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(displayVersion))
        {
            parts.Add(displayVersion);
        }

        if (!string.IsNullOrWhiteSpace(build))
        {
            parts.Add($"(build {build})");
        }

        return SafeText.From(string.Join(' ', parts));
    }

    private static (SafeText Name, AntivirusState State) ReadAntivirus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            var products = new List<(string Name, AntivirusState State)>();
            foreach (var result in searcher.Get())
            {
                using var product = (ManagementObject)result;
                var name = product["displayName"] as string ?? "";
                int? rawState = product["productState"] is { } value ? unchecked((int)Convert.ToUInt32(value)) : null;
                products.Add((name, AntivirusStateDecoder.Decode(rawState)));
            }

            var combined = AntivirusStateDecoder.Combine(products.Select(p => p.State));
            var chosen = products.FirstOrDefault(p => p.State == AntivirusState.ActiveUpToDate);
            if (chosen.Name is null)
            {
                chosen = products.FirstOrDefault();
            }

            return (SafeText.From(chosen.Name), combined);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Centre de sécurité absent (Windows Server) ou inaccessible.
            return (SafeText.Empty, AntivirusState.Unknown);
        }
    }
}

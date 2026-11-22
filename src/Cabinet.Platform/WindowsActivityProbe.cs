using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Cabinet.Core;

namespace Cabinet.Platform;

/// <summary>Windows : fenêtre au premier plan (titre et processus) et inactivité.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsActivityProbe : IActivityProbe
{
    private const int MaxTitleLength = 512;

    public unsafe ActiveWindow? ReadActiveWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == 0)
        {
            return null;
        }

        var length = Math.Clamp(GetWindowTextLengthW(handle), 0, MaxTitleLength);
        var buffer = new char[length + 1];
        int copied;
        fixed (char* pointer = buffer)
        {
            copied = GetWindowTextW(handle, pointer, buffer.Length);
        }

        GetWindowThreadProcessId(handle, out var processId);
        var app = "";
        if (processId != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                app = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                app = "";
            }
        }

        return new ActiveWindow(app, new string(buffer, 0, Math.Max(0, copied)));
    }

    public TimeSpan? IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount64 - info.Time));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint hWnd);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint hWnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>Clé de chiffrement protégée par Windows (DPAPI, liée à la session de l'utilisateur).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiKeyStore(string path) : IDisposableKeyStore
{
    private static readonly byte[] Entropy = "OutilCabinet.v1"u8.ToArray();

    public byte[] GetOrCreateKey()
    {
        if (File.Exists(path))
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }

    public void DeleteKey() => File.Delete(path);
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cabinet.Core;

namespace Cabinet.Platform;

/// <summary>
/// macOS : application au premier plan (NSWorkspace), titre de sa fenêtre active (API d'accessibilité,
/// autorisation requise) et inactivité (CoreGraphics). À appeler depuis le fil principal de l'interface.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacActivityProbe : IActivityProbe
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const uint Utf8Encoding = 0x08000100;
    private const float AccessibilityTimeoutSeconds = 0.3f;

    private static readonly Lazy<bool> AppKitLoaded = new(() => NativeLibrary.TryLoad(AppKit, out _));

    public ActiveWindow? ReadActiveWindow()
    {
        if (!AppKitLoaded.Value)
        {
            return null;
        }

        var pool = objc_autoreleasePoolPush();
        try
        {
            var workspace = Send(objc_getClass("NSWorkspace"), Sel("sharedWorkspace"));
            var application = Send(workspace, Sel("frontmostApplication"));
            if (application == IntPtr.Zero)
            {
                return null;
            }

            var name = NSStringToString(Send(application, Sel("localizedName"))) ?? "";
            var pid = SendInt(application, Sel("processIdentifier"));
            var title = AXIsProcessTrusted() ? ReadFocusedWindowTitle(pid) : null;
            return new ActiveWindow(name, title ?? "");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public TimeSpan? IdleTime()
    {
        // kCGEventSourceStateCombinedSessionState = 0, kCGAnyInputEventType = ~0.
        var seconds = CGEventSourceSecondsSinceLastEventType(0, uint.MaxValue);
        return double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    public static bool HasAccessibilityPermission() => AXIsProcessTrusted();

    /// <summary>Fait apparaître l'outil dans la liste « Accessibilité » des Réglages Système.</summary>
    public static void RequestAccessibilityPermission()
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, "AXTrustedCheckOptionPrompt", Utf8Encoding);
        var trueValue = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFBooleanTrue"));
        var keys = new[] { key };
        var values = new[] { trueValue };
        var options = CFDictionaryCreate(IntPtr.Zero, keys, values, 1, IntPtr.Zero, IntPtr.Zero);
        try
        {
            AXIsProcessTrustedWithOptions(options);
        }
        finally
        {
            CFRelease(options);
            CFRelease(key);
        }
    }

    private static string? ReadFocusedWindowTitle(int pid)
    {
        var application = AXUIElementCreateApplication(pid);
        if (application == IntPtr.Zero)
        {
            return null;
        }

        var focusedWindowName = CFStringCreateWithCString(IntPtr.Zero, "AXFocusedWindow", Utf8Encoding);
        var titleName = CFStringCreateWithCString(IntPtr.Zero, "AXTitle", Utf8Encoding);
        var window = IntPtr.Zero;
        var title = IntPtr.Zero;
        try
        {
            AXUIElementSetMessagingTimeout(application, AccessibilityTimeoutSeconds);
            if (AXUIElementCopyAttributeValue(application, focusedWindowName, out window) != 0 || window == IntPtr.Zero)
            {
                return null;
            }

            if (AXUIElementCopyAttributeValue(window, titleName, out title) != 0 || title == IntPtr.Zero)
            {
                return null;
            }

            return CFGetTypeID(title) == CFStringGetTypeID() ? CFStringToString(title) : null;
        }
        finally
        {
            if (title != IntPtr.Zero)
            {
                CFRelease(title);
            }

            if (window != IntPtr.Zero)
            {
                CFRelease(window);
            }

            CFRelease(titleName);
            CFRelease(focusedWindowName);
            CFRelease(application);
        }
    }

    private static unsafe string? CFStringToString(IntPtr cfString)
    {
        var length = CFStringGetLength(cfString);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length];
        fixed (char* pointer = buffer)
        {
            CFStringGetCharacters(cfString, new CFRange(0, length), pointer);
        }

        return new string(buffer);
    }

    private static string? NSStringToString(IntPtr nsString)
        => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    private static IntPtr Sel(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        public readonly nint Location = location;
        public readonly nint Length = length;
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial int SendInt(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC)]
    private static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(IntPtr pool);

    [LibraryImport(AppServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXIsProcessTrusted();

    [LibraryImport(AppServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXIsProcessTrustedWithOptions(IntPtr options);

    [LibraryImport(AppServices)]
    private static partial IntPtr AXUIElementCreateApplication(int pid);

    [LibraryImport(AppServices)]
    private static partial int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    [LibraryImport(AppServices)]
    private static partial int AXUIElementSetMessagingTimeout(IntPtr element, float timeoutInSeconds);

    [LibraryImport(CoreGraphics)]
    private static partial double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(IntPtr text);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial void CFStringGetCharacters(IntPtr text, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(IntPtr value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);
}

/// <summary>Clé de chiffrement conservée dans le Trousseau de l'utilisateur (commande système « security »).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainKeyStore(string service) : IDisposableKeyStore
{
    private const string Account = "cle-donnees";
    private const string Security = "/usr/bin/security";

    public byte[] GetOrCreateKey()
    {
        var (code, output) = Run("find-generic-password", "-s", service, "-a", Account, "-w");
        if (code == 0 && TryDecode(output.Trim(), out var existing))
        {
            return existing;
        }

        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var (addCode, _) = Run("add-generic-password", "-s", service, "-a", Account, "-w", Convert.ToBase64String(key), "-U");
        if (addCode != 0)
        {
            throw new InvalidOperationException("Impossible d'enregistrer la clé dans le Trousseau.");
        }

        return key;
    }

    public void DeleteKey() => Run("delete-generic-password", "-s", service, "-a", Account);

    private static bool TryDecode(string text, out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(text);
            return key.Length == 32;
        }
        catch (FormatException)
        {
            key = Array.Empty<byte>();
            return false;
        }
    }

    private static (int Code, string Output) Run(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Security)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}

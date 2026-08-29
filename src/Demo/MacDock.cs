using System;
using System.Runtime.InteropServices;

namespace Demo;

/// <summary>
/// The macOS half of RequestAttention: Avalonia exposes no cross-platform attention API, so the
/// demo shows what a host can do with the event — bounce the Dock icon via NSApplication.
/// "yes" bounces until focused (critical), "once"/"fireworks" bounce once (informational), and
/// "no" cancels a pending request. A no-op everywhere but macOS.
/// </summary>
internal static class MacDock
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendInt(IntPtr receiver, IntPtr selector, nint arg);

    private const nint CriticalRequest = 0;
    private const nint InformationalRequest = 10;

    private static nint _pending = -1;

    public static void RequestAttention(string action)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            var app = Send(GetClass("NSApplication"), Selector("sharedApplication"));
            if (action == "no")
            {
                if (_pending >= 0)
                    SendInt(app, Selector("cancelUserAttentionRequest:"), _pending);
                _pending = -1;
                return;
            }

            var kind = action == "yes" ? CriticalRequest : InformationalRequest;
            _pending = SendInt(app, Selector("requestUserAttention:"), kind);
        }
        catch
        {
            // A demo nicety; a failed bounce is not worth a crash.
        }
    }
}

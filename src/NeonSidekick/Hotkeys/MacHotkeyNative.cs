using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Images;

namespace NeonSidekick.Hotkeys;

/// <summary>
/// The Carbon and CoreFoundation calls behind <see cref="MacHotkeyProbe"/> (2026-10-11, a Mac's <c>/keycheck</c>): HIToolbox's
/// <c>CopySymbolicHotKeys</c>, <c>RegisterEventHotKey</c>/<c>UnregisterEventHotKey</c> on the application target, and
/// <c>CFPreferencesCopyAppValue</c> for the terminals' settings (through cfprefsd, never the plist files, which it may not have
/// written yet; never <c>defaults</c>, which would be a process-start site). The CLT SDK has no Carbon headers, so the prototypes and
/// constants are the spike's, measured (<c>.docs/mac_keycheck_spike.md</c>). The rest of CoreFoundation comes from
/// <see cref="ImageIONative"/>. HIToolbox is a system framework in the dyld shared cache: bound by its install name, nothing joins
/// <c>SmokeChecks.MacNativeLibraries</c>; smoke <c>keys:carbon</c> proves it on the published exe. Excluded from coverage.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacHotkeyNative
{
    private const string HIToolbox = "/System/Library/Frameworks/Carbon.framework/Versions/A/Frameworks/HIToolbox.framework/Versions/A/HIToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>The signature the probe registers under ('NSkc'): its own, so nothing else of the process's meets it.</summary>
    private const uint Signature = 0x4E536B63;

    /// <summary>Carbon's <c>EventHotKeyID</c>, passed by value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [LibraryImport(HIToolbox)]
    public static partial nint GetApplicationEventTarget();

    [LibraryImport(HIToolbox)]
    public static partial int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, nint target, uint options, out nint hotKey);

    [LibraryImport(HIToolbox)]
    public static partial int UnregisterEventHotKey(nint hotKey);

    [LibraryImport(HIToolbox)]
    public static partial int CopySymbolicHotKeys(out nint hotKeys);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFPreferencesCopyAppValue(nint key, nint applicationId);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFBooleanGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial byte CFBooleanGetValue(nint boolean);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFArrayGetTypeID();

    /// <summary>The exclusive register and, when it took, the release at once: the chord is the probe's for microseconds. The OSStatus.</summary>
    public static int ProbeExclusive(ushort code, uint modifiers, uint id)
    {
        var hotKeyId = new EventHotKeyId { Signature = Signature, Id = id };
        int status = RegisterEventHotKey(code, modifiers, hotKeyId, GetApplicationEventTarget(), MacHotkeyRules.Exclusive, out nint hotKey);
        if (status == MacHotkeyRules.NoErr && hotKey != 0)
        {
            _ = UnregisterEventHotKey(hotKey);
        }

        return status;
    }

    /// <summary>macOS's own shortcuts as they are now, and the call's OSStatus (an empty list unless it is 0).</summary>
    public static List<SymbolicHotKey> SymbolicHotKeys(out int status)
    {
        var list = new List<SymbolicHotKey>();
        status = CopySymbolicHotKeys(out nint array);
        if (status != MacHotkeyRules.NoErr || array == 0)
        {
            return list;
        }

        nint codeKey = ImageIONative.CreateString("kHISymbolicHotKeyCode");
        nint modifiersKey = ImageIONative.CreateString("kHISymbolicHotKeyModifiers");
        nint enabledKey = ImageIONative.CreateString("kHISymbolicHotKeyEnabled");
        try
        {
            nint count = ImageIONative.CFArrayGetCount(array);
            for (nint i = 0; i < count; i++)
            {
                nint entry = ImageIONative.CFArrayGetValueAtIndex(array, i);
                if (!ImageIONative.IsDictionary(entry))
                {
                    continue;
                }

                int? code = ImageIONative.Int(ImageIONative.CFDictionaryGetValue(entry, codeKey));
                int? modifiers = ImageIONative.Int(ImageIONative.CFDictionaryGetValue(entry, modifiersKey));
                bool enabled = Bool(ImageIONative.CFDictionaryGetValue(entry, enabledKey)) ?? false;
                if (code is { } c && modifiers is { } m)
                {
                    list.Add(new SymbolicHotKey((ushort)c, (uint)m, enabled));
                }
            }
        }
        finally
        {
            ImageIONative.CFRelease(codeKey);
            ImageIONative.CFRelease(modifiersKey);
            ImageIONative.CFRelease(enabledKey);
            ImageIONative.CFRelease(array);
        }

        return list;
    }

    /// <summary>Terminal.app's default profile and its <c>useOptionAsMetaKey</c> (null when never set); a null profile when unreadable.</summary>
    public static (string? Profile, bool? Meta) TerminalAppSettings()
    {
        nint profileValue = Preference("Default Window Settings", MacHotkeyRules.TerminalAppDomain);
        nint settings = Preference("Window Settings", MacHotkeyRules.TerminalAppDomain);
        try
        {
            string? profile = String(profileValue);
            if (profile is null)
            {
                return (null, null);
            }

            nint dictionary = ImageIONative.IsDictionary(settings) ? Get(settings, profile) : 0;
            return (profile, ImageIONative.IsDictionary(dictionary) ? Bool(Get(dictionary, "useOptionAsMetaKey")) : null);
        }
        finally
        {
            Release(profileValue);
            Release(settings);
        }
    }

    /// <summary>iTerm2's profile for this session (<see cref="MacHotkeyRules.ItermProfileIndex"/>) and its two Option settings; a null profile when none.</summary>
    public static (string? Profile, int? Left, int? Right) ItermSettings(string? sessionProfile)
    {
        nint bookmarks = Preference("New Bookmarks", MacHotkeyRules.ItermDomain);
        nint defaultGuid = Preference("Default Bookmark Guid", MacHotkeyRules.ItermDomain);
        try
        {
            if (bookmarks == 0 || ImageIONative.CFGetTypeID(bookmarks) != CFArrayGetTypeID())
            {
                return (null, null, null);
            }

            var profiles = new List<(string?, string?)>();
            var dictionaries = new List<nint>();
            nint count = ImageIONative.CFArrayGetCount(bookmarks);
            for (nint i = 0; i < count; i++)
            {
                nint profile = ImageIONative.CFArrayGetValueAtIndex(bookmarks, i);
                if (ImageIONative.IsDictionary(profile))
                {
                    profiles.Add((String(Get(profile, "Name")), String(Get(profile, "Guid"))));
                    dictionaries.Add(profile);
                }
            }

            int index = MacHotkeyRules.ItermProfileIndex(profiles, sessionProfile, String(defaultGuid));
            if (index < 0)
            {
                return (null, null, null);
            }

            nint chosen = dictionaries[index];
            return (profiles[index].Item1, ImageIONative.Int(Get(chosen, "Option Key Sends")), ImageIONative.Int(Get(chosen, "Right Option Key Sends")));
        }
        finally
        {
            Release(bookmarks);
            Release(defaultGuid);
        }
    }

    /// <summary>A preference of another app's (owned: released by the caller), or 0.</summary>
    private static nint Preference(string key, string domain)
    {
        nint k = ImageIONative.CreateString(key);
        nint d = ImageIONative.CreateString(domain);
        try
        {
            return CFPreferencesCopyAppValue(k, d);
        }
        finally
        {
            ImageIONative.CFRelease(k);
            ImageIONative.CFRelease(d);
        }
    }

    /// <summary>A dictionary's value under a string key (not owned), or 0.</summary>
    private static nint Get(nint dictionary, string key)
    {
        nint k = ImageIONative.CreateString(key);
        try
        {
            return ImageIONative.CFDictionaryGetValue(dictionary, k);
        }
        finally
        {
            ImageIONative.CFRelease(k);
        }
    }

    /// <summary>A CFString's text, or null when <paramref name="value"/> is none.</summary>
    private static string? String(nint value) =>
        value != 0 && ImageIONative.CFGetTypeID(value) == CFStringGetTypeID() ? ImageIONative.Text(value) : null;

    /// <summary>A CFBoolean's value (a CFNumber's too, as preferences sometimes keep one), or null.</summary>
    private static bool? Bool(nint value)
    {
        if (value == 0)
        {
            return null;
        }

        if (ImageIONative.CFGetTypeID(value) == CFBooleanGetTypeID())
        {
            return CFBooleanGetValue(value) != 0;
        }

        return ImageIONative.Int(value) is { } n ? n != 0 : null;
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            ImageIONative.CFRelease(value);
        }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Sql;

/// <summary>
/// The macOS Keychain's generic-password calls (2026-10-06, the macOS build), for <see cref="MacKeychain"/>: Security.framework's
/// <c>SecKeychainFindGenericPassword</c> family, plain C arguments over typed pointers, nothing marshalled by value. The
/// <c>SecKeychain…GenericPassword</c> calls are deprecated in favour of <c>SecItem…</c> with CoreFoundation dictionaries, but they
/// still work on every macOS the app targets and need no CFDictionary building, which is the riskier native code; moving to
/// <c>SecItem…</c> is a contained change here. The smoke's <c>keys:keychain</c> round-trips a throwaway item on the published binary.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class KeychainNative
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int ErrSecSuccess = 0;
    public const int ErrSecItemNotFound = -25300;
    public const int ErrSecDuplicateItem = -25299;
    public const int ErrSecUserCanceled = -128;
    public const int ErrSecAuthFailed = -25293;
    public const int ErrSecInteractionNotAllowed = -25308;

    [LibraryImport(Security)]
    public static partial int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray, uint serviceNameLength, byte* serviceName, uint accountNameLength, byte* accountName,
        uint* passwordLength, void** passwordData, IntPtr* itemRef);

    [LibraryImport(Security)]
    public static partial int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, byte* serviceName, uint accountNameLength, byte* accountName,
        uint passwordLength, void* passwordData, IntPtr* itemRef);

    [LibraryImport(Security)]
    public static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(Security)]
    public static partial int SecKeychainItemFreeContent(void* attrList, void* data);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(IntPtr cf);
}

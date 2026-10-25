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

    /// <summary><c>kSecAccountItemAttr</c>, 'acct' (2026-10-23, DA2).</summary>
    public const uint AccountAttribute = 0x61636374;

    /// <summary><c>kSecCommentItemAttr</c>, 'icmt' (2026-10-24, decision DB9: the host a credential was saved for, <see cref="CredentialMark"/>).</summary>
    public const uint CommentAttribute = 0x69636d74;

    /// <summary><c>CSSM_DB_ATTRIBUTE_FORMAT_STRING</c>.</summary>
    public const uint FormatString = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SecKeychainAttributeInfo
    {
        public uint Count;
        public uint* Tag;
        public uint* Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SecKeychainAttribute
    {
        public uint Tag;
        public uint Length;
        public void* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SecKeychainAttributeList
    {
        public uint Count;
        public SecKeychainAttribute* Attr;
    }

    [LibraryImport(Security)]
    public static partial int SecKeychainItemCopyAttributesAndData(
        IntPtr itemRef, SecKeychainAttributeInfo* info, uint* itemClass, SecKeychainAttributeList** attrList, uint* length, void** outData);

    [LibraryImport(Security)]
    public static partial int SecKeychainItemFreeAttributesAndData(SecKeychainAttributeList* attrList, void* data);

    /// <summary>Changes an item's attributes and (with <paramref name="data"/>) its password in place (2026-10-24, review I11, DB9).</summary>
    [LibraryImport(Security)]
    public static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, SecKeychainAttributeList* attrList, uint length, void* data);
}

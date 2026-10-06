using System.Runtime.InteropServices;

namespace NeonSidekick.Viewer;

/// <summary>
/// The environment options WebView2's loader reads as it starts the browser (2026-10-05): an app-made
/// <c>ICoreWebView2EnvironmentOptions</c>, the <see cref="WebViewHandler"/> shape with the interface's ten members. Only the
/// browser's arguments and the SDK's target version say anything; the language and single sign-on are the browser's own
/// (null, false), and every setter is accepted and ignored, the loader never calling one.
///
/// <para>Why it exists: Chromium refuses a video's sound before the user has clicked in the page, and the model starting a
/// video is no click. The Phase 0 spike's one unmuted start played without these options, so the plan dropped them; the first
/// run of the real window then refused (cued, buffering, back to unstarted, and a later play did nothing) while a muted video
/// played at once — the autoplay policy, which <see cref="BrowserArguments"/> turns off for this browser alone. The loader
/// asks it for eleven newer options interfaces too (<c>ICoreWebView2EnvironmentOptions2</c> on); each is refused and it goes
/// on with this one, as the spike saw.</para>
/// </summary>
internal static unsafe class WebViewOptions
{
    /// <summary>The browser's arguments: a video may start with its sound without a click.</summary>
    public const string BrowserArguments = "--autoplay-policy=no-user-gesture-required";

    /// <summary><c>CORE_WEBVIEW_TARGET_PRODUCT_VERSION</c> of the SDK the slots come from (1.0.4258.31): the oldest runtime it expects.</summary>
    public const string TargetVersion = "154.0.4258.31";

    internal static readonly Guid IidEnvironmentOptions = new("2FDE08A8-1E9A-4766-8C05-95A9CEB9D1C5");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private struct Block
    {
        public nint* Vtbl;
        public int References;
        public nint Arguments;
    }

    private static readonly nint* s_vtbl = Table();

    /// <summary>An options object answering <paramref name="arguments"/>, at one reference, the caller's: hand it over and <see cref="Release"/> it.</summary>
    public static nint Create(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var block = (Block*)NativeMemory.Alloc((nuint)sizeof(Block));
        block->Vtbl = s_vtbl;
        block->References = 1;
        block->Arguments = GCHandle.ToIntPtr(GCHandle.Alloc(arguments));
        return (nint)block;
    }

    public static uint Release(nint options) => options == 0 ? 0 : ReleaseCore((Block*)options);

    private static nint* Table()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(11 * sizeof(nint)));
        table[0] = (nint)(delegate* unmanaged<Block*, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged<Block*, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<Block*, uint>)&ComRelease;
        table[3] = (nint)(delegate* unmanaged<Block*, char**, int>)&GetArguments;
        table[4] = (nint)(delegate* unmanaged<Block*, char*, int>)&PutString;
        table[5] = (nint)(delegate* unmanaged<Block*, char**, int>)&GetNothing;   // the language: the browser's own
        table[6] = (nint)(delegate* unmanaged<Block*, char*, int>)&PutString;
        table[7] = (nint)(delegate* unmanaged<Block*, char**, int>)&GetTargetVersion;
        table[8] = (nint)(delegate* unmanaged<Block*, char*, int>)&PutString;
        table[9] = (nint)(delegate* unmanaged<Block*, int*, int>)&GetFalse;       // single sign-on with the Windows account: no
        table[10] = (nint)(delegate* unmanaged<Block*, int, int>)&PutBool;
        return table;
    }

    private static uint ReleaseCore(Block* self)
    {
        int left = Interlocked.Decrement(ref self->References);
        if (left == 0)
        {
            GCHandle.FromIntPtr(self->Arguments).Free();
            NativeMemory.Free(self);
        }

        return (uint)left;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(Block* self, Guid* iid, nint* found)
    {
        if (*iid == IidUnknown || *iid == IidEnvironmentOptions)
        {
            Interlocked.Increment(ref self->References);
            *found = (nint)self;
            return WebViewHandler.SOk;
        }

        *found = 0;
        return WebViewHandler.ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(Block* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly]
    private static uint ComRelease(Block* self) => ReleaseCore(self);

    // A string out is the caller's to free (CoTaskMemFree), as WebView2's own options class hands them.
    [UnmanagedCallersOnly]
    private static int GetArguments(Block* self, char** value)
    {
        *value = (char*)Marshal.StringToCoTaskMemUni((string)GCHandle.FromIntPtr(self->Arguments).Target!);
        return WebViewHandler.SOk;
    }

    [UnmanagedCallersOnly]
    private static int GetTargetVersion(Block* self, char** value)
    {
        *value = (char*)Marshal.StringToCoTaskMemUni(TargetVersion);
        return WebViewHandler.SOk;
    }

    [UnmanagedCallersOnly]
    private static int GetNothing(Block* self, char** value)
    {
        *value = null;
        return WebViewHandler.SOk;
    }

    [UnmanagedCallersOnly]
    private static int GetFalse(Block* self, int* value)
    {
        *value = 0;
        return WebViewHandler.SOk;
    }

    [UnmanagedCallersOnly]
    private static int PutString(Block* self, char* value) => WebViewHandler.SOk;

    [UnmanagedCallersOnly]
    private static int PutBool(Block* self, int value) => WebViewHandler.SOk;
}

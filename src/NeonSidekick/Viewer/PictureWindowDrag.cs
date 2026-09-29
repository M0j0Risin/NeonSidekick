using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture viewer's picture dragged out of the window onto the desktop, an Explorer folder or anything else that takes
/// a dropped file, and copied there (2026-09-28, the user's ask: "drag an image … to have it copied outside the
/// application"). Only from the viewer: a drag that starts in the terminal is Windows Terminal's — its window has the press
/// and gets the release, the app sees cells — so the strip and the transcript open the viewer and the drag starts there
/// (the user's call). The shell does the work: <c>SHCreateDataObject</c> makes the same data object Explorer makes for a
/// file (CF_HDROP, the shell's ID list, the thumbnail the drag shows) and <c>SHDoDragDrop</c> runs the drag with the
/// shell's own drop source (Esc, the release, the drag image), so no COM class of ours exists; the object is an
/// <see cref="IntPtr"/> released through its vtable. Copy is the only effect offered, never a move: the ComfyUI output
/// folder and the strip keep their file. Needs OLE on the calling thread, an STA (the viewer's thread since this day).
/// Proven by the smoke's <c>viewer:drag</c> (<see cref="Probe"/>) and by hand.
/// </summary>
internal static unsafe class PictureWindowDrag
{
    /// <summary>
    /// Drags <paramref name="path"/> out of <paramref name="hwnd"/> until the button is let go or Esc is pressed: the
    /// HRESULT (<see cref="DragDropDropped"/>, <see cref="DragDropCancelled"/> or a failure) and the effect the target took.
    /// On the window's thread, with OLE started on it; modal, the thread's messages still pumped.
    /// </summary>
    public static (int Hr, uint Effect) Start(IntPtr hwnd, string path)
    {
        int hr = CreateDataObject(path, out IntPtr data);
        if (hr < 0)
        {
            return (hr, 0);
        }

        try
        {
            uint effect = 0;
            hr = SHDoDragDrop(hwnd, data, IntPtr.Zero, DropEffectCopy, &effect);
            return (hr, effect);
        }
        finally
        {
            Release(data);
        }
    }

    /// <summary>
    /// <c>viewer:drag</c>'s proof, on an STA thread of its own: OLE started, the shell's data object made for
    /// <paramref name="path"/> (a file that exists) and asked whether it offers CF_HDROP. Nothing is dragged.
    /// </summary>
    public static (bool Ok, string Detail) Probe(string path)
    {
        (bool Ok, string Detail) result = (false, "the probe's thread did not run");
        var thread = new Thread(() => result = ProbeHere(path)) { IsBackground = true, Name = "Picture drag probe" };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        thread.Join();
        return result;
    }

    private static (bool Ok, string Detail) ProbeHere(string path)
    {
        try
        {
            int ole = OleInitialize(IntPtr.Zero);
            if (ole < 0)
            {
                return (false, $"OleInitialize failed (0x{ole:X8})");
            }

            try
            {
                int hr = CreateDataObject(path, out IntPtr data);
                if (hr < 0)
                {
                    return (false, $"the shell's data object was not made (0x{hr:X8})");
                }

                try
                {
                    var format = new FormatEtc { cfFormat = CfHDrop, dwAspect = DvAspectContent, lindex = -1, tymed = TymedHGlobal };
                    int offers = QueryGetData(data, &format);
                    return offers == 0
                        ? (true, "ole32/shell32 bound; the shell's data object for a file offers CF_HDROP")
                        : (false, $"the shell's data object answered 0x{offers:X8} for CF_HDROP");
                }
                finally
                {
                    Release(data);
                }
            }
            finally
            {
                OleUninitialize();
            }
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The shell's data object for one file: its absolute ID list split into the folder's and the file's last id, as
    // SHCreateDataObject takes them (it copies both, so they are freed here).
    private static int CreateDataObject(string path, out IntPtr data)
    {
        data = IntPtr.Zero;
        IntPtr absolute;
        uint attributes;
        int hr = SHParseDisplayName(path, IntPtr.Zero, &absolute, 0, &attributes);
        if (hr < 0)
        {
            return hr;
        }

        IntPtr folder = IntPtr.Zero;
        try
        {
            folder = ILClone(absolute);
            if (folder == IntPtr.Zero || !ILRemoveLastID(folder))
            {
                return EFail;
            }

            IntPtr child = ILFindLastID(absolute);
            Guid iid = IidDataObject;
            IntPtr made;
            hr = SHCreateDataObject(folder, 1, &child, IntPtr.Zero, &iid, &made);
            if (hr >= 0)
            {
                data = made;
            }

            return hr;
        }
        finally
        {
            if (folder != IntPtr.Zero)
            {
                ILFree(folder);
            }

            ILFree(absolute);
        }
    }

    // IDataObject::QueryGetData, the vtable's sixth slot (IUnknown's three, GetData, GetDataHere, then this).
    private static int QueryGetData(IntPtr data, FormatEtc* format) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, FormatEtc*, int>)(*(IntPtr**)data)[5])(data, format);

    // IUnknown::Release, the vtable's third slot.
    private static void Release(IntPtr unknown) =>
        _ = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)unknown)[2])(unknown);
}

using NeonSidekick.Settings;

namespace NeonSidekick.Images;

/// <summary>
/// What a row of the picture windows' right-click menu does (2026-10-04, <c>Viewer.PictureMenu</c>): an edit through
/// <see cref="PictureActions.Edit"/>, or one of the file's own actions the window carries out. The values are the menu's commands.
/// </summary>
public enum PictureCommand
{
    None = 0,
    RotateRight,
    RotateLeft,
    Rotate180,
    FlipHorizontal,
    FlipVertical,
    Grey,
    Sepia,
    Negative,
    Polaroid,
    Half,
    Quarter,
    Fit3840,
    Fit1920,
    Fit1280,
    Fit1024,
    Fit512,
    ToPng,
    ToJpeg,
    ToGif,
    ToBmp,
    Under2Mb,
    Under1Mb,
    Under500Kb,
    Under200Kb,
    OpenInViewer,
    CopyPath,
    ShowInExplorer,
    Attach,
    Print,
    Delete,
}

/// <summary>The settings a menu edit reads (2026-10-04): <c>Image edit quality</c>, <c>Image edit metadata</c> and <c>Image edit mode</c>.</summary>
public sealed record PictureEditSettings(int Quality, ImageMetadataPolicy Metadata, ImageEditMode Mode)
{
    /// <summary>The defaults: quality 90, no metadata, beside the original.</summary>
    public static PictureEditSettings Default { get; } = new(ImageEditor.DefaultQuality, ImageMetadataPolicy.None, ImageEditMode.BesideOriginal);

    /// <summary>The three as <paramref name="effective"/> holds them, read the way <c>image_edit</c> reads them.</summary>
    public static PictureEditSettings From(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return new PictureEditSettings(
            Math.Clamp(effective.ImageEditQuality, AppSettingsData.MinImageEditQuality, AppSettingsData.MaxImageEditQuality),
            ImageWords.MetadataOf(effective.ImageEditMetadata),
            ImageWords.EditModeOf(effective.ImageEditMode));
    }
}

/// <summary>
/// A menu edit's end: the file written (full path), the source deleted after a format change under <c>overwrite-original</c>,
/// whether the written file is the source itself (replaced in place), the line that says so (<see cref="ImageText"/>'s), and
/// whether it failed (then nothing was written and <see cref="Line"/> is the error).
/// </summary>
public sealed record PictureEditOutcome(string? Written, string? Removed, bool Replaced, string Line, bool Failed)
{
    public static PictureEditOutcome Failure(string line) => new(null, null, false, line, true);
}

/// <summary>
/// The edits behind the picture windows' right-click menu (2026-10-04, the user's ask: rotate and flip, the colour presets,
/// resizes, conversions and a file-size cap on a thumbnail or the viewer's picture), each one fixed request through
/// <see cref="ImageEditor"/> — the engine <c>image_edit</c> runs, so the two never disagree — on a full path the window already
/// holds (a folder the chat resolved in the sandbox). Where the result goes is <c>Image edit mode</c>'s (the user's call, shared with
/// <c>image_edit</c>): <c>beside-original</c> writes <c>photo-edited.png</c> beside the source (<c>photo.jpg</c> for a conversion),
/// <c>-2</c>, <c>-3</c> on a clash, never over a file; <c>overwrite-original</c> replaces the source in place, or for a conversion
/// writes <c>photo.jpg</c> and deletes <c>photo.png</c>. No confirmation (the user's call). Every new file is written whole beside
/// its name and moved onto it, so a window watching the folder never reads half of one. The resize presets only ever shrink, and an
/// edit that would change nothing (a picture already that small, already that format, already under the cap) says so and writes
/// nothing. The conversions are the four formats the viewer lists (<c>ImageFile.IsImagePath</c>), so the result shows in the windows.
/// Blocking: the window runs it on a pool thread.
/// </summary>
public static class PictureActions
{
    /// <summary>Whether <paramref name="command"/> edits the picture (rather than being a file action the window carries out).</summary>
    public static bool IsEdit(PictureCommand command) => command is >= PictureCommand.RotateRight and <= PictureCommand.Under200Kb;

    /// <summary>The side a Fit preset shrinks to, or null.</summary>
    public static int? FitSide(PictureCommand command) => command switch
    {
        PictureCommand.Fit3840 => 3840,
        PictureCommand.Fit1920 => 1920,
        PictureCommand.Fit1280 => 1280,
        PictureCommand.Fit1024 => 1024,
        PictureCommand.Fit512 => 512,
        _ => null,
    };

    /// <summary>The cap a Shrink preset gets the file under, in KB, or null.</summary>
    public static int? MaxKb(PictureCommand command) => command switch
    {
        PictureCommand.Under2Mb => 2048,
        PictureCommand.Under1Mb => 1024,
        PictureCommand.Under500Kb => 500,
        PictureCommand.Under200Kb => 200,
        _ => null,
    };

    /// <summary>The format a conversion writes, or null.</summary>
    public static ImageFormat? FormatOf(PictureCommand command) => command switch
    {
        PictureCommand.ToPng => ImageFormats.Png,
        PictureCommand.ToJpeg => ImageFormats.Jpeg,
        PictureCommand.ToGif => ImageFormats.Gif,
        PictureCommand.ToBmp => ImageFormats.Bmp,
        _ => null,
    };

    /// <summary>The fixed request an edit command is, with <paramref name="metadata"/> kept; null for a command that is no edit. Pure.</summary>
    public static ImageEditRequest? RequestFor(PictureCommand command, ImageMetadataPolicy metadata = ImageMetadataPolicy.None)
    {
        var request = new ImageEditRequest { Metadata = metadata };
        return command switch
        {
            PictureCommand.RotateRight => request with { Rotate = 90 },
            PictureCommand.RotateLeft => request with { Rotate = 270 },
            PictureCommand.Rotate180 => request with { Rotate = 180 },
            PictureCommand.FlipHorizontal => request with { Flip = ImageFlip.Horizontal },
            PictureCommand.FlipVertical => request with { Flip = ImageFlip.Vertical },
            PictureCommand.Grey => request with { Filter = ImageFilter.Grey },
            PictureCommand.Sepia => request with { Filter = ImageFilter.Sepia },
            PictureCommand.Negative => request with { Filter = ImageFilter.Negative },
            PictureCommand.Polaroid => request with { Filter = ImageFilter.Polaroid },
            PictureCommand.Half => request with { Scale = 0.5 },
            PictureCommand.Quarter => request with { Scale = 0.25 },
            _ when FitSide(command) is int side => request with { Width = side, Height = side, Fit = ImageFit.Shrink },
            _ when FormatOf(command) is { } format => request with { Format = format },
            _ when MaxKb(command) is int kb => request with { MaxKb = kb },
            _ => null,
        };
    }

    /// <summary>
    /// <paramref name="command"/> run on the picture at <paramref name="full"/> under <paramref name="settings"/>: read, edited, written
    /// as <c>Image edit mode</c> says. Never throws for a bad picture or a refused write: the outcome carries the error line.
    /// </summary>
    public static PictureEditOutcome Edit(string full, PictureCommand command, PictureEditSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(full);
        ArgumentNullException.ThrowIfNull(settings);
        if (RequestFor(command, settings.Metadata) is not { } request)
        {
            return PictureEditOutcome.Failure(ImageText.NothingToDo);
        }

        byte[] bytes;
        try
        {
            var file = new FileInfo(full);
            if (!file.Exists)
            {
                return PictureEditOutcome.Failure(ImageText.Gone(Path.GetFileName(full)));
            }

            if (file.Length > ImageEditor.MaxSourceBytes)
            {
                return PictureEditOutcome.Failure(ImageText.SourceTooBig);
            }

            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PictureEditOutcome.Failure(ImageText.Failed(ex.Message));
        }

        if (ImageEditor.Info(bytes) is not { } info)
        {
            return PictureEditOutcome.Failure(ImageText.NotAnImage);
        }

        var source = ImageFormats.ByMimeType(info.MimeType);
        if (FitSide(command) is int side && Math.Max(info.Width, info.Height) <= side)
        {
            return PictureEditOutcome.Failure(ImageText.AlreadyFits(side, info.Width, info.Height));
        }

        if (FormatOf(command) is { } wanted && wanted == source)
        {
            return PictureEditOutcome.Failure(ImageText.AlreadyFormat(wanted));
        }

        if (MaxKb(command) is int kb && bytes.LongLength <= kb * 1024L)
        {
            return PictureEditOutcome.Failure(ImageText.AlreadyUnder(kb, bytes.LongLength));
        }

        var format = ImageOutput.FormatFor(request.Format, null, false, info.MimeType, ImageFormats.CanWrite);
        bool sameFormat = source == format;
        var (result, error) = ImageEditor.Apply(bytes, request, format, settings.Quality, cancellationToken);
        if (result is null)
        {
            return PictureEditOutcome.Failure(error ?? ImageText.NotAnImage);
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool overwrite = settings.Mode == ImageEditMode.OverwriteOriginal;
        try
        {
            if (overwrite && sameFormat)
            {
                Files.WorkingDirectory.WriteAtomically(full, result.Bytes, preserve: true);
                return new PictureEditOutcome(full, null, true, ImageText.Written(Path.GetFileName(full), result, request.MaxKb, ImageText.ReplacedVerb), false);
            }

            var (target, nameError) = ImageOutput.OutputFor(null, false, full, format, onlyFormatChanged: overwrite || FormatOf(command) is not null, null);
            if (target is null)
            {
                return PictureEditOutcome.Failure(nameError ?? ImageText.NothingToDo);
            }

            string written = WriteNew(Path.GetFullPath(target), full, result.Bytes);
            string line = ImageText.Written(Path.GetFileName(written), result, request.MaxKb);
            if (!overwrite)
            {
                return new PictureEditOutcome(written, null, false, line, false);
            }

            // overwrite-original's conversion: the new file is whole, so the source goes now; a refusal leaves both and says so.
            try
            {
                File.Delete(full);
                return new PictureEditOutcome(written, full, false, line + ImageText.SourceDeleted(Path.GetFileName(full)), false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new PictureEditOutcome(written, null, false, line + ImageText.SourceKept(Path.GetFileName(full), ex.Message), false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PictureEditOutcome.Failure(ImageText.Failed(ex.Message));
        }
    }

    // The bytes under the first free name of target, -2, -3… (never over a file, never the source): written whole beside it and
    // moved onto it, so a watcher sees the finished picture arrive. The name written, in full.
    private static string WriteNew(string target, string source, byte[] bytes)
    {
        for (int n = 1; n < 100; n++)
        {
            string candidate = ImageOutput.Numbered(target, n);
            if (string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase) || File.Exists(candidate))
            {
                continue;
            }

            string temp = Files.WorkingDirectory.TempSibling(candidate);
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken between the look and the move: the next number.
            }
            finally
            {
                Files.WorkingDirectory.DeleteQuietly(temp);
            }
        }

        throw new IOException($"no free name for {Path.GetFileName(target)} up to -99");
    }
}

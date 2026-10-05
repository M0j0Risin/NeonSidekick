using System.Globalization;
using NeonSidekick.Camera;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The Camera tab of /tools (2026-10-02) ──────────────────────────────────

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The cameras Windows lists, for <c>Camera device</c>'s picker (2026-10-02): the screen's camera session; null where there
    /// is none (the picker then offers only the first-camera row). Throws <see cref="CameraException"/> when the layer fails.
    /// </summary>
    public Func<IReadOnlyList<CameraDevice>>? Cameras { get; set; }

    /// <summary>
    /// Whether <c>/camera watch</c> runs, for the Camera tool page's button (2026-10-04, the user's ask): the screen's watch; null
    /// where there is no camera layer (the page then has no button), and set with <see cref="SetCameraWatch"/>.
    /// </summary>
    public Func<bool>? CameraWatching { get; set; }

    /// <summary>
    /// Watch mode started (true, at <c>Camera watch every</c>) or stopped (false) for the Camera tool page's button (2026-10-04):
    /// the screen's own start and stop, with the line <c>/camera watch</c> would say and whether it failed.
    /// </summary>
    public Func<bool, (bool Ok, string Text)>? SetCameraWatch { get; set; }

    /// <summary>
    /// The Camera tool page's button (2026-10-04, the user's ask: an action at the top, as the Web page's): <c>/camera watch</c> on
    /// or off, a monochrome glyph before the word as every header button has. Pinned.
    /// </summary>
    public const string CameraWatchButtonTitle = "◉ watch";

    /// <summary>The key that is <see cref="CameraWatchButtonTitle"/>.</summary>
    public const char CameraWatchKey = 'w';

    /// <summary>The Camera tool page's hint: <see cref="PickKeys"/> with the watch button's key (2026-10-04). Pinned.</summary>
    public const string CameraToggleKeys = "Enter = choose · W = watch · ESC = back";

    /// <summary>The Camera tool page's one button (2026-10-04): <see cref="CameraWatchButtonTitle"/>, lit while watch mode runs, a press switching it. Pinned.</summary>
    public static IReadOnlyList<MenuButton> CameraWatchButtons(bool watching) => [new(CameraWatchButtonTitle, CameraWatchKey, watching)];

    /// <summary>How the menu shows an empty <c>Camera output folder</c> (2026-10-02): the photos land in the working directory itself. Pinned.</summary>
    public const string CameraOutputHereLabel = "(the working directory)";

    /// <summary>The settings-menu wording for a bad <c>Camera output folder</c>. Pinned.</summary>
    public const string CameraOutputFolderError = "must be a folder under the working directory (a relative path), or empty";

    /// <summary>The <c>Camera device</c> value with none saved: the first camera. Pinned.</summary>
    public const string FirstCameraLabel = "(first camera)";

    public static readonly string CameraWatchSecondsRangeError =
        "must be " + AppSettingsData.MinCameraWatchSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxCameraWatchSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    public static readonly string CameraWatchThresholdRangeError =
        "must be " + AppSettingsData.MinCameraWatchThreshold.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxCameraWatchThreshold.ToString(CultureInfo.InvariantCulture) + " percent";

    public static readonly string CameraWatchMinGapRangeError =
        "must be " + AppSettingsData.MinCameraWatchMinGapSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxCameraWatchMinGapSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>One row of the camera mode pickers: the name padded to nine (<c>disabled</c> is eight), then its hint, dim. Pinned.</summary>
    public static string CameraModeLabel(string name, string hint) => Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(Markup.Escape(hint));

    /// <summary>
    /// The four camera pickers (2026-10-02), and the screen capture's ask (2026-10-04, the shutter's shape): the shutter and the preview over their names with a hint each, the resolution over
    /// its three sizes, the device over the cameras Windows lists now (read here; a failure is the sentence on the status line)
    /// with the first-camera row on top. The saved one under the cursor; true when a pick changed it.
    /// </summary>
    private async Task<bool> PickCameraAsync(SettingsField field, AppSettingsData saved, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> values;
        IReadOnlyList<string> rows;
        string current;
        switch (field)
        {
            case SettingsField.CameraShutter:
                values = CameraShutterMode.Names;
                rows = values.Select(n => CameraModeLabel(n, CameraShutterMode.Describe(n))).ToList();
                current = saved.CameraShutter;
                break;
            case SettingsField.CameraPreview:
                values = CameraPreviewMode.Names;
                rows = values.Select(n => CameraModeLabel(n, CameraPreviewMode.Describe(n))).ToList();
                current = saved.CameraPreview;
                break;
            case SettingsField.ScreenAsk:
                values = Screen.ScreenAskMode.Names;
                rows = values.Select(n => CameraModeLabel(n, Screen.ScreenAskMode.Describe(n))).ToList();
                current = saved.ScreenAsk;
                break;
            case SettingsField.CameraResolution:
                values = AppSettingsData.CameraResolutions;
                rows = values.Select(Markup.Escape).ToList();
                current = saved.CameraResolution;
                break;
            default:
                IReadOnlyList<CameraDevice> devices = [];
                try
                {
                    devices = Cameras is { } list ? await Task.Run(list, cancellationToken).ConfigureAwait(false) : [];
                }
                catch (CameraException e)
                {
                    Sink.Error(e.Message);
                }

                values = ["", .. devices.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
                rows = values.Select(v => v.Length == 0 ? Markup.Escape(FirstCameraLabel) : Markup.Escape(v)).ToList();
                current = saved.CameraDevice ?? "";
                break;
        }

        var page = new MenuPage(Crumb(FieldName(field)), rows, PickKeys);
        int at = Math.Max(0, values.ToList().FindIndex(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase)));
        int? picked = await PickAsync(page, at, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index || index >= values.Count)
        {
            return Unchanged();
        }

        string value = values[index];
        Apply(field, d =>
        {
            switch (field)
            {
                case SettingsField.CameraShutter: d.CameraShutter = value; break;
                case SettingsField.CameraPreview: d.CameraPreview = value; break;
                case SettingsField.CameraResolution: d.CameraResolution = value; break;
                case SettingsField.ScreenAsk: d.ScreenAsk = value; break;
                default: d.CameraDevice = value; break;
            }
        });
        return true;
    }
}

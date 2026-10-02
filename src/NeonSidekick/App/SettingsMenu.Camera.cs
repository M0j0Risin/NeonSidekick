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
    /// The four camera pickers (2026-10-02): the shutter and the preview over their names with a hint each, the resolution over
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
                default: d.CameraDevice = value; break;
            }
        });
        return true;
    }
}

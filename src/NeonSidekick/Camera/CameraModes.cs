using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Camera;

/// <summary>Who takes the model's photo (<see cref="CameraShutterMode"/>).</summary>
public enum CameraShutter
{
    /// <summary>The user, on the shutter pane.</summary>
    User,

    /// <summary>The app, once the user allows it.</summary>
    Model,
}

/// <summary>How a shot is previewed (<see cref="CameraPreviewMode"/>).</summary>
public enum CameraPreview
{
    /// <summary>The viewer shows the camera live while the shutter pane is open.</summary>
    Live,

    /// <summary>The viewer opens on the shot once it is taken.</summary>
    Post,

    /// <summary>No window.</summary>
    Disabled,
}

/// <summary>
/// The setting <c>Camera shutter</c> (2026-10-02, the user's call): <c>user</c> or <c>model</c>. The <c>SttDestinationMode</c>
/// shape: <see cref="Resolve"/> is the one place the saved string becomes the enum.
/// </summary>
public static class CameraShutterMode
{
    public const string Default = "user";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = ["user", "model"];

    public static bool TryParse(string? text, out CameraShutter shutter)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "user": shutter = CameraShutter.User; return true;
            case "model": shutter = CameraShutter.Model; return true;
            default: shutter = CameraShutter.User; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "user" => "you take the photo the model asks for, on the camera pane",
        "model" => "you allow the photo, and the app takes it",
        _ => "",
    };

    public static CameraShutter Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.CameraShutter, out var shutter))
        {
            return shutter;
        }

        DiagnosticLog.Warn("Camera", $"{nameof(AppSettingsData.CameraShutter)}='{effective.CameraShutter}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return CameraShutter.User;
    }
}

/// <summary>The setting <c>Camera preview</c> (2026-10-02, the user's call): <c>live</c>, <c>post</c> or <c>disabled</c>.</summary>
public static class CameraPreviewMode
{
    public const string Default = "live";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = ["live", "post", "disabled"];

    public static bool TryParse(string? text, out CameraPreview preview)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "live": preview = CameraPreview.Live; return true;
            case "post": preview = CameraPreview.Post; return true;
            case "disabled": preview = CameraPreview.Disabled; return true;
            default: preview = CameraPreview.Live; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "live" => "the picture viewer shows the camera live while you frame the shot",
        "post" => "the picture viewer opens on the shot once it is taken",
        "disabled" => "no preview window",
        _ => "",
    };

    public static CameraPreview Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.CameraPreview, out var preview))
        {
            return preview;
        }

        DiagnosticLog.Warn("Camera", $"{nameof(AppSettingsData.CameraPreview)}='{effective.CameraPreview}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return CameraPreview.Live;
    }
}

/// <summary>The session's options as the settings stand: the saved device and the resolution (an unknown one reads as the default).</summary>
public static class CameraSettings
{
    public static CameraOptions Options(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (!CameraFormats.TryParseSize(effective.CameraResolution, out var size))
        {
            _ = CameraFormats.TryParseSize(AppSettingsData.DefaultCameraResolution, out size);
        }

        return new CameraOptions(effective.CameraDevice ?? "", size);
    }
}

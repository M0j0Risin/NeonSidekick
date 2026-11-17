using NeonSidekick.Web;

namespace NeonSidekick.Render;

/// <summary>
/// The pictures drawn for the model (2026-11-16, plan sharded-hugging-puppy #9): <see cref="IHeadlessBrowser.RenderPictureAsync"/> through the
/// browser <c>Web browser path</c> names, found again at every call as <c>Pdf/PdfReading</c> does, so a browser installed or a path typed in
/// <c>/settings</c> counts at once. No browser: a draw says so and how to set one.
/// </summary>
public sealed class PictureRendering(IHeadlessBrowser browser, Func<string> configuredPath)
{
    private readonly IHeadlessBrowser _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    private readonly Func<string> _configuredPath = configuredPath ?? throw new ArgumentNullException(nameof(configuredPath));

    /// <summary>The browser a draw would run now; null for none.</summary>
    public string? Executable => _browser.Locate(_configuredPath());

    public async Task<PictureRender> RenderAsync(RenderJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        string configured = _configuredPath();
        if (_browser.Locate(configured) is not { } executable)
        {
            return new PictureRender(false, RenderText.NoBrowser(configured.Trim()), null, 0, 0) { NoBrowser = true };
        }

        return await _browser.RenderPictureAsync(executable, job, cancellationToken).ConfigureAwait(false);
    }
}

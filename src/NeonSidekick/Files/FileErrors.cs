namespace NeonSidekick.Files;

/// <summary>
/// What an <see cref="IOException"/> says about the name it was about (2026-11-02, review P13-4/P13-8): the "a file is there" answers were spelled
/// in <c>OwnerLock</c> alone, and the custom commands editor read any failure as a name clash when the file stood there, a file its own call
/// had made included. Pure.
/// </summary>
public static class FileErrors
{
    // Windows' "a file is there" answers (a create-new, a move without replace): ERROR_FILE_EXISTS and ERROR_ALREADY_EXISTS as HRESULTs.
    private const int FileExistsHResult = unchecked((int)0x80070050);
    private const int AlreadyExistsHResult = unchecked((int)0x800700B7);

    /// <summary>EEXIST, the Unix errno (macOS and Linux) for a name already taken; .NET puts it in an <see cref="IOException"/>'s <c>HResult</c> there.</summary>
    public const int Eexist = 17;

    /// <summary>Whether <paramref name="ex"/> says the name was taken already, rather than that the write itself failed.</summary>
    public static bool IsExists(IOException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.HResult is FileExistsHResult or AlreadyExistsHResult || (!OperatingSystem.IsWindows() && ex.HResult == Eexist);
    }
}

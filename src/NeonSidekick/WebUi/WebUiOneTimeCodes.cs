using System.Security.Cryptography;
using System.Text;

namespace NeonSidekick.WebUi;

/// <summary>
/// The Web UI's one-time sign-in codes (later on 2026-10-15, <c>/webui browser</c> and <i>Web browser sign in mode</i> <c>one-time-link</c>): 32
/// random bytes in base64url, each good once and for <see cref="Lifetime"/>, at most <see cref="MaxOutstanding"/> at a time (a ninth drops the
/// oldest). A code is taken whether or not it is still good, compared in constant time against every one held; none is ever logged. The host
/// clears them at every start and stop, so a restart leaves only dead codes. Thread-safe (a code is minted on the screen's thread and taken on a
/// Kestrel one).
/// </summary>
public sealed class WebUiOneTimeCodes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public const int MaxOutstanding = 8;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<(string Code, long Minted)> _codes = [];

    public WebUiOneTimeCodes(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    public string Mint()
    {
        string code = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            _codes.Add((code, _time.GetTimestamp()));
            while (_codes.Count > MaxOutstanding)
            {
                _codes.RemoveAt(0);
            }
        }

        return code;
    }

    /// <summary>Whether <paramref name="presented"/> is a code held and still good; a code matched is spent either way.</summary>
    public bool Take(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[] given = Encoding.UTF8.GetBytes(presented);
        lock (_gate)
        {
            int match = -1;
            for (int i = 0; i < _codes.Count; i++)
            {
                byte[] held = Encoding.UTF8.GetBytes(_codes[i].Code);
                if (held.Length == given.Length && CryptographicOperations.FixedTimeEquals(held, given))
                {
                    match = i;
                }
            }

            if (match < 0)
            {
                return false;
            }

            var (_, minted) = _codes[match];
            _codes.RemoveAt(match);
            return _time.GetElapsedTime(minted) <= Lifetime;
        }
    }

    /// <summary>Every code dropped (the server starting or stopping).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _codes.Clear();
        }
    }

    /// <summary>How many are held (the tests').</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _codes.Count;
            }
        }
    }
}

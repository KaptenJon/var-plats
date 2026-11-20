using System.Globalization;

namespace VarPlats.Web;

/// <summary>Write commands that are limited per account when <c>Reporting:RateLimits:Enabled</c> is on.</summary>
public enum WriteKind
{
    Report,
    Action,
    Confirmation,
    Flag,
    Photo
}

public sealed class RateLimitWindow
{
    public const int MaxPermitLimit = 10_000;
    public const int MaxWindowSeconds = 86_400;

    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>
/// Bound from <c>Reporting:RateLimits</c>. Off by default. The defaults are conservative for a small prototype:
/// they stop scripted floods from one account without getting in the way of a person who reports a handful of
/// places. <see cref="Api"/> limits <c>GET /api/reports</c> per client address that the server sees.
/// </summary>
public sealed class RateLimitOptions
{
    public bool Enabled { get; set; }
    public RateLimitWindow Reports { get; set; } = new() { PermitLimit = 10, WindowSeconds = 3600 };
    public RateLimitWindow Actions { get; set; } = new() { PermitLimit = 20, WindowSeconds = 3600 };
    public RateLimitWindow Confirmations { get; set; } = new() { PermitLimit = 60, WindowSeconds = 3600 };
    public RateLimitWindow Flags { get; set; } = new() { PermitLimit = 20, WindowSeconds = 3600 };
    public RateLimitWindow Photos { get; set; } = new() { PermitLimit = 10, WindowSeconds = 3600 };
    public RateLimitWindow Api { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };

    public RateLimitWindow For(WriteKind kind) => kind switch
    {
        WriteKind.Report => Reports,
        WriteKind.Action => Actions,
        WriteKind.Confirmation => Confirmations,
        WriteKind.Flag => Flags,
        WriteKind.Photo => Photos,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Checked at startup whether or not the switch is on, so a bad value is never latent.</summary>
    public void Validate()
    {
        foreach (var (name, window) in new[]
        {
            (nameof(Reports), Reports), (nameof(Actions), Actions), (nameof(Confirmations), Confirmations),
            (nameof(Flags), Flags), (nameof(Photos), Photos), (nameof(Api), Api)
        })
        {
            if (window is null
                || window.PermitLimit is < 1 or > RateLimitWindow.MaxPermitLimit
                || window.WindowSeconds is < 1 or > RateLimitWindow.MaxWindowSeconds)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"Reporting:RateLimits:{name} kräver PermitLimit 1–{RateLimitWindow.MaxPermitLimit} och WindowSeconds 1–{RateLimitWindow.MaxWindowSeconds}."));
        }
    }
}

/// <summary>Thrown before anything is saved when an account has used up a write limit.</summary>
public sealed class WriteRateLimitException(WriteKind kind, TimeSpan retryAfter)
    : Exception(UserMessage(kind, retryAfter))
{
    public WriteKind Kind { get; } = kind;
    public TimeSpan RetryAfter { get; } = retryAfter;

    private static string UserMessage(WriteKind kind, TimeSpan retryAfter)
    {
        var what = kind switch
        {
            WriteKind.Report => "rapporter",
            WriteKind.Action => "åtgärdsuppgifter",
            WriteKind.Confirmation => "bekräftelser",
            WriteKind.Flag => "anmälningar",
            _ => "bilduppladdningar"
        };
        var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
        var wait = minutes == 1 ? "en minut" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minuter");
        return $"Gräns nådd: ditt konto har skickat för många {what} på kort tid. Inget sparades. Vänta cirka {wait} och försök igen.";
    }
}

/// <summary>
/// In-process fixed-window limiter keyed on account id and write kind. Blazor components call the command
/// classes in-process, so HTTP rate limiting middleware never sees these writes. State is per process and is
/// lost on restart, which is acceptable for a single-instance prototype.
/// </summary>
public sealed class WriteRateLimiter(RateLimitOptions options, TimeProvider time, ILogger<WriteRateLimiter> logger)
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private readonly Lock gate = new();
    private readonly Dictionary<(Guid Account, WriteKind Kind), Window> windows = [];
    private DateTimeOffset nextSweep = DateTimeOffset.MinValue;

    public bool Enabled => options.Enabled;

    /// <summary>
    /// Takes one permit from every listed kind, or none if any of them is exhausted. A photo upload of a report
    /// counts both as a report and as a photo.
    /// </summary>
    public void Acquire(Guid accountId, params ReadOnlySpan<WriteKind> kinds)
    {
        if (!options.Enabled)
            return;
        var now = time.GetUtcNow();
        lock (gate)
        {
            Sweep(now);
            foreach (var kind in kinds)
            {
                var limit = options.For(kind);
                if (windows.TryGetValue((accountId, kind), out var window)
                    && now < window.Start + limit.Window
                    && window.Count >= limit.PermitLimit)
                {
                    // No account id or content: the kind is enough to tune limits.
                    logger.LogWarning("Skrivgräns nådd för {WriteKind}.", kind);
                    throw new WriteRateLimitException(kind, window.Start + limit.Window - now);
                }
            }
            foreach (var kind in kinds)
            {
                var limit = options.For(kind);
                if (windows.TryGetValue((accountId, kind), out var window) && now < window.Start + limit.Window)
                    window.Count++;
                else
                    windows[(accountId, kind)] = new Window(now);
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now < nextSweep)
            return;
        nextSweep = now + SweepInterval;
        foreach (var key in windows.Where(entry => now >= entry.Value.Start + options.For(entry.Key.Kind).Window)
                     .Select(entry => entry.Key).ToList())
            windows.Remove(key);
    }

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;
        public int Count { get; set; } = 1;
    }
}

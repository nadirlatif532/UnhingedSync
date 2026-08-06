using UnhingedSync.Models;

namespace UnhingedSync.ViewModels;

public enum BadgeKind { None, Available, Failed, Syncing, Expired, Building }

/// <summary>One row of the commit list -- a commit joined with its build status.</summary>
public sealed class CommitRowViewModel
{
    public required CommitInfo Commit { get; init; }
    public BuildRecord? Record { get; init; }
    public bool IsWorkspace { get; init; }
    public bool IsInstalled { get; init; }
    public string? ClaimedBy { get; init; }

    public string CommitLabel => Commit.Ordinal > 0 ? $"#{Commit.Ordinal}" : Commit.CommitId;

    // Sort keys, because the displayed values sort wrongly. "#9" beats "#52"
    // alphabetically, "Aug 5" beats "Jul 30", and "9.4 MB" beats "27.7 MB". The grid
    // columns point at these instead of at the text the user reads.
    public int OrdinalSort => Commit.Ordinal;
    public DateTimeOffset WhenSort => Commit.Date ?? DateTimeOffset.MinValue;
    public long SizeSort => Record?.ZipBytes ?? 0;

    /// <summary>
    /// Ranks the badge so sorting by state groups usefully: ready first, then in progress,
    /// then problems, then nothing. Sorting by the glyph would order by character code.
    /// </summary>
    public int StateSort => Badge switch
    {
        BadgeKind.Available => 0,
        BadgeKind.Building => 1,
        BadgeKind.Syncing => 2,
        BadgeKind.Failed => 3,
        BadgeKind.Expired => 4,
        _ => 5
    };
    public string Author => ShortenEmail(Commit.AuthorEmail);
    public string When => Commit.Date is { } d ? d.ToLocalTime().ToString("MMM d, HH:mm") : "";
    public string Message => Commit.Message;

    public BadgeKind Badge => ClaimedBy is not null
        ? BadgeKind.Building
        : Record?.Status switch
        {
            "success" => BadgeKind.Available,
            "failed" => BadgeKind.Failed,
            "syncing" => BadgeKind.Syncing,
            "expired" => BadgeKind.Expired,
            _ => BadgeKind.None
        };

    public string BadgeGlyph => Badge switch
    {
        BadgeKind.Available => "●",
        BadgeKind.Failed => "✕",
        BadgeKind.Syncing => "◔",
        BadgeKind.Expired => "○",
        BadgeKind.Building => "▶",
        _ => "–"
    };

    public string BadgeColour => Badge switch
    {
        BadgeKind.Available => "#4EC96A",
        BadgeKind.Failed => "#E05561",
        BadgeKind.Syncing => "#E5C07B",
        BadgeKind.Expired => "#6B6B70",
        BadgeKind.Building => "#61AFEF",
        _ => "#4A4A4F"
    };

    public string BinariesText => Badge switch
    {
        BadgeKind.Available => Record?.ZipBytes > 0 ? $"{Record.ZipBytes / 1024.0 / 1024.0:0.#} MB" : "available",
        BadgeKind.Failed => "build failed",
        BadgeKind.Syncing => "syncing…",
        BadgeKind.Expired => "expired",
        BadgeKind.Building => $"building on {ClaimedBy}",
        _ => "none"
    };

    public string Marker => (IsWorkspace, IsInstalled) switch
    {
        (true, true) => "▶ ✓",
        (true, false) => "▶",
        (false, true) => "✓",
        _ => ""
    };

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Commit.CommitId };
            if (IsWorkspace) lines.Add("Your workspace is on this commit");
            if (IsInstalled) lines.Add("Your installed binaries are from this commit");
            if (Record is not null)
            {
                lines.Add($"Build: {Record.Status} on {Record.BuiltBy}");
                if (Record.BuildDurationSeconds > 0)
                    lines.Add($"Took {Record.BuildDurationSeconds}s, {Record.FileCount} files");
                if (!string.IsNullOrEmpty(Record.EngineVersion))
                    lines.Add($"Engine {Record.EngineVersion} (BuildId {Record.EngineBuildId})");
            }
            if (ClaimedBy is not null) lines.Add($"{ClaimedBy} is building this now");
            return string.Join('\n', lines);
        }
    }

    private static string ShortenEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return "";
        var at = email.IndexOf('@');
        return at > 0 ? email[..at] : email;
    }
}

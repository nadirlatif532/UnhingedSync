using System.Net.Http;

namespace UnhingedSync.Services;

/// <summary>
/// What this machine is allowed to do with the shared folder.
///
/// The role recorded by the Syncthing setup script is a hint, not proof. Syncthing's own
/// folder type is the only authority on whether a write here reaches anybody else, so a
/// decision that deletes other people's data has to be based on that, and on nothing else.
/// </summary>
/// <param name="Role">artist, programmer, buildhost, or empty when never recorded.</param>
/// <param name="FolderType">
/// Syncthing's type for the folder: sendreceive, receiveonly, sendonly. Empty when
/// Syncthing could not be reached, which is not the same as "no restriction".
/// </param>
/// <param name="SyncthingReachable">Whether the answers above could be confirmed at all.</param>
public sealed record ShareRoleInfo(string Role, string FolderType, bool SyncthingReachable)
{
    private bool IsReceiveOnly =>
        FolderType.Equals("receiveonly", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether deleting from the share here actually frees space for the team.
    ///
    /// Deliberately requires a confirmed folder type rather than trusting the recorded
    /// role. A machine can have "programmer" written in config.local.json while its folder
    /// is receive-only (set up by hand, or the role changed later), and on a receive-only
    /// folder Syncthing keeps local deletions to itself: nothing is freed for anyone, the
    /// folder is flagged as locally modified forever, and the Revert button Syncthing then
    /// offers pulls every deleted zip straight back. Guessing wrong in that direction
    /// destroys data locally and lies about having helped, so an unverifiable answer counts
    /// as no.
    /// </summary>
    public bool WritesReachTeam =>
        SyncthingReachable && FolderType.Length > 0 && !IsReceiveOnly && Role is not "artist";

    /// <summary>
    /// Whether this machine may hand out introducer trust. Introducer implies
    /// auto-accepting whatever that peer offers, which is a decision about who is allowed
    /// onto the share, not something to give every artist's laptop.
    /// </summary>
    public bool MayGrantIntroducer => Role is "programmer" or "buildhost";

    public bool IsKnown => Role.Length > 0 || FolderType.Length > 0;
}

public static class ShareRole
{
    /// <summary>
    /// Resolves the role, priming the client first.
    ///
    /// GetStatusAsync is not optional here: it is what loads the API key and resolves the
    /// address Syncthing is really serving on. Calling GetFolderTypeAsync on a fresh client
    /// without it sends an empty API key to a hardcoded port, gets a 403, and reports an
    /// empty folder type indistinguishable from "send-receive" -- which is exactly how a
    /// receive-only guard ends up passing.
    /// </summary>
    public static async Task<ShareRoleInfo> ResolveAsync(
        SyncthingClient client, string folderId, CancellationToken ct = default)
    {
        var reachable = false;
        var folderType = "";

        try
        {
            var status = await client.GetStatusAsync(ct);
            reachable = status.State == SyncthingState.Running;
            if (reachable) folderType = await client.GetFolderTypeAsync(folderId, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            reachable = false;
        }

        var role = ConfigLoader.GetRole();
        if (string.IsNullOrEmpty(role) && folderType.Length > 0)
        {
            role = folderType.Equals("receiveonly", StringComparison.OrdinalIgnoreCase)
                ? "artist"
                : "programmer";
        }

        return new ShareRoleInfo(role, folderType, reachable);
    }
}

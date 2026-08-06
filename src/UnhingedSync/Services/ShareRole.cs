using System.Net.Http;

namespace UnhingedSync.Services;

/// <summary>
/// What this machine is allowed to do with the shared folder.
///
/// The role is recorded by the Syncthing setup script, but a machine set up before that
/// was recorded, or set up by hand, has none. Syncthing's own folder type is the reliable
/// fallback: receive-only is an artist by construction, because that is exactly what the
/// setup script gives them.
/// </summary>
public sealed record ShareRoleInfo(string Role, string FolderType)
{
    /// <summary>
    /// Whether writes to the share reach the rest of the team. On a receive-only folder
    /// Syncthing keeps local changes to itself and flags the folder as locally modified,
    /// so deleting a build there frees space on this machine only, leaves the folder
    /// permanently out of sync, and offers a Revert button that undoes the whole thing.
    /// </summary>
    public bool WritesReachTeam =>
        !FolderType.Equals("receiveonly", StringComparison.OrdinalIgnoreCase) &&
        Role is not "artist";

    /// <summary>
    /// Whether this machine may hand out introducer trust. Introducer implies
    /// auto-accepting whatever that peer offers, which is a decision about who is allowed
    /// onto the share, not something to give every artist's laptop.
    /// </summary>
    public bool MayGrantIntroducer => Role is "programmer" or "buildhost";

    public bool IsKnown => !string.IsNullOrEmpty(Role) || !string.IsNullOrEmpty(FolderType);
}

public static class ShareRole
{
    public static async Task<ShareRoleInfo> ResolveAsync(
        SyncthingClient client, string folderId, CancellationToken ct = default)
    {
        var folderType = "";
        try { folderType = await client.GetFolderTypeAsync(folderId, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }

        var role = ConfigLoader.GetRole();
        if (string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(folderType))
        {
            role = folderType.Equals("receiveonly", StringComparison.OrdinalIgnoreCase)
                ? "artist"
                : "programmer";
        }

        return new ShareRoleInfo(role, folderType);
    }
}

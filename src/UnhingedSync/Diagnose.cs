using System.IO;
using System.Text.Json;
using UnhingedSync.Services;

namespace UnhingedSync;

/// <summary>
/// Reports what the app can see of the local Syncthing, so the pairing plumbing can be
/// checked without driving the window.
///   UnhingedSync.exe --syncthing [outputPath]
/// </summary>
public static class Diagnose
{
    public static async Task<int> RunSyncthingAsync(string? outputPath)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), "unhingedsync-syncthing.json");

        object report;
        var ok = false;

        try
        {
            var config = ConfigLoader.Load();
            var client = new SyncthingClient();
            var status = await client.GetStatusAsync();

            if (status.State != SyncthingState.Running)
            {
                report = new
                {
                    ok = false,
                    state = status.State.ToString(),
                    detail = status.Detail,
                    configPath = client.ConfigPath
                };
            }
            else
            {
                var pendingDevices = await client.GetPendingDevicesAsync();
                var pendingFolders = await client.GetPendingFoldersAsync();
                var peers = await client.GetPeersAsync(config.SyncthingFolderId);
                var completion = await client.GetLocalCompletionAsync(config.SyncthingFolderId);

                var recordedRole = ConfigLoader.GetRole();
                var folderType = await client.GetFolderTypeAsync(config.SyncthingFolderId);
                var effectiveRole = !string.IsNullOrEmpty(recordedRole)
                    ? recordedRole
                    : folderType.Equals("receiveonly", StringComparison.OrdinalIgnoreCase)
                        ? "artist"
                        : string.IsNullOrEmpty(folderType) ? "" : "programmer";

                ok = true;
                report = new
                {
                    ok,
                    state = status.State.ToString(),
                    recordedRole,
                    folderType,
                    effectiveRole,
                    mayGrantIntroducer = effectiveRole is "programmer" or "buildhost",
                    configPath = client.ConfigPath,
                    webUi = client.WebUiUri,
                    myDeviceId = status.DeviceId,
                    folderId = config.SyncthingFolderId,
                    publishRoot = config.PublishRoot,
                    localCompletionPercent = completion,
                    pendingDevices = pendingDevices.Select(d => new { d.DeviceId, d.Name }),
                    pendingFolders = pendingFolders.Select(f => new { f.FolderId, f.Label, f.OfferedByDeviceId }),
                    peers = peers.Select(p => new { p.DeviceId, p.Name, p.SharesOurFolder, p.CompletionPercent })
                };
            }
        }
        catch (Exception e)
        {
            report = new { ok = false, error = $"{e.GetType().Name}: {e.Message}" };
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
        Console.Error.WriteLine(json);

        return ok ? 0 : 1;
    }
}

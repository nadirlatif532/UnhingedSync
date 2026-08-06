using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using UnhingedSync.Models;
using UnhingedSync.Services;

namespace UnhingedSync;

public partial class PeersWindow : Window
{
    private enum PendingKind { Device, Folder }

    private sealed record PendingItem(
        PendingKind Kind, string Title, string Subtitle,
        string DeviceId, string FolderId, string Label);

    /// <summary>Shown in place of the device ID before one is known, and checked for
    /// before letting Copy put it on the clipboard.</summary>
    private const string NoDeviceId = "not available";

    private readonly AppConfig _config;
    private readonly SyncthingClient _syncthing = new();
    private bool _mayIntroduce;

    public PeersWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var status = await _syncthing.GetStatusAsync();

        RunSetupButton.Visibility = status.State == SyncthingState.Running
            ? Visibility.Collapsed : Visibility.Visible;
        AddPeerButton.IsEnabled = status.State == SyncthingState.Running;

        if (status.State != SyncthingState.Running)
        {
            StatusHeadline.Text = status.State switch
            {
                SyncthingState.NotInstalled => "Syncthing is not set up on this machine",
                SyncthingState.Unauthorized => "Syncthing is running but will not let this app in",
                _ => "Syncthing is not running"
            };
            StatusDetail.Text = status.Detail ?? "";
            MyDeviceId.Text = NoDeviceId;
            PendingList.ItemsSource = null;
            PeersList.ItemsSource = null;
            NoPending.Visibility = Visibility.Collapsed;
            NoPeers.Visibility = Visibility.Collapsed;
            FolderStatus.Text = "";
            return;
        }

        StatusHeadline.Text = "Syncthing is running";
        StatusDetail.Text = $"Folder '{_config.SyncthingFolderId}' at {_config.PublishRoot}";
        MyDeviceId.Text = status.DeviceId ?? "unknown";

        await ApplyRolePolicyAsync();

        try
        {
            var pending = new List<PendingItem>();

            foreach (var device in await _syncthing.GetPendingDevicesAsync())
            {
                pending.Add(new PendingItem(
                    PendingKind.Device,
                    $"{device.Name} wants to connect",
                    $"{device.DeviceId}   {device.Address}".Trim(),
                    device.DeviceId, "", ""));
            }

            foreach (var folder in await _syncthing.GetPendingFoldersAsync())
            {
                pending.Add(new PendingItem(
                    PendingKind.Folder,
                    $"Someone shared '{folder.Label}' with you",
                    $"Accepting puts it in {_config.PublishRoot}",
                    folder.OfferedByDeviceId, folder.FolderId, folder.Label));
            }

            PendingList.ItemsSource = pending;
            NoPending.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var peers = await _syncthing.GetPeersAsync(_config.SyncthingFolderId);
            PeersList.ItemsSource = peers.Select(p => new
            {
                p.Name,
                p.DeviceId,
                ShareText = (p.SharesOurFolder
                    ? $"Sharing the binaries folder, {p.CompletionPercent}% in sync with them"
                    : "Connected, but not sharing the binaries folder yet")
                    + (p.IsIntroducer ? "   ·   hub (introduces others to you)" : "")
            }).ToList();
            NoPeers.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var local = await _syncthing.GetLocalCompletionAsync(_config.SyncthingFolderId);
            FolderStatus.Text = $"Your copy of the folder: {local}% complete";
        }
        catch (Exception e)
        {
            StatusDetail.Text = $"Could not read Syncthing state: {e.Message}";
        }
    }

    private async void AddPeer_Click(object sender, RoutedEventArgs e)
    {
        var id = PeerIdBox.Text.Trim();

        // Syncthing IDs are 8 groups of 7 characters. Catching an obvious paste error
        // here is friendlier than letting the API reject it.
        if (id.Replace("-", "").Length != 56)
        {
            MessageBox.Show(
                "That does not look like a Syncthing device ID. They are 56 characters, " +
                "usually shown as eight groups of seven separated by dashes.",
                "Check the device ID", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            AddPeerButton.IsEnabled = false;
            var isIntroducer = IntroducerCheck.IsChecked == true;

            await _syncthing.AddDeviceAsync(id, PeerName(isIntroducer), isIntroducer);
            await _syncthing.ShareFolderWithAsync(_config.SyncthingFolderId, id);
            PeerIdBox.Clear();
            IntroducerCheck.IsChecked = false;

            MessageBox.Show(
                "Added, and the binaries folder is now offered to them.\n\n" +
                (isIntroducer
                    ? "Marked as your hub, so their other devices will be introduced to you automatically.\n\n"
                    : "") +
                "They still have to accept on their side. Until they do, nothing transfers.",
                "Invitation sent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not add that device",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            AddPeerButton.IsEnabled = true;
            await RefreshAsync();
        }
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PendingItem item }) return;

        try
        {
            if (item.Kind == PendingKind.Device)
            {
                // Same policy as the checkbox: only a building machine may grant this.
                var asHub = _mayIntroduce && MessageBox.Show(
                    "Is this the machine that hosts the share for your team, the one everyone " +
                    "pairs with?\n\n" +
                    "Yes: their other devices get introduced to you automatically, so you only " +
                    "ever pair once.\n" +
                    "No: treat them as an ordinary teammate.",
                    "Is this your team's hub?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

                await _syncthing.AddDeviceAsync(item.DeviceId, PeerName(asHub), asHub);
                await _syncthing.ShareFolderWithAsync(_config.SyncthingFolderId, item.DeviceId);
            }
            else
            {
                // Anyone who cannot compile has no business publishing, and receive-only
                // is also what stops an artist's machine from pushing junk into the share.
                var receiveOnly = MessageBox.Show(
                    "Will you build and publish binaries from this machine?\n\n" +
                    "Yes: you can publish (programmers, build machines).\n" +
                    "No: receive only, which is right for artists and designers.",
                    "How should this machine use the folder?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No;

                Directory.CreateDirectory(_config.PublishRoot);
                await _syncthing.AcceptFolderAsync(
                    item.FolderId, item.Label, _config.PublishRoot, item.DeviceId, receiveOnly);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not accept that",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    /// <summary>
    /// Only a machine that builds may grant introducer trust. Introducer implies
    /// auto-accepting devices and folders that machine offers, which is a decision about
    /// who is allowed onto the share -- not something to hand to every artist's laptop.
    ///
    /// Consequence, stated plainly because it is a real trade-off: an artist who cannot
    /// mark the hub as introducer will only ever sync with the peers they were explicitly
    /// paired with, usually just the hub. That works, it simply means the hub carries
    /// their traffic instead of the mesh spreading it.
    /// </summary>
    private async Task ApplyRolePolicyAsync()
    {
        var role = ConfigLoader.GetRole();

        if (string.IsNullOrEmpty(role))
        {
            // Setup either has not run or predates the role being recorded. Fall back to
            // what Syncthing itself says: receive-only is an artist by construction.
            var folderType = await _syncthing.GetFolderTypeAsync(_config.SyncthingFolderId);
            role = folderType.Equals("receiveonly", StringComparison.OrdinalIgnoreCase)
                ? "artist"
                : string.IsNullOrEmpty(folderType) ? "" : "programmer";
        }

        var mayIntroduce = role is "programmer" or "buildhost";
        _mayIntroduce = mayIntroduce;

        IntroducerCheck.IsEnabled = mayIntroduce;
        if (!mayIntroduce) IntroducerCheck.IsChecked = false;

        IntroducerNote.Text = mayIntroduce
            ? "Leave unticked for an ordinary teammate. Ticking it also lets that machine " +
              "offer you new folders, which is why it should only be someone you'd trust to " +
              "run the share."
            : role switch
            {
                "artist" =>
                    "Only machines set up as programmer or build host can mark a hub as " +
                    "introducer. This one is set up as an artist, so it syncs with the peers " +
                    "it is paired with. Ask the hub owner to add you.",
                _ =>
                    "Syncthing setup has not run on this machine yet, so its role is unknown. " +
                    "Run the setup first if you need to mark a hub as introducer."
            };

        RoleBadge.Text = string.IsNullOrEmpty(role) ? "role: unknown" : $"role: {role}";
    }

    private string PeerName(bool isIntroducer) =>
        isIntroducer ? $"{_config.ProjectName} hub" : $"{_config.ProjectName} teammate";

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MyDeviceId.Text) || MyDeviceId.Text == NoDeviceId) return;
        try
        {
            Clipboard.SetText(MyDeviceId.Text);
            FolderStatus.Text = "Device ID copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process can hold the clipboard open; not worth a dialog.
            FolderStatus.Text = "Could not reach the clipboard. Select the ID and copy it manually.";
        }
    }

    private void OpenWebUi_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_syncthing.WebUiUri) { UseShellExecute = true });

    private void RunSetup_Click(object sender, RoutedEventArgs e)
    {
        var bat = EmbeddedScripts.SetupBat;
        if (!File.Exists(bat))
        {
            MessageBox.Show($"Setup script could not be extracted:\n{bat}", "Missing setup script",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // Its own console window: it installs software and asks questions. The project is
        // passed through the environment rather than as an argument, because an argument
        // would put the .bat into pass-through mode and skip its interactive menu.
        var psi = new ProcessStartInfo(bat) { UseShellExecute = false };
        psi.EnvironmentVariables["UNHINGEDSYNC_PROJECT_ROOT"] = _config.ProjectRoot;
        Process.Start(psi);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}

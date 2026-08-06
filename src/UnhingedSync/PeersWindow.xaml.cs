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

    /// <summary>
    /// One row of the peer list. CanToggleHub travels with the row rather than being bound
    /// to the window, because an ItemsControl row has no straightforward path to it.
    /// </summary>
    private sealed record PeerRow(
        string Name, string DeviceId, string ShareText, bool IsHub, bool CanToggleHub)
    {
        public string HubButtonText => IsHub ? "Not a hub" : "Make hub";
    }

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

        var running = status.State == SyncthingState.Running;

        RunSetupButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        AddPeerButton.IsEnabled = running;

        // Everything that writes to Syncthing is inert without it, and a rename box holding
        // a name nobody can save is worse than an empty one.
        SaveNameButton.IsEnabled = running;
        MyNameBox.IsEnabled = running;
        ShareAllButton.IsEnabled = running;

        if (!running)
        {
            StatusHeadline.Text = status.State switch
            {
                SyncthingState.NotInstalled => "Syncthing is not set up on this machine",
                SyncthingState.Unauthorized => "Syncthing is running but will not let this app in",
                _ => "Syncthing is not running"
            };
            StatusDetail.Text = status.Detail ?? "";
            MyDeviceId.Text = NoDeviceId;
            MyNameBox.Text = "";
            PendingList.ItemsSource = null;
            PeersList.ItemsSource = null;
            NoPending.Visibility = Visibility.Collapsed;
            NoPeers.Visibility = Visibility.Collapsed;
            FolderStatus.Text = "";
            HubSummary.Text = "";
            ShareAllButton.IsEnabled = false;
            return;
        }

        StatusHeadline.Text = "Syncthing is running";
        StatusDetail.Text = $"Folder '{_config.SyncthingFolderId}' at {_config.PublishRoot}";
        MyDeviceId.Text = status.DeviceId ?? "unknown";

        // Not overwritten while the user is partway through typing a new one.
        if (!MyNameBox.IsKeyboardFocusWithin)
            MyNameBox.Text = await _syncthing.GetOwnDeviceNameAsync();

        await ApplyRolePolicyAsync();

        try
        {
            var pending = new List<PendingItem>();

            foreach (var device in await _syncthing.GetPendingDevicesAsync())
            {
                // Label carries the name the device reported for itself. For a device there
                // is no folder label to put there, and Accept needs the raw name rather than
                // the "... wants to connect" sentence built for display.
                pending.Add(new PendingItem(
                    PendingKind.Device,
                    $"{device.Name} wants to connect",
                    $"{device.DeviceId}   {device.Address}".Trim(),
                    device.DeviceId, "", device.Name));
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
            PeersList.ItemsSource = peers.Select(p => new PeerRow(
                p.Name,
                p.DeviceId,
                (p.SharesOurFolder
                    ? $"Sharing the binaries folder, {p.CompletionPercent}% in sync with them"
                    : "Connected, but not sharing the binaries folder yet")
                + (p.IsIntroducer ? "   ·   hub (introduces others to you)" : ""),
                p.IsIntroducer,
                _mayIntroduce)).ToList();
            NoPeers.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var hubCount = peers.Count(p => p.IsIntroducer);
            HubSummary.Text = hubCount == 0
                ? "No hub set. You will only sync with peers you pair with by hand."
                : $"{hubCount} hub(s): {string.Join(", ", peers.Where(p => p.IsIntroducer).Select(p => p.Name))}";

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

            // A device we are inviting has not contacted us, so there is no name of theirs
            // to fall back on. Whatever was typed is all we have.
            await _syncthing.AddDeviceAsync(id, PeerName(isIntroducer, null, id), isIntroducer);
            await _syncthing.ShareFolderWithAsync(_config.SyncthingFolderId, id);
            PeerIdBox.Clear();
            PeerNameBox.Clear();
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

                // item.Title carries the name the requesting device reported for itself,
                // which is the best answer available unless the user typed one.
                await _syncthing.AddDeviceAsync(
                    item.DeviceId, PeerName(asHub, item.Label, item.DeviceId), asHub);
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

        ActAsHubNote.Text =
            "You become the team's hub when teammates tick \"they are our hub\" against your " +
            "device ID. There is nothing to switch on here. Use this after anyone joins, " +
            "because a device a hub introduces is paired without being offered the folder.";

        RoleBadge.Text = string.IsNullOrEmpty(role) ? "role: unknown" : $"role: {role}";
    }

    /// <summary>
    /// Settles what to call a peer, best information first.
    ///
    /// This used to return nothing but "&lt;Project&gt; teammate" for everybody, which on a team
    /// of thirty produced thirty identical rows and made the peer list useless for the one
    /// thing it is for: seeing who is in sync. Worse, a device requesting a connection
    /// already tells us its own name, and that was being thrown away.
    ///
    /// So: what the user typed, else what the device calls itself, else a labelled fallback
    /// with a few ID characters so at least two unnamed peers can be told apart.
    /// </summary>
    private string PeerName(bool isIntroducer, string? theirOwnName, string deviceId)
    {
        if (PeerNameBox.Text.Trim() is { Length: > 0 } typed) return typed;

        if (theirOwnName?.Trim() is { Length: > 0 } reported &&
            !reported.Equals("(unnamed)", StringComparison.Ordinal))
            return reported;

        var suffix = deviceId.Replace("-", "");
        suffix = suffix.Length >= 7 ? suffix[..7] : deviceId;
        return isIntroducer ? $"{_config.ProjectName} hub ({suffix})" : $"teammate {suffix}";
    }

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

    private async void SaveName_Click(object sender, RoutedEventArgs e)
    {
        var name = MyNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Give this machine a name your teammates will recognise.",
                "Name required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SaveNameButton.IsEnabled = false;
            await _syncthing.SetOwnDeviceNameAsync(name);
            FolderStatus.Text = $"This machine now appears as '{name}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not rename this machine",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveNameButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Promotes or demotes one peer as a hub. Same policy as everything else that grants
    /// introducer trust: only a machine that builds may do it.
    /// </summary>
    private async void ToggleHub_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PeerRow row }) return;
        if (!_mayIntroduce) return;

        if (!row.IsHub)
        {
            var confirm = MessageBox.Show(this,
                $"Treat '{row.Name}' as a hub?\n\n" +
                "That means this machine will automatically accept the devices they introduce, " +
                "and will let them offer you new folders. Only do this for a machine you would " +
                "trust to run the share.",
                "Make this peer a hub", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        try
        {
            await _syncthing.SetPeerIsHubAsync(row.DeviceId, !row.IsHub);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not change that peer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    /// <summary>
    /// Offers the binaries folder to every peer not already receiving it.
    ///
    /// This is the one mechanical duty a hub owner actually has. Syncthing has no "I am a
    /// hub" flag to set: the introducer bit lives on everyone else's machine, so you become
    /// the hub only when teammates tick the box against your device ID. What does NOT happen
    /// automatically is folder sharing. A device introduced by a hub is added to the device
    /// list without being offered any folder, so it looks correctly paired and receives
    /// nothing at all until someone runs this.
    ///
    /// This replaced a checkbox that claimed to make this machine the hub. That checkbox
    /// wrote a flag nothing ever read, which is worse than not having it.
    /// </summary>
    private async void ShareAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShareAllButton.IsEnabled = false;

            var peers = await _syncthing.GetPeersAsync(_config.SyncthingFolderId);
            var missing = peers.Where(p => !p.SharesOurFolder).ToList();

            if (missing.Count == 0)
            {
                FolderStatus.Text = "Every peer is already being offered the folder.";
                return;
            }

            foreach (var peer in missing)
                await _syncthing.ShareFolderWithAsync(_config.SyncthingFolderId, peer.DeviceId);

            FolderStatus.Text = $"Offered the folder to {missing.Count} more peer(s).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not offer the folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ShareAllButton.IsEnabled = true;
            await RefreshAsync();
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

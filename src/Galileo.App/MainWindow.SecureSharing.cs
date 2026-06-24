using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Galileo;

public sealed partial class MainWindow
{
    // ===================== Secure peer-to-peer sharing (UI) =====================
    // Crypto/transport live in the Services layer (PeerIdentity, SecureChannel, ShareProtocol,
    // SecureSharing). This is the WinUI glue, dialog-driven (no new XAML panel) to avoid XAML-load risk
    // and keep the surface deniable. Model: a mutual friend list (request → accept), per-vault grants
    // either side can set/revoke, and B browses shares via Ctrl+Alt+V after entering their passphrase.

    private SecureSharing? _sharing;
    private bool _accessLogRevealed;          // access-log entry hidden until Ctrl+Alt+L
    private bool _sharingEventsAttached;

    private void RelayUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SecureRelayUrl = RelayUrlBox.Text.Trim();
        _state.Save();
    }

    // Entry points -----------------------------------------------------------

    private async void ManageSharing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureIdentityAsync()) return;
            await EnsureOnlineAsync();
            await ShowSharingHubAsync();
        }
        catch (Exception ex) { await MessageAsync("Secure sharing", ex.Message); App.Log("Sharing", ex); }
    }

    /// <summary>Ctrl+Alt+V: open a local vault, or browse what friends share with you.</summary>
    private async void OpenVaultShortcutAsync()
    {
        try
        {
            if (!SecureSharing.Exists()) { await ShowVaultPickerAsync(); return; }
            var dlg = new ContentDialog
            {
                Title = "Open",
                Content = new TextBlock { Text = "Open one of your local vaults, or browse what friends are sharing with you." , TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Shared with me",
                SecondaryButtonText = "Local vault",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary) await OpenSharesAsync();
            else if (r == ContentDialogResult.Secondary) await ShowVaultPickerAsync();
        }
        catch (Exception ex) { await MessageAsync("Secure sharing", ex.Message); App.Log("Sharing", ex); }
    }

    /// <summary>Command-strip "Share" button (visible inside an unlocked vault): pick which friends get it.</summary>
    private async void VaultShare_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vaults.Current is null) { await MessageAsync("Share", "Unlock a vault first."); return; }
            if (!await EnsureIdentityAsync()) return;
            await EnsureOnlineAsync();
            await ShareCurrentVaultAsync();
        }
        catch (Exception ex) { await MessageAsync("Share", ex.Message); App.Log("Sharing", ex); }
    }

    /// <summary>Unlocks the existing identity (passphrase) or runs first-time setup. Returns true if ready.</summary>
    private async Task<bool> EnsureIdentityAsync()
    {
        if (_sharing is not null) return true;
        if (SecureSharing.Exists())
        {
            var pass = await PromptPassphraseAsync("Secure sharing", "Enter your sharing passphrase.", "Unlock");
            if (pass is null) return false;
            try { _sharing = SecureSharing.Open(pass); }
            catch (CryptographicException) { await MessageAsync("Secure sharing", "Wrong passphrase."); return false; }
        }
        else
        {
            await FirstRunSetupAsync();
            if (_sharing is null) return false;
        }
        AttachSharingEvents();
        return true;
    }

    private void AttachSharingEvents()
    {
        if (_sharing is null || _sharingEventsAttached) return;
        _sharingEventsAttached = true;
        // Friend requests surface in the hub's friends list (Accept there); just log here.
        _sharing.FriendRequestReceived += f => App.Log("Sharing", new Exception($"friend request from {f.Alias} ({f.Uuid})"));
        // Owner locked/revoked while we were browsing them → tear our copy down immediately.
        _sharing.ShareRevokedByOwner += peer => RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (_remoteBrowse?.Session.PeerUuid != peer) return;
            var dir = _remoteBrowse.Dir;
            if (string.Equals(_currentFolder, dir, StringComparison.OrdinalIgnoreCase)
                || (_currentFolder?.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ?? false))
                NavigateTo(null); // leaving triggers CleanupRemoteBrowse (secure wipe)
            else
                CleanupRemoteBrowse();
            StatusText.Text = "The owner stopped sharing — the shared files were removed.";
        });
    }

    // First-run: create or recover --------------------------------------------

    private async Task FirstRunSetupAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "Set up secure sharing",
            Content = new TextBlock
            {
                Text = "Create a new identity (you'll get a recovery phrase to back up), or recover one from its "
                     + "phrase. You'll choose a display name friends will see, and a passphrase that protects the "
                     + "identity on this device.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Create new",
            SecondaryButtonText = "Recover",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        var choice = await dlg.ShowAsync();
        if (choice == ContentDialogResult.Primary) await CreateIdentityAsync();
        else if (choice == ContentDialogResult.Secondary) await RecoverIdentityAsync();
    }

    private async Task CreateIdentityAsync()
    {
        var (alias, pass) = await PromptAliasAndPassphraseAsync(null);
        if (alias is null || pass is null) return;
        var (sharing, seed) = SecureSharing.CreateNew(pass, alias);
        _sharing = sharing;
        AttachSharingEvents();
        await ShowRecoveryPhraseAsync(seed);
    }

    private async Task RecoverIdentityAsync()
    {
        var phraseBox = new TextBox { PlaceholderText = "twelve words…", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Enter your recovery phrase.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(phraseBox);
        var dlg = new ContentDialog
        {
            Title = "Recover identity", Content = panel, PrimaryButtonText = "Next",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (!PeerIdentity.ValidateSeedPhrase(phraseBox.Text))
        {
            await MessageAsync("Recover identity", "That doesn't look like a valid recovery phrase.");
            return;
        }
        var (alias, pass) = await PromptAliasAndPassphraseAsync(null);
        if (alias is null || pass is null) return;
        _sharing = SecureSharing.Recover(phraseBox.Text, pass, alias);
        AttachSharingEvents();
    }

    private async Task ShowRecoveryPhraseAsync(string seed)
    {
        var box = new TextBox
        {
            Text = seed, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        var copy = new Button { Content = "Copy phrase" };
        copy.Click += (_, _) => SetClipboard(seed);
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "Write down this recovery phrase and keep it offline. It's the only backup of your identity.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(copy);
        await new ContentDialog { Title = "Your recovery phrase", Content = panel, CloseButtonText = "I've saved it", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    // The hub ----------------------------------------------------------------

    private async Task ShowSharingHubAsync()
    {
        _accessLogRevealed = false;
        while (_sharing is not null)
        {
            string? action = null;
            var dlg = new ContentDialog { Title = "Secure sharing", CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };
            var panel = new StackPanel { Spacing = 8, MinWidth = 360 };

            panel.Children.Add(new TextBlock
            {
                Text = (_sharing.IsOnline ? "● Online" : "○ Offline (relay unreachable)") + $"   ·   You are \"{_sharing.Alias}\"",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(_sharing.IsOnline ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.Gray),
                FontWeight = FontWeights.SemiBold,
            });

            Button Hub(string text, string act)
            {
                var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
                b.Click += (_, _) => { action = act; dlg.Hide(); };
                return b;
            }

            panel.Children.Add(Hub("Show my ID & fingerprint", "show"));
            panel.Children.Add(Hub("Add a friend…", "addfriend"));
            if (_vaults.Current is not null)
                panel.Children.Add(Hub($"Share \"{_vaults.Current.Name}\" with friends…", "grant"));
            panel.Children.Add(Hub("Refresh", "refresh"));

            // Friends
            var friends = _sharing.Friends.ToList();
            if (friends.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = "Friends", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                foreach (var f in friends)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    var label = string.IsNullOrWhiteSpace(f.Alias) ? f.Uuid[..8] : f.Alias;
                    if (f.IsLinked)
                    {
                        row.Children.Add(new TextBlock { Text = "● " + label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 });
                        row.Children.Add(Hub("Browse", "browse:" + f.Uuid));
                        row.Children.Add(Hub("Unfriend", "unfriend:" + f.Uuid));
                    }
                    else if (f.Status == "pending_in")
                    {
                        row.Children.Add(new TextBlock { Text = $"{label} wants to connect", VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 });
                        row.Children.Add(Hub("Accept", "accept:" + f.Uuid));
                        row.Children.Add(Hub("Decline", "unfriend:" + f.Uuid));
                    }
                    else // pending_out
                    {
                        row.Children.Add(new TextBlock { Text = $"{label} — request sent", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
                        row.Children.Add(Hub("Cancel", "unfriend:" + f.Uuid));
                    }
                    panel.Children.Add(row);
                }
            }

            if (_accessLogRevealed) panel.Children.Add(Hub("View access log", "audit"));

            var regen = Hub("Delete identity & regenerate…", "regen");
            regen.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            regen.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(regen);

            panel.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.L && IsCtrlDown() && IsAltDown() && !_accessLogRevealed)
                { _accessLogRevealed = true; action = "refresh"; dlg.Hide(); }
            };

            dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 520 };
            await dlg.ShowAsync();

            if (action is null) break;
            if (action == "show") await ShowMyIdentityAsync();
            else if (action == "addfriend") await AddFriendAsync();
            else if (action == "grant") await ShareCurrentVaultAsync();
            else if (action == "audit") await ShowAuditAsync();
            else if (action == "regen") await RegenerateIdentityAsync();
            else if (action == "refresh") { }
            else if (action.StartsWith("accept:") && Guid.TryParse(action[7..], out var au)) await DoAsync(() => _sharing!.AcceptFriendAsync(au), "Accept");
            else if (action.StartsWith("unfriend:") && Guid.TryParse(action[9..], out var uu)) await DoAsync(() => _sharing!.UnfriendAsync(uu), "Unfriend");
            else if (action.StartsWith("browse:") && Guid.TryParse(action[7..], out var bu)) await BrowsePeerAsync(bu);

            if (_sharing is null) break;
        }
    }

    private async Task DoAsync(Func<Task> op, string title)
    {
        try { await op(); } catch (Exception ex) { await MessageAsync(title, ex.Message); }
    }

    private async Task ShowMyIdentityAsync()
    {
        if (_sharing is null) return;
        var uuid = _sharing.Identity.Uuid.ToString();
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = $"Display name: {_sharing.Alias}" });
        panel.Children.Add(new TextBlock { Text = "Your ID (share this so friends can add you):" });
        panel.Children.Add(new TextBox { Text = uuid, IsReadOnly = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        panel.Children.Add(new TextBlock { Text = "Safety number (verify out-of-band it matches on both devices):", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBox { Text = _sharing.Identity.Fingerprint, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        var copy = new Button { Content = "Copy ID" };
        copy.Click += (_, _) => SetClipboard(uuid);
        panel.Children.Add(copy);
        await new ContentDialog { Title = "My identity", Content = panel, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    private async Task AddFriendAsync()
    {
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Add a friend", "You're offline — check the relay URL in Settings."); return; }
        var idBox = new TextBox { PlaceholderText = "friend's ID (a UUID)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = "Send a friend request by their ID. They must accept before you're linked." });
        panel.Children.Add(idBox);
        var dlg = new ContentDialog
        {
            Title = "Add a friend", Content = panel, PrimaryButtonText = "Send request",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (!Guid.TryParse(idBox.Text.Trim(), out var to)) { await MessageAsync("Add a friend", "That's not a valid ID."); return; }
        try { await _sharing.SendFriendRequestAsync(to); await MessageAsync("Add a friend", "Request sent. They'll appear as linked once they accept."); }
        catch (Exception ex) { await MessageAsync("Add a friend", ex.Message); }
    }

    private async Task ShareCurrentVaultAsync()
    {
        if (_sharing is null || _vaults.Current is null) { await MessageAsync("Share", "Unlock a vault first."); return; }
        var linked = _sharing.LinkedFriends.ToList();
        if (linked.Count == 0) { await MessageAsync("Share", "Add a friend first."); return; }
        var vaultId = _vaults.Current.Id;
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = $"Share \"{_vaults.Current.Name}\" with:", TextWrapping = TextWrapping.Wrap });
        foreach (var f in linked)
        {
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
            var name = new TextBlock { Text = string.IsNullOrWhiteSpace(f.Alias) ? f.Uuid[..8] : f.Alias, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new ToggleSwitch { IsOn = _sharing.IsGranted(vaultId, Guid.Parse(f.Uuid)) };
            Grid.SetColumn(toggle, 1);
            var fid = Guid.Parse(f.Uuid);
            toggle.Toggled += (_, _) => { try { _sharing!.SetGrant(vaultId, fid, toggle.IsOn); } catch { } };
            row.Children.Add(name); row.Children.Add(toggle);
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = "Friends can browse it live while this vault is unlocked and you're online. Revoke any time.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        await new ContentDialog { Title = "Share vault", Content = panel, CloseButtonText = "Done", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    // Browsing — present a friend's shared vault in the real file explorer ----
    // Files stream into a temp working folder (read-only copy, wiped on the next browse / next launch);
    // the explorer then shows them with thumbnails, gallery, viewer — exactly like any other folder.

    private sealed class RemoteBrowse
    {
        public required SecureSession Session { get; set; } // replaced if the session dies and we reconnect
        public required string Dir { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        // full temp path (lowercased) -> shared object id, so opening a temp file maps back to the share.
        public required Dictionary<string, string> PathToId { get; init; }
        // Serializes list/download so an initial sync and a refresh can't race the session's request/response.
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
    private RemoteBrowse? _remoteBrowse;
    private string? _currentRemoteViewId; // the shared file currently open in the viewer (for view/close audit)
    private readonly DispatcherTimer _remoteSyncTimer = new() { Interval = TimeSpan.FromSeconds(8) }; // live auto-sync

    /// <summary>Securely wipe any leftover remote-browse temp folders (called at startup and on exit).</summary>
    private static void WipeShareTempDirs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "GalileoShare");
            if (Directory.Exists(root)) VaultCrypto.WipeDirectory(root);
        }
        catch { /* best effort */ }
    }

    /// <summary>If we're navigating out of the current shared-browse folder, tear it down and securely wipe
    /// the downloaded copies — decrypted shared files never linger once you leave.</summary>
    private void CheckLeftRemoteBrowse(string? target)
    {
        var rb = _remoteBrowse;
        if (rb is null) return;
        var inside = target is not null &&
            (string.Equals(target, rb.Dir, StringComparison.OrdinalIgnoreCase)
             || target.StartsWith(rb.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!inside) CleanupRemoteBrowse();
    }

    // Hub "Browse <friend>" → open that friend's shared vault in the explorer.
    private async Task BrowsePeerAsync(Guid peer)
    {
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Browse", "You're offline."); return; }
        SecureSession? session = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            session = await _sharing.ConnectToPeerAsync(peer, cts.Token);
            var listing = await ShareProtocol.ListAsync(_sharing.Relay, session, cts.Token);
            if (listing.Items.Count == 0) { session.Dispose(); await MessageAsync("Browse", "That friend isn't sharing anything right now."); return; }
            StartRemoteBrowse(session, listing);
            session = null; // ownership handed to _remoteBrowse
        }
        catch (Exception ex) { await MessageAsync("Browse", "Couldn't reach that friend (online + sharing?). " + ex.Message); }
        finally { session?.Dispose(); }
    }

    /// <summary>Ctrl+Alt+V → "Shared with me": find which friends are sharing, pick one, open it in the explorer.</summary>
    private async Task OpenSharesAsync()
    {
        if (!await EnsureIdentityAsync()) return;
        await EnsureOnlineAsync();
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Shared with me", "You're offline — check the relay URL in Settings."); return; }

        var found = new List<(Friend f, SecureSession s, SharedListing l)>();
        foreach (var f in _sharing.LinkedFriends)
        {
            if (!Guid.TryParse(f.Uuid, out var fid)) continue;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var s = await _sharing.ConnectToPeerAsync(fid, cts.Token);
                var l = await ShareProtocol.ListAsync(_sharing.Relay, s, cts.Token);
                if (l.Items.Count == 0) { s.Dispose(); continue; }
                found.Add((f, s, l));
            }
            catch { /* friend offline or not sharing — skip */ }
        }

        if (found.Count == 0) { await MessageAsync("Shared with me", "No friends are sharing anything with you right now."); return; }

        var pick = found.Count == 1 ? 0 : await ChooseShareAsync(found);
        if (pick < 0) { foreach (var x in found) x.s.Dispose(); return; }
        for (var i = 0; i < found.Count; i++) if (i != pick) found[i].s.Dispose();
        StartRemoteBrowse(found[pick].s, found[pick].l);
    }

    private async Task<int> ChooseShareAsync(List<(Friend f, SecureSession s, SharedListing l)> found)
    {
        var result = -1;
        var dlg = new ContentDialog { Title = "Shared with me", CloseButtonText = "Cancel", XamlRoot = RootGrid.XamlRoot };
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Open a shared vault:" });
        for (var i = 0; i < found.Count; i++)
        {
            var idx = i;
            var who = string.IsNullOrWhiteSpace(found[i].f.Alias) ? found[i].f.Uuid[..8] : found[i].f.Alias;
            var b = new Button { Content = $"{who} — {found[i].l.VaultName}  ({found[i].l.Items.Count} files)", HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => { result = idx; dlg.Hide(); };
            panel.Children.Add(b);
        }
        dlg.Content = panel;
        await dlg.ShowAsync();
        return result;
    }

    // Open a friend's shared vault in the explorer. Files stream into a temp folder; refreshing (F5)
    // re-lists against the live vault so adds/deletes/changes on the owner's side show up.
    private void StartRemoteBrowse(SecureSession session, SharedListing listing)
    {
        CleanupRemoteBrowse(); // tear down any previous browse
        var dir = Path.Combine(Path.GetTempPath(), "GalileoShare", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _remoteBrowse = new RemoteBrowse
        {
            Session = session, Dir = dir, Cts = new CancellationTokenSource(),
            PathToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        ShowExplorer();
        NavigateTo(dir);
        _ = Task.Run(() => SyncRemoteBrowseAsync(_remoteBrowse, listing));
        _remoteSyncTimer.Start(); // keep it live: auto-sync the owner's adds/deletes every few seconds
    }

    /// <summary>Re-sync the open shared folder against the owner's live vault (F5): pull new/changed files,
    /// remove ones the owner deleted, drop stale partials.</summary>
    private void RefreshRemoteBrowse()
    {
        var rb = _remoteBrowse;
        if (rb is null) return;
        StatusText.Text = "Refreshing shared vault…";
        _ = Task.Run(() => SyncRemoteBrowseAsync(rb, null)); // null → re-list from the owner
    }

    /// <summary>Periodic auto-sync so the owner's adds/deletes show up without a manual refresh.</summary>
    private void RemoteSyncTick()
    {
        var rb = _remoteBrowse;
        if (rb is null) { _remoteSyncTimer.Stop(); return; }
        if (rb.Gate.CurrentCount == 0) return; // a sync is already in progress
        _ = Task.Run(() => SyncRemoteBrowseAsync(rb, null));
    }

    private async Task SyncRemoteBrowseAsync(RemoteBrowse rb, SharedListing? listing)
    {
        var relay = _sharing?.Relay;
        if (relay is null) return;
        await rb.Gate.WaitAsync();
        try
        {
            // Re-list from the owner unless we were handed a fresh listing (initial open). If the session
            // has died (owner reconnected / a relay drop), re-establish it once and retry — otherwise a
            // refresh would silently keep stale files.
            if (listing is null)
            {
                try
                {
                    using var lcts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    listing = await ShareProtocol.ListAsync(relay, rb.Session, lcts.Token);
                }
                catch
                {
                    try
                    {
                        using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                        var fresh = await _sharing!.ConnectToPeerAsync(rb.Session.PeerUuid, rcts.Token);
                        var old = rb.Session; rb.Session = fresh; try { old.Dispose(); } catch { }
                        listing = await ShareProtocol.ListAsync(relay, fresh, rcts.Token);
                    }
                    catch { return; } // owner offline — keep what we have
                }
            }
            App.LogInfo($"remote sync: owner lists {listing.Items.Count} item(s) for {listing.VaultName}");
            var dir = rb.Dir;
            var sep = Path.DirectorySeparatorChar;

            // Desired state: dest path -> object id.
            var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in listing.Items)
                desired[Path.Combine(dir, it.Name.Replace('/', sep))] = it.Id;

            // Remove files the owner deleted, and any leftover partials.
            var removed = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !desired.ContainsKey(f))
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); removed++; } catch { }
            }
            catch { }
            if (removed > 0) App.LogInfo($"remote sync: removed {removed} file(s) the owner deleted");

            rb.PathToId.Clear();
            foreach (var kv in desired) rb.PathToId[kv.Key] = kv.Value;

            var items = listing.Items;
            var name = listing.VaultName;
            var done = 0;
            foreach (var it in items)
            {
                if (rb.Cts.IsCancellationRequested) return;
                var dest = Path.Combine(dir, it.Name.Replace('/', sep));
                // Download only what we don't already have at the right size (handles adds + changes).
                if (!File.Exists(dest) || new FileInfo(dest).Length != it.Size)
                {
                    var part = dest + ".part";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using (var fs = File.Create(part))
                        {
                            try { File.SetAttributes(part, FileAttributes.Hidden); } catch { } // hide the partial from the view
                            await ShareProtocol.FetchAsync(relay, rb.Session, it.Id, it.Size, fs, null, rb.Cts.Token);
                        }
                        File.Move(part, dest, overwrite: true);
                        try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                    }
                    catch { try { File.Delete(part); } catch { } } // clear the partial if the file is gone / failed
                }
                done++;
                var d = done; var total = items.Count;
                RootGrid.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_remoteBrowse?.Dir != dir) return; // a newer browse replaced us
                    if (string.Equals(_currentFolder, dir, StringComparison.OrdinalIgnoreCase)) RefreshFolderIncremental();
                    StatusText.Text = d < total ? $"{name}: syncing {d}/{total}…" : $"{name} — {total} file(s) (shared, read-only)";
                });
            }
            // Final refresh so deletions disappear even if nothing was downloaded.
            RootGrid.DispatcherQueue.TryEnqueue(() =>
            {
                if (_remoteBrowse?.Dir == dir && string.Equals(_currentFolder, dir, StringComparison.OrdinalIgnoreCase))
                    RefreshFolderIncremental();
            });
        }
        catch (Exception ex) { App.Log("RemoteSync", ex); }
        finally { rb.Gate.Release(); }
    }

    private void CleanupRemoteBrowse()
    {
        _remoteSyncTimer.Stop();
        NoteRemoteView(null); // close out any in-viewer access first
        var rb = _remoteBrowse;
        _remoteBrowse = null;
        if (rb is null) return;
        try { rb.Cts.Cancel(); } catch { }
        _ = FinishRemoteBrowseAsync(rb); // signal the owner we left, then dispose + securely wipe
    }

    private async Task FinishRemoteBrowseAsync(RemoteBrowse rb)
    {
        // Tell the host we've closed the folder (logged in their access log) before tearing the session down.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ShareProtocol.EndBrowseAsync(_sharing!.Relay, rb.Session, cts.Token);
        }
        catch { /* best effort */ }
        try { rb.Session.Dispose(); } catch { }
        rb.Cts.Dispose();
        rb.Gate.Dispose();
        try { if (Directory.Exists(rb.Dir)) VaultCrypto.WipeDirectory(rb.Dir); } catch { }
    }

    /// <summary>Call when the actively-viewed file changes (image load / video open), or null when leaving
    /// the viewer. If the file belongs to the current remote browse, signal the owner so their access log
    /// records what was actually viewed (open) and when it was closed (duration).</summary>
    private void NoteRemoteView(string? path)
    {
        var rb = _remoteBrowse;
        string? newId = null;
        if (rb is not null && path is not null && rb.PathToId.TryGetValue(path, out var id)) newId = id;
        if (newId == _currentRemoteViewId) return; // unchanged

        var prev = _currentRemoteViewId;
        _currentRemoteViewId = newId;
        if (rb is null) { _currentRemoteViewId = null; return; }

        if (prev is not null) _ = SafeSignalAsync(rb.Session, prev, open: false);
        if (newId is not null) _ = SafeSignalAsync(rb.Session, newId, open: true);
    }

    private async Task SafeSignalAsync(SecureSession session, string id, bool open)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (open) await ShareProtocol.ViewAsync(_sharing!.Relay, session, id, cts.Token);
            else await ShareProtocol.CloseAsync(_sharing!.Relay, session, id, cts.Token);
        }
        catch { /* best effort */ }
    }

    // Online + regenerate ----------------------------------------------------

    private bool _sharingOnlineDeclined; // skip re-prompting after the user cancels once this session

    /// <summary>Called when a vault is unlocked: bring sharing online so friends can reach it. Silent if the
    /// identity is already loaded; otherwise a single passphrase prompt (skipped if declined this session,
    /// or if no sharing identity exists — preserving deniability).</summary>
    private async Task MaybeBringSharingOnlineAsync()
    {
        try
        {
            if (_sharing is not null) { await EnsureOnlineAsync(); return; }
            if (_sharingOnlineDeclined || !SecureSharing.Exists()) return;

            var pass = await PromptPassphraseAsync("Secure sharing",
                "Bring secure sharing online so friends can access the vaults you share with them? "
                + "Enter your sharing passphrase, or cancel to skip.", "Go online");
            if (pass is null) { _sharingOnlineDeclined = true; return; }
            try { _sharing = SecureSharing.Open(pass); }
            catch (CryptographicException) { await MessageAsync("Secure sharing", "Wrong passphrase."); return; }
            AttachSharingEvents();
            await EnsureOnlineAsync();
        }
        catch (Exception ex) { App.Log("Sharing", ex); }
    }

    private async Task EnsureOnlineAsync()
    {
        if (_sharing is null || _sharing.IsOnline) return;
        var url = _state.SecureRelayUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        // Serve the currently-unlocked vault to a friend only while it's granted to them (checked live).
        Func<Guid, IShareSource?> shareForPeer = peer =>
            new GrantGatedSource(
                () => _vaults.Current is not null && _sharing is not null && _sharing.IsGranted(_vaults.Current.Id, peer),
                new LiveCurrentVaultSource(this));
        App.LogInfo($"sharing: going online to {url}");
        try { await _sharing.GoOnlineAsync(url, shareForPeer); App.LogInfo("sharing: online"); }
        catch (Exception ex) { App.LogInfo("sharing: go-online failed: " + ex.Message); }
    }

    private async Task RegenerateIdentityAsync()
    {
        if (_sharing is null) return;
        var confirm = new ContentDialog
        {
            Title = "Delete identity & regenerate",
            Content = new TextBlock
            {
                Text = "This permanently erases your current identity, alias, friends and shares from this device "
                     + "and creates a brand-new identity with a new ID. Friends will no longer recognise you. This "
                     + "can't be undone unless you backed up the old recovery phrase.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete & regenerate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await _sharing.GoOfflineAsync();
        _sharing.Dispose();
        _sharing = null;
        _sharingEventsAttached = false;
        SecureSharing.DeleteStore();

        await CreateIdentityAsync();
        await EnsureOnlineAsync();
    }

    // Access log -------------------------------------------------------------

    private async Task ShowAuditAsync()
    {
        if (_sharing is null) return;
        var url = _state.SecureRelayUrl;
        if (string.IsNullOrWhiteSpace(url)) { await MessageAsync("Access log", "Set a relay server URL in Settings first."); return; }

        const string TimeFmt = "yyyy-MM-dd HH:mm:ss";

        // Turn a fresh batch of audit records into display rows. Names/paths are re-resolved on every call
        // so the log tracks whichever vault is currently unlocked. Newest-first.
        List<(DateTimeOffset t, string title, string detail, string? path)> BuildRows(IReadOnlyList<RelayClient.AuditRecord> records)
        {
            var names = new Dictionary<string, string>();
            if (_vaults.Current is not null)
                foreach (var en in _vaults.Current.ShareEntries()) names[en.BlobId] = en.RelPath;

            string PeerName(Guid v)
            {
                var n = _sharing!.Friends.FirstOrDefault(p => p.Uuid == v.ToString())?.Alias;
                return string.IsNullOrWhiteSpace(n) ? v.ToString()[..8] : n!;
            }
            string FileName(string id) => names.TryGetValue(id, out var f) ? f : "(item no longer shared)";
            string? PathFor(string objectId)
            {
                var wd = _vaults.Current?.WorkingDir;
                if (wd is null || !names.TryGetValue(objectId, out var rel)) return null;
                var p = Path.Combine(wd, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(p) ? p : null;
            }

            var asc = records.OrderBy(r => r.Time).ToList();
            var openAt = new Dictionary<string, DateTimeOffset>();
            var rows = new List<(DateTimeOffset t, string title, string detail, string? path)>();
            foreach (var r in asc)
            {
                var who = PeerName(r.Viewer);
                var key = r.Viewer + "|" + r.ObjectId;
                switch (r.Action)
                {
                    case "list":
                        rows.Add((r.Time, "Opened your shared vault", $"by {who}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", null));
                        break;
                    case "browse_end":
                        rows.Add((r.Time, "Closed your shared vault", $"by {who}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", null));
                        break;
                    case "fetch":
                        rows.Add((r.Time, FileName(r.ObjectId), $"downloaded by {who}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", PathFor(r.ObjectId)));
                        break;
                    case "open":
                        openAt[key] = r.Time;
                        break;
                    case "close" when openAt.TryGetValue(key, out var t):
                        rows.Add((t, FileName(r.ObjectId),
                            $"viewed by {who}   ·   {t.LocalDateTime.ToString(TimeFmt)} → {r.Time.LocalDateTime:HH:mm:ss}  ({FormatDuration(r.Time - t)})", PathFor(r.ObjectId)));
                        openAt.Remove(key);
                        break;
                }
            }
            foreach (var kv in openAt) // opens still without a close
            {
                var parts = kv.Key.Split('|', 2);
                Guid.TryParse(parts[0], out var v);
                rows.Add((kv.Value, FileName(parts[1]), $"viewing by {PeerName(v)}   ·   {kv.Value.LocalDateTime.ToString(TimeFmt)}  (still open)", PathFor(parts[1])));
            }
            rows.Sort((a, b) => b.t.CompareTo(a.t)); // newest first
            return rows;
        }

        var dlg = new ContentDialog
        {
            Title = "Access log",
            CloseButtonText = "Close",
            PrimaryButtonText = "Clear log",
            SecondaryButtonText = "Export…",
            XamlRoot = RootGrid.XamlRoot,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 1200.0; // default cap (~548) is far too narrow for the log

        var list = new StackPanel { Spacing = 12, MinWidth = 680 };
        var scroller = new ScrollViewer
        {
            Content = list,
            MinWidth = 700,
            MaxHeight = 620,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        dlg.Content = scroller;

        var currentRows = new List<(DateTimeOffset t, string title, string detail, string? path)>();

        void Populate(List<(DateTimeOffset t, string title, string detail, string? path)> rows)
        {
            currentRows = rows;
            list.Children.Clear();
            if (rows.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "No file access recorded yet.", Opacity = 0.7 });
                return;
            }
            foreach (var (_, title, detail, path) in rows)
            {
                var card = new StackPanel { Spacing = 1 };
                if (path is not null)
                {
                    // Clickable link: open the file in this window (its vault is unlocked) — closes the log.
                    var link = new HyperlinkButton { Content = title, Padding = new Thickness(0), FontWeight = FontWeights.SemiBold };
                    var p = path;
                    link.Click += (_, _) => { dlg.Hide(); _ = OpenLocalFileInViewerAsync(p); };
                    if (PhotoLibrary.IsSupported(p)) AttachImageHoverPreview(link, p); // hover thumbnail for images
                    card.Children.Add(link);
                }
                else
                {
                    card.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                }
                card.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                list.Children.Add(card);
            }
        }

        // Re-query the relay and refresh the list in place; auto-scroll to the top (newest) when new
        // entries have arrived, so the log "tails" live while it's open.
        var refreshing = false;
        var lastCount = -1;
        async Task RefreshAsync()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var records = await _sharing!.QueryAuditAsync(url);
                Populate(BuildRows(records));
                if (records.Count != lastCount)
                {
                    var grew = records.Count > lastCount && lastCount >= 0;
                    lastCount = records.Count;
                    if (grew) { scroller.UpdateLayout(); scroller.ChangeView(null, 0, null, true); }
                }
            }
            catch { /* transient relay hiccup — keep the last view, try again next tick */ }
            finally { refreshing = false; }
        }

        async Task ExportAsync()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Galileo access log — exported {DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                if (currentRows.Count == 0) sb.AppendLine("No file access recorded yet.");
                foreach (var (_, title, detail, _) in currentRows) { sb.AppendLine(title); sb.AppendLine("    " + detail); sb.AppendLine(); }

                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "galileo-access-log" };
                picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                await File.WriteAllTextAsync(file.Path, sb.ToString());
                StatusText.Text = $"Access log exported to {file.Path}";
            }
            catch (Exception ex) { StatusText.Text = "Access log export failed: " + ex.Message; }
        }

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; // clear, then keep the (now-empty) log open and live
            var def = args.GetDeferral();
            try { await _sharing!.ClearAuditAsync(url); lastCount = -1; await RefreshAsync(); } catch { }
            def.Complete();
        };
        dlg.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; // exporting must not close the log
            var def = args.GetDeferral();
            try { await ExportAsync(); } finally { def.Complete(); }
        };

        // Initial load, then poll the relay every few seconds for live updates while the dialog is open.
        await RefreshAsync();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.Tick += (_, _) => _ = RefreshAsync();
        timer.Start();
        dlg.Closing += (_, _) => timer.Stop();

        await dlg.ShowAsync();
    }

    /// <summary>Shows a small image thumbnail in a tooltip when the pointer hovers over <paramref name="target"/>.
    /// The bitmap is loaded lazily (only when the tooltip first opens) and decoded small, so building a long log
    /// doesn't read every file. Used for image links in the access log.</summary>
    private void AttachImageHoverPreview(FrameworkElement target, string path)
    {
        var img = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxWidth = 360, MaxHeight = 360 };
        var tip = new ToolTip
        {
            Padding = new Thickness(4),
            Content = new Border { CornerRadius = new CornerRadius(6), Child = img },
        };
        tip.Opened += async (_, _) =>
        {
            if (img.Source is not null) return; // load once
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 360 };
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());
                img.Source = bmp;
            }
            catch { /* unreadable / unsupported — leave the tooltip empty */ }
        };
        ToolTipService.SetToolTip(target, tip);
    }

    /// <summary>Opens a local file in THIS window (image → viewer, video/audio → player, else default app).
    /// Used by access-log links and for vault files (which must never spawn a second instance).</summary>
    private async Task OpenLocalFileInViewerAsync(string path)
    {
        try
        {
            if (PhotoLibrary.IsSupported(path)) await OpenSinglePhotoAsync(path);
            else if (PhotoLibrary.IsMedia(path))
            {
                var fi = new FileInfo(path);
                var item = new Models.ExplorerItem(path, Models.ExplorerItemKind.File, fi.Length, fi.LastWriteTime, fi.Extension);
                OpenVideoFromExplorer(item);
            }
            else
            {
                try { ShellOps.AllowForeground(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
                catch (Exception ex) { StatusText.Text = ex.Message; }
            }
        }
        catch (Exception ex) { App.Log("OpenLocalFile", ex); }
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 1) return "<1s";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{(int)d.TotalHours}h {d.Minutes}m";
    }

    // Small dialog helpers ---------------------------------------------------

    private async Task<string?> PromptPassphraseAsync(string title, string label, string primary)
    {
        var pw = new PasswordBox { PlaceholderText = "Passphrase" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(pw);
        var dlg = new ContentDialog
        {
            Title = title, Content = panel, PrimaryButtonText = primary, CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary && pw.Password.Length > 0 ? pw.Password : null;
    }

    private async Task<(string? alias, string? pass)> PromptAliasAndPassphraseAsync(string? existingAlias)
    {
        var aliasBox = new TextBox { PlaceholderText = "display name (friends see this)", Text = existingAlias ?? "" };
        var pw1 = new PasswordBox { PlaceholderText = "Passphrase" };
        var pw2 = new PasswordBox { PlaceholderText = "Confirm passphrase" };
        var err = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = "Choose a display name and a passphrase to protect this identity on this device.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(aliasBox);
        panel.Children.Add(pw1);
        panel.Children.Add(pw2);
        panel.Children.Add(err);
        var dlg = new ContentDialog
        {
            Title = "New identity", Content = panel, PrimaryButtonText = "OK", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(aliasBox.Text)) { err.Text = "Enter a display name."; err.Visibility = Visibility.Visible; args.Cancel = true; }
            else if (pw1.Password.Length < 6) { err.Text = "Passphrase: at least 6 characters."; err.Visibility = Visibility.Visible; args.Cancel = true; }
            else if (pw1.Password != pw2.Password) { err.Text = "Passphrases don't match."; err.Visibility = Visibility.Visible; args.Cancel = true; }
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? (aliasBox.Text.Trim(), pw1.Password) : (null, null);
    }

    private Task MessageAsync(string title, string message) =>
        new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK", XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync().AsTask();

    private static void SetClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    /// <summary>An IShareSource that always reflects the currently-unlocked vault (so unlocking a different
    /// vault changes what's served without reconnecting).</summary>
    private sealed class LiveCurrentVaultSource : IShareSource
    {
        private readonly MainWindow _mw;
        public LiveCurrentVaultSource(MainWindow mw) => _mw = mw;
        public string ShareName => _mw._vaults.Current?.ShareName ?? "";
        public IReadOnlyList<VaultEntry> ShareEntries() => _mw._vaults.Current?.ShareEntries() ?? Array.Empty<VaultEntry>();
        public Stream OpenSharedEntry(string blobId) =>
            _mw._vaults.Current?.OpenSharedEntry(blobId) ?? throw new FileNotFoundException("Not shared.");
    }
}

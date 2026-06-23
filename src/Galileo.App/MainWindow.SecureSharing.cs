using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
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

    // Browsing ---------------------------------------------------------------

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
            await ShowListingAsync(session, listing);
        }
        catch (Exception ex) { await MessageAsync("Browse", "Couldn't reach that friend (are they online and sharing?). " + ex.Message); }
        finally { session?.Dispose(); }
    }

    /// <summary>Ctrl+Alt+V → "Shared with me": connect to each linked, online friend and list their share.</summary>
    private async Task OpenSharesAsync()
    {
        if (!await EnsureIdentityAsync()) return;
        await EnsureOnlineAsync();
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Shared with me", "You're offline — check the relay URL in Settings."); return; }

        var sessions = new List<SecureSession>();
        var rows = new List<(string label, SecureSession s, SharedItem item)>();
        try
        {
            foreach (var f in _sharing.LinkedFriends)
            {
                if (!Guid.TryParse(f.Uuid, out var fid)) continue;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var s = await _sharing.ConnectToPeerAsync(fid, cts.Token);
                    var listing = await ShareProtocol.ListAsync(_sharing.Relay, s, cts.Token);
                    if (listing.Items.Count == 0) { s.Dispose(); continue; }
                    sessions.Add(s);
                    var who = string.IsNullOrWhiteSpace(f.Alias) ? f.Uuid[..8] : f.Alias;
                    foreach (var it in listing.Items)
                        rows.Add(($"{who}  ·  {listing.VaultName}  ·  {it.Name}", s, it));
                }
                catch { /* friend offline or not sharing — skip */ }
            }

            if (rows.Count == 0) { await MessageAsync("Shared with me", "No friends are sharing anything with you right now."); return; }

            while (true)
            {
                (string label, SecureSession s, SharedItem item)? picked = null;
                var dlg = new ContentDialog { Title = "Shared with me", CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };
                var list = new ListView { ItemsSource = rows.Select(r => r.label).ToList(), IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 460, MinWidth = 460 };
                list.ItemClick += (_, e) => { var i = rows.FindIndex(r => r.label == (string)e.ClickedItem); if (i >= 0) { picked = rows[i]; dlg.Hide(); } };
                dlg.Content = list;
                await dlg.ShowAsync();
                if (picked is null) break;
                await ViewSharedMediaAsync(picked.Value.s, picked.Value.item);
            }
        }
        finally { foreach (var s in sessions) s.Dispose(); }
    }

    private async Task ShowListingAsync(SecureSession session, SharedListing listing)
    {
        while (true)
        {
            SharedItem? picked = null;
            var dlg = new ContentDialog { Title = string.IsNullOrWhiteSpace(listing.VaultName) ? "Shared vault" : listing.VaultName, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };
            var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
            panel.Children.Add(new TextBlock { Text = listing.Items.Count == 0 ? "Nothing shared right now." : "Click a file to view it." });
            if (listing.Items.Count > 0)
            {
                var lv = new ListView { ItemsSource = listing.Items, DisplayMemberPath = nameof(SharedItem.Name), IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 400 };
                lv.ItemClick += (_, e) => { if (e.ClickedItem is SharedItem si) { picked = si; dlg.Hide(); } };
                panel.Children.Add(lv);
            }
            dlg.Content = panel;
            await dlg.ShowAsync();
            if (picked is null) break;
            await ViewSharedMediaAsync(session, picked);
        }
    }

    private async Task ViewSharedMediaAsync(SecureSession session, SharedItem item)
    {
        string path;
        try { path = await FetchToTempAsync(session, item); }
        catch (Exception ex) { await MessageAsync("Open", "Couldn't fetch that file: " + ex.Message); return; }

        FrameworkElement? view = null;
        MediaPlayerElement? mpe = null;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            if (PhotoLibrary.IsMedia(path))
            {
                mpe = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = true, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Height = 460, MinWidth = 640 };
                mpe.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                view = mpe;
            }
            else if (PhotoLibrary.IsSupported(path))
            {
                var img = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxHeight = 600, MaxWidth = 860 };
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using (var stream = await file.OpenReadAsync()) await bmp.SetSourceAsync(stream);
                img.Source = bmp;
                view = img;
            }
        }
        catch (Exception ex) { await MessageAsync("Open", "Couldn't display that file: " + ex.Message); }

        if (view is null) { OpenInNewWindow(path); return; } // non-viewable type → external; no close-time tracking

        var dlg = new ContentDialog { Title = item.Name, Content = view, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };
        try { await dlg.ShowAsync(); }
        finally
        {
            if (mpe is not null) { var prev = mpe.Source as Windows.Media.Core.MediaSource; mpe.Source = null; prev?.Dispose(); }
            try { await ShareProtocol.CloseAsync(_sharing!.Relay, session, item.Id); } catch { }
            try { var d = Path.GetDirectoryName(path); if (d is not null) Directory.Delete(d, true); } catch { }
        }
    }

    private async Task<string> FetchToTempAsync(SecureSession session, SharedItem si)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GalileoShare", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var safeName = Path.GetFileName(si.Name.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "file";
        var path = Path.Combine(dir, safeName);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using (var fs = File.Create(path))
            await ShareProtocol.FetchAsync(_sharing!.Relay, session, si.Id, si.Size, fs, null, cts.Token);
        return path;
    }

    // Online + regenerate ----------------------------------------------------

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
        try { await _sharing.GoOnlineAsync(url, shareForPeer); } catch { }
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
        try
        {
            var records = await _sharing.QueryAuditAsync(url);

            var names = new Dictionary<string, string>();
            if (_vaults.Current is not null)
                foreach (var en in _vaults.Current.ShareEntries()) names[en.BlobId] = en.RelPath;

            string PeerName(Guid v)
            {
                var n = _sharing!.Friends.FirstOrDefault(p => p.Uuid == v.ToString())?.Alias;
                return string.IsNullOrWhiteSpace(n) ? v.ToString()[..8] : n!;
            }
            string FileName(string id) => names.TryGetValue(id, out var f) ? f : "(item no longer shared)";

            var asc = records.OrderBy(r => r.Time).ToList();
            var openAt = new Dictionary<string, DateTimeOffset>();
            var rows = new List<string>();
            foreach (var r in asc)
            {
                var key = r.Viewer + "|" + r.ObjectId;
                if (r.Action == "open") openAt[key] = r.Time;
                else if (r.Action == "close" && openAt.TryGetValue(key, out var t))
                {
                    rows.Add($"{PeerName(r.Viewer)} — {FileName(r.ObjectId)} — opened {t.LocalDateTime:g}, closed {r.Time.LocalDateTime:t}  ({FormatDuration(r.Time - t)})");
                    openAt.Remove(key);
                }
            }
            foreach (var kv in openAt)
            {
                var parts = kv.Key.Split('|', 2);
                Guid.TryParse(parts[0], out var v);
                rows.Add($"{PeerName(v)} — {FileName(parts[1])} — opened {kv.Value.LocalDateTime:g}  (still open)");
            }
            rows.Reverse();

            object content = rows.Count == 0
                ? new TextBlock { Text = "No file access recorded yet." }
                : new ListView { ItemsSource = rows, SelectionMode = ListViewSelectionMode.None, MaxHeight = 460, MinWidth = 540 };
            await new ContentDialog { Title = "Access log", Content = content, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
        }
        catch (Exception ex) { await MessageAsync("Access log", "Couldn't fetch the log: " + ex.Message); }
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

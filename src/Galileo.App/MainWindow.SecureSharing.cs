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
    // The crypto/transport lives in the Services layer (PeerIdentity, SecureChannel, ShareProtocol,
    // SecureSharing). This file is just the WinUI glue: a relay-URL setting and a dialog-driven hub to
    // create/recover the identity, manage peers, go online to host an unlocked vault, browse a peer's
    // vault, and review the access log. Built from ContentDialogs (no new XAML panel) to avoid XAML-load
    // crashes and to keep the surface deniable.

    private SecureSharing? _sharing;
    private string? _sharingPass;                       // held while the hub is in use, to re-seal on edits
    private bool _accessLogRevealed;                    // the access-log entry stays hidden until Ctrl+Alt+L

    private void RelayUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SecureRelayUrl = RelayUrlBox.Text.Trim();
        _state.Save();
    }

    private async void ManageSharing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sharing is null)
            {
                if (SecureSharing.Exists())
                {
                    var pass = await PromptPassphraseAsync("Secure sharing",
                        "Enter your sharing passphrase to unlock your identity.", "Unlock");
                    if (pass is null) return;
                    try { _sharing = SecureSharing.Open(pass); _sharingPass = pass; }
                    catch (CryptographicException) { await MessageAsync("Secure sharing", "Wrong passphrase."); return; }
                }
                else
                {
                    await FirstRunSetupAsync();
                    if (_sharing is null) return;
                }
            }
            await EnsureOnlineAsync(); // always go online once the identity is loaded
            await ShowSharingHubAsync();
        }
        catch (Exception ex) { await MessageAsync("Secure sharing", ex.Message); App.Log("Sharing", ex); }
    }

    // ---- first-run: create or recover an identity ----

    private async Task FirstRunSetupAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "Set up secure sharing",
            Content = new TextBlock
            {
                Text = "Create a new sharing identity (you'll get a recovery phrase to back up), or recover an "
                     + "existing one from its phrase. A passphrase protects the identity on this device.",
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
        var pass = await PromptNewPassphraseAsync();
        if (pass is null) return;
        var (sharing, seed) = SecureSharing.CreateNew(pass);
        _sharing = sharing;
        _sharingPass = pass;

        // Show the recovery phrase once, with a copy button. This is the only backup of the identity.
        var box = new TextBox
        {
            Text = seed, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        var copy = new Button { Content = "Copy phrase" };
        copy.Click += (_, _) => SetClipboard(seed);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Write down this recovery phrase and keep it offline. Anyone with it can act as you; "
                 + "lose it and a forgotten passphrase means the identity is gone.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(copy);
        await new ContentDialog
        {
            Title = "Your recovery phrase", Content = panel, CloseButtonText = "I've saved it",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
    }

    private async Task RecoverIdentityAsync()
    {
        var phraseBox = new TextBox { PlaceholderText = "twelve words…", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
        var panel = new StackPanel { Spacing = 10 };
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
        var pass = await PromptNewPassphraseAsync();
        if (pass is null) return;
        _sharing = SecureSharing.Recover(phraseBox.Text, pass);
        _sharingPass = pass;
    }

    // ---- the hub ----

    private async Task ShowSharingHubAsync()
    {
        _accessLogRevealed = false; // re-hide the access-log entry every time the hub is opened
        while (_sharing is not null)
        {
            string? action = null;
            var dlg = new ContentDialog { Title = "Secure sharing", CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };

            var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
            panel.Children.Add(new TextBlock
            {
                Text = _sharing.IsOnline ? "● Online" : "○ Offline (relay unreachable)",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    _sharing.IsOnline ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.Gray),
                FontWeight = FontWeights.SemiBold,
            });

            Button Hub(string text, string act)
            {
                var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
                b.Click += (_, _) => { action = act; dlg.Hide(); };
                return b;
            }

            panel.Children.Add(Hub("Show my ID & fingerprint", "show"));
            panel.Children.Add(Hub("Add a peer…", "addpeer"));

            if (_sharing.Peers.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = "Peers", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                foreach (var p in _sharing.Peers)
                {
                    var label = string.IsNullOrWhiteSpace(p.Name) ? p.Uuid : $"{p.Name}";
                    panel.Children.Add(Hub($"Browse  {label}", "browse:" + p.Uuid));
                }
            }

            // Access log is hidden until the user presses the secret sequence Ctrl+Alt+L (deniability).
            if (_accessLogRevealed) panel.Children.Add(Hub("View access log", "audit"));

            var regen = Hub("Delete identity & regenerate…", "regen");
            regen.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            regen.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(regen);

            // Catch Ctrl+Alt+L anywhere in the hub: reveal the access-log entry and rebuild.
            panel.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.L && IsCtrlDown() && IsAltDown() && !_accessLogRevealed)
                {
                    _accessLogRevealed = true;
                    action = "refresh";
                    dlg.Hide();
                }
            };

            dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 460 };
            await dlg.ShowAsync();

            if (action is null) break;
            if (action == "show") await ShowMyIdentityAsync();
            else if (action == "addpeer") await AddPeerAsync();
            else if (action == "audit") await ShowAuditAsync();
            else if (action == "regen") await RegenerateIdentityAsync();
            else if (action == "refresh") { /* loop re-shows the hub with the log entry revealed */ }
            else if (action.StartsWith("browse:") && Guid.TryParse(action[7..], out var pu)) await BrowsePeerAsync(pu);

            if (_sharing is null) break; // regenerate may have been cancelled mid-way
        }
    }

    private async Task ShowMyIdentityAsync()
    {
        if (_sharing is null) return;
        var uuid = _sharing.Identity.Uuid.ToString();
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Your ID (share this so peers can add you):" });
        panel.Children.Add(new TextBox { Text = uuid, IsReadOnly = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        panel.Children.Add(new TextBlock { Text = "Safety number (verify out-of-band that it matches on both devices):", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBox { Text = _sharing.Identity.Fingerprint, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        var copy = new Button { Content = "Copy ID" };
        copy.Click += (_, _) => SetClipboard(uuid);
        panel.Children.Add(copy);
        await new ContentDialog { Title = "My identity", Content = panel, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    /// <summary>Connect to the relay if we aren't already (always-online). Best-effort: a failure just
    /// leaves the hub showing "offline" — the user can still manage peers and retry by reopening.</summary>
    private async Task EnsureOnlineAsync()
    {
        if (_sharing is null || _sharing.IsOnline) return;
        var url = _state.SecureRelayUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { await _sharing.GoOnlineAsync(url, _vaults.Current); } // hosts the unlocked vault, if any
        catch { /* stay offline; status line reflects it */ }
    }

    /// <summary>Securely wipes the current identity (and peers) and creates a fresh one in its place.</summary>
    private async Task RegenerateIdentityAsync()
    {
        if (_sharing is null) return;
        var confirm = new ContentDialog
        {
            Title = "Delete identity & regenerate",
            Content = new TextBlock
            {
                Text = "This permanently erases your current sharing identity and peer list from this device "
                     + "and creates a brand-new identity with a new ID. Peers will no longer recognise you until "
                     + "you re-share your new ID. This can't be undone unless you backed up the old recovery phrase.",
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
        _sharingPass = null;
        SecureSharing.DeleteStore();

        await CreateIdentityAsync(); // prompts a new passphrase and shows the new recovery phrase
        await EnsureOnlineAsync();
    }

    private async Task AddPeerAsync()
    {
        if (_sharing is null || _sharingPass is null) return;
        var idBox = new TextBox { PlaceholderText = "peer ID (a UUID)" };
        var nameBox = new TextBox { PlaceholderText = "name (optional)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = "Add a peer by the ID they shared with you." });
        panel.Children.Add(idBox);
        panel.Children.Add(nameBox);
        var dlg = new ContentDialog
        {
            Title = "Add a peer", Content = panel, PrimaryButtonText = "Add",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try { _sharing.AddPeer(idBox.Text, nameBox.Text, _sharingPass); }
        catch (Exception ex) { await MessageAsync("Add a peer", ex.Message); }
    }

    private async Task BrowsePeerAsync(Guid peer)
    {
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Browse", "Go online first."); return; }

        SecureSession? session = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            session = await _sharing.ConnectToPeerAsync(peer, cts.Token);
            var items = await ShareProtocol.ListAsync(_sharing.Relay, session, cts.Token);
            await ShowPeerFilesAsync(session, items);
        }
        catch (Exception ex)
        {
            await MessageAsync("Browse", "Couldn't reach that peer (are they online and sharing?). " + ex.Message);
        }
        finally { session?.Dispose(); }
    }

    private async Task ShowPeerFilesAsync(SecureSession session, IReadOnlyList<SharedItem> items)
    {
        while (true)
        {
            SharedItem? picked = null;
            var dlg = new ContentDialog { Title = "Peer's shared vault", CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };
            var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
            panel.Children.Add(new TextBlock { Text = items.Count == 0 ? "This peer isn't sharing anything right now." : "Click a file to view it." });
            if (items.Count > 0)
            {
                var list = new ListView
                {
                    ItemsSource = items, DisplayMemberPath = nameof(SharedItem.Name),
                    IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 380,
                };
                list.ItemClick += (_, e) => { if (e.ClickedItem is SharedItem si) { picked = si; dlg.Hide(); } };
                panel.Children.Add(list);
            }
            dlg.Content = panel;
            await dlg.ShowAsync();

            if (picked is null) break;          // user closed the list
            await ViewSharedMediaAsync(session, picked); // view in-process, then re-show the list
        }
    }

    /// <summary>Fetches a shared file and views it in-process so we know exactly when it's opened and
    /// closed — and signal the host on close so its access log can show how long it was open.</summary>
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
                mpe = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = true, AutoPlay = true,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Height = 460, MinWidth = 640,
                };
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

        if (view is null)
        {
            // Not viewable in-app (e.g. a document) — open externally; close time can't be tracked for these.
            OpenInNewWindow(path);
            return;
        }

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

    private async Task ShowAuditAsync()
    {
        if (_sharing is null) return;
        var url = _state.SecureRelayUrl;
        if (string.IsNullOrWhiteSpace(url)) { await MessageAsync("Access log", "Set a relay server URL in Settings first."); return; }
        try
        {
            var records = await _sharing.QueryAuditAsync(url);

            // The relay only stores opaque object ids; resolve them to real filenames locally from the
            // currently-shared (unlocked) vault so the log reads naturally without the relay ever knowing.
            var names = new Dictionary<string, string>();
            if (_vaults.Current is not null)
                foreach (var e in _vaults.Current.ShareEntries()) names[e.BlobId] = e.RelPath;

            string PeerName(Guid v)
            {
                var n = _sharing!.Peers.FirstOrDefault(p => p.Uuid == v.ToString())?.Name;
                return string.IsNullOrWhiteSpace(n) ? v.ToString()[..8] : n!;
            }
            string FileName(string id) => names.TryGetValue(id, out var f) ? f : "(item no longer shared)";

            // Pair each "open" with its later "close" per (viewer, file) to show who, what, when, and how long.
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
            // Opens with no matching close yet (still open, or the viewer didn't signal a close).
            foreach (var kv in openAt)
            {
                var parts = kv.Key.Split('|', 2);
                Guid.TryParse(parts[0], out var v);
                rows.Add($"{PeerName(v)} — {FileName(parts[1])} — opened {kv.Value.LocalDateTime:g}  (still open)");
            }
            rows.Reverse(); // newest first

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

    // ---- small dialog helpers ----

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

    private async Task<string?> PromptNewPassphraseAsync()
    {
        var pw1 = new PasswordBox { PlaceholderText = "Passphrase" };
        var pw2 = new PasswordBox { PlaceholderText = "Confirm passphrase" };
        var err = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed), Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = "Choose a passphrase to protect this identity on this device.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(pw1);
        panel.Children.Add(pw2);
        panel.Children.Add(err);
        var dlg = new ContentDialog
        {
            Title = "Set a passphrase", Content = panel, PrimaryButtonText = "OK", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            if (pw1.Password.Length < 6) { err.Text = "Use at least 6 characters."; err.Visibility = Visibility.Visible; args.Cancel = true; }
            else if (pw1.Password != pw2.Password) { err.Text = "Passphrases don't match."; err.Visibility = Visibility.Visible; args.Cancel = true; }
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? pw1.Password : null;
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
}

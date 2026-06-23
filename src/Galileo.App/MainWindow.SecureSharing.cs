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
    private readonly SemaphoreSlim _fetchLock = new(1, 1); // serialize fetches over a single session

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
        while (_sharing is not null)
        {
            string? action = null;
            var dlg = new ContentDialog { Title = "Secure sharing", CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };

            var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
            panel.Children.Add(new TextBlock
            {
                Text = _sharing.IsOnline ? "● Online" : "○ Offline",
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
            panel.Children.Add(Hub(_sharing.IsOnline ? "Go offline" : "Go online", "toggle"));
            panel.Children.Add(Hub("Add a peer…", "addpeer"));
            panel.Children.Add(Hub("View access log", "audit"));

            if (_sharing.Peers.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = "Peers", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                foreach (var p in _sharing.Peers)
                {
                    var label = string.IsNullOrWhiteSpace(p.Name) ? p.Uuid : $"{p.Name}";
                    panel.Children.Add(Hub($"Browse  {label}", "browse:" + p.Uuid));
                }
            }

            dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 460 };
            await dlg.ShowAsync();

            if (action is null) break;
            if (action == "show") await ShowMyIdentityAsync();
            else if (action == "toggle") await ToggleOnlineAsync();
            else if (action == "addpeer") await AddPeerAsync();
            else if (action == "audit") await ShowAuditAsync();
            else if (action.StartsWith("browse:") && Guid.TryParse(action[7..], out var pu)) await BrowsePeerAsync(pu);
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

    private async Task ToggleOnlineAsync()
    {
        if (_sharing is null) return;
        if (_sharing.IsOnline) { await _sharing.GoOfflineAsync(); return; }

        var url = _state.SecureRelayUrl;
        if (string.IsNullOrWhiteSpace(url)) { await MessageAsync("Go online", "Set a relay server URL in Settings first."); return; }

        IShareSource? share = _vaults.Current; // host the currently-unlocked vault, if any
        try
        {
            await _sharing.GoOnlineAsync(url, share);
            await MessageAsync("Secure sharing", share is null
                ? "You're online (browse-only — unlock a vault to share it with peers)."
                : $"You're online. Approved peers can now browse \"{_vaults.Current!.Name}\" while it stays unlocked.");
        }
        catch (Exception ex) { await MessageAsync("Go online", "Couldn't connect to the relay: " + ex.Message); }
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
        var status = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var list = new ListView
        {
            ItemsSource = items, DisplayMemberPath = nameof(SharedItem.Name),
            IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 380,
        };
        list.ItemClick += async (_, e) =>
        {
            if (e.ClickedItem is not SharedItem si) return;
            if (!await _fetchLock.WaitAsync(0)) { status.Text = "Still fetching the last file…"; return; }
            try
            {
                status.Text = $"Fetching {si.Name}…";
                var path = await FetchToTempAsync(session, si);
                status.Text = $"Opened {si.Name}.";
                OpenInNewWindow(path);
            }
            catch (Exception ex) { status.Text = "Couldn't open that file: " + ex.Message; }
            finally { _fetchLock.Release(); }
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = items.Count == 0 ? "This peer isn't sharing anything right now." : "Click a file to open it." });
        if (items.Count > 0) panel.Children.Add(list);
        panel.Children.Add(status);

        await new ContentDialog { Title = "Peer's shared vault", Content = panel, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
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
            var rows = records.Select(r =>
            {
                var name = _sharing.Peers.FirstOrDefault(p => p.Uuid == r.Viewer.ToString())?.Name;
                var who = string.IsNullOrWhiteSpace(name) ? r.Viewer.ToString()[..8] : name;
                var what = r.Action == "list" ? "listed your files" : $"opened an item ({r.Bytes:n0} bytes)";
                return $"{r.Time.LocalDateTime:g}  —  {who}  {what}";
            }).ToList();

            var content = rows.Count == 0
                ? (object)new TextBlock { Text = "No access recorded yet." }
                : new ListView { ItemsSource = rows, SelectionMode = ListViewSelectionMode.None, MaxHeight = 420, MinWidth = 420 };
            await new ContentDialog { Title = "Access log", Content = content, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
        }
        catch (Exception ex) { await MessageAsync("Access log", "Couldn't fetch the log: " + ex.Message); }
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

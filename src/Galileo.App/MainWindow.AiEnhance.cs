using System;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Galileo;

/// <summary>
/// AI enhancement in the image editor: Real-ESRGAN x4 (ONNX) run on the GPU via DirectML.
///
/// The model rewrites pixels, so unlike the sliders/filters (which are a non-destructive Win2D effect graph)
/// this replaces the editor's source bitmap. That's undoable via the normal edit-undo stack only for the
/// *parameters*, so we treat AI as an explicit, confirmed step and let "Cancel" in the editor discard it.
/// </summary>
public sealed partial class MainWindow
{
    private readonly AiUpscaler _ai = new();
    private bool _aiBusy;
    private CancellationTokenSource? _aiCts;

    /// <summary>One-deep undo for AI actions. The AI rewrites pixels, so it can't live on the EditState undo
    /// stack (which only snapshots parameters) — we keep the previous pixels instead.</summary>
    private (byte[] Pixels, int W, int H, Rect? Crop)? _aiUndo;

    private async void AiEnhance_Click(object sender, RoutedEventArgs e) => await RunAiAsync(keepUpscale: false);
    private async void AiUpscale_Click(object sender, RoutedEventArgs e) => await RunAiAsync(keepUpscale: true);

    private void AiUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_aiBusy || _aiUndo is not { } snap) return;
        _editor.ReplaceSource(snap.Pixels, snap.W, snap.H);
        _edit.Crop = snap.Crop;
        _aiUndo = null;
        if (AiUndoBtn is not null) AiUndoBtn.IsEnabled = false;
        AiSay("Reverted the last AI action.");
        InvalidateEditImage();
    }

    private void SetAiBusy(bool busy)
    {
        _aiBusy = busy;
        if (AiEnhanceBtn is not null) AiEnhanceBtn.IsEnabled = !busy;
        if (AiUpscaleBtn is not null) AiUpscaleBtn.IsEnabled = !busy;
        if (AiProgress is not null)
        {
            AiProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            AiProgress.Value = 0;
        }
    }

    private void AiSay(string? text)
    {
        if (AiStatus is not null) AiStatus.Text = text ?? "";
    }

    /// <summary>Makes sure the ~64MB model is on disk, downloading it once (with confirmation) if not.</summary>
    private async Task<bool> EnsureAiModelAsync()
    {
        if (AiUpscaler.ModelReady) return true;

        var confirm = new ContentDialog
        {
            Title = "Download the AI model?",
            Content = new TextBlock
            {
                Text = "AI enhancement uses the Real-ESRGAN model (about 64 MB). It downloads once and is "
                     + "kept on this PC, then runs locally on your GPU — images are never uploaded anywhere.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return false;

        SetAiBusy(true);
        AiSay("Downloading the AI model…");
        try
        {
            var progress = new Progress<double>(v => { if (AiProgress is not null) AiProgress.Value = v; });
            await AiUpscaler.DownloadModelAsync(progress);
            AiSay("Model downloaded.");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("AiModelDownload", ex);
            AiSay(null);
            SetAiBusy(false);
            await MessageAsync("AI enhance", "Couldn't download the AI model.\n\n" + ex.Message);
            return false;
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private async Task RunAiAsync(bool keepUpscale)
    {
        if (_aiBusy || _editor.Source is null) return;
        if (!await EnsureAiModelAsync()) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Snapshot the current pixels first so this action can be undone.
            var undoPixels = _editor.GetSourcePixels(0, out var uw, out var uh);
            _aiUndo = (undoPixels, uw, uh, _edit.Crop);

            // Upscaling multiplies the pixel count by 16, so cap the input; enhancing keeps the size and can
            // stream the whole image (each tile is downsampled straight back, so there's no 4x intermediate).
            var cap = keepUpscale ? AiUpscaler.MaxUpscaleInputEdge : 0;
            var pixels = _editor.GetSourcePixels(cap, out var w, out var h);

            AiSay(keepUpscale ? $"Upscaling {w}×{h} → {w * 4}×{h * 4}…" : $"Enhancing {w}×{h}…");
            var progress = new Progress<double>(v => { if (AiProgress is not null) AiProgress.Value = v; });

            var ct = _aiCts.Token;
            int outW = 0, outH = 0;
            var result = await Task.Run(() =>
                _ai.Run(pixels, w, h, keepUpscale, out outW, out outH, progress, ct), ct);

            // The AI rewrote the pixels — swap them in and rescale any pending crop to match.
            var factor = _editor.ReplaceSource(result, outW, outH);
            if (_edit.Crop is { } c && Math.Abs(factor - 1.0) > 0.001)
                _edit.Crop = new Rect(c.X * factor, c.Y * factor, c.Width * factor, c.Height * factor);

            sw.Stop();
            if (AiUndoBtn is not null) AiUndoBtn.IsEnabled = true;
            AiSay($"{(keepUpscale ? "Upscaled" : "Enhanced")} → {outW}×{outH}  ·  {_ai.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
            InvalidateEditImage();
        }
        catch (OperationCanceledException)
        {
            _aiUndo = null;
            AiSay("Cancelled.");
        }
        catch (Exception ex)
        {
            _aiUndo = null;   // nothing changed, so there's nothing to undo
            App.Log("AiEnhance", ex);
            AiSay(null);
            await MessageAsync("AI enhance", "Enhancement failed.\n\n" + ex.Message);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
        }
    }
}

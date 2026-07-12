using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Galileo;

/// <summary>
/// AI restoration in the image editor — Real-ESRGAN (upscale / detail / denoise), CodeFormer face recovery,
/// and an Autopilot that measures the image and picks the steps for you. Everything runs locally on the GPU
/// through DirectML; nothing is uploaded.
///
/// These models rewrite pixels, so unlike the sliders/filters (a non-destructive Win2D effect graph) each
/// action replaces the editor's source bitmap — hence the dedicated one-deep Undo.
/// </summary>
public sealed partial class MainWindow
{
    private readonly AiEngine _ai = new();
    private bool _aiBusy;
    private CancellationTokenSource? _aiCts;

    /// <summary>One-deep undo for AI actions (the EditState stack only snapshots parameters).</summary>
    private (byte[] Pixels, int W, int H, Rect? Crop)? _aiUndo;

    private double Strength => (AiStrength?.Value ?? 80) / 100.0;
    private double Fidelity => (AiFidelity?.Value ?? 70) / 100.0;

    private async void AiEnhance_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Enhance);
    private async void AiUpscale_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Upscale);
    private async void AiDenoise_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Denoise);
    private async void AiFaces_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Faces);
    private async void AiAuto_Click(object sender, RoutedEventArgs e) => await RunAutopilotAsync();

    private enum AiJob { Enhance, Upscale, Denoise, Faces }

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
        foreach (var b in new[] { AiEnhanceBtn, AiUpscaleBtn, AiDenoiseBtn, AiFacesBtn, AiAutoBtn })
            if (b is not null) b.IsEnabled = !busy;
        if (AiProgress is not null)
        {
            AiProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            AiProgress.Value = 0;
        }
    }

    private void AiSay(string? text) { if (AiStatus is not null) AiStatus.Text = text ?? ""; }

    private IProgress<double> AiProgressReporter() =>
        new Progress<double>(v => { if (AiProgress is not null) AiProgress.Value = v; });

    /// <summary>Downloads a model once, with confirmation (they range from 0.2 MB to 360 MB).</summary>
    private async Task<bool> EnsureModelAsync(AiModel model)
    {
        if (AiEngine.IsReady(model)) return true;
        var spec = AiEngine.Catalog[model];

        var confirm = new ContentDialog
        {
            Title = "Download the AI model?",
            Content = new TextBlock
            {
                Text = $"This needs {spec.Label}. It downloads once, is kept on this PC, and then runs "
                     + "locally on your GPU — images are never uploaded anywhere.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return false;

        SetAiBusy(true);
        AiSay($"Downloading {spec.Label}…");
        try
        {
            await AiEngine.DownloadAsync(model, AiProgressReporter());
            return true;
        }
        catch (Exception ex)
        {
            App.Log("AiModelDownload", ex);
            await MessageAsync("AI", "Couldn't download the model.\n\n" + ex.Message);
            return false;
        }
        finally { SetAiBusy(false); }
    }

    /// <summary>Snapshots the current pixels so the action can be undone, then swaps in the AI result.</summary>
    private void ApplyAiResult(byte[] result, int outW, int outH)
    {
        var factor = _editor.ReplaceSource(result, outW, outH);
        if (_edit.Crop is { } c && Math.Abs(factor - 1.0) > 0.001)
            _edit.Crop = new Rect(c.X * factor, c.Y * factor, c.Width * factor, c.Height * factor);
        if (AiUndoBtn is not null) AiUndoBtn.IsEnabled = true;
        InvalidateEditImage();
    }

    private void SnapshotForUndo()
    {
        var px = _editor.GetSourcePixels(0, out var uw, out var uh);
        _aiUndo = (px, uw, uh, _edit.Crop);
    }

    private static AiModel ModelFor(AiJob job) => job switch
    {
        AiJob.Enhance or AiJob.Upscale => AiModel.Upscale,
        AiJob.Denoise => AiModel.General,
        _ => AiModel.Face,
    };

    private async Task RunAiAsync(AiJob job)
    {
        if (_aiBusy || _editor.Source is null) return;
        if (!await EnsureModelAsync(ModelFor(job))) return;
        if (job == AiJob.Faces && !await EnsureModelAsync(AiModel.FaceDetect)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            SnapshotForUndo();

            // Only a kept 4x result needs the input capped (its output has 16x the pixels); the others keep
            // the original size, so they can stream a full-resolution photo.
            var cap = job == AiJob.Upscale ? AiEngine.MaxUpscaleInputEdge : 0;
            var pixels = _editor.GetSourcePixels(cap, out var w, out var h);

            AiSay(job switch
            {
                AiJob.Upscale => $"Upscaling {w}×{h} → {w * 4}×{h * 4}…",
                AiJob.Denoise => $"Denoising {w}×{h}…",
                AiJob.Faces => "Finding and restoring faces…",
                _ => $"Enhancing {w}×{h}…",
            });

            var p = AiProgressReporter();
            var ct = _aiCts.Token;
            var strength = Strength;
            var fidelity = Fidelity;

            int outW = w, outH = h, faces = 0;
            var result = await Task.Run(() => job switch
            {
                AiJob.Upscale => _ai.Enhance(pixels, w, h, true, out outW, out outH, p, ct),
                AiJob.Enhance => _ai.Enhance(pixels, w, h, false, out outW, out outH, p, ct),
                AiJob.Denoise => _ai.Denoise(pixels, w, h, strength, p, ct),
                _ => FaceRestore.Run(_ai, pixels, w, h, fidelity, out faces, p, ct),
            }, ct);

            if (job == AiJob.Faces && faces == 0)
            {
                _aiUndo = null;
                AiSay("No faces found in this image.");
                return;
            }

            ApplyAiResult(result, outW, outH);
            sw.Stop();
            AiSay(job switch
            {
                AiJob.Upscale => $"Upscaled → {outW}×{outH}",
                AiJob.Denoise => $"Denoised (strength {strength:P0})",
                AiJob.Faces => $"Restored {faces} face{(faces == 1 ? "" : "s")}",
                _ => $"Enhanced → {outW}×{outH}",
            } + $"  ·  {_ai.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { _aiUndo = null; AiSay("Cancelled."); }
        catch (Exception ex)
        {
            _aiUndo = null;
            App.Log("AiEnhance", ex);
            await MessageAsync("AI", "AI processing failed.\n\n" + ex.Message);
            AiSay(null);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
        }
    }

    /// <summary>
    /// Autopilot: measure the image, then apply only what it actually needs.
    /// Blur is the variance of the Laplacian (low = soft), noise is the mean deviation from a local mean
    /// (high = grainy), and faces come from the detector. Cheap to compute and far more useful than
    /// blindly running every model.
    /// </summary>
    private async Task RunAutopilotAsync()
    {
        if (_aiBusy || _editor.Source is null) return;
        if (!await EnsureModelAsync(AiModel.FaceDetect)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var probe = _editor.GetSourcePixels(0, out var pw, out var ph);
            AiSay("Analysing…");

            var (blur, noise) = await Task.Run(() => AiEngine.Analyze(probe, pw, ph));
            var faceCount = await Task.Run(() => FaceRestore.DetectRestorable(_ai, probe, pw, ph, _aiCts.Token).Count);

            var wantDenoise = noise > 2.0;
            var wantDetail = blur < 150;
            var wantFaces = faceCount > 0;

            var plan = new List<string>();
            if (wantDenoise) plan.Add("denoise");
            if (wantDetail) plan.Add("enhance");
            if (wantFaces) plan.Add($"restore {faceCount} face{(faceCount == 1 ? "" : "s")}");

            if (plan.Count == 0)
            {
                AiSay($"Autopilot: this image already looks clean and sharp (blur {blur:0}, noise {noise:0.0}). Nothing to do.");
                return;
            }

            // Make sure everything the plan needs is present before touching the image.
            if (wantDenoise && !await EnsureModelAsync(AiModel.General)) return;
            if (wantDetail && !await EnsureModelAsync(AiModel.Upscale)) return;
            if (wantFaces && !await EnsureModelAsync(AiModel.Face)) return;

            SetAiBusy(true);
            SnapshotForUndo();
            AiSay($"Autopilot: {string.Join(" + ", plan)}…");

            var p = AiProgressReporter();
            var ct = _aiCts.Token;
            // Scale denoise with how noisy it actually is, rather than using a fixed amount.
            var denoiseStrength = Math.Clamp((noise - 1.5) / 6.0, 0.35, 1.0);
            var fidelity = Fidelity;

            int outW = pw, outH = ph, facesDone = 0;
            var result = await Task.Run(() =>
            {
                var cur = probe;
                if (wantDenoise) cur = _ai.Denoise(cur, pw, ph, denoiseStrength, p, ct);
                if (wantDetail) cur = _ai.Enhance(cur, pw, ph, false, out outW, out outH, p, ct);
                if (wantFaces) cur = FaceRestore.Run(_ai, cur, pw, ph, fidelity, out facesDone, p, ct);
                return cur;
            }, ct);

            ApplyAiResult(result, pw, ph);
            sw.Stop();
            AiSay($"Autopilot: {string.Join(" + ", plan)}  ·  {_ai.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { _aiUndo = null; AiSay("Cancelled."); }
        catch (Exception ex)
        {
            _aiUndo = null;
            App.Log("AiAutopilot", ex);
            await MessageAsync("AI", "Autopilot failed.\n\n" + ex.Message);
            AiSay(null);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Built on first actual use — never just because the editor was opened. Constructing it would
    /// pull in the ONNX Runtime assembly (and, through it, the native runtime), which has no business being
    /// loaded for someone who only wants to crop a photo.</summary>
    private AiEngine? _ai;
    private AiEngine Ai => _ai ??= new AiEngine();

    private bool _aiBusy;
    private CancellationTokenSource? _aiCts;

    // Live denoise: the network runs once at full strength; the strength slider then re-blends the
    // original against that processed result instantly, always re-rendering from the original rather
    // than compounding passes. Invalidated whenever any other operation rewrites the pixels.
    private byte[]? _denoiseBase;
    private byte[]? _denoiseProcessed;
    private int _denoiseW, _denoiseH;

    internal void InvalidateLiveDenoise() { _denoiseBase = null; _denoiseProcessed = null; }

    private double Strength => (AiStrength?.Value ?? 40) / 100.0;
    private double Fidelity => (AiFidelity?.Value ?? 70) / 100.0;

    private async void AiEnhance_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Enhance);
    private async void AiUpscale_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Upscale);
    private async void AiDenoise_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Denoise);
    private async void AiFaces_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Faces);
    private async void AiAuto_Click(object sender, RoutedEventArgs e) => await RunAutopilotAsync();

    private enum AiJob { Enhance, Upscale, Denoise, Faces }

    /// <summary>Content-aware fill: rasterize the lasso into a source-space mask and let LaMa paint it out.</summary>
    private async void AiFill_Click(object sender, RoutedEventArgs e)
    {
        if (_aiBusy || _editor.Source is null || _lasso.Count < 3) return;
        if (!await EnsureModelAsync(AiModel.Inpaint)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var before = _editor.GetSourcePixels(0, out var w, out var h);

            // The lasso is drawn on the oriented preview; map it back to raw source pixels.
            var poly = new List<Point>(_lasso.Count);
            foreach (var p in _lasso)
            {
                if (!_editor.TryOrientedToSource(_edit, p, out var sp)) continue;
                poly.Add(sp);
            }
            if (poly.Count < 3) { AiSay("Selection is outside the image."); return; }

            var mask = RasterizePolygon(poly, w, h);
            AiSay("Filling the selection…");

            var p2 = AiProgressReporter();
            var ct = _aiCts.Token;
            var engine = Ai;
            var result = await Task.Run(() => Inpaint.Fill(engine, before, w, h, mask, p2, ct), ct);

            ApplyAiResult(before, w, h, result, w, h);
            _lasso.Clear();
            UpdateLassoUi();
            sw.Stop();
            AiSay($"Filled the selection  ·  {engine.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { AiSay("Cancelled."); }
        catch (Exception ex)
        {
            App.Log("AiFill", ex);
            await MessageAsync("Content-aware fill", "The fill failed.\n\n" + ex.Message);
            AiSay(null);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
            UpdateLassoUi();
        }
    }

    /// <summary>Fills a closed polygon into a byte mask (255 inside) by scanline — testing every pixel
    /// against every edge would crawl on a lasso with hundreds of points.</summary>
    private static byte[] RasterizePolygon(List<Point> poly, int w, int h)
    {
        var mask = new byte[w * h];
        var n = poly.Count;
        var xs = new List<double>(8);

        var minY = Math.Max(0, (int)Math.Floor(poly.Min(p => p.Y)));
        var maxY = Math.Min(h - 1, (int)Math.Ceiling(poly.Max(p => p.Y)));

        for (var y = minY; y <= maxY; y++)
        {
            var cy = y + 0.5;
            xs.Clear();
            for (var i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];           // closing edge included
                if (a.Y == b.Y) continue;
                // Half-open rule: counts each crossing once, so shared vertices can't double-count.
                if (cy < Math.Min(a.Y, b.Y) || cy >= Math.Max(a.Y, b.Y)) continue;
                xs.Add(a.X + (cy - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (var i = 0; i + 1 < xs.Count; i += 2)
            {
                var x0 = Math.Max(0, (int)Math.Ceiling(xs[i] - 0.5));
                var x1 = Math.Min(w - 1, (int)Math.Floor(xs[i + 1] - 0.5));
                for (var x = x0; x <= x1; x++) mask[y * w + x] = 255;
            }
        }
        return mask;
    }

    private void SetAiBusy(bool busy)
    {
        _aiBusy = busy;
        foreach (var b in new[] { AiEnhanceBtn, AiUpscaleBtn, AiDenoiseBtn, AiFacesBtn, AiAutoBtn })
            if (b is not null) b.IsEnabled = !busy;
        if (AiProgress is not null)
        {
            AiProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            // Indeterminate until the first real progress tick: the gap covers loading the model into the
            // GPU (a few seconds on first use, since AI loads only when actually used), so the bar visibly
            // works instead of sitting at 0.
            AiProgress.IsIndeterminate = busy;
            AiProgress.Value = 0;
        }
    }

    private void AiSay(string? text) { if (AiStatus is not null) AiStatus.Text = text ?? ""; }

    private IProgress<double> AiProgressReporter() =>
        new Progress<double>(v =>
        {
            if (AiProgress is null) return;
            AiProgress.IsIndeterminate = false;
            AiProgress.Value = v;
        });

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

    /// <summary>Called when the live-denoise slider moves: re-blend original vs processed and redisplay.
    /// This always starts from the original pixels, so dragging never compounds the effect.</summary>
    private void AiStrength_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_aiBusy || _denoiseBase is null || _denoiseProcessed is null) return;
        var blended = AiEngine.Blend(_denoiseBase, _denoiseProcessed, Strength);
        _editor.ReplaceSource(blended, _denoiseW, _denoiseH);
        AiSay($"Denoise strength {Strength:P0}");
        InvalidateEditImage();
    }

    /// <summary>Pushes the pre-AI pixels onto the shared undo history, then swaps in the result. Pushed only
    /// on success, so a failed or cancelled run doesn't leave a bogus entry to "undo".</summary>
    private void ApplyAiResult(byte[] before, int beforeW, int beforeH, byte[] result, int outW, int outH)
    {
        InvalidateLiveDenoise();   // any pixel rewrite obsoletes the live-blend pair (denoise re-arms after)
        PushUndo(before, beforeW, beforeH);
        var factor = _editor.ReplaceSource(result, outW, outH);
        if (_edit.Crop is { } c && Math.Abs(factor - 1.0) > 0.001)
            _edit.Crop = new Rect(c.X * factor, c.Y * factor, c.Width * factor, c.Height * factor);
        InvalidateEditImage();
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
            // Pixels as they are now — pushed onto the undo history only if the run succeeds.
            var before = _editor.GetSourcePixels(0, out var bw, out var bh);

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

            var engine = Ai;
            int outW = w, outH = h, faces = 0;
            byte[]? denoisedFull = null;   // pure processed result, kept so the strength slider can re-blend live
            var result = await Task.Run(() =>
            {
                switch (job)
                {
                    case AiJob.Upscale: return engine.Enhance(pixels, w, h, true, out outW, out outH, p, ct);
                    case AiJob.Enhance: return engine.Enhance(pixels, w, h, false, out outW, out outH, p, ct);
                    case AiJob.Denoise:
                        // Run the network once at 100%; the displayed result is a blend, and the slider can
                        // then re-blend from the original instantly without another inference pass.
                        denoisedFull = engine.Denoise(pixels, w, h, 1.0, p, ct);
                        return AiEngine.Blend(pixels, denoisedFull, strength);
                    default: return FaceRestore.Run(engine, pixels, w, h, fidelity, out faces, p, ct);
                }
            }, ct);

            if (job == AiJob.Faces && faces == 0)
            {
                AiSay("No faces found in this image.");
                return;
            }

            ApplyAiResult(before, bw, bh, result, outW, outH);
            if (job == AiJob.Denoise)
            {
                // Arm the live slider (after ApplyAiResult, which clears any previous pair).
                _denoiseBase = pixels; _denoiseProcessed = denoisedFull; _denoiseW = w; _denoiseH = h;
            }
            sw.Stop();
            AiSay(job switch
            {
                AiJob.Upscale => $"Upscaled → {outW}×{outH}",
                AiJob.Denoise => $"Denoised (strength {strength:P0}) — drag the slider to fine-tune live",
                AiJob.Faces => $"Restored {faces} face{(faces == 1 ? "" : "s")}",
                _ => $"Enhanced → {outW}×{outH}",
            } + $"  ·  {engine.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { AiSay("Cancelled."); }
        catch (Exception ex)
        {
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
        // Face restoration redraws faces, so Autopilot only considers it when explicitly opted in;
        // with the toggle off we don't even need (or download) the face-detect model.
        var allowFaces = AiAutoFaces?.IsChecked == true;
        if (allowFaces && !await EnsureModelAsync(AiModel.FaceDetect)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var probe = _editor.GetSourcePixels(0, out var pw, out var ph);
            AiSay("Analysing…");

            var engine = Ai;
            var (blur, noise) = await Task.Run(() => AiEngine.Analyze(probe, pw, ph));
            var faceCount = allowFaces
                ? await Task.Run(() => FaceRestore.DetectRestorable(engine, probe, pw, ph, _aiCts.Token).Count)
                : 0;

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
                if (wantDenoise) cur = engine.Denoise(cur, pw, ph, denoiseStrength, p, ct);
                if (wantDetail) cur = engine.Enhance(cur, pw, ph, false, out outW, out outH, p, ct);
                if (wantFaces) cur = FaceRestore.Run(engine, cur, pw, ph, fidelity, out facesDone, p, ct);
                return cur;
            }, ct);

            ApplyAiResult(probe, pw, ph, result, pw, ph);
            sw.Stop();
            AiSay($"Autopilot: {string.Join(" + ", plan)}  ·  {engine.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { AiSay("Cancelled."); }
        catch (Exception ex)
        {
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

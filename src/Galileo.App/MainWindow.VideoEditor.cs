using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Galileo.Services;
using Windows.Storage.Pickers;

namespace Galileo;

public sealed partial class MainWindow
{
    // ===================== FFmpeg-powered video editor =====================

    private string? _currentVideoPath;     // the real file path of the open video (for editing)
    private VideoInfo? _editVideoInfo;
    private int _editRotate;               // 0 / 90 / 180 / 270
    private double _editTrimStart;
    private double? _editTrimEnd;
    private readonly List<(double Start, double End)> _editSegments = new();
    private readonly List<string> _editCodecIds = new();

    private double CurrentVideoSeconds()
        => VideoPlayer.MediaPlayer?.PlaybackSession?.Position.TotalSeconds ?? 0;

    private async void VideoEdit_Click(object sender, RoutedEventArgs e) => await OpenVideoEditorAsync();

    private async Task OpenVideoEditorAsync()
    {
        if (string.IsNullOrEmpty(_currentVideoPath) || !File.Exists(_currentVideoPath))
        {
            StatusText.Text = "Open a video to edit it.";
            return;
        }
        if (!FfmpegVideo.Available)
        {
            EditorStatus.Text = "FFmpeg isn't bundled with this build (Assets/ffmpeg).";
            VideoEditorPanel.Visibility = Visibility.Visible;
            return;
        }

        // Reset editor state.
        _editRotate = 0;
        _editTrimStart = 0;
        _editTrimEnd = null;
        _editSegments.Clear();
        EditRotateText.Text = "0°";
        EditFlipHSwitch.IsOn = EditFlipVSwitch.IsOn = false;
        EditDeinterlace.IsOn = EditDenoise.IsOn = EditSharpen.IsOn = EditStabilize.IsOn = false;
        EditCropL.Value = EditCropT.Value = EditCropR.Value = EditCropB.Value = 0;
        EditResizeW.Value = EditResizeH.Value = 0;
        EditBrightness.Value = 0; EditContrast.Value = 1; EditSaturation.Value = 1;
        EditSpeed.Value = 1; EditFps.Value = 0;
        EditAudioCombo.SelectedIndex = 0;
        EditContainerCombo.SelectedIndex = 0;
        EditCrf.Value = 21;
        EditPresetCombo.SelectedIndex = 2;
        UpdateTrimText();
        UpdateSegmentsText();

        VideoEditorPanel.Visibility = Visibility.Visible;
        EditorStatus.Text = "Probing…";

        _editVideoInfo = await FfmpegVideo.ProbeAsync(_currentVideoPath);
        var encoders = await FfmpegVideo.DetectEncodersAsync();

        // Codec choices: software + copy + any detected GPU encoders.
        _editCodecIds.Clear();
        EditCodecCombo.Items.Clear();
        void AddCodec(string label, string id) { EditCodecCombo.Items.Add(new ComboBoxItem { Content = label }); _editCodecIds.Add(id); }
        AddCodec("H.264", "h264");
        AddCodec("H.265 (HEVC)", "h265");
        AddCodec("Copy (no re-encode)", "copy");
        foreach (var enc in encoders)
            AddCodec(enc switch
            {
                "h264_nvenc" => "H.264 — NVIDIA NVENC",
                "hevc_nvenc" => "H.265 — NVIDIA NVENC",
                "h264_qsv" => "H.264 — Intel Quick Sync",
                "hevc_qsv" => "H.265 — Intel Quick Sync",
                "h264_amf" => "H.264 — AMD",
                "hevc_amf" => "H.265 — AMD",
                _ => enc,
            }, enc);
        EditCodecCombo.SelectedIndex = 0;

        if (_editVideoInfo is { } info)
            EditorStatus.Text = $"{info.Width}×{info.Height} · {info.Fps:0.##} fps · {FormatClock(info.Duration)}";
        else
            EditorStatus.Text = "Couldn't probe the file; export may still work.";
    }

    private void CloseVideoEditor_Click(object sender, RoutedEventArgs e)
        => VideoEditorPanel.Visibility = Visibility.Collapsed;

    private void EditSetStart_Click(object sender, RoutedEventArgs e) { _editTrimStart = CurrentVideoSeconds(); UpdateTrimText(); }
    private void EditSetEnd_Click(object sender, RoutedEventArgs e) { _editTrimEnd = CurrentVideoSeconds(); UpdateTrimText(); }
    private void EditResetTrim_Click(object sender, RoutedEventArgs e) { _editTrimStart = 0; _editTrimEnd = null; UpdateTrimText(); }

    private void EditAddSegment_Click(object sender, RoutedEventArgs e)
    {
        var end = _editTrimEnd ?? (_editVideoInfo?.Duration ?? 0);
        if (end <= _editTrimStart) { EditorStatus.Text = "Set a start before the end first."; return; }
        _editSegments.Add((_editTrimStart, end));
        _editTrimStart = 0; _editTrimEnd = null;
        UpdateTrimText();
        UpdateSegmentsText();
    }

    private void EditClearSegments_Click(object sender, RoutedEventArgs e) { _editSegments.Clear(); UpdateSegmentsText(); }

    private void UpdateTrimText()
        => EditTrimText.Text = _editTrimStart <= 0 && _editTrimEnd is null
            ? "Whole clip"
            : $"{FormatClock(_editTrimStart)} → {(_editTrimEnd is { } e ? FormatClock(e) : "end")}";

    private void UpdateSegmentsText()
        => EditSegmentsText.Text = _editSegments.Count == 0
            ? ""
            : $"{_editSegments.Count} segment(s) will be stitched: " +
              string.Join(", ", _editSegments.ConvertAll(s => $"{FormatClock(s.Start)}–{FormatClock(s.End)}"));

    private void EditRotateLeft_Click(object sender, RoutedEventArgs e) { _editRotate = (_editRotate + 270) % 360; EditRotateText.Text = $"{_editRotate}°"; }
    private void EditRotateRight_Click(object sender, RoutedEventArgs e) { _editRotate = (_editRotate + 90) % 360; EditRotateText.Text = $"{_editRotate}°"; }

    private void EditContainer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // GIF ignores codec/CRF/preset/audio; dim them as a hint (export handles it regardless).
        var gif = EditContainerCombo.SelectedIndex == 3;
        if (EditCodecCombo is not null) EditCodecCombo.IsEnabled = !gif;
        if (EditCrf is not null) EditCrf.IsEnabled = !gif;
        if (EditPresetCombo is not null) EditPresetCombo.IsEnabled = !gif;
        if (EditAudioCombo is not null) EditAudioCombo.IsEnabled = !gif;
    }

    private VideoEditSettings BuildEditSettings()
    {
        double Nz(double v) => double.IsNaN(v) ? 0 : v;
        var s = new VideoEditSettings
        {
            InputPath = _currentVideoPath!,
            SourceDuration = _editVideoInfo?.Duration ?? 0,
            CropL = (int)Nz(EditCropL.Value), CropT = (int)Nz(EditCropT.Value),
            CropR = (int)Nz(EditCropR.Value), CropB = (int)Nz(EditCropB.Value),
            ResizeW = (int)Nz(EditResizeW.Value), ResizeH = (int)Nz(EditResizeH.Value),
            Rotate = _editRotate,
            FlipH = EditFlipHSwitch.IsOn, FlipV = EditFlipVSwitch.IsOn,
            Deinterlace = EditDeinterlace.IsOn, Denoise = EditDenoise.IsOn,
            Sharpen = EditSharpen.IsOn, Stabilize = EditStabilize.IsOn,
            Brightness = EditBrightness.Value, Contrast = EditContrast.Value, Saturation = EditSaturation.Value,
            Fps = Nz(EditFps.Value),
            Speed = EditSpeed.Value <= 0 ? 1 : EditSpeed.Value,
            AudioMode = EditAudioCombo.SelectedIndex switch { 1 => "aac", 2 => "mp3", 3 => "none", _ => "keep" },
            Container = EditContainerCombo.SelectedIndex switch { 1 => "mkv", 2 => "ts", 3 => "gif", _ => "mp4" },
            VideoCodec = _editCodecIds.Count > 0 && EditCodecCombo.SelectedIndex >= 0
                ? _editCodecIds[EditCodecCombo.SelectedIndex] : "h264",
            Crf = (int)EditCrf.Value,
            Preset = (EditPresetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "medium",
        };
        s.ColorAdjust = s.Brightness != 0 || s.Contrast != 1 || s.Saturation != 1;
        if (_editSegments.Count > 0) s.Segments = new List<(double, double)>(_editSegments);
        else { s.TrimStart = _editTrimStart; s.TrimEnd = _editTrimEnd; }
        return s;
    }

    private async void EditExport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        var s = BuildEditSettings();
        var ext = "." + s.Container;

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var typeName = s.Container switch { "gif" => "Animated GIF", "mkv" => "Matroska", "ts" => "MPEG-TS", _ => "MP4 video" };
        picker.FileTypeChoices.Add(typeName, new List<string> { ext });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentVideoPath) + "-edited";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await RunFfmpegExportAsync(s, file.Path, s.Container == "gif" ? "Exporting GIF" : "Exporting video");
    }

    private async void EditSaveFrame_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentVideoPath) + "-frame";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        EditorStatus.Text = "Saving frame…";
        try { await FfmpegVideo.SnapshotAsync(_currentVideoPath, CurrentVideoSeconds(), file.Path); EditorStatus.Text = "Saved frame: " + file.Path; }
        catch (Exception ex) { EditorStatus.Text = "Save frame failed: " + ex.Message; App.Log("VideoSnapshot", ex); }
    }

    /// <summary>Runs an FFmpeg export behind the floating progress card (Cancel + Hide).</summary>
    private async Task RunFfmpegExportAsync(VideoEditSettings s, string outPath, string title)
    {
        _progressCancel?.Invoke();
        var cts = new CancellationTokenSource();
        var token = new object();
        BeginProgressOp(token, title, cancel: cts.Cancel, pauseToggle: null, isPaused: null, hideable: true);
        TransferFile.Text = System.IO.Path.GetFileName(outPath);
        ShowTransferPanel();

        var progress = new Progress<double>(pct =>
        {
            _transferFrac = Math.Clamp(pct / 100.0, 0, 1);
            if (TransferBarTrack.ActualWidth > 0) TransferBarFill.Width = TransferBarTrack.ActualWidth * _transferFrac;
            TransferStats.Text = $"{pct:0}%";
            TransferEta.Text = "";
        });
        try
        {
            await FfmpegVideo.ExportAsync(s, outPath, progress, cts.Token);
            EditorStatus.Text = "Exported: " + outPath;
            StatusText.Text = "Video exported.";
        }
        catch (OperationCanceledException)
        {
            EditorStatus.Text = "Export canceled.";
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
        catch (Exception ex) { EditorStatus.Text = "Export failed: " + ex.Message; App.Log("VideoExport", ex); }
        finally { EndProgressOp(token, null); }
    }

    private static string FormatClock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Galileo.Models;
using Galileo.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Galileo;

/// <summary>Image editor overlay: a Win2D live preview driven by an <see cref="EditState"/>, with
/// transform/adjust/filter/markup tools and copy/as/overwrite saving. Originals stay untouched
/// unless the user explicitly overwrites.</summary>
public sealed partial class MainWindow
{
    private readonly ImageEditor _editor = new();
    private CanvasControl? _editCanvas;
    private EditState _edit = new();
    private string? _editPath;
    private readonly Stack<EditState> _editUndo = new();
    private readonly Stack<EditState> _editRedo = new();
    private readonly List<MarkupItem> _markup = new();

    private bool _editLoading;
    private long _lastEditChangeTick;

    private Rect _editFitRect;      // where the image is drawn in the canvas (display space)
    private double _editFitScale = 1;
    private double _orientedW, _orientedH;

    private bool _cropMode;
    private string _markupTool = "";
    private double _cropAspect;     // 0 = free
    private bool _dragging;
    private Point _dragStart;       // oriented-image space
    private Rect? _pendingCrop;
    private MarkupItem? _pendingShape;

    // ---- enter / exit ----

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EnterEditModeAsync();

    private async Task EnterEditModeAsync(PhotoItem? item = null)
    {
        item ??= Current ?? _contextItem;
        if (item is null || !PhotoLibrary.IsSupported(item.Path)) { StatusText.Text = "This file can't be edited."; return; }

        _editPath = item.Path;
        _editLoading = true;
        _edit = new EditState();
        _editUndo.Clear();
        _editRedo.Clear();
        _markup.Clear();

        if (_editCanvas is null)
        {
            // Created in code (not XAML) and sharing the editor's device so effects/bitmaps match.
            _editCanvas = new CanvasControl { UseSharedDevice = true };
            _editCanvas.Draw += EditCanvas_Draw;
            EditCanvasHost.Children.Insert(0, _editCanvas);
        }

        ResetEditSliders();
        _editLoading = true;
        if (CropAspectCombo.Items.Count > 0) CropAspectCombo.SelectedIndex = 0;
        _editLoading = false;
        _cropAspect = 0;
        SetCanvasMode("none");

        try { await _editor.LoadAsync(item.Path); }
        catch (Exception ex) { _editLoading = false; StatusText.Text = "Couldn't open for editing: " + ex.Message; App.Log("Editor", ex); return; }

        ViewerView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        _editLoading = false;
        _editCanvas?.Invalidate();
    }

    private void ExitEditMode(bool reloadViewer)
    {
        EditorView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        if (reloadViewer) _ = LoadCurrentAsync();
    }

    private void EditCancel_Click(object sender, RoutedEventArgs e) => ExitEditMode(reloadViewer: false);

    // ---- canvas drawing ----

    private void EditCanvasHost_SizeChanged(object sender, SizeChangedEventArgs e) => _editCanvas?.Invalidate();

    private void EditCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_editor.Source is null) return;
        var ds = args.DrawingSession;

        var oriented = _editor.BuildOriented(_edit, out var bounds);
        double ow = bounds.Width, oh = bounds.Height;
        _orientedW = ow; _orientedH = oh;
        double cw = sender.Size.Width, ch = sender.Size.Height;
        if (ow <= 0 || oh <= 0 || cw <= 0 || ch <= 0) return;

        double scale = Math.Min(cw / ow, ch / oh);
        double dw = ow * scale, dh = oh * scale;
        double ox = (cw - dw) / 2, oy = (ch - dh) / 2;
        _editFitRect = new Rect(ox, oy, dw, dh);
        _editFitScale = scale;

        ds.DrawImage(oriented, new Rect(ox, oy, dw, dh), new Rect(0, 0, ow, oh));

        // Crop overlay (dim outside + bright border).
        var crop = _pendingCrop ?? _edit.Crop;
        if (crop is Rect c && c.Width > 0 && c.Height > 0)
        {
            var disp = new Rect(ox + c.X * scale, oy + c.Y * scale, c.Width * scale, c.Height * scale);
            var shade = Color.FromArgb(120, 0, 0, 0);
            ds.FillRectangle(new Rect(ox, oy, dw, disp.Y - oy), shade);
            ds.FillRectangle(new Rect(ox, disp.Y + disp.Height, dw, oy + dh - (disp.Y + disp.Height)), shade);
            ds.FillRectangle(new Rect(ox, disp.Y, disp.X - ox, disp.Height), shade);
            ds.FillRectangle(new Rect(disp.X + disp.Width, disp.Y, ox + dw - (disp.X + disp.Width), disp.Height), shade);
            ds.DrawRectangle(disp, Microsoft.UI.Colors.White, 2);
        }

        foreach (var m in _markup) DrawShape(ds, m, ox, oy, scale);
        if (_pendingShape is MarkupItem ps) DrawShape(ds, ps, ox, oy, scale);
    }

    private static void DrawShape(CanvasDrawingSession ds, MarkupItem m, double offX, double offY, double sc)
    {
        float sx = (float)(offX + m.Start.X * sc), sy = (float)(offY + m.Start.Y * sc);
        float ex = (float)(offX + m.End.X * sc), ey = (float)(offY + m.End.Y * sc);
        float th = (float)Math.Max(1, m.Thickness * sc);
        var col = m.Color;
        switch (m.Kind)
        {
            case MarkupKind.Pen:
                for (var i = 1; i < m.Points.Count; i++)
                {
                    var a = m.Points[i - 1]; var b = m.Points[i];
                    ds.DrawLine((float)(offX + a.X * sc), (float)(offY + a.Y * sc),
                                (float)(offX + b.X * sc), (float)(offY + b.Y * sc), col, th);
                }
                break;
            case MarkupKind.Rectangle:
                ds.DrawRectangle(new Rect(Math.Min(sx, ex), Math.Min(sy, ey), Math.Abs(ex - sx), Math.Abs(ey - sy)), col, th);
                break;
            case MarkupKind.Ellipse:
                ds.DrawEllipse((sx + ex) / 2, (sy + ey) / 2, Math.Abs(ex - sx) / 2, Math.Abs(ey - sy) / 2, col, th);
                break;
            case MarkupKind.Line:
                ds.DrawLine(sx, sy, ex, ey, col, th);
                break;
            case MarkupKind.Arrow:
                ds.DrawLine(sx, sy, ex, ey, col, th);
                var ang = Math.Atan2(ey - sy, ex - sx);
                float head = Math.Max(10, th * 4);
                ds.DrawLine(ex, ey, (float)(ex - head * Math.Cos(ang - 0.5)), (float)(ey - head * Math.Sin(ang - 0.5)), col, th);
                ds.DrawLine(ex, ey, (float)(ex - head * Math.Cos(ang + 0.5)), (float)(ey - head * Math.Sin(ang + 0.5)), col, th);
                break;
            case MarkupKind.Text:
                ds.DrawText(m.Text, sx, sy, col, new CanvasTextFormat { FontSize = (float)Math.Max(6, m.FontSize * sc) });
                break;
        }
    }

    // ---- canvas modes & pointer ----

    private void SetCanvasMode(string mode)
    {
        _cropMode = mode == "crop";
        if (mode != "shape") _markupTool = "";
        OverlayCanvas.IsHitTestVisible = mode is "crop" or "shape";
    }

    private Point DisplayToOriented(Point p)
    {
        var ox = Math.Clamp((p.X - _editFitRect.X) / _editFitScale, 0, _orientedW);
        var oy = Math.Clamp((p.Y - _editFitRect.Y) / _editFitScale, 0, _orientedH);
        return new Point(ox, oy);
    }

    private void Overlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var p = DisplayToOriented(e.GetCurrentPoint(OverlayCanvas).Position);
        if (_cropMode)
        {
            _dragging = true; _dragStart = p; _pendingCrop = new Rect(p.X, p.Y, 0, 0);
            OverlayCanvas.CapturePointer(e.Pointer);
        }
        else if (_markupTool == "Text")
        {
            _ = AddTextAsync(p);
        }
        else if (_markupTool.Length > 0)
        {
            _dragging = true; _dragStart = p;
            _pendingShape = new MarkupItem
            {
                Kind = Enum.Parse<MarkupKind>(_markupTool),
                Start = p, End = p, Color = SelectedMarkupColor(),
                Thickness = 4 / Math.Max(0.0001, _editFitScale),
            };
            if (_pendingShape.Kind == MarkupKind.Pen) _pendingShape.Points.Add(p);
            OverlayCanvas.CapturePointer(e.Pointer);
        }
    }

    private void Overlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var p = DisplayToOriented(e.GetCurrentPoint(OverlayCanvas).Position);
        if (_cropMode) { _pendingCrop = MakeCropRect(_dragStart, p); _editCanvas?.Invalidate(); }
        else if (_pendingShape is not null)
        {
            _pendingShape.End = p;
            if (_pendingShape.Kind == MarkupKind.Pen) _pendingShape.Points.Add(p);
            _editCanvas?.Invalidate();
        }
    }

    private void Overlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        OverlayCanvas.ReleasePointerCapture(e.Pointer);
        if (_cropMode)
        {
            if (_pendingCrop is Rect c && c.Width > 8 && c.Height > 8) { PushUndo(); _edit.Crop = c; }
            _pendingCrop = null;
        }
        else if (_pendingShape is MarkupItem ps)
        {
            var keep = ps.Kind == MarkupKind.Pen
                ? ps.Points.Count > 1
                : Math.Abs(ps.End.X - ps.Start.X) > 3 || Math.Abs(ps.End.Y - ps.Start.Y) > 3;
            if (keep) _markup.Add(ps);
            _pendingShape = null;
        }
        _editCanvas?.Invalidate();
    }

    private Rect MakeCropRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        if (_cropAspect > 0) h = w / _cropAspect;
        w = Math.Min(w, _orientedW - x);
        h = Math.Min(h, _orientedH - y);
        return new Rect(x, y, w, h);
    }

    private async Task AddTextAsync(Point at)
    {
        var box = new TextBox { PlaceholderText = "Text", AcceptsReturn = false, MinWidth = 240 };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var dlg = new ContentDialog
        {
            Title = "Add text", Content = box, PrimaryButtonText = "Add", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrEmpty(box.Text)) return;
        _markup.Add(new MarkupItem
        {
            Kind = MarkupKind.Text, Start = at, End = at, Text = box.Text,
            Color = SelectedMarkupColor(), FontSize = 28 / Math.Max(0.0001, _editFitScale),
        });
        _editCanvas?.Invalidate();
    }

    private Color SelectedMarkupColor()
    {
        var name = (MarkupColorCombo.SelectedItem as ComboBoxItem)?.Content as string;
        return name switch
        {
            "Yellow" => Microsoft.UI.Colors.Yellow,
            "Lime" => Microsoft.UI.Colors.Lime,
            "White" => Microsoft.UI.Colors.White,
            "Black" => Microsoft.UI.Colors.Black,
            _ => Microsoft.UI.Colors.Red,
        };
    }

    // ---- transform handlers ----

    private void RotateCw_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.Quarter = (_edit.Quarter + 1) % 4; _edit.Crop = null; _editCanvas?.Invalidate(); }
    private void RotateCcw_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.Quarter = (_edit.Quarter + 3) % 4; _edit.Crop = null; _editCanvas?.Invalidate(); }
    private void FlipH_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.FlipH = !_edit.FlipH; _edit.Crop = null; _editCanvas?.Invalidate(); }
    private void FlipV_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.FlipV = !_edit.FlipV; _edit.Crop = null; _editCanvas?.Invalidate(); }

    private void Straighten_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_editCanvas is null || _editLoading) return;
        DebounceUndo();
        _edit.StraightenDeg = StraightenSlider.Value;
        _edit.Crop = null;
        _editCanvas?.Invalidate();
    }

    private void CropAspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_editCanvas is null || _editLoading) return; // ignore events fired during XAML load
        _cropAspect = ((CropAspectCombo.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Original" => _orientedH > 0 ? _orientedW / _orientedH : 0,
            "1:1" => 1.0,
            "4:3" => 4.0 / 3,
            "3:2" => 3.0 / 2,
            "16:9" => 16.0 / 9,
            _ => 0,
        };
        SetCanvasMode("crop");
        StatusText.Text = "Drag on the image to set the crop.";
    }

    private void CropReset_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.Crop = null; _editCanvas?.Invalidate(); }

    // ---- adjustments & filters ----

    private void Adjust_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_editCanvas is null || _editLoading) return;
        DebounceUndo();
        _edit.Exposure = ExposureSlider.Value / 50.0;     // -2..2
        _edit.Brightness = BrightnessSlider.Value / 100.0;
        _edit.Contrast = ContrastSlider.Value / 100.0;
        _edit.Saturation = SaturationSlider.Value / 100.0;
        _edit.Temperature = TemperatureSlider.Value / 100.0;
        _edit.Tint = TintSlider.Value / 100.0;
        _edit.Sharpness = SharpnessSlider.Value / 100.0;
        _editCanvas?.Invalidate();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && Enum.TryParse<ImageFilter>(fe.Tag as string, out var f))
        {
            PushUndo();
            _edit.Filter = f;
            _editCanvas?.Invalidate();
        }
    }

    private void ResetEditSliders()
    {
        _editLoading = true;
        ExposureSlider.Value = 0; BrightnessSlider.Value = 0; ContrastSlider.Value = 0;
        SaturationSlider.Value = 0; TemperatureSlider.Value = 0; TintSlider.Value = 0; SharpnessSlider.Value = 0;
        StraightenSlider.Value = 0;
        _editLoading = false;
    }

    private void SyncSlidersFromState()
    {
        _editLoading = true;
        ExposureSlider.Value = _edit.Exposure * 50;
        BrightnessSlider.Value = _edit.Brightness * 100;
        ContrastSlider.Value = _edit.Contrast * 100;
        SaturationSlider.Value = _edit.Saturation * 100;
        TemperatureSlider.Value = _edit.Temperature * 100;
        TintSlider.Value = _edit.Tint * 100;
        SharpnessSlider.Value = _edit.Sharpness * 100;
        StraightenSlider.Value = _edit.StraightenDeg;
        _editLoading = false;
    }

    // ---- markup tool buttons ----

    private void MarkupTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) { _markupTool = fe.Tag as string ?? ""; SetCanvasMode("shape"); }
    }

    private void MarkupNone_Click(object sender, RoutedEventArgs e) => SetCanvasMode("none");

    private void MarkupClear_Click(object sender, RoutedEventArgs e)
    {
        _markup.Clear();
        _editCanvas?.Invalidate();
    }

    // ---- undo / redo / reset ----

    private void PushUndo() { _editUndo.Push(_edit.Clone()); _editRedo.Clear(); }

    private void DebounceUndo()
    {
        var now = Environment.TickCount64;
        if (now - _lastEditChangeTick > 500) PushUndo();
        _lastEditChangeTick = now;
    }

    private void EditUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_editUndo.Count == 0) return;
        _editRedo.Push(_edit.Clone());
        _edit = _editUndo.Pop();
        SyncSlidersFromState();
        _editCanvas?.Invalidate();
    }

    private void EditRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_editRedo.Count == 0) return;
        _editUndo.Push(_edit.Clone());
        _edit = _editRedo.Pop();
        SyncSlidersFromState();
        _editCanvas?.Invalidate();
    }

    private void EditReset_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        _edit = new EditState();
        _markup.Clear();
        ResetEditSliders();
        SetCanvasMode("none");
        _editCanvas?.Invalidate();
    }

    // ---- save ----

    private void BakeOverlay(CanvasDrawingSession ds, Rect crop)
    {
        // Markup is stored in oriented-image space; shift by the crop origin to land in the render target.
        foreach (var m in _markup) DrawShape(ds, m, -crop.X, -crop.Y, 1.0);
    }

    private async Task<bool> ExportToAsync(string destPath)
    {
        try
        {
            var quality = 0.92f;
            await _editor.ExportAsync(_edit, destPath, quality, BakeOverlay);
            return true;
        }
        catch (Exception ex) { StatusText.Text = "Save failed: " + ex.Message; App.Log("EditorSave", ex); return false; }
    }

    private async void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_editPath is null) return;
        var dir = System.IO.Path.GetDirectoryName(_editPath)!;
        var ext = System.IO.Path.GetExtension(_editPath);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(_editPath);
        var dest = UniquePath(System.IO.Path.Combine(dir, baseName + "-edited" + ext), isDir: false);
        if (await ExportToAsync(dest))
        {
            StatusText.Text = "Saved " + System.IO.Path.GetFileName(dest);
            ExitEditMode(reloadViewer: false);
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_editPath is null) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_editPath) + "-edited";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (await ExportToAsync(file.Path))
        {
            StatusText.Text = "Saved " + file.Name;
            ExitEditMode(reloadViewer: false);
        }
    }

    private async void SaveOverwrite_Click(object sender, RoutedEventArgs e)
    {
        if (_editPath is null) return;
        var confirm = new ContentDialog
        {
            Title = "Overwrite original?",
            Content = "This replaces the original file on disk. This can't be undone.",
            PrimaryButtonText = "Overwrite", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        // The source bitmap holds pixels in memory (the load stream was already closed), so the
        // file isn't locked and we can overwrite it directly.
        var path = _editPath;
        if (await ExportToAsync(path))
        {
            StatusText.Text = "Saved " + System.IO.Path.GetFileName(path);
            ExitEditMode(reloadViewer: true);
        }
    }
}

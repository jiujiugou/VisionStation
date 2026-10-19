using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services;
using VisionStation.App.Services.Coordinates;
using VisionStation.Core.Models;

namespace VisionStation.App.ViewModels;

/// <summary>
/// ROI 可视化编辑器 —— 鼠标拖拽画框 + 移动 + 缩放 + 保存/加载
///
/// 职责：管理 ROI 列表 + 处理鼠标交互
/// 坐标换算全部委托给 CoordinateMapper，本类不碰换算细节
/// </summary>
public partial class RoiEditorViewModel : ObservableObject
{
    private readonly CoordinateMapper _mapper;
    private readonly RecipeConfig _recipe;
    private readonly AppPaths _paths;

    /// <summary>当前编辑的图像</summary>
    [ObservableProperty]
    private BitmapSource? _image;

    /// <summary>ROI 列表</summary>
    public ObservableCollection<EditableRoi> Rois { get; } = new();

    /// <summary>当前选中的 ROI</summary>
    [ObservableProperty]
    private EditableRoi? _selectedRoi;

    // ── 拖拽状态 ──
    private bool _isDrawing;
    private Point _drawStart;       // 画矩形起点（图像坐标）
    private Point _drawStartScreen; // 画矩形起点（屏幕坐标，用于点击/拖拽判定）
    private EditableRoi? _drawingRoi;
    private bool _drawAdded;        // 新框是否已加入列表（拖出阈值才加入，防止点击误建框）
    private bool _isDragging;
    private Point _dragStart;       // 拖拽起点（屏幕坐标）
    private double _dragStartX, _dragStartY;  // 拖拽前 ROI 的图像坐标
    private bool _isResizing;
    private Point _resizeStart;
    private double _resizeStartW, _resizeStartH;

    // ── 交互参数 ──
    // 按下后移动超过此距离（屏幕 dp）才算画框，纯点击只取消选中
    private const double DrawStartThresholdPx = 4.0;
    // 新画的框小于此尺寸（图像像素）视为误触，松手即丢弃
    private const double MinRoiSizePx = 8.0;

    public RoiEditorViewModel(CoordinateMapper mapper, RecipeConfig recipe, AppPaths paths)
    {
        _mapper = mapper;
        _recipe = recipe;
        _paths = paths;

        // 从配方加载已有 ROI
        foreach (var d in recipe.Rois)
        {
            var r = new EditableRoi { Name = d.Name, ImageX = d.X, ImageY = d.Y, ImageWidth = d.Width, ImageHeight = d.Height };
            Rois.Add(r);
        }
    }

    // ════════════════════════════════════════════════
    //  触发布局更新（由 View 的 SizeChanged 调用）
    // ════════════════════════════════════════════════

    /// <summary>View 通知 VM：容器和图像渲染尺寸变了，重新算映射</summary>
    public void UpdateLayout(double containerW, double containerH,
                              double imageRenderW, double imageRenderH)
    {
        if (Image == null) return;
        _mapper.UpdateLayout(containerW, containerH,
            imageRenderW, imageRenderH,
            Image.PixelWidth, Image.PixelHeight);

        WeakReferenceMessenger.Default.Send(new LogMessage(
            $"ROI编辑器: 图像原始尺寸 {Image.PixelWidth}x{Image.PixelHeight}px, " +
            $"渲染尺寸 {imageRenderW:F0}x{imageRenderH:F0}dp, " +
            $"容器 {containerW:F0}x{containerH:F0}dp, " +
            $"缩放比例 {_mapper.ScaleFactor:F4}",
            LogMsgLevel.Info));

        // 所有已有 ROI 的屏幕显示位置刷新
        foreach (var r in Rois)
            RefreshDisplay(r);
    }

    /// <summary>刷新单个 ROI 的屏幕显示坐标（从图像坐标→屏幕坐标）</summary>
    private void RefreshDisplay(EditableRoi r)
    {
        var rect = _mapper.ImageRectToScreen(r.ImageX, r.ImageY, r.ImageWidth, r.ImageHeight);
        r.DisplayX = rect.X;
        r.DisplayY = rect.Y;
        r.DisplayWidth = rect.Width;
        r.DisplayHeight = rect.Height;
    }

    // ════════════════════════════════════════════════
    //  鼠标事件 —— 画出图像坐标，渲染用 Mapper
    // ════════════════════════════════════════════════

    public void MouseDown(Point screenPoint)
    {
        var imgPt = _mapper.ScreenToImage(screenPoint);

        // 点中已有 ROI → 选中
        var hit = HitTest(screenPoint);
        if (hit != null)
        {
            SelectRoi(hit);

            if (IsNearCorner(screenPoint, hit))
            {
                _isResizing = true;
                _resizeStart = screenPoint;
                _resizeStartW = hit.ImageWidth;
                _resizeStartH = hit.ImageHeight;
                return;
            }

            _isDragging = true;
            _dragStart = screenPoint;
            _dragStartX = hit.ImageX;
            _dragStartY = hit.ImageY;
            return;
        }

        // 空白处 → 准备画新矩形（先不入列，拖出阈值才真正建框）
        SelectedRoi = null;
        foreach (var roi in Rois) roi.IsSelected = false;
        _isDrawing = true;
        _drawStart = imgPt;
        _drawStartScreen = screenPoint;
        _drawAdded = false;

        _drawingRoi = new EditableRoi
        {
            Name = NextRoiName(),
            ImageX = imgPt.X,
            ImageY = imgPt.Y,
            ImageWidth = 0,
            ImageHeight = 0
        };
    }

    public void MouseMove(Point screenPoint)
    {
        var imgPt = _mapper.ScreenToImage(screenPoint);

        if (_isDrawing && _drawingRoi != null)
        {
            // 点击/拖拽分离：按下后移动距离超过阈值才真正建框
            if (!_drawAdded)
            {
                double dxs = screenPoint.X - _drawStartScreen.X;
                double dys = screenPoint.Y - _drawStartScreen.Y;
                if (Math.Sqrt(dxs * dxs + dys * dys) < DrawStartThresholdPx)
                    return; // 还在阈值内 → 视为普通点击，不画框

                RefreshDisplay(_drawingRoi);
                Rois.Add(_drawingRoi);
                _drawAdded = true;
            }

            double x = Math.Min(_drawStart.X, imgPt.X);
            double y = Math.Min(_drawStart.Y, imgPt.Y);
            double w = Math.Abs(imgPt.X - _drawStart.X);
            double h = Math.Abs(imgPt.Y - _drawStart.Y);
            _drawingRoi.ImageX = x;
            _drawingRoi.ImageY = y;
            _drawingRoi.ImageWidth = w;
            _drawingRoi.ImageHeight = h;
            RefreshDisplay(_drawingRoi);
        }
        else if (_isDragging && SelectedRoi != null)
        {
            double dx = (screenPoint.X - _dragStart.X) / _mapper.ScaleFactor;
            double dy = (screenPoint.Y - _dragStart.Y) / _mapper.ScaleFactor;
            SelectedRoi.ImageX = _dragStartX + dx;
            SelectedRoi.ImageY = _dragStartY + dy;
            RefreshDisplay(SelectedRoi);
        }
        else if (_isResizing && SelectedRoi != null)
        {
            double dw = (screenPoint.X - _resizeStart.X) / _mapper.ScaleFactor;
            double dh = (screenPoint.Y - _resizeStart.Y) / _mapper.ScaleFactor;
            SelectedRoi.ImageWidth = Math.Max(5, _resizeStartW + dw);
            SelectedRoi.ImageHeight = Math.Max(5, _resizeStartH + dh);
            RefreshDisplay(SelectedRoi);
        }
    }

    public void MouseUp()
    {
        // ── 画框结束：误触/过小框丢弃，正常框选中 ──
        if (_isDrawing && _drawingRoi != null)
        {
            bool tooSmall = _drawingRoi.ImageWidth < MinRoiSizePx || _drawingRoi.ImageHeight < MinRoiSizePx;
            if (!_drawAdded || tooSmall)
            {
                Rois.Remove(_drawingRoi);   // 未入列时 Remove 是安全的空操作
                SelectedRoi = null;
            }
            else
            {
                SelectRoi(_drawingRoi);     // 画完自动选中，方便马上改名/微调
            }
        }

        _isDrawing = false; _isDragging = false; _isResizing = false;
        _drawingRoi = null;
        _drawAdded = false;
    }

    private void SelectRoi(EditableRoi r)
    {
        SelectedRoi = r;
        foreach (var roi in Rois) roi.IsSelected = (roi == r);
    }

    private EditableRoi? HitTest(Point screenPoint)
    {
        for (int i = Rois.Count - 1; i >= 0; i--)
        {
            var r = Rois[i];
            // 过小的框（历史误触产物）扩大 6dp 命中范围，否则窄于点击容差的框永远选不中
            double inflateX = r.DisplayWidth < 8 ? 6 : 0;
            double inflateY = r.DisplayHeight < 8 ? 6 : 0;
            if (screenPoint.X >= r.DisplayX - inflateX && screenPoint.X <= r.DisplayX + r.DisplayWidth + inflateX &&
                screenPoint.Y >= r.DisplayY - inflateY && screenPoint.Y <= r.DisplayY + r.DisplayHeight + inflateY)
                return r;
        }
        return null;
    }

    private static bool IsNearCorner(Point screenPoint, EditableRoi r)
    {
        double cx = r.DisplayX + r.DisplayWidth, cy = r.DisplayY + r.DisplayHeight;
        return Math.Abs(screenPoint.X - cx) < 16 && Math.Abs(screenPoint.Y - cy) < 16;
    }

    // ════════════════════════════════════════════════
    //  命令
    // ════════════════════════════════════════════════

    /// <summary>
    /// 生成不重复的默认 ROI 名。
    ///
    /// 不能直接用 Rois.Count+1：删掉中间一个再添加就会撞上已有名字。
    /// 重名 ROI 写进配方后，Pipeline 侧只能保留一个 —— 等于悄悄改掉了检测区域，
    /// 是那种"界面看着正常、结果不对"的故障。
    /// </summary>
    private string NextRoiName()
    {
        int n = Rois.Count + 1;
        while (Rois.Any(r => r.Name == $"ROI-{n}")) n++;
        return $"ROI-{n}";
    }

    [RelayCommand]
    private void AddRoi()
    {
        var r = new EditableRoi { Name = NextRoiName(), ImageX = 50, ImageY = 50, ImageWidth = 100, ImageHeight = 80 };
        RefreshDisplay(r);
        Rois.Add(r); SelectRoi(r);
    }

    [RelayCommand] private void DeleteSelected() { if (SelectedRoi != null) { Rois.Remove(SelectedRoi); SelectedRoi = null; } }

    [RelayCommand]
    private void RenameSelected()
    {
        if (SelectedRoi == null) return;
        SelectedRoi.Name = SelectedRoi.Name.EndsWith("*") ? SelectedRoi.Name.TrimEnd('*') : SelectedRoi.Name + "*";
    }

    [RelayCommand]
    private void SaveToFile()
    {
        _recipe.Rois = Rois.Select(r => new RoiData
        {
            Name = r.Name, X = (int)r.ImageX, Y = (int)r.ImageY,
            Width = (int)r.ImageWidth, Height = (int)r.ImageHeight
        }).ToList();

        // 写到与加载一致的配方路径（AppPaths.RecipePath），避免存到 bin 副本导致下次加载旧数据
        var dir = Path.GetDirectoryName(_paths.RecipePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(_recipe, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_paths.RecipePath, json);

        WeakReferenceMessenger.Default.Send(new RoisUpdatedMessage());
        MessageBox.Show($"已保存到 {_paths.RecipePath}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void LoadFromFile()
    {
        Rois.Clear();
        foreach (var d in _recipe.Rois)
        {
            var r = new EditableRoi { Name = d.Name, ImageX = d.X, ImageY = d.Y, ImageWidth = d.Width, ImageHeight = d.Height };
            RefreshDisplay(r); Rois.Add(r);
        }
    }
}

/// <summary>可编辑的 ROI 矩形</summary>
public partial class EditableRoi : ObservableObject
{
    [ObservableProperty] private string _name = "ROI";
    [ObservableProperty] private bool _isSelected;

    // 图像像素坐标（唯一真值）
    public double ImageX { get; set; }
    public double ImageY { get; set; }
    public double ImageWidth { get; set; }
    public double ImageHeight { get; set; }

    // 屏幕显示坐标（由 RefreshDisplay 计算刷新）
    [ObservableProperty] private double _displayX;
    [ObservableProperty] private double _displayY;
    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;
}

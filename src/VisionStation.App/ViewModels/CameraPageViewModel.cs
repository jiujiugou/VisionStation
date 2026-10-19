using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.Devices;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 相机管理面板 VM —— 查看状态 + 切换相机类型
/// </summary>
public partial class CameraPageViewModel : ObservableObject
{
    private readonly SwappableCamera _swappable;
    private readonly DeviceManager _devices;

    [ObservableProperty] private string _cameraType = "--";
    [ObservableProperty] private string _cameraStatus = "离线";
    [ObservableProperty] private int _frameCount;
    [ObservableProperty] private string _cameraPath = "--";

    /// <summary>可选相机列表</summary>
    public string[] Modes => SwappableCamera.AvailableModes;

    /// <summary>当前选中的模式</summary>
    [ObservableProperty] private string _selectedMode;

    public CameraPageViewModel(SwappableCamera swappable, DeviceManager devices)
    {
        _swappable = swappable;
        _devices = devices;
        _selectedMode = swappable.CurrentMode;
        Refresh();
    }

    partial void OnSelectedModeChanged(string value)
    {
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        CameraType = _swappable.CurrentMode switch
        {
            "File" => "文件相机", "Webcam" => "USB摄像头", "Hikvision" => "海康工业相机", _ => _swappable.CurrentMode
        };

        var camDev = _devices.Devices.FirstOrDefault(d => d.Type == "Camera");
        CameraStatus = camDev?.Status ?? (_swappable.IsAvailable ? "就绪" : "离线");
        FrameCount = _swappable.FrameCount;
        CameraPath = _swappable.CurrentMode == "File" ? "本地图片文件夹" : "实时采集";
    }

    /// <summary>切换到选中的相机类型</summary>
    [RelayCommand]
    private void ApplySwitch()
    {
        if (SelectedMode == _swappable.CurrentMode) return;

        try
        {
            // SwapTo 返回 false = 新相机没建成，已回滚（旧相机仍可用）。
            // 不能无条件报"切换成功" —— 那会把"相机全没了"记成一条 Success 日志。
            if (!_swappable.SwapTo(SelectedMode))
            {
                Refresh();
                WeakReferenceMessenger.Default.Send(new LogMessage(
                    $"切换到 {SelectedMode} 失败：相机不可用，已保持原相机", LogMsgLevel.Error));
                SelectedMode = _swappable.CurrentMode;
                return;
            }

            Refresh();

            // 通知其他模块相机变了
            var camDev = _devices.Devices.FirstOrDefault(d => d.Type == "Camera");
            if (camDev is CameraDevice cd) cd.Refresh();
            WeakReferenceMessenger.Default.Send(new CameraStatusMessage(_swappable.IsAvailable));
            WeakReferenceMessenger.Default.Send(new LogMessage(
                $"已切换到 {CameraType}", LogMsgLevel.Success));
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(
                $"切换失败: {ex.Message}", LogMsgLevel.Error));
        }
    }

    [RelayCommand]
    private void ReconnectCamera()
    {
        var camDev = _devices.Devices.FirstOrDefault(d => d.Type == "Camera");
        camDev?.Connect();
        Refresh();
        WeakReferenceMessenger.Default.Send(new CameraStatusMessage(_swappable.IsAvailable));
    }
}

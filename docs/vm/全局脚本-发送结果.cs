using System;
using System.Text;
using VM.GlobalScript.Methods;
using System.Windows.Forms;
using iMVS_6000PlatformSDKCS;
using System.Runtime.InteropServices;

/**************************************************************
 * 用途：VisionMaster 全局脚本 —— 直接把检测结果发给 VisionStation
 *
 * 为什么用脚本而不是「发送数据」模块：
 *   4.2 的发送数据模块只接受"订阅"，一行一行点链子；要拼一条带判定的
 *   结果得点七八下，还容易点错。脚本里一行 SendCommDeviceData 就完了。
 *
 * 前提（只配一次）：
 *   系统 → 通信管理 → 建一个「TCP客户端通信」设备，目标 127.0.0.1:9100
 *   （设备列表里的第 1 个就是 DeviceId = 1）
 *
 * 发出去的格式（VisionStation 已支持）：
 *   OK;123          ← 第 1 段是判定，后面按配方 vm.outputs 顺序对应
 *   末尾的 \r\n 必须有：接收端按行定界
 **************************************************************/

public class UserGlobalScript : UserGlobalMethods, IScriptMethods
{
    /// <summary>通信管理里第 1 个通信设备的编号（列表从 1 开始）</summary>
    private const int DeviceId = 1;

    private int _count;

    public int Init()
    {
        // 二次开发 SDK 初始化
        InitSDK();

        // 挂上全局通信模块（否则 SendCommDeviceData 找不到设备）
        StartGlobalCommunicate();

        // 连续运行时每隔 1000ms 执行一次 Process
        SetScriptContinuousExecuteInterval(1000);

        ConsoleWrite("[VisionStation] 发送脚本已启动，设备ID=" + DeviceId);
        return 0;
    }

    public int Process()
    {
        if (m_operateHandle == IntPtr.Zero)
        {
            return ImvsSdkPFDefine.IMVS_EC_NULL_PTR;
        }

        _count++;

        // ① 先发一条假的判定，把链路验通（这一步不依赖任何模块输出）
        //    格式：判定;数值 —— 接收端第 1 段当判定，后面按配方顺序对名字
        //    末尾 \r\n 是硬要求：接收端按行定界，没有换行会一直攒着不发出去
        string line = "OK;" + _count + "\r\n";

        SendCommDeviceData(line, DeviceId, 0, DataType.StringType);

        ConsoleWrite("[VisionStation] 已发送: OK;" + _count);
        return 0;
    }
}

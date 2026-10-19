using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.UnitTests;

/// <summary>
/// 阶段3 · core-main 切片 第二批（补强）：三个工具判定段的**边界与防护**。
///
/// 本文件专门针对第二批变异测试的存活变异体逐条补靶：
///   排除圆"三条件齐备才启用"的短路逻辑、贴边上界、距离计算的平方和、
///   总数合成、ROI 坐标还原、构造期参数校验、模板加载通道、标注颜色与标签。
///
/// 与 ToolJudgementTests 的分工：那份钉"判定方向"，这份钉"判定边界与元数据"。
/// </summary>
public class ToolBoundaryTests
{
    private static RoiRegion Roi(string name, int x = 0, int y = 0, int w = 200, int h = 200) =>
        new() { Name = name, X = x, Y = y, Width = w, Height = h };

    // ══════════════ DefectTool：排除圆的"三条件齐备" ══════════════
    // 代码：exclusionEnabled = ExcludeRadius > 0 && ExcludeCenterX >= 0 && ExcludeCenterY >= 0
    // 任一条件不成立就不启用。以下四条分别打断三个条件中的一个。

    /// <summary>半径 0（= 未启用）时不得排除：块落在圆心位置也必须检出</summary>
    [Fact]
    public void DefectTool_ExcludeRadiusZero_ExclusionDisabled()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(90, 90, 20, 20), new Scalar(20), -1);   // 正好压在圆心

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128)
        {
            ExcludeCenterX = 100, ExcludeCenterY = 100, ExcludeRadius = 0
        };
        var result = tool.Execute(img, Roi("Defect"));

        Assert.False(result.IsOk, "半径 0 表示不排除，圆心处的块必须算缺陷");
    }

    /// <summary>
    /// 圆心 X 非法（-1）时不得排除。
    /// 块特意放在 x≈30——若短路条件被改成 `||`，排除圆会以 (-1,100) 为心生效并吞掉这个块，
    /// 判定从 NG 变 OK，用例即失败。放在圆心附近则两种逻辑都排除不掉，等于没测。
    /// </summary>
    [Fact]
    public void DefectTool_ExcludeCenterXInvalid_ExclusionDisabled()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(20, 90, 20, 20), new Scalar(20), -1);   // 重心 (30,100)

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128)
        {
            ExcludeCenterX = -1, ExcludeCenterY = 100, ExcludeRadius = 50
        };
        var result = tool.Execute(img, Roi("Defect"));

        Assert.False(result.IsOk, "圆心 X 为 -1 表示未配置，不得启用排除");
    }

    /// <summary>圆心 Y 非法（-1）时不得排除；块放在 y≈30，理由同上</summary>
    [Fact]
    public void DefectTool_ExcludeCenterYInvalid_ExclusionDisabled()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(90, 20, 20, 20), new Scalar(20), -1);   // 重心 (100,30)

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128)
        {
            ExcludeCenterX = 100, ExcludeCenterY = -1, ExcludeRadius = 50
        };
        var result = tool.Execute(img, Roi("Defect"));

        Assert.False(result.IsOk, "圆心 Y 为 -1 表示未配置，不得启用排除");
    }

    /// <summary>圆心坐标为 0 是**合法位置**（图像左上角区域），必须启用排除</summary>
    [Fact]
    public void DefectTool_ExcludeCenterAtOrigin_ExclusionEnabled()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(20, 20, 20, 20), new Scalar(20), -1);   // 重心 (30,30)

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128)
        {
            ExcludeCenterX = 0, ExcludeCenterY = 0, ExcludeRadius = 50   // dist = 42.4 < 50
        };
        var result = tool.Execute(img, Roi("Defect"));

        Assert.True(result.IsOk, "坐标 0 是合法圆心，必须参与排除判定");
    }

    /// <summary>
    /// 距离用两轴平方和：块与圆心在 X、Y 两轴都有偏移时必须按真实欧氏距离判定。
    /// 若平方和被改成平方差，X/Y 偏移相同时会算出 0，误把缺陷当成孔。
    /// </summary>
    [Fact]
    public void DefectTool_DiagonalOffset_UsesBothAxes()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(130, 130, 20, 20), new Scalar(20), -1);  // 重心 (140,140)

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128)
        {
            ExcludeCenterX = 100, ExcludeCenterY = 100, ExcludeRadius = 50   // dist = 56.6 > 50
        };
        var result = tool.Execute(img, Roi("Defect"));

        Assert.False(result.IsOk, "对角方向 56.6px 在排除圆外，必须算缺陷");
    }

    /// <summary>贴边规则的上界：块贴顶边（top == 0）也必须被抑制并留痕</summary>
    [Fact]
    public void DefectTool_TouchingTopEdge_IsSuppressed()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(80, 0, 40, 40), new Scalar(20), -1);     // top = 0

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128);
        var result = tool.Execute(img, Roi("Defect"));

        Assert.True(result.IsOk);
        Assert.Equal(1, (int)result.Metrics["EdgeSuppressed"]);
    }

    /// <summary>
    /// 总数 = 划痕 + 缺口。同时存在两类时才可能暴露"和"被写成"差"的变异，
    /// 且 Summary 的总数必须与分类计数一致。
    /// </summary>
    [Fact]
    public void DefectTool_ScratchAndNotchTogether_TotalIsSum()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(40, 50, 60, 10), new Scalar(20), -1);    // 6:1 划痕
        Cv2.Rectangle(img, new Rect(140, 140, 20, 20), new Scalar(20), -1);  // 1:1 缺口

        var tool = new DefectTool(areaMin: 30, thresholdValue: 128);
        var result = tool.Execute(img, Roi("Defect"));

        Assert.False(result.IsOk);
        Assert.Equal(1, (int)result.Metrics["ScratchCount"]);
        Assert.Equal(1, (int)result.Metrics["NotchCount"]);
        Assert.Contains("总数:2", result.Summary);
    }

    // ══════════════ DefectTool：ROI 坐标还原与标注 ══════════════

    /// <summary>轮廓点必须从 ROI 局部坐标还原到整幅图坐标（+roi.X / +roi.Y）</summary>
    [Fact]
    public void DefectTool_NonZeroRoi_ContourPointsInImageSpace()
    {
        using var img = new Mat(300, 300, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(140, 140, 30, 30), new Scalar(20), -1);  // 原图坐标 140..170

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128);
        var result = tool.Execute(img, Roi("Defect", 100, 100, 150, 150));   // ROI 从 (100,100) 起

        var contour = Assert.IsType<DrawContour>(result.Overlays[0]);
        int minX = contour.Points.Min(p => p.X);
        int minY = contour.Points.Min(p => p.Y);

        Assert.InRange(minX, 138, 144);
        Assert.InRange(minY, 138, 144);
    }

    /// <summary>分类着色：划痕红、缺口橙——操作员靠颜色区分类型，判错会导致返修方向错误</summary>
    [Fact]
    public void DefectTool_OverlayColors_DistinguishScratchFromNotch()
    {
        using (var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200)))
        {
            Cv2.Rectangle(img, new Rect(70, 95, 60, 10), new Scalar(20), -1);
            var tool = new DefectTool(areaMin: 30, thresholdValue: 128);
            var c = Assert.IsType<DrawContour>(tool.Execute(img, Roi("Defect")).Overlays[0]);
            Assert.Equal(255, c.Color.Val2);      // 红
            Assert.Equal(0, c.Color.Val1);
        }

        using (var img2 = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200)))
        {
            Cv2.Rectangle(img2, new Rect(90, 90, 20, 20), new Scalar(20), -1);
            var tool = new DefectTool(areaMin: 30, thresholdValue: 128);
            var c = Assert.IsType<DrawContour>(tool.Execute(img2, Roi("Defect")).Overlays[0]);
            Assert.Equal(165, c.Color.Val1);      // 橙
        }
    }

    // ══════════════ DimensionTool：构造期校验 ══════════════

    [Fact]
    public void DimensionTool_NullCalibration_MustThrow()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new DimensionTool(null!));
        Assert.Equal("calibration", ex.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-0.1)]
    public void DimensionTool_InvalidTolerance_MustThrow(double tolerance)
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0 });

        Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, 4.0, tolerance));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-3)]
    public void DimensionTool_InvalidDesignedDiameter_MustThrow(double designed)
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0 });

        Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, designed, 0.1));
    }

    /// <summary>圆心坐标必须从 ROI 局部还原到整幅图（+roi.X / +roi.Y）</summary>
    [Fact]
    public void DimensionTool_NonZeroRoi_CenterInImageSpace()
    {
        using var img = new Mat(300, 300, MatType.CV_8UC1, new Scalar(30));
        Cv2.Circle(img, 180, 180, 20, new Scalar(200), -1);   // 原图圆心 (180,180)

        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0, DesignedDiameterMm = 4.0, DimensionToleranceMm = 0.5 });
        var tool = new DimensionTool(calib, 4.0, 0.5);

        var result = tool.Execute(img, Roi("Dimension", 100, 100, 160, 160));

        var circle = Assert.IsType<DrawCircle>(result.Overlays[0]);
        Assert.InRange(circle.Center.X, 176, 184);
        Assert.InRange(circle.Center.Y, 176, 184);
    }

    /// <summary>合格/超差的标注颜色与标签必须区分——复核员靠它判断</summary>
    [Fact]
    public void DimensionTool_OverlayColorsAndLabels_ReflectVerdict()
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0, DesignedDiameterMm = 4.0, DimensionToleranceMm = 0.5 });
        var tool = new DimensionTool(calib, 4.0, 0.5);

        using (var ok = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30)))
        {
            Cv2.Circle(ok, 100, 100, 20, new Scalar(200), -1);
            var c = Assert.IsType<DrawCircle>(tool.Execute(ok, Roi("Dimension")).Overlays[0]);
            Assert.Equal(0, c.Color.Val2);          // 绿：无红分量
            Assert.Equal("OK", c.Label);
        }

        using (var ng = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30)))
        {
            Cv2.Circle(ng, 100, 100, 10, new Scalar(200), -1);   // 明显偏小
            var c = Assert.IsType<DrawCircle>(tool.Execute(ng, Roi("Dimension")).Overlays[0]);
            Assert.Equal(220, c.Color.Val2);        // 红
            Assert.Equal("NG超差", c.Label);
        }
    }

    // ══════════════ PresenceTool：模板加载通道 ══════════════

    /// <summary>从文件加载失败（路径不存在）必须归一化为"模板无效"，而不是留一个空 Mat 每帧抛异常</summary>
    [Fact]
    public void PresenceTool_LoadFromMissingFile_ReportsInvalidTemplate()
    {
        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "__no_such_template__.png"));

        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(128));
        var result = tool.Execute(img, Roi("Presence"));

        Assert.False(result.IsOk);
        Assert.Contains("模板无效", result.Summary);
    }

    /// <summary>传 null Mat 不得抛 NullReferenceException，应归一化为"模板无效"</summary>
    [Fact]
    public void PresenceTool_LoadNullMat_ReportsInvalidTemplate()
    {
        var tool = new PresenceTool(0.70);
        tool.LoadTemplate((Mat)null!);

        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(128));
        var result = tool.Execute(img, Roi("Presence"));

        Assert.False(result.IsOk);
        Assert.Contains("模板无效", result.Summary);
    }

    /// <summary>空 Mat 同样视为加载失败</summary>
    [Fact]
    public void PresenceTool_LoadEmptyMat_ReportsInvalidTemplate()
    {
        var tool = new PresenceTool(0.70);
        using var empty = new Mat();
        tool.LoadTemplate(empty);

        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(128));
        var result = tool.Execute(img, Roi("Presence"));

        Assert.False(result.IsOk);
        Assert.Contains("模板无效", result.Summary);
    }

    /// <summary>标注框必须覆盖整个 ROI，颜色与标签反映结论</summary>
    [Fact]
    public void PresenceTool_OverlayCoversRoi_WithVerdictLabel()
    {
        var img = new Mat(200, 200, MatType.CV_8UC3, new Scalar(60, 60, 60));
        Cv2.Circle(img, 100, 100, 80, new Scalar(160, 160, 160), -1);
        Cv2.Circle(img, 100, 100, 18, new Scalar(30, 30, 30), -1);
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        using var template = new Mat(gray, new Rect(82, 82, 36, 36));
        img.Dispose();

        var roi = Roi("Presence", 50, 50, 100, 100);
        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(template);
        var result = tool.Execute(gray, roi);

        var rect = Assert.IsType<DrawRect>(result.Overlays[0]);
        Assert.Equal(roi.ToRect(), rect.Rect);
        Assert.Equal(0, rect.Color.Val2);          // 绿
        Assert.StartsWith("OK", rect.Label);
    }

    /// <summary>NG 侧标注：颜色必须转红、标签必须以 NG 开头（复核员据此拦件）</summary>
    [Fact]
    public void PresenceTool_OverlayOnMissingFeature_IsRedWithNgLabel()
    {
        var template = new Mat(36, 36, MatType.CV_8UC1, new Scalar(40));
        Cv2.Circle(template, 18, 18, 12, new Scalar(200), -1);

        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(template);

        using var blank = new Mat(200, 200, MatType.CV_8UC1, new Scalar(160));
        var result = tool.Execute(blank, Roi("Presence", 50, 50, 100, 100));

        var rect = Assert.IsType<DrawRect>(result.Overlays[0]);
        Assert.Equal(220, rect.Color.Val2);        // 红
        Assert.StartsWith("NG", rect.Label);
        Assert.Contains("特征缺失", result.Summary);

        template.Dispose();
    }

    // ══════════════ 工具名契约 ══════════════
    // 工具名是 Pipeline 与配方 ROI 的唯一连接键（同名即覆盖，见 F-C02）。
    // 名字被改空或改错，配方里的 ROI 就再也匹配不上工具。

    [Fact]
    public void Tools_ExposeStableNames()
    {
        Assert.Equal("Defect", new DefectTool().Name);

        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0 });
        Assert.Equal("Dimension", new DimensionTool(calib).Name);

        Assert.Equal("Presence", new PresenceTool().Name);
    }

    /// <summary>排除圆默认坐标必须是 -1（未启用哨兵），不能是任何合法坐标</summary>
    [Fact]
    public void DefectTool_ExclusionDefaultsToDisabled()
    {
        var tool = new DefectTool();
        Assert.Equal(-1.0, tool.ExcludeCenterX);
        Assert.Equal(-1.0, tool.ExcludeCenterY);
        Assert.Equal(0.0, tool.ExcludeRadius);
    }

    // ══════════════ DefectTool：贴边另两侧 ══════════════

    /// <summary>贴右边必须被抑制（同时钉住 `left + width` 的加号）</summary>
    [Fact]
    public void DefectTool_TouchingRightEdge_IsSuppressed()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(160, 80, 40, 40), new Scalar(20), -1);

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128);
        var result = tool.Execute(img, Roi("Defect"));

        Assert.True(result.IsOk);
        Assert.Equal(1, (int)result.Metrics["EdgeSuppressed"]);
    }

    /// <summary>贴底边必须被抑制（同时钉住 `top + height` 的加号）</summary>
    [Fact]
    public void DefectTool_TouchingBottomEdge_IsSuppressed()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(80, 160, 40, 40), new Scalar(20), -1);

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128);
        var result = tool.Execute(img, Roi("Defect"));

        Assert.True(result.IsOk);
        Assert.Equal(1, (int)result.Metrics["EdgeSuppressed"]);
    }

    /// <summary>合格帧的结论文字必须是"表面无缺陷"，不能是空串或缺陷描述</summary>
    [Fact]
    public void DefectTool_CleanSurface_SummarySaysNoDefect()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128);
        var result = tool.Execute(img, Roi("Defect"));

        Assert.Equal("表面无缺陷", result.Summary);
    }

    // ══════════════ DimensionTool：异常消息与指标契约 ══════════════

    [Fact]
    public void DimensionTool_ExceptionMessages_NameTheCause()
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0 });

        var ex1 = Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, 0, 0.1));
        Assert.Contains("设计直径", ex1.Message);

        var ex2 = Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, 4.0, 0));
        Assert.Contains("公差", ex2.Message);
    }

    /// <summary>四件 Metrics 缺一不可：现场排查标定问题时 PixeLDiameter 是唯一线索</summary>
    [Fact]
    public void DimensionTool_MetricsExposeAllFourKeys()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30));
        Cv2.Circle(img, 100, 100, 20, new Scalar(200), -1);

        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0, DesignedDiameterMm = 4.0, DimensionToleranceMm = 0.5 });
        var result = new DimensionTool(calib, 4.0, 0.5).Execute(img, Roi("Dimension"));

        foreach (var key in new[] { "MeasuredDiameter_mm", "DesignedDiameter_mm", "Deviation_mm", "PixelDiameter" })
            Assert.True(result.Metrics.ContainsKey(key), $"缺指标 {key}");

        Assert.True((double)result.Metrics["DesignedDiameter_mm"] == 4.0);
    }

    /// <summary>结论文字必须区分合格与超差两种</summary>
    [Fact]
    public void DimensionTool_SummariesDistinguishVerdicts()
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0, DesignedDiameterMm = 4.0, DimensionToleranceMm = 0.5 });
        var tool = new DimensionTool(calib, 4.0, 0.5);

        using (var ok = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30)))
        {
            Cv2.Circle(ok, 100, 100, 20, new Scalar(200), -1);
            var s = tool.Execute(ok, Roi("Dimension")).Summary;
            Assert.DoesNotContain("超差", s);
            Assert.Contains("设计:", s);
        }

        using (var ng = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30)))
        {
            Cv2.Circle(ng, 100, 100, 10, new Scalar(200), -1);
            var s = tool.Execute(ng, Roi("Dimension")).Summary;
            Assert.Contains("超差", s);
            Assert.Contains("设计:", s);
        }
    }

    /// <summary>合格帧的结论文字必须说明"特征存在"</summary>
    [Fact]
    public void PresenceTool_HitSummary_SaysFeaturePresent()
    {
        var img = new Mat(200, 200, MatType.CV_8UC3, new Scalar(60, 60, 60));
        Cv2.Circle(img, 100, 100, 80, new Scalar(160, 160, 160), -1);
        Cv2.Circle(img, 100, 100, 18, new Scalar(30, 30, 30), -1);
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        using var template = new Mat(gray, new Rect(82, 82, 36, 36));
        img.Dispose();

        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(template);
        var result = tool.Execute(gray, Roi("Presence", 50, 50, 100, 100));

        Assert.Contains("特征存在", result.Summary);
        Assert.DoesNotContain("特征缺失", result.Summary);
    }
}

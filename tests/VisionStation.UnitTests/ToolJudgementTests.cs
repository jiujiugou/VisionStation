using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.UnitTests;

/// <summary>
/// 阶段3 · core-main 切片 第二批：三个工具的**判定段**测试。
///
/// 为什么要单独一批：第一批变异只覆盖 Calibration + CoordinateMath（纯换算），
/// 而规程 §6 要求靶点是"判定即产品"的代码——DefectTool 判 NG/OK、DimensionTool 判超差、
/// PresenceTool 判有无，这三处判错就是漏检。本文件把判定边界逐条钉死，
/// 使 Stryker 在判定段上不再有存活变异。
///
/// 构图原则：全部程序合成，不用真实照片（判定段只需要可区分的灰度形态，
/// 真实图的噪声反而让边界不可复现）。
/// </summary>
public class ToolJudgementTests
{
    private static RoiRegion FullRoi(string name = "Defect") =>
        new() { Name = name, X = 0, Y = 0, Width = 200, Height = 200 };

    // ══════════════════════ DefectTool ══════════════════════

    /// <summary>面积门槛是闭区间下界：面积 == AreaMin 必须**保留**（`area &lt; AreaMin` 才丢）</summary>
    [Fact]
    public void DefectTool_AreaExactlyAtMin_MustBeKept()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(80, 80, 20, 20), new Scalar(20), -1);   // 20x20 = 400px

        var tool = new DefectTool(areaMin: 400, thresholdValue: 128);
        var result = tool.Execute(img, FullRoi());

        Assert.False(result.IsOk);   // 恰好等于下限 → 保留 → 检出缺陷
        Assert.Equal(1, (int)result.Metrics["TotalContours"]);
    }

    /// <summary>反面：面积低于门槛必须丢弃，且不得计入任何计数</summary>
    [Fact]
    public void DefectTool_AreaBelowMin_MustBeDropped()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(80, 80, 19, 19), new Scalar(20), -1);   // 19x19 = 361px

        var tool = new DefectTool(areaMin: 400, thresholdValue: 128);
        var result = tool.Execute(img, FullRoi());

        Assert.True(result.IsOk);
        Assert.Equal(0, (int)result.Metrics["TotalContours"]);
    }

    /// <summary>
    /// 孔排除圆是闭区间：重心到圆心距离 == ExcludeRadius 必须**排除**（`dist &lt;= R`），
    /// 差 1px 则必须保留。这一对用例同时钉住比较方向与闭区间语义。
    /// </summary>
    [Fact]
    public void DefectTool_CentroidExactlyOnExcludeCircle_MustBeExcluded()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(140, 90, 20, 20), new Scalar(20), -1);  // 重心 (150,100)

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128)
        {
            ExcludeCenterX = 100, ExcludeCenterY = 100, ExcludeRadius = 50   // dist == 50
        };
        var result = tool.Execute(img, FullRoi());

        Assert.True(result.IsOk, "重心恰在排除圆边界上应被排除");
        Assert.Equal(0, (int)result.Metrics["NotchCount"]);
    }

    [Fact]
    public void DefectTool_CentroidJustOutsideExcludeCircle_MustBeKept()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(141, 90, 20, 20), new Scalar(20), -1);  // 重心 (151,100)

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128)
        {
            ExcludeCenterX = 100, ExcludeCenterY = 100, ExcludeRadius = 50   // dist == 51
        };
        var result = tool.Execute(img, FullRoi());

        Assert.False(result.IsOk, "落在排除圆外 1px 必须算缺陷");
        Assert.Equal(1, (int)result.Metrics["NotchCount"]);
    }

    /// <summary>长宽比分类：细长归划痕、方块归缺口，两类计数不得互换</summary>
    [Fact]
    public void DefectTool_LongBlob_ClassifiedAsScratch()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(70, 95, 60, 10), new Scalar(20), -1);   // 6:1

        var tool = new DefectTool(areaMin: 30, thresholdValue: 128);
        var result = tool.Execute(img, FullRoi());

        Assert.False(result.IsOk);
        Assert.Equal(1, (int)result.Metrics["ScratchCount"]);
        Assert.Equal(0, (int)result.Metrics["NotchCount"]);
    }

    [Fact]
    public void DefectTool_SquareBlob_ClassifiedAsNotch()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(90, 90, 20, 20), new Scalar(20), -1);   // 1:1

        var tool = new DefectTool(areaMin: 30, thresholdValue: 128);
        var result = tool.Execute(img, FullRoi());

        Assert.False(result.IsOk);
        Assert.Equal(0, (int)result.Metrics["ScratchCount"]);
        Assert.Equal(1, (int)result.Metrics["NotchCount"]);
    }

    /// <summary>
    /// 分类只看长宽比，不看形状（F-C11 契约）。
    /// 圆形团块与方形团块的长宽比都是 1:1，必须同样归为缺口——
    /// 若日后有人加入"圆形度判定"，这条会红，从而强制重新确认判定契约。
    /// </summary>
    [Fact]
    public void DefectTool_CircularBlob_TreatedLikeSquare_NotchByAspectOnly()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Circle(img, 100, 100, 12, new Scalar(20), -1);   // 圆形，外接框近似 1:1

        var tool = new DefectTool(areaMin: 30, thresholdValue: 128);
        var result = tool.Execute(img, FullRoi());

        Assert.False(result.IsOk);
        Assert.Equal(0, (int)result.Metrics["ScratchCount"]);
        Assert.Equal(1, (int)result.Metrics["NotchCount"]);
    }

    /// <summary>
    /// 分类边界：长宽比恰等于阈值 3.0 必须判为划痕（代码用 `&gt;=`）。
    /// 这条专杀 `&gt;=` → `&gt;` 的边界变异。
    /// </summary>
    [Fact]
    public void DefectTool_AspectExactlyAtThreshold_IsScratch()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(85, 95, 30, 10), new Scalar(20), -1);   // 3:1

        var tool = new DefectTool(areaMin: 30, thresholdValue: 128)
        {
            ScratchAspectRatioMin = 3.0
        };
        var result = tool.Execute(img, FullRoi());

        Assert.Equal(1, (int)result.Metrics["ScratchCount"]);
        Assert.Equal(0, (int)result.Metrics["NotchCount"]);
    }

    /// <summary>三类过滤的先后顺序：面积先过滤，贴边计数不得把"面积不足"也算进贴边抑制数</summary>
    [Fact]
    public void DefectTool_TinyEdgeBlob_CountsAsAreaNotEdge()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(0, 90, 10, 10), new Scalar(20), -1);    // 贴左 + 面积 100

        var tool = new DefectTool(areaMin: 400, thresholdValue: 128);
        var result = tool.Execute(img, FullRoi());

        Assert.True(result.IsOk);
        Assert.Equal(0, (int)result.Metrics["EdgeSuppressed"]);   // 被面积关先拦掉，不应记到贴边
    }

    // ══════════════════════ DimensionTool ══════════════════════

    private static DimensionTool MakeDimensionTool(double designed = 4.0, double tolerance = 0.5)
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig
        {
            PixelPerMillimeter = 10.0,
            DesignedDiameterMm = designed,
            DimensionToleranceMm = tolerance
        });
        return new DimensionTool(calib, designed, tolerance);
    }

    private static Mat CircleImage(int radius)
    {
        var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30));
        Cv2.Circle(img, 100, 100, radius, new Scalar(200), -1);
        return img;
    }

    /// <summary>偏差取绝对值：负偏差超差同样必须判 NG（代码用 Math.Abs）</summary>
    [Fact]
    public void DimensionTool_NegativeDeviationBeyondTolerance_MustNG()
    {
        using var img = CircleImage(10);        // 实测约 2.08mm，偏差 -1.92mm
        var result = MakeDimensionTool().Execute(img, FullRoi("Dimension"));

        Assert.False(result.IsOk, $"负偏差超差必须 NG，实际 {result.Summary}");
        Assert.True((double)result.Metrics["Deviation_mm"] < 0);
    }

    /// <summary>反面：公差内的小负偏差必须判 OK</summary>
    [Fact]
    public void DimensionTool_NegativeDeviationWithinTolerance_MustOK()
    {
        using var img = CircleImage(18);        // 实测约 3.63mm，偏差 -0.37mm
        var result = MakeDimensionTool().Execute(img, FullRoi("Dimension"));

        Assert.True(result.IsOk, $"公差内必须 OK，实际 {result.Summary}");
    }

    /// <summary>正偏差超差必须判 NG</summary>
    [Fact]
    public void DimensionTool_PositiveDeviationBeyondTolerance_MustNG()
    {
        using var img = CircleImage(23);        // 实测约 4.62mm，偏差 +0.62mm
        var result = MakeDimensionTool().Execute(img, FullRoi("Dimension"));

        Assert.False(result.IsOk, $"正偏差超差必须 NG，实际 {result.Summary}");
        Assert.True((double)result.Metrics["Deviation_mm"] > 0);
    }

    /// <summary>多圆时取半径最大的那个：小圆在前也不得被选中</summary>
    [Fact]
    public void DimensionTool_MultipleCircles_PicksLargest()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30));
        Cv2.Circle(img, 60, 100, 10, new Scalar(200), -1);    // 小圆先画
        Cv2.Circle(img, 145, 100, 20, new Scalar(200), -1);   // 大圆

        var result = MakeDimensionTool().Execute(img, FullRoi("Dimension"));

        double px = (double)result.Metrics["PixelDiameter"];
        Assert.True(px > 35, $"应选半径 20 的大圆（直径约 41px），实际 {px}");
    }

    /// <summary>未检出圆必须 NG 并说明原因，不能当作 0mm 合格</summary>
    [Fact]
    public void DimensionTool_NoCircle_MustNGWithReason()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(120));
        var result = MakeDimensionTool().Execute(img, FullRoi("Dimension"));

        Assert.False(result.IsOk);
        Assert.Contains("未检测到圆孔", result.Summary);
    }

    // ══════════════════════ PresenceTool ══════════════════════

    private static (Mat gray, Mat template) BuildHoleScene()
    {
        var img = new Mat(200, 200, MatType.CV_8UC3, new Scalar(60, 60, 60));
        Cv2.Circle(img, 100, 100, 80, new Scalar(160, 160, 160), -1);
        Cv2.Circle(img, 100, 100, 18, new Scalar(30, 30, 30), -1);

        var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        var template = new Mat(gray, new Rect(82, 82, 36, 36));
        img.Dispose();
        return (gray, template);
    }

    /// <summary>
    /// 阈值比较：得分明显高于阈值必须 OK。
    ///
    /// 注意未覆盖的一点：`&gt;=` 与 `&gt;` 只在与浮点精确相等处结果不同，
    /// 而 Metrics 里的 MatchScore 已 Math.Round(,4)，拿它当阈值再比较的是未舍入的原值
    /// （本例原值 0.99996 会显示成 1），因此"恰好相等"这一点**在本工具的公共 API 上不可构造**。
    /// 该边界由代码审查保证，不假称已被测试覆盖。
    /// </summary>
    [Fact]
    public void PresenceTool_ScoreAboveThreshold_MustOK()
    {
        var (gray, template) = BuildHoleScene();
        var roi = new RoiRegion { Name = "Presence", X = 50, Y = 50, Width = 100, Height = 100 };

        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(template);
        var result = tool.Execute(gray, roi);

        Assert.True(result.IsOk);
        Assert.True((double)result.Metrics["MatchScore"] >= 0.99);

        gray.Dispose(); template.Dispose();
    }

    /// <summary>反面：特征缺失时低分必须 NG</summary>
    [Fact]
    public void PresenceTool_FeatureMissing_MustNG()
    {
        var (_, template) = BuildHoleScene();
        using var blank = new Mat(200, 200, MatType.CV_8UC1, new Scalar(160));

        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(template);
        var result = tool.Execute(blank, new RoiRegion { Name = "Presence", X = 50, Y = 50, Width = 100, Height = 100 });

        Assert.False(result.IsOk);
        Assert.Contains("特征缺失", result.Summary);

        template.Dispose();
    }

    /// <summary>阈值必须被钳到 [0,1]：越界配置不得让判定反转</summary>
    [Fact]
    public void PresenceTool_ThresholdIsClampedToUnitRange()
    {
        Assert.Equal(1.0, new PresenceTool(1.5).Threshold);
        Assert.Equal(0.0, new PresenceTool(-0.5).Threshold);
    }

    /// <summary>
    /// NaN 阈值必须被拒（F-C10）。
    /// Math.Clamp(NaN,0,1) 返回 NaN，而 `score &gt;= NaN` 恒为 false ——
    /// 即配方里出现 NaN 时每个零件都判"特征缺失"，产线全停却没有任何参数线索。
    /// </summary>
    [Fact]
    public void PresenceTool_NaNThreshold_MustThrow()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceTool(double.NaN));
        Assert.Contains("阈值", ex.Message);
    }
}

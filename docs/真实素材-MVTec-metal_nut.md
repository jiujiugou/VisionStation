# 真实素材：MVTec AD / metal_nut

> 一句话：**267 张真实工业相机拍的五金件图**（不是渲染图），带 25 张缺陷掩膜和逐张真值清单。
> 用来验"缺陷检出 / 误报率 / 链路吞吐"，**不能**验尺寸准确度——它没有卡尺真值。

## 1. 是什么

MVTec AD 是工业异常检测领域最常被引用的公开数据集，图像全部由工业相机实拍。
本仓库只取了其中 `metal_nut` 一个类别（金属螺母/挡片），因为它的外形和垫圈接近：
圆形内孔 + 外缘缺口，既能跑圆查找，也能跑缺陷检测。

| 项 | 值 |
|---|---|
| 图像 | 700 × 700，8 位灰度 PNG |
| 总数 | **267 张**（坏件 25 / 好件 242） |
| 缺陷掩膜 | 25 张（像素级，能看出坏在哪） |
| 体积 | 约 57 MB |
| 位置 | `TestImages/MVTec-AD/metal_nut/`（已在 `.gitignore` 里，不进仓库） |

缺陷类型：这一版重打包把原始 MVTec 的 `bent / color / flip / scratch` 合并成了二分类
`bad / good`，所以**没有细类标签**。从掩膜看，绝大多数是**边缘变形、缺口**这类几何缺陷。

缺陷面积（非零掩膜像素，画幅 700×700 = 49 万像素）：

| 最小 | 中位 | 最大 |
|---|---|---|
| 9,177 px（1.9%） | 23,344 px（4.8%） | 60,275 px（12.3%） |

→ **这批缺陷面积都很大**。在 VM 里把面积阈值定在几千像素就能全检出，真正难的是
别把合格件边缘的毛刺和反光误判成缺陷（242 张好件就是用来量这个误判率的）。

## 2. 来源与许可

- 官方：<https://www.mvtec.com/company/research/datasets/mvtec-ad>
- 本仓库的取数途径：Kaggle `nguyenduchuhu/mvtec-metal-nut`（MVTec AD 的重打包版）
- **许可：CC BY-NC-SA 4.0 —— 仅限非商业用途**。
  放进求职作品集 / 面试演示 / 教学都没问题，但要署名；**不要**塞进任何商用交付物。

## 3. 目录

```
TestImages/MVTec-AD/metal_nut/
├─ test/                    47 张：25 坏 + 22 好（扁平，文件名带真值）
│   ├─ metal_nut_bad_000.png ... 024
│   └─ metal_nut_good_000.png ... 021
├─ good/                    220 张好件（误报率的分母）
├─ ground_truth/bad_mask/   25 张缺陷掩膜（原名）
├─ manifest.json            逐张真值 + sha256 + 尺寸 + 掩膜关联 + 面积统计
├─ preview.png              缩略总览
├─ preview_defects.png      原图 / 掩膜叠加（看坏件坏在哪）
└─ README.md                同目录内的简要说明
```

`manifest.json` 里的 `sha256` 是对**写出去的 PNG 文件**算的，可以当版本锁：
素材被替换或改动会立即发现。已实测 267 项全部一致。

## 4. 能验什么 / 不能验什么

| 能力 | 能不能验 | 说明 |
|---|---|---|
| 缺陷检出率 | ✅ | 25 张坏件，知道哪张该判 NG |
| 误报率 | ✅ | 242 张好件，理想情况全判 OK |
| 全链路吞吐 / 丢帧 | ✅ | 267 条连续推送，能压出 EF 写库变慢、丢帧计数 |
| 测量重复性 | ⚠️ 只能算"含位姿扰动的波动" | 图上没有件号，分不出「同一件多次拍」和「不同件」，所以严格说不叫重复性 σ |
| 尺寸准确度 / 超差判定 | ❌ | **没有卡尺真值**，任何"偏 0.02mm"的说法都是编的 |
| 光学可行性 | ⚠️ 参考价值有限 | 这批图的光照/镜头和你现场不一样，参数不能直接搬 |

**注意**：`models/neu-det.onnx` 是 NEU-DET 六类模型，跟 metal_nut 语义无关，
所以这套素材 **不能** 用 ONNX 引擎跑。要么 `engine="Vm"`，要么只当 File 相机素材。

## 5. VM 侧怎么跑（配置口径）

> 下面这些是**参数口径**，不是我已代你在 VM 里点过的操作 —— VM 的流程必须在它自己的
> 客户端里搭。第一次跑完建议用 `tools/VmSimulator --listen` 抓一次包核对格式。

1. **图像源1**：类型「本地图像」，路径指向
   `TestImages/MVTec-AD/metal_nut/test/metal_nut_bad_000.png`。
2. **缺陷检测1**（VM 的缺陷检测类模块）：ROI 圈住整个件，
   面积范围按上面的分布取（例如 3,000 ~ 100,000 px），判定接总判定。
3. **圆查找1**：测中心孔，输出 `Radius` —— 用来做测量波动的观测量。
4. **发送数据**：`输出至` 选**通信设备**（先在 系统 → 通信管理 建 TCP 客户端，目标 `127.0.0.1:9100`）。
   分隔符 `;`，结束符 `\r\n`，按顺序订阅：总判定 → `圆查找1.Radius` → `缺陷检测1` 的斑点数。
   发出来是 `OK;62.4;0` 这样的三段。
5. 模块名必须叫 `图像源1` / `圆查找1` / `缺陷检测1` —— 配方
   `Config/recipes/default.json` 的 `vm.outputs` 就是按这三个名字映射的，改名就要改配方。

**批量跑 47 张（2026-10-24 实测：不用改名）**：VM 4.2 的「图像源」只有 `本地图像` / `相机` 两种类型
（`ImageSourceModuleAlgorithmTab.xml` 的枚举），**没有"文件夹"入口**，参数是
`CurrentImagePath`(单个路径) + `AutoPlay` + `Interval` + `InitialSN`。
实测把 `test/` 里的 `metal_nut_bad_000.png` 指进去后，模块标题直接显示 **(1/47)**，
状态栏写「图像源1 自动切换 参数已修改。旧值: False, 新值: True」——
即 `AutoPlay=True` 会**自动枚举同目录下的全部图片**并按序号往下走，文件名带前缀也不影响。
所以批量喂图这一环不需要改命名。

## 6. 本程序侧怎么跑

`appsettings.json` 里 File 相机是单层扫描（`Directory.GetFiles`，不递归），所以按用途选文件夹：

| 用途 | `Camera:ImageFolder` |
|---|---|
| 快速验收（47 张：25 坏 22 好） | `TestImages/MVTec-AD/metal_nut/test` |
| 误报率压测（220 张全好） | `TestImages/MVTec-AD/metal_nut/good` |

`FileFilter` 用默认的 `*.jpg;*.png;*.bmp` 即可（这批全是 PNG）。
配方保持 `engine="Vm"`；改完配置记得 rebuild —— 相对路径是基于 `bin` 的。

## 7. 怎么重新生成

```bash
python scripts/prepare_mvtec_subset.py \
  --zip D:/Temp/mvtec/metal_nut.zip \
  --out D:/Code/VisionStation/TestImages/MVTec-AD \
  --categories metal_nut
```

脚本做四件事：摊平目录、统一转灰度 PNG、关联缺陷掩膜、写 manifest 与两张预览图。
换类别只要改 `--categories`（`screw` 等类别同理）。

## 8. 已核对的数字（2026-10-24）

- 267 张，缺失 0，sha256 不一致 0
- 掩膜关联 25/25
- 缺陷面积 9,177 ~ 60,275 px，中位 23,344 px
- 磁盘占用 57 MB

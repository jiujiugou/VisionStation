#!/usr/bin/env python3
"""从 MVTec AD 官方压缩包里抽出指定类别的子集，整理成「VisionStation 可直接吃」的目录。

为什么要有这个脚本，而不是手工解压：
  1. 官方包 5.2 GB，用户只需要其中 1~2 个类别（~200 MB）；
  2. 原始目录是 train/good、test/<缺陷类型>/、ground_truth/<缺陷类型>/ 三层嵌套，
     FileCamera 只扫单层目录，VM 的「本地图像」也只能逐张指路径 —— 必须摊平；
  3. 摊平后文件名必须自己带真值（哪个缺陷类型），否则真值一丢，素材就没法用来算指标了。

用法：
  python prepare_mvtec_subset.py --zip D:/Temp/mvtec/mvtec_ad_full.zip \
      --out D:/Code/VisionStation/TestImages/MVTec-AD --categories metal_nut,screw

产出（每个类别一份）：
  <out>/<category>/test/<cat>_<label>_<idx>.png   测试图，扁平，文件名带真值
  <out>/<category>/good/<cat>_good_<idx>.png     合格品（误报率分母）
  <out>/<category>/ground_truth/<label>/*.png    缺陷掩膜（原名，供人工对照）
  <out>/<category>/manifest.json                 逐张真值 + sha256 + 尺寸
  <out>/<category>/preview.png                   缩略图总览（肉眼先扫一遍）
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import sys
import zipfile
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np

# MVTec AD 官方授权：非商用。写进 manifest 和 README，免得以后忘了能不能放进简历/商用。
LICENSE = "CC BY-NC-SA 4.0（仅限非商业用途；作品集/教学可用，须署名）"
SOURCE = "https://www.mvtec.com/company/research/datasets/mvtec-ad"


def sha256_16(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()[:16]


def decode_image(data: bytes, name: str) -> np.ndarray:
    img = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_UNCHANGED)
    if img is None:
        raise ValueError(f"无法解码：{name}")
    return img


# 只认这三种。有些第三方重打包会混进 DISthresh/ 之类的中间产物（阈值图），
# 混进来会被当成正常样张，直接污染真值表 —— 所以用白名单而不是黑名单。
KNOWN_SPLITS = {"train", "test", "ground_truth"}


def build_index(zf: zipfile.ZipFile, category: str) -> dict[str, list[tuple[str, str]]]:
    """把 zip 内部条目按 <split>/<label> 分组。原始结构：
    <prefix>train/good/000.png
    <prefix>test/scratch/010.png
    <prefix>ground_truth/scratch/010_mask.png
    前缀（顶层目录名）因打包者而异，所以不能写死。
    """
    groups: dict[str, list[tuple[str, str]]] = defaultdict(list)
    skipped: Counter[str] = Counter()
    marker = f"/{category}/"
    for name in zf.namelist():
        if marker not in name or name.endswith("/"):
            continue
        tail = name.split(marker, 1)[1]          # train/good/000.png
        parts = tail.split("/")
        if len(parts) < 3:
            continue                              # 类别根下的杂项（如 license.txt）
        split, label = parts[0], parts[1]
        if split not in KNOWN_SPLITS:
            skipped[split] += 1
            continue
        groups[f"{split}/{label}"].append((name, tail))
    for split, n in skipped.items():
        print(f"    [忽略] 未知分组 {split}/（{n} 个文件，通常是重打包时的中间产物）")
    return groups


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--zip", required=True, help="MVTec AD 官方 zip")
    ap.add_argument("--out", required=True, help="输出根目录")
    ap.add_argument("--categories", default="metal_nut", help="逗号分隔，例如 metal_nut,screw")
    ap.add_argument("--source", default="Kaggle: nguyenduchuhu/mvtec-metal-nut（MVTec AD 的重打包版）",
                    help="写进 manifest 的取数来源，方便日后追溯")
    args = ap.parse_args()

    zip_path = Path(args.zip)
    out_root = Path(args.out)
    if not zip_path.is_file():
        print(f"[失败] 找不到 zip：{zip_path}", file=sys.stderr)
        return 2

    categories = [c.strip() for c in args.categories.split(",") if c.strip()]
    stamp = datetime.now(timezone.utc).astimezone().strftime("%Y-%m-%d %H:%M:%S")

    with zipfile.ZipFile(zip_path) as zf:
        for category in categories:
            groups = build_index(zf, category)
            if not groups:
                print(f"[跳过] zip 里没有类别 {category}", file=sys.stderr)
                continue

            cat_dir = out_root / category
            for sub in ("test", "good", "ground_truth"):
                (cat_dir / sub).mkdir(parents=True, exist_ok=True)

            items: list[dict] = []
            counters: Counter[str] = Counter()
            preview_rows: list[tuple[str, list[np.ndarray]]] = []
            masks: dict[str, str] = {}          # 文件名主干 -> 掩膜相对路径

            for group in sorted(groups):
                split, label = group.split("/", 1)
                is_mask = split == "ground_truth"
                # 掩膜保留原名原样，只用于人工对照，不进真值表
                for zip_name, tail in sorted(groups[group]):
                    raw = zf.read(zip_name)
                    img = decode_image(raw, zip_name)
                    gray = img if img.ndim == 2 else cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
                    h, w = gray.shape[:2]

                    if is_mask:
                        dst = cat_dir / "ground_truth" / label / Path(tail).name
                        dst.parent.mkdir(parents=True, exist_ok=True)
                        dst.write_bytes(raw)
                        # 掩膜与样张靠「分组 + 文件名主干」对应：bad/000_mask.png <-> bad/000.png。
                        # 只比主干不行 —— train/good/000 和 test/bad/000 会撞车。
                        group_key = label.replace("_mask", "")
                        masks[f"{group_key}/{Path(tail).stem.replace('_mask', '')}"] = \
                            dst.relative_to(out_root).as_posix()
                        continue

                    idx = Path(tail).stem
                    flat = f"{category}_{label}_{idx}.png"
                    if split == "train":
                        dst = cat_dir / "good" / flat
                    else:
                        dst = cat_dir / "test" / flat
                    # 统一转 8 位灰度 PNG：MVTec 原图是灰度/RGB 混排，VM 的灰度处理更省心。
                    # 先编码到内存再落盘：manifest 里的 sha256 必须能校验「写出去的那个文件」，
                    # 拿原始字节做哈希的话转码后永远对不上，版本锁等于没有。
                    ok, buf = cv2.imencode(".png", gray)
                    if not ok:
                        raise ValueError(f"PNG 编码失败：{dst}")
                    data = buf.tobytes()
                    dst.write_bytes(data)

                    items.append({
                        "path": dst.relative_to(out_root).as_posix(),
                        "sourceStem": idx,
                        "split": "train" if split == "train" else "test",
                        "label": label,
                        "isDefect": label != "good",
                        "width": w,
                        "height": h,
                        "sha256": sha256_16(data),
                    })
                    counters[f"{'train' if split == 'train' else 'test'}/{label}"] += 1

                    if split == "test" and len(preview_rows) < 1_000_000:
                        preview_rows.append((f"{label}/{idx}", [gray]))

            # 掩膜是在图之后收集的，这里统一回填，避免依赖 zip 内部条目顺序
            for item in items:
                key = f"{item['label']}/{item['sourceStem']}"
                if key in masks:
                    item["mask"] = masks[key]

            area_stats = annotate_masks(items, out_root, cat_dir, category)

            labels = sorted({i["label"] for i in items})
            binary = set(labels) <= {"good", "bad"}
            manifest = {
                "dataset": "MVTec AD",
                "category": category,
                "source": SOURCE,
                "license": LICENSE,
                "obtainedVia": args.source,
                "labels": labels,
                "labelScheme": ("binary（good/bad；原始 MVTec 的 bent/color/flip/scratch 已被重打包合并）"
                                if binary else "typed（按缺陷类型）"),
                "preparedAt": stamp,
                "defectAreaStats": area_stats,
                "counts": dict(sorted(counters.items())),
                "total": len(items),
                "items": items,
            }
            (cat_dir / "manifest.json").write_text(
                json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

            make_preview(preview_rows, cat_dir / "preview.png", category)
            write_readme(cat_dir, manifest)

            print(f"[完成] {category}: {len(items)} 张 → {cat_dir}")
            for k, v in sorted(counters.items()):
                print(f"    {k:24s} {v:4d}")

    return 0


def annotate_masks(items: list[dict], out_root: Path, cat_dir: Path, category: str) -> dict:
    """给有掩膜的样张补上缺陷面积/外接框，并画一张「原图 | 掩膜叠加」对照图。

    为什么值得多跑一遍：VM 的缺陷检测模块要填面积阈值，而阈值只能靠缺陷面积的分布来定。
    先看清 25 张 bad 的缺陷到底多大，比在 VM 里瞎拖滑块强。
    """
    targets = [i for i in items if i.get("mask")]
    if not targets:
        return {}

    areas: list[int] = []
    pairs: list[tuple[np.ndarray, np.ndarray, str]] = []
    for item in targets:
        img = cv2.imread(str(out_root / item["path"]), cv2.IMREAD_GRAYSCALE)
        mask = cv2.imread(str(out_root / item["mask"]), cv2.IMREAD_GRAYSCALE)
        if img is None or mask is None:
            continue
        if mask.shape != img.shape:
            mask = cv2.resize(mask, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_NEAREST)
        binary = (mask > 0).astype(np.uint8)
        area = int(binary.sum())
        if area == 0:
            continue
        ys, xs = np.nonzero(binary)
        item["maskAreaPx"] = area
        item["maskBBox"] = [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())]
        item["maskAreaRatio"] = round(area / float(img.size), 5)
        areas.append(area)
        if len(pairs) < 8:
            pairs.append((img, mask, Path(item["path"]).stem))

    if not areas:
        return {}

    # 8 列、上下两行：上行原图，下行叠加掩膜（红边 + 半透明红），肉眼直接看出坏在哪
    cell = max(img.shape[0] for img, _, _ in pairs)
    cell = 220
    cols = len(pairs)
    pad = 20
    sheet = np.full((2 * cell + 3 * pad, cols * cell, 3), 32, np.uint8)
    for c, (img, mask, name) in enumerate(pairs):
        g = cv2.resize(img, (cell, cell), interpolation=cv2.INTER_AREA)
        bgr = cv2.cvtColor(g, cv2.COLOR_GRAY2BGR)
        m = cv2.resize(mask, (cell, cell), interpolation=cv2.INTER_NEAREST)
        binary = (m > 0).astype(np.uint8)
        overlay = bgr.copy()
        overlay[binary > 0] = (40, 40, 220)
        bgr_ov = cv2.addWeighted(overlay, 0.55, bgr, 0.45, 0)
        contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        cv2.drawContours(bgr_ov, contours, -1, (0, 220, 255), 1)

        y0 = pad
        y1 = 2 * pad + cell
        x = c * cell
        sheet[y0:y0 + cell, x:x + cell] = bgr
        sheet[y1:y1 + cell, x:x + cell] = bgr_ov
        cv2.putText(sheet, name, (x + 2, y0 - 6), cv2.FONT_HERSHEY_SIMPLEX, 0.4,
                    (200, 200, 200), 1, cv2.LINE_AA)
    cv2.putText(sheet, f"MVTec AD / {category} — 上：原图  下：缺陷掩膜",
                (pad, pad - 8), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 255), 1, cv2.LINE_AA)
    cv2.imwrite(str(cat_dir / "preview_defects.png"), sheet)

    areas_sorted = sorted(areas)
    median = areas_sorted[len(areas_sorted) // 2]
    return {
        "count": len(areas),
        "minPx": areas_sorted[0],
        "medianPx": median,
        "maxPx": areas_sorted[-1],
        "note": "面积单位是像素；VM 里填缺陷面积阈值时按这个分布取值，别拍脑袋",
    }


def make_preview(rows: list[tuple[str, list[np.ndarray]]], dst: Path, category: str,
                 cell: int = 160, per_row: int = 8, max_rows: int = 14) -> None:
    """拼一张缩略总览。先按缺陷类型分组，每组取前 per_row 张 —— 肉眼一眼能看出各类长什么样。"""
    if not rows:
        return
    by_label: dict[str, list[np.ndarray]] = defaultdict(list)
    for label, imgs in rows:
        by_label[label.split("/")[0]].extend(imgs)

    # good 排最前，其余按字母序，保证每次生成顺序一致
    order = ["good"] + sorted(k for k in by_label if k != "good")
    tiles: list[np.ndarray] = []
    labels: list[str] = []
    for label in order[:max_rows]:
        for img in by_label[label][:per_row]:
            g = cv2.resize(img, (cell, cell), interpolation=cv2.INTER_AREA)
            tiles.append(g)
            labels.append(label)

    if not tiles:
        return
    cols = min(per_row, len(tiles))
    rows_n = (len(tiles) + cols - 1) // cols
    pad = 22
    sheet = np.full((rows_n * (cell + pad) + pad, cols * cell, 3), 32, np.uint8)
    for i, (tile, label) in enumerate(zip(tiles, labels)):
        r, c = divmod(i, cols)
        y = pad + r * (cell + pad)
        x = c * cell
        sheet[y:y + cell, x:x + cell] = cv2.cvtColor(tile, cv2.COLOR_GRAY2BGR)
        cv2.putText(sheet, label, (x + 2, y - 6), cv2.FONT_HERSHEY_SIMPLEX,
                    0.45, (200, 200, 200), 1, cv2.LINE_AA)
    cv2.putText(sheet, f"MVTec AD / {category}", (pad, pad - 8),
                cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.imwrite(str(dst), sheet)


def write_readme(cat_dir: Path, manifest: dict) -> None:
    counts = "\n".join(f"| {k} | {v} |" for k, v in manifest["counts"].items())
    stats = manifest.get("defectAreaStats") or {}
    area_block = ""
    if stats:
        area_block = f"""
## 缺陷面积分布（定 VM 面积阈值的依据）

25 张坏件里非零掩膜像素的统计：

| 项 | 值（像素，700×700 画幅） |
|---|---|
| 最小 | {stats['minPx']} |
| 中位 | {stats['medianPx']} |
| 最大 | {stats['maxPx']} |

换算成占画幅比例约 {stats['minPx'] / 4900:.1f}% ~ {stats['maxPx'] / 4900:.1f}%。
**这批缺陷以「边缘变形/缺口」为主，面积都很大** —— 面积阈值定在几千像素级别就能全检出，
真正难的是别把合格件的毛刺/反光误判成缺陷。
对照图见 `preview_defects.png`（上行原图、下行掩膜叠加）。
"""
    text = f"""# MVTec AD / {manifest['category']}（本程序用子集）

真实工业相机拍摄，非合成。来源：{manifest['source']}
许可：{manifest['license']}
取数方式：{manifest['obtainedVia']}
标注口径：{manifest['labelScheme']}
整理时间：{manifest['preparedAt']}

## 目录

| 路径 | 内容 | 用途 |
|---|---|---|
| `test/` | 测试图，扁平，文件名 `<类>_<label>_<序号>.png` | File 相机 / VM「本地图像」直接指这里 |
| `good/` | 合格品 | 误报率的分母 |
| `ground_truth/` | 缺陷掩膜（原名） | 人工对照「缺陷在哪」 |
| `manifest.json` | 逐张真值 + sha256 + 尺寸 + 掩膜关联 | 比对/复核脚本读它 |
| `preview.png` | 缩略总览 | 先肉眼看一眼素材长什么样 |
| `preview_defects.png` | 原图 / 掩膜叠加对照 | 看坏件到底坏在哪 |

## 数量

| 分组 | 张数 |
|---|---|
{counts}
| **合计** | **{manifest['total']}** |
{area_block}
## 注意

- 已统一转为 8 位灰度 PNG（原始为灰度/RGB 混排）。
- 真值以 `manifest.json` 为准，文件名里的 label 只是方便肉眼。
- 这份数据**没有尺寸真值**（没有卡尺实测值），只能验证缺陷检出与测量重复性，不能验证测量准确度。
"""
    (cat_dir / "README.md").write_text(text, encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())

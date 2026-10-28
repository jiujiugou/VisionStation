#!/usr/bin/env python3
"""参数底册门禁：docs/参数与策略.md ↔ 代码 ↔ appsettings.json 三方对账。

三个方向（任一失败整体红，退出码 1）：
  C1 配置键对齐   —— appsettings.json 的每个叶子键必须登记在 §1 表。
  C2 登记的存在性 —— §2 表登记的每个 (文件, 字段) 必须在代码里真实存在；
                    数值型简单字面量还要比对值，防止"文档说 2000 代码改 3000"。
  C3 声明的登记率 —— 扫描范围内每个 const / static readonly 声明，
                    必须登记（§2）或进白名单（§5，带理由）。棘轮式：存量赦免，新增必红。

v1 已知边界（写在 §3 的设计里）：内联字面量（如 ModbusTcpPlc 的 2000）不自动扫，
靠 §3 表人工同步——精确优先于覆盖，噪音大的门禁会被绕过。
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOC = ROOT / "docs" / "参数与策略.md"
SETTINGS = ROOT / "src" / "VisionStation.App" / "appsettings.json"

# 扫描范围（与用户拍板的试点范围一致：产线语义参数集中地）
SCAN_DIRS = [
    "src/VisionStation.Devices",
    "src/VisionStation.Service",
    "src/VisionStation.Watchdog",
    "src/VisionStation.App/Services",
]

# const / static readonly 声明：字段名 + 同行字面量
DECL = re.compile(r"\b(?:const|static readonly)\s+[\w<>\[\]?,.]+\s+(\w+)\s*=\s*([^;]+);")
SIMPLE_INT = re.compile(r"^[\d_]+$")


def parse_tables(text: str):
    """按内容列数分流四类行：常量登记(5列)、配置键(4列+点号键)、白名单(3列)、其余跳过。"""
    consts: dict[tuple[str, str], list[str]] = {}
    config: dict[str, str] = {}
    whitelist: set[tuple[str, str]] = set()

    for line in text.splitlines():
        if not line.strip().startswith("|"):
            continue
        cells = [c.strip() for c in line.split("|")]
        if len(cells) < 5:
            continue
        body = cells[1:-1]
        n = len(body)
        if n == 5 and body[0].startswith("`") and body[0].endswith(".cs`"):
            path = body[0].strip("`")
            field = body[1].strip("`")
            consts[(path, field)] = body
        elif n == 3 and body[0].startswith("`") and body[0].endswith(".cs`"):
            path = body[0].strip("`")
            for name in body[1].split("/"):
                whitelist.add((path, name.strip().strip("`")))
        elif n == 4 and re.fullmatch(r"[A-Za-z]+\.[A-Za-z]+", body[0].strip("`")):
            config[body[0].strip("`")] = body[1]

    return consts, config, whitelist


def flatten_keys(node, prefix=""):
    keys = []
    for k, v in node.items():
        full = f"{prefix}.{k}" if prefix else k
        if isinstance(v, dict):
            keys += flatten_keys(v, full)
        else:
            keys.append(full)
    return keys


def declared_consts():
    """收集扫描范围内所有 (相对路径, 字段名, 字面量文本) 声明。"""
    found = []
    for d in SCAN_DIRS:
        for f in sorted((ROOT / d).rglob("*.cs")):
            rel = f.relative_to(ROOT).as_posix()
            for i, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
                for m in DECL.finditer(line):
                    found.append((rel, m.group(1), m.group(2).strip(), i))
    return found


def main() -> int:
    text = DOC.read_text(encoding="utf-8")
    consts, config, whitelist = parse_tables(text)
    errors: list[str] = []

    # C1：appsettings 每个叶子键必须登记
    settings = json.loads(SETTINGS.read_text(encoding="utf-8-sig"))
    for key in flatten_keys(settings):
        if key not in config:
            errors.append(f"C1 配置键未登记：{key}")

    # C2：登记的 (文件, 字段) 必须存在；简单数值还要比对值
    for (rel, field), body in consts.items():
        f = ROOT / rel
        if not f.exists():
            errors.append(f"C2 登记的文件不存在：{rel}")
            continue
        src = f.read_text(encoding="utf-8")
        if not re.search(rf"\b{re.escape(field)}\b", src):
            errors.append(f"C2 登记的字段在代码中找不到：{rel} · {field}")
            continue
        # 数值比对：声明行是纯数字字面量时，登记值必须含同一个数
        m = re.search(rf"\b{re.escape(field)}\b\s*=\s*([^;]+);", src)
        if m:
            lit = m.group(1).strip()
            if SIMPLE_INT.match(lit):
                value = int(lit.replace("_", ""))
                if str(value) not in body[2]:
                    errors.append(f"C2 值不一致：{rel} · {field} 代码={value} 底册={body[2]!r}")

    # C3：扫描范围内每个声明必须登记或白名单
    for rel, field, literal, lineno in declared_consts():
        if (rel, field) in consts or (rel, field) in whitelist:
            continue
        errors.append(f"C3 未登记的声明：{rel}:{lineno} · {field}（登记进 §2 或带理由进 §5）")

    if errors:
        print(f"参数门禁：{len(errors)} 处不一致")
        for e in errors:
            print("  " + e)
        return 1

    print(
        f"参数门禁 OK：配置键 {len(config)} · 常量登记 {len(consts)} · 白名单 {len(whitelist)} · "
        f"扫描声明 {len(declared_consts())} 处"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

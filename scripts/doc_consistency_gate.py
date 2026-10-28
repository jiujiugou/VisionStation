#!/usr/bin/env python3
"""文档-代码一致性门禁：主表里写的类名/测试名，必须在代码里真实存在。

只查可证伪的部分（docs/功能-实现-验证.md）：
  1. F 行行内代码中的 PascalCase 标识符 -> 在 src/tools/tests 的 .cs 文本中按词边界搜索；
  2. `*Tests` 结尾的标识符（证据列）-> 必须在 tests/ 目录下存在，专治"引用的测试类已删"；
  3. 主表 F 编号 -> 不许重号、不许缺号；
  4. 状态列的语义（✅/◐/✗ 是否属实）不在本脚本能力范围内，仍靠人工审计。

已删除但文档故意留名的标识符走 ALLOWED_MISSING 白名单，必须带理由。

用法:
    python scripts/doc_consistency_gate.py
    python scripts/doc_consistency_gate.py --doc docs/功能-实现-验证.md
"""

import argparse
import os
import re
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

SKIP_DIRS = {"bin", "obj", "artifacts", "publish", "StrykerOutput", ".git", ".vs"}

# 文件扩展名 -> 整个 span 按文件路径处理，不做代码标识符检查
FILE_EXTS = (".cs", ".st", ".py", ".cmd", ".slnx", ".sln", ".xaml", ".json", ".db", ".md")

# 全大写且 <=5 字符的视为缩写（OK/NG/PASS/D106/SN...），不查
TOKEN = re.compile(r"\b[A-Z][A-Za-z0-9]*\b")
F_ROW = re.compile(r"^\|\s*F-(\d+)\s*\|")
F_ID = re.compile(r"F-(\d+)")

# 文档故意记录"已删除"事实的标识符 -> 理由。加条目必须写明出处 F 编号。
ALLOWED_MISSING = {
    "IPushDrivenEngine": "F-10：2026-10-27 已删除，主表记录的是删除事实本身",
    "EngineGuardTests": "F-09：随 ONNX 删除，主表留名以说明该条现为 0 测试",
    "Io": "F-11：2026-10-27 删除的零消费配方字段",
    "IoKind": "F-11：2026-10-27 删除的零消费配方字段",
    "ClosureProbe": "F-58：空转工装已于 2026-10-27 删除，主表记录的是删除事实本身",
}


def load_corpus(rel_dirs):
    """把指定目录下所有 .cs 文本拼成一个大字符串。"""
    parts = []
    for rel in rel_dirs:
        root = os.path.join(REPO, rel)
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for name in filenames:
                if name.endswith(".cs"):
                    with open(os.path.join(dirpath, name),
                              encoding="utf-8", errors="ignore") as f:
                        parts.append(f.read())
    return "\n".join(parts)


def extract_tokens(line):
    """从一行表格里提取待检查的 PascalCase 标识符。"""
    tokens = set()
    for span in re.findall(r"`([^`]+)`", line):
        if ":" in span:                      # 配置键 / 行号 / URL：Storage:ImageFolder 等
            continue
        if any(ext in span for ext in FILE_EXTS):   # 文件路径整体跳过
            continue
        segments = re.split(r"[/\\（(，,；;。 ]", span)   # Of/Has 拆开查
        for seg in segments:
            for tok in TOKEN.findall(seg):
                if tok.upper() == tok and len(tok) <= 5:  # 缩写
                    continue
                tokens.add(tok)
    return tokens


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--doc", default=os.path.join("docs", "功能-实现-验证.md"))
    args = ap.parse_args()

    doc_path = os.path.join(REPO, args.doc) if not os.path.isabs(args.doc) else args.doc
    if not os.path.exists(doc_path):
        print(f"[docgate] 找不到文档: {doc_path}")
        return 2

    with open(doc_path, encoding="utf-8") as f:
        lines = f.read().splitlines()

    src_corpus = load_corpus(["src", "tools"])
    tests_corpus = load_corpus(["tests"])

    # 主表区间（## 2. 到 ## 3.）用于 F 编号覆盖检查
    in_main = False
    main_ids = []
    failures = []
    checked = 0

    for line in lines:
        if line.startswith("## 2."):
            in_main = True
        elif line.startswith("## 3."):
            in_main = False
        if in_main:
            m = F_ROW.match(line)
            if m:
                main_ids.append(int(m.group(1)))

        m = F_ROW.match(line)
        if not m:
            continue
        fid = f"F-{m.group(1)}"

        for tok in sorted(extract_tokens(line)):
            if tok in ALLOWED_MISSING:
                continue
            checked += 1
            corpus = tests_corpus if tok.endswith("Tests") else src_corpus + "\n" + tests_corpus
            if not re.search(rf"\b{re.escape(tok)}\b", corpus):
                failures.append((fid, tok))

    # F 编号覆盖：主表内不许重号、不许缺号。
    # 已删除的 F 编号（行被删、编号永久退役，不补位不重用）——加条目必须写明去处。
    DELETED_FIDS = {
        15: "2026-10-28 删除：与 F-10 是同一行为（「开始检测」只提示不检测），"
            "旧行实现描述（判 IPushDrivenEngine）随接口删除已失效，口径以 F-10 为准",
    }
    dupes = sorted({i for i in main_ids if main_ids.count(i) > 1})
    if dupes:
        failures.append(("主表", f"F 编号重复: " + ", ".join(f"F-{i}" for i in dupes)))
    missing = [i for i in range(1, 72) if i not in main_ids and i not in DELETED_FIDS]
    if missing:
        failures.append(("主表", "F 编号缺号: " + ", ".join(f"F-{i}" for i in missing)))

    print(f"[docgate] 检查标识符 {checked} 个（另白名单 {len(ALLOWED_MISSING)} 个），主表 {len(main_ids)} 条")
    if failures:
        print(f"[docgate] 阻断：{len(failures)} 处文档-代码不一致")
        for fid, tok in failures:
            reason = ALLOWED_MISSING.get(tok)
            print(f"           {fid}  `{tok}` 代码中不存在"
                  + (f"  <- {reason}" if reason else ""))
        return 1

    print("[docgate] 一致性检查通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""分析器告警棘轮：告警只允许减少，不允许增加。

解析 dotnet build 日志，按 "文件+行+规则" 去重后统计。
与基线对比：
  - 减少 -> 自动下调基线（降级是好事，不需要人工确认）
  - 增加 -> 退出码 1，列出新增的规则

用法:
    python scripts/analyzer_gate.py --log artifacts/ci/build.log
    python scripts/analyzer_gate.py --log artifacts/ci/build.log --update
"""

import argparse
import json
import os
import re
import sys

PAT = re.compile(r": warning ([A-Za-z]+\d+):")

# 需要优先清零的规则，按"正确性危害"排序，不是按条数
GATED = {
    "S3881": "IDisposable 模式实现不完整",
    "CA1816": "Dispose 未调用 GC.SuppressFinalize",
    "S2486": "catch 块吞掉异常",
    "S108": "空代码块",
    "S2696": "静态字段在实例方法中被赋值",
    "CA1305": "格式化未指定 IFormatProvider",
    "CA1869": "JsonSerializerOptions 每次重建",
}


def parse(path):
    seen = set()
    with open(path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            if ": warning " in line:
                seen.add(line.strip())
    counts = {}
    for line in seen:
        m = PAT.search(line)
        if m:
            rid = m.group(1)
            counts[rid] = counts.get(rid, 0) + 1
    return len(seen), counts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", required=True)
    ap.add_argument("--baseline", default="artifacts/ci/warn-baseline.json")
    ap.add_argument("--update", action="store_true", help="强制重设基线")
    args = ap.parse_args()

    if not os.path.exists(args.log):
        print(f"[analyzer] 找不到构建日志: {args.log}")
        return 2

    total, counts = parse(args.log)
    gated = {k: v for k, v in counts.items() if k in GATED}

    print(f"[analyzer] 告警 {total} 条（去重后）")
    if counts:
        top = sorted(counts.items(), key=lambda kv: -kv[1])[:6]
        print("[analyzer] 分布: " + ", ".join(f"{k}×{v}" for k, v in top))
    if gated:
        print("[analyzer] 需优先清零:")
        for rid, n in sorted(gated.items(), key=lambda kv: -kv[1]):
            print(f"           {rid:<8} {n:>3}  {GATED[rid]}")

    base = None
    if os.path.exists(args.baseline) and not args.update:
        with open(args.baseline, encoding="utf-8") as f:
            base = json.load(f)

    if base is None:
        os.makedirs(os.path.dirname(args.baseline) or ".", exist_ok=True)
        with open(args.baseline, "w", encoding="utf-8") as f:
            json.dump({"total": total, "by_rule": counts}, f,
                      ensure_ascii=False, indent=2, sort_keys=True)
        print(f"[analyzer] 基线已建立: {total} 条 -> {args.baseline}")
        return 0

    delta = total - base["total"]
    if total <= base["total"]:
        if total < base["total"]:
            with open(args.baseline, "w", encoding="utf-8") as f:
                json.dump({"total": total, "by_rule": counts}, f,
                          ensure_ascii=False, indent=2, sort_keys=True)
            print(f"[analyzer] 基线下调 {base['total']} -> {total}（好）")
        else:
            print(f"[analyzer] 与基线持平: {total} 条")
        return 0

    print(f"[analyzer] 阻断：比基线多 {delta} 条（{base['total']} -> {total}）")
    for rid, n in sorted(counts.items(), key=lambda kv: -kv[1]):
        added = n - base["by_rule"].get(rid, 0)
        if added > 0:
            print(f"           +{added}  {rid}")
    return 1


if __name__ == "__main__":
    sys.exit(main())

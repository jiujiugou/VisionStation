#!/usr/bin/env python3
"""金标准抽取器 —— 把项目里"真实发生过的故障痕迹"机械抽成 incidents.json。

为什么金标准必须来自日志：
  用"我知道哪里有问题"当答案，等于让出题人兼考生。日志是时间戳客观记录，
  与本次分析相互独立，可以算召回率。

纪律：只机械抽取，不解读。抽取完封存，阶段1 分析期间不打开。
"""
import json
import os
import re
from collections import Counter

ROOT = r"D:/Code/VisionStation"
OUT = r"C:/Users/jiuji/WorkBuddy/2026-10-19-10-37-21/visionstation-audit/ground-truth/incidents.json"

PAT = re.compile(
    r"(Exception|错误码|失败|异常|ERROR|FATAL|WARN|崩溃|Unhandled|Exception thrown|占用|超时|timeout|denied)",
    re.I)
EXC = re.compile(r"([A-Z][A-Za-z]*(?:Exception|Error))")
CODE = re.compile(r"0x[0-9A-Fa-f]{8}|错误码[:：]\s*(-?\d+)")


def scan(paths):
    found = []
    for p in paths:
        if not os.path.exists(p):
            continue
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                for i, line in enumerate(f, 1):
                    if not PAT.search(line):
                        continue
                    if len(line.strip()) < 12:
                        continue
                    exc = EXC.search(line)
                    code = CODE.search(line)
                    found.append({
                        "source": os.path.relpath(p, ROOT).replace("\\", "/"),
                        "line": i,
                        "exc": exc.group(1) if exc else None,
                        "code": code.group(0) if code else None,
                        "text": line.strip()[:220],
                        "fingerprint": (exc.group(1) if exc else "") + "|" + (code.group(0) if code else "") + "|" + line.strip()[:60],
                    })
        except OSError:
            continue
    return found


def main():
    logs = [os.path.join(ROOT, "crash.log"), os.path.join(ROOT, "diag.log")]
    logdir = os.path.join(ROOT, "Logs")
    if os.path.isdir(logdir):
        logs += [os.path.join(logdir, f) for f in sorted(os.listdir(logdir)) if f.endswith(".log")]
    hits = scan(logs)
    groups = {}
    for h in hits:
        groups.setdefault(h["fingerprint"], []).append(h)
    incidents = []
    for k, v in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        incidents.append({
            "gid": f"GT-{len(incidents)+1:03d}",
            "occurrences": len(v),
            "sources": sorted({x["source"] for x in v}),
            "exc": v[0]["exc"],
            "code": v[0]["code"],
            "sample": v[0]["text"],
        })
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump({"log_files": [os.path.relpath(p, ROOT).replace("\\", "/") for p in logs],
                   "raw_hits": len(hits),
                   "incident_groups": len(incidents),
                   "incidents": incidents}, f, ensure_ascii=False, indent=1)
    # 只打印计数，不打印内容 —— 保持盲测
    print(f"扫描日志 {len(logs)} 个，命中 {len(hits)} 行，归并为 {len(incidents)} 组")
    print(f"来源文件: {[os.path.relpath(p, ROOT).replace(chr(92), '/') for p in logs]}")
    print("内容已封存至 ground-truth/incidents.json（阶段1 结束前不打开）")


if __name__ == "__main__":
    main()

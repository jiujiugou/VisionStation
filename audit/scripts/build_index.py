#!/usr/bin/env python3
"""阶段0 机械索引器 —— 只抽结构事实，不做任何故障判断。

设计原则：模块"数量"必须由脚本定，不能由模型自由裁量，否则门禁1 的自证问题会从
索引层就复发。本脚本输出 raw_index.json；模型只能在这份骨架上补语义字段（输入/
输出/状态/外部依赖/资源），不能增删单元 —— 增删必须在 index.json 里显式记录
deviation 与理由。
"""
import json
import os
import re

ROOT = r"D:/Code/VisionStation"
SCOPE = [
    "src/VisionStation.Core",
    "src/VisionStation.Devices",
    "src/VisionStation.Storage",
    "src/VisionStation.Service",
]

TYPE_RE = re.compile(r"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(class|record|interface|struct|enum)\s+(\w+)")
USING_RE = re.compile(r"^using\s+([\w\.]+)\s*;")
METHOD_RE = re.compile(r"^\s*(public|private|protected|internal)\s+(?:static\s+|async\s+|override\s+|virtual\s+|new\s+|unsafe\s+|extern\s+)*([\w<>\[\],\.\?]+)\s+(\w+)\s*\(")
FIELD_RE = re.compile(r"^\s*(?:private|protected|internal|public)\s+(?:static\s+|readonly\s+|volatile\s+)*(?!class|record|interface|struct|enum)([\w<>\[\],\.\?]+)\s+(\w+)\s*(?:=|;)")
MARKERS = {
    "IDisposable": r"IDisposable",
    "IAsyncDisposable": r"IAsyncDisposable",
    "event": r"\bevent\s+\w+",
    "async": r"\basync\b",
    "Task": r"\bTask\b",
    "lock": r"\block\s*\(",
    "SemaphoreSlim": r"SemaphoreSlim",
    "Interlocked": r"Interlocked\.",
    "CancellationToken": r"CancellationToken",
    "Dispatcher": r"Dispatcher\.",
    "catch": r"\bcatch\b",
    "catch_empty": r"catch\s*(\([^)]*\))?\s*\{\s*\}",
    "throw": r"\bthrow\b",
    "finalizer": r"~\w+\s*\(",
    "FileIO": r"File\.(Write|Read|Delete|Move|Copy)",
    "DbTask": r"(SaveChangesAsync?|ExecuteSql|FromSql)",
    "PInvoke": r"DllImport|Marshal\.",
    "timer": r"(DispatcherTimer|System\.Timers|Timer\s*\()",
    "static_state": r"static\s+(readonly\s+)?[\w<>\[\],\.]+\s+\w+\s*(=|;)",
}

files = []
for sc in SCOPE:
    for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, sc)):
        dirnames[:] = [d for d in dirnames if d not in ("obj", "bin")]
        for fn in sorted(filenames):
            if not fn.endswith(".cs"):
                continue
            p = os.path.join(dirpath, fn)
            src = open(p, encoding="utf-8-sig").read()
            lines = src.splitlines()
            rel = os.path.relpath(p, ROOT).replace("\\", "/")
            types = []
            cur = None
            for ln in lines:
                m = TYPE_RE.match(ln)
                if m:
                    cur = {"kind": m.group(1), "name": m.group(2), "bases": "", "methods": [], "fields": []}
                    bm = re.search(r":\s*([\w<>,\.\s]+)$", ln.strip())
                    if bm:
                        cur["bases"] = bm.group(1).strip()
                    types.append(cur)
                    continue
                mm = METHOD_RE.match(ln)
                if mm and cur is not None:
                    name = mm.group(3)
                    if name not in ("if", "for", "while", "switch", "catch", "using", "lock", "return"):
                        cur["methods"].append(f'{mm.group(1)} {mm.group(2)} {name}'.strip())
                    continue
                fm = FIELD_RE.match(ln)
                if fm and cur is not None and fm.group(2) not in ("get", "set"):
                    cur["fields"].append(f"{fm.group(1)} {fm.group(2)}")
            markers = sorted(k for k, pat in MARKERS.items() if re.search(pat, src))
            uses = [USING_RE.match(l).group(1) for l in lines[:40] if USING_RE.match(l)]
            entry = {
                "path": rel,
                "loc": len(lines),
                "namespace": next((l.split()[1].rstrip(";") for l in lines if l.startswith("namespace")), ""),
                "usings": uses,
                "types": types,
                "markers": markers,
            }
            # 单元候选规则（写死，不由模型裁量）
            units = []
            for t in types:
                if t["kind"] != "class":
                    continue
                real_methods = [m for m in t["methods"] if not m.endswith(("get", "set"))]
                if real_methods:
                    units.append(t["name"])
            entry["unit_candidates"] = units
            files.append(entry)

total_loc = sum(f["loc"] for f in files)
all_units = [(u, f["path"]) for f in files for u in f["unit_candidates"]]
out = {
    "scope": SCOPE,
    "file_count": len(files),
    "total_loc": total_loc,
    "unit_rule": "class 且含至少一个非属性成员方法；record/interface/struct/enum/纯DTO 不计",
    "unit_count": len(all_units),
    "units": [{"unit": u, "path": p} for u, p in all_units],
    "files": files,
}
os.makedirs(os.path.dirname(r"C:/Users/jiuji/WorkBuddy/2026-10-19-10-37-21/visionstation-audit/artifacts/raw_index.json"), exist_ok=True)
with open(r"C:/Users/jiuji/WorkBuddy/2026-10-19-10-37-21/visionstation-audit/artifacts/raw_index.json", "w", encoding="utf-8") as fh:
    json.dump(out, fh, ensure_ascii=False, indent=1)

print(f"文件 {len(files)} 个 / {total_loc} 行")
print(f"单元候选 {len(all_units)} 个：")
for u, p in all_units:
    print(f"  {u:32s} {p}")

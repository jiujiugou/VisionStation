# -*- coding: utf-8 -*-
"""
运维模块重做前置统计：只做机械计数，不含任何主观判断。
输出 artifacts/metrics.json（机器用）与 artifacts/规模统计.md（人看）。
"""
import json
import os
import re
import sys
from collections import Counter, defaultdict

ROOT = r"D:/Code/VisionStation"
SRC = os.path.join(ROOT, "src/VisionStation.Service")
AUDIT = r"C:/Users/jiuji/WorkBuddy/2026-10-19-10-37-21/visionstation-audit"
ART = os.path.join(AUDIT, "artifacts")

# 被运维模块直接压着的下游（只统计引用面，不计入规模）
DOWNSTREAM = {
    "U17 InspectionRepository": "src/VisionStation.Storage/InspectionRepository.cs",
    "U10 DeviceManager": "src/VisionStation.Devices/DeviceManager.cs",
    "U13 ImageStoreService": "src/VisionStation.Storage/ImageStoreService.cs",
}

# ---------- 机械模式表（全部是字符串计数，不含推理） ----------
PATTERNS = {
    "io_file":      r"\b(File|Directory|FileStream|StreamWriter|StreamReader|DirectoryInfo|FileInfo)\s*\.",
    "io_path":      r"\bPath\s*\.",
    "concurrency":  r"\b(lock|SemaphoreSlim|Semaphore|Interlocked|Monitor|Mutex|Thread|ThreadPool|Task\.Run|Parallel)\b",
    "async_void":   r"\basync\s+void\b",
    "timer":        r"\b(Timer|ElapsedEventArgs|Stopwatch|CancellationToken)\b",
    "clock":        r"\b(DateTime\.(Now|UtcNow|Today)|Environment\.TickCount)\b",
    "try":          r"\btry\b",
    "catch_any":    r"catch\s*(\(\s*(Exception|System\.Exception)\s+\w*\s*\))?\s*\{",
    "throw":        r"\bthrow\b",
    "branch":       r"\b(if|while|switch|case)\b|\?[^;]*:|&&|\|\|",
    "compare":      r"[^=!<>]=[=]|!=|<=|>=|<|>",
    "public_method": r"^\s*public\s+(?:async\s+)?[\w<>\[\],\.\?]+\s+\w+\s*\(",
    "private_method": r"^\s*(?:private|internal|protected)\s+(?:static\s+|async\s+|virtual\s+)*[\w<>\[\],\.\?]+\s+\w+\s*\(",
    "type_decl":    r"^\s*(?:public|internal|private|sealed|abstract|static|partial|\s)*\b(class|record|interface|struct|enum)\s+\w+",
}
COMPILED = {k: re.compile(v, re.M) for k, v in PATTERNS.items()}


def classify_line(line: str) -> str:
    s = line.strip()
    if not s:
        return "blank"
    if s.startswith("//") or s.startswith("///") or s.startswith("/*") or s.startswith("*"):
        return "comment"
    return "code"


def analyze_file(path: str) -> dict:
    with open(path, encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    lines = text.splitlines()
    kinds = Counter(classify_line(l) for l in lines)
    res = {
        "path": os.path.relpath(path, ROOT).replace("\\", "/"),
        "loc_total": len(lines),
        "loc_blank": kinds["blank"],
        "loc_comment": kinds["comment"],
        "loc_code": kinds["code"],
    }
    for name, rx in COMPILED.items():
        res[name] = len(rx.findall(text))
    return res


def main() -> int:
    files = []
    for dirpath, _, names in os.walk(SRC):
        if os.sep + "obj" in dirpath or os.sep + "bin" in dirpath:
            continue
        for n in names:
            if n.endswith(".cs"):
                files.append(os.path.join(dirpath, n))
    files.sort()

    per_file = [analyze_file(f) for f in files]
    totals = defaultdict(int)
    for f in per_file:
        for k, v in f.items():
            if k != "path":
                totals[k] += v

    # 下游文件只统计"引用面"，不并入规模
    downstream = []
    for name, rel in DOWNSTREAM.items():
        p = os.path.join(ROOT, rel)
        if os.path.exists(p):
            a = analyze_file(p)
            downstream.append({"unit": name, "loc_code": a["loc_code"],
                               "concurrency": a["concurrency"], "io_file": a["io_file"]})

    # ---------- 上轮账本（从上轮产物直接读，不做二次解释） ----------
    prev = {"error": None}
    try:
        fc = json.load(open(os.path.join(ART, "FailureCatalog.json"), encoding="utf-8"))
        rc = json.load(open(os.path.join(ART, "RequirementCatalog.json"), encoding="utf-8"))
        vr = json.load(open(os.path.join(ART, "VerificationReport.json"), encoding="utf-8"))
        entries = fc["entries"]
        found = [e for e in entries if e.get("status") == "FOUND"]
        na = [e for e in entries if e.get("status") == "N/A"]
        # 同一 code_ref 文件:行 → 视为同一根因（机械去重，不做语义合并）
        root = defaultdict(list)
        for e in found:
            ref = e.get("code_ref", "")
            key = ref.split("@")[0]
            root[key].append(e["id"])
        prev = {
            "grid_entries": len(entries),
            "found": len(found),
            "na": len(na),
            "by_category": dict(Counter(e.get("category") for e in found)),
            "by_severity": dict(Counter(e.get("severity") for e in found)),
            "by_target": dict(Counter(e.get("target") for e in found)),
            "by_reachability": dict(Counter(e.get("reachability") for e in found)),
            "distinct_code_refs": len(root),
            "code_ref_dup_top": sorted(((k, len(v)) for k, v in root.items()), key=lambda x: -x[1])[:5],
            "req_total": len(rc.get("entries", [])),
            "req_by_method": dict(Counter(r.get("verification_method") for r in rc.get("entries", []))),
            "ver_by_status": dict(Counter(v.get("status") for v in vr.get("results", []))),
            "ver_total": len(vr.get("results", [])),
            "ver_executable": len([v for v in vr.get("results", [])
                                   if v.get("status") in ("PASS", "FAIL")
                                   and str(v.get("evidence", "")).strip()]),
            "artifact_bytes": sum(os.path.getsize(os.path.join(ART, f))
                                  for f in ("FailureCatalog.json", "RequirementCatalog.json",
                                            "VerificationReport.json") if os.path.exists(os.path.join(ART, f))),
            "evidence_files": len([f for f in os.listdir(os.path.join(ART, "evidence"))]) if os.path.isdir(os.path.join(ART, "evidence")) else 0,
        }
    except Exception as exc:  # noqa: BLE001
        prev = {"error": repr(exc)}

    # ---------- 网格推算 ----------
    units = len(per_file)
    behavior_units = 2  # HealthChecker / HousekeepingService（另两个为数据载体与接口）
    interactions = 5
    cells = (behavior_units + interactions) * 9

    doc = {
        "module": "VisionStation.Service (运维模块：自检 + 管家)",
        "root": ROOT,
        "files": per_file,
        "totals": dict(totals),
        "downstream_refs": downstream,
        "grid": {"behavior_units": behavior_units, "interactions": interactions,
                 "categories_per_object": 9, "cells": cells},
        "previous_round": prev,
    }
    os.makedirs(ART, exist_ok=True)
    with open(os.path.join(ART, "metrics.json"), "w", encoding="utf-8") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=1)

    # ---------- 控制台摘要 ----------
    print("files", len(per_file), "| code", totals["loc_code"], "| comment", totals["loc_comment"], "| blank", totals["loc_blank"])
    for f in per_file:
        print(f"  {f['loc_total']:5d} tot {f['loc_code']:5d} code  {f['path']}")
    print("branch", totals["branch"], "| compare", totals["compare"], "| try", totals["try"], "| catch_any", totals["catch_any"])
    print("io_file", totals["io_file"], "| concurrency", totals["concurrency"], "| timer", totals["timer"], "| clock", totals["clock"])
    print("grid cells", cells)
    if prev.get("error"):
        print("prev round: ERROR", prev["error"])
    else:
        print("prev: grid", prev["grid_entries"], "found", prev["found"], "na", prev["na"],
              "| distinct code_ref", prev["distinct_code_refs"],
              "| R", prev["req_total"], "| V", prev["ver_total"], prev["ver_by_status"])
        print("prev by severity", prev["by_severity"], "| by target", prev["by_target"])
        print("prev by category", prev["by_category"])
        print("prev top dup refs", prev["code_ref_dup_top"])
        print("prev by method", prev["req_by_method"])
    return 0


if __name__ == "__main__":
    sys.exit(main())

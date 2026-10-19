#!/usr/bin/env python3
"""门禁检查器 —— 三个阶段的机械门槛，全部由脚本判，不接受模型口头完成。

用法:
    python scripts/gate_check.py                 # 全部三个门禁
    python scripts/gate_check.py --gate 1        # 只检查门禁1

退出码: 0 = 全部通过; 1 = 有阻断项; 2 = 产物缺失/结构非法

门禁1 覆盖对账:  (单元+交互) × 9 类失效，每格必须有 F 条目 / 带理由的 N/A / 类级豁免
        + 根因主键唯一: 同一 path:line 只许一条 FOUND，重复登记 = 重复计数
        + 分级: THEORETICAL 必须 OBSERVATION（不进主清单，不计密度）
        + 密度天花板: MAIN FOUND / KLOC 超线即判定口径失控
门禁2 约束合法:  provenance 四值合法; CODE_AS_IS 不得作为 PASS 依据; 每条约束可判定
        一切验证手段（含 CODE_REVIEW）都必须给可复现 command
门禁3 验证收口:  每条 R 至少 1 个 V; V 状态四态; evidence 必须存在; 未验证必须留痕
"""
import argparse
import json
import os
import sys

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ART = os.path.join(BASE, "artifacts")
ROOT = r"D:/Code/VisionStation"

PROVENANCE = {"PRODUCT_REQ", "ARCH_RULE", "CODE_AS_IS", "ASSUMPTION"}
CONTROL = {"PREVENT", "DETECT", "RECOVER", "ACCEPT"}
VSTATUS = {"PASS", "FAIL", "UNVERIFIED", "NOT_RUN"}
REACH = {"REACHABLE", "CONSTRUCTIBLE", "THEORETICAL"}

# 验证手段封闭枚举 —— 四个硬标准 + 两个补充 + 人工审查兜底。
# 不在枚举内 = 没有验证方法，不许进入阶段3。
VERIFY_METHODS = {
    "UNIT_TEST", "CONCURRENCY_TEST", "STRESS_TEST", "MUTATION_TEST",
    "FAULT_INJECTION", "RECOVERY_DRILL", "CODE_REVIEW",
}
# 可执行类必须给出可复现命令；CODE_REVIEW 只需指出审查位置
EXEC_METHODS = VERIFY_METHODS - {"CODE_REVIEW"}
# 只有这四类算"工具验收的硬标准"，CODE_REVIEW 与故障注入/恢复演练要额外理由
HARD_STANDARDS = {"UNIT_TEST", "CONCURRENCY_TEST", "STRESS_TEST", "MUTATION_TEST"}

# ---- 计数口径约束（2026-10-19 加，针对"35 条里有 10 条是重复的"）----
# 密度天花板：MAIN 级 FOUND 条数 / 千行有效代码。工程经验值在个位到十几/KLOC，
# 超过此线说明登记口径把同一根因拆成了多条，不是代码特别烂。阈值待实测校准，
# 改动必须在此处留下日期与理由。
RATIO_WARN = 30.0     # >30/KLOC 警告：复核是否重复登记
RATIO_BLOCK = 60.0    # >60/KLOC 阻断：口径已失控，退回重做阶段1
MIN_LOC_FOR_RATIO = 200  # 切片小于 200 行时密度无统计意义，不做判定

problems = []   # (gate, severity, msg)
BLOCK, WARN = "BLOCK", "WARN"


def load(name, required=True):
    p = os.path.join(ART, name)
    if not os.path.exists(p):
        if required:
            problems.append((0, BLOCK, f"产物缺失: {name}"))
        return None
    with open(p, encoding="utf-8") as f:
        try:
            return json.load(f)
        except json.JSONDecodeError as e:
            problems.append((0, BLOCK, f"{name} JSON 非法: {e}"))
            return None


def norm_target(t):
    """'U08 HikCamera' / 'U08' / 'E06' 统一成 U08 / E06"""
    return str(t).strip().split(" ")[0].split("(")[0]


def check_code_ref(ref):
    """code_ref 形如 src/a.cs:123 或 src/a.cs:123@Symbol —— 必须真实存在"""
    if not ref:
        return "缺少 code_ref"
    if ":" not in ref:
        return f"code_ref 格式非法（应为 path:line）: {ref}"
    path, rest = ref.split(":", 1)
    line_s, _, symbol = rest.partition("@")
    full = os.path.join(ROOT, path.replace("/", os.sep))
    if not os.path.exists(full):
        return f"code_ref 指向的文件不存在: {path}"
    try:
        n = int(line_s)
    except ValueError:
        return f"code_ref 行号非法: {ref}"
    with open(full, encoding="utf-8-sig", errors="replace") as f:
        lines = f.readlines()
    if n < 1 or n > len(lines):
        return f"code_ref 行号越界: {path} 共{len(lines)}行，引用第{n}行"
    if symbol and symbol not in "".join(lines):
        return f"code_ref 符号在文件中不存在: {symbol} @ {path}"
    return None


def file_loc(path):
    """有效代码行（非空、非注释）。密度判定需要分母，不许凭感觉说'问题很多'。"""
    full = os.path.join(ROOT, path.replace("/", os.sep))
    if not os.path.exists(full):
        return 0
    n = 0
    with open(full, encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            s = line.strip()
            if s and not s.startswith(("//", "/*", "*", "#")):
                n += 1
    return n


def gate1(idx, fcat, only=None):
    units = [u["id"] for u in idx["units"]]
    edges = [e["id"] for e in idx["interactions"]]
    targets = units + edges
    if only:
        targets = [t for t in targets if t in set(only)]
    cats = idx["failure_categories"]
    grid = {(t, c) for t in targets for c in cats}
    total = len(grid)
    if fcat is None:
        problems.append((1, BLOCK, f"门禁1：FailureCatalog 缺失，{total} 格全部未覆盖"))
        return
    entries = fcat.get("entries", [])
    covered = set()
    ref_owner = {}          # "path:line" -> [条目 id]，用于根因唯一性
    main_ids, obs_ids, fixed_ids = [], [], []

    for e in entries:
        eid = e.get("id")
        t = norm_target(e.get("target", ""))
        c = e.get("category", "")
        st = e.get("status")
        if st not in ("FOUND", "N/A"):
            problems.append((1, BLOCK, f"{eid}: status 必须是 FOUND 或 N/A"))
            continue
        if t not in targets:
            problems.append((1, BLOCK, f"{eid}: target 不在索引中: {e.get('target')}"))
            continue
        if c not in cats:
            problems.append((1, BLOCK, f"{eid}: category 非法: {c}"))
            continue
        # 一格一条；同根因的其他落格通过 instances 声明，不再复制成新条目。
        # 注意：已修复条目（FIXED）仍然证明「这一格检查过」，必须计入覆盖率；
        # 它只是不计入密度、不进「必须被约束处理」的清单。早先版本把两者混在一起，
        # 结果修完一个缺陷反而让覆盖率掉下来——那是判定缺陷，不是产物缺陷。
        is_fixed = str(e.get("disposition", "")).startswith("FIXED")
        keys = [(t, c)]
        for i in e.get("instances", []) or []:
            keys.append((norm_target(i.get("target", "")), i.get("category", "")))
        for key in keys:
            if key in covered:
                problems.append((1, WARN, f"{eid}: 重复覆盖 {key[0]}/{key[1]}"))
            covered.add(key)

        if st == "FOUND":
            tier = e.get("tier", "MAIN")
            if tier not in ("MAIN", "OBSERVATION"):
                problems.append((1, BLOCK, f"{eid}: tier 必须是 MAIN 或 OBSERVATION，实际 {tier}"))
                tier = "MAIN"
            if e.get("reachability") == "THEORETICAL" and tier == "MAIN":
                problems.append((1, BLOCK,
                                 f"{eid}: reachability=THEORETICAL 不得进 MAIN，必须 tier=OBSERVATION（理论项不计入问题总数）"))
            if tier != "MAIN":
                obs_ids.append(eid)
            elif is_fixed:
                fixed_ids.append(eid)
            else:
                main_ids.append(eid)

            ref = e.get("code_ref")
            err = check_code_ref(ref)
            if err:
                problems.append((1, BLOCK, f"{eid}: {err}"))
            elif not str(e.get("disposition", "")).startswith("FIXED"):
                p, _, rest = ref.partition(":")
                ref_owner.setdefault(f"{p.strip()}:{rest.partition('@')[0].strip()}", []).append(eid)

            if e.get("reachability") not in REACH:
                problems.append((1, BLOCK, f"{eid}: reachability 缺失/非法"))
            if not e.get("trigger") or not e.get("impact"):
                problems.append((1, BLOCK, f"{eid}: 缺 trigger 或 impact"))
            if e.get("danger_level") not in ("S1", "S2", "S3"):
                if e.get("severity") in ("H", "M", "L"):
                    problems.append((1, WARN, f"{eid}: 使用旧字段 severity，请迁移为 danger_level(S1/S2/S3)"))
                else:
                    problems.append((1, BLOCK, f"{eid}: 缺 danger_level（S1/S2/S3）"))
        else:
            if len(str(e.get("na_reason", "")).strip()) < 8:
                problems.append((1, BLOCK, f"{eid}: N/A 但无实质理由"))

    # ---- 根因唯一性：同一行代码被登记两次 = 把一个问题数成两个 ----
    dup_groups = {k: v for k, v in ref_owner.items() if len(v) > 1}
    if dup_groups:
        shown = list(dup_groups.items())[:4]
        detail = "; ".join(f"{k} ← {', '.join(v)}" for k, v in shown)
        problems.append((1, BLOCK,
                         f"根因重复登记 {len(dup_groups)} 组：{detail}（同一 path:line 只许一条 FOUND，其余并入 instances[]）"))

    # ---- 类级豁免：诚实申报"这一类不适用"的成本必须低于编造一条故障 ----
    exempt = set()
    for ex in fcat.get("category_exemptions", []) or []:
        c = ex.get("category")
        reason = str(ex.get("reason", "")).strip()
        applies = ex.get("applies_to") or []
        if c not in cats:
            problems.append((1, BLOCK, f"豁免条目 category 非法: {c}"))
            continue
        if len(reason) < 20:
            problems.append((1, BLOCK, f"豁免 {c}: 理由不足 20 字符（须说明为何整类不适用）"))
        if not applies:
            problems.append((1, BLOCK, f"豁免 {c}: applies_to 为空（写 ALL 或列目标）"))
            continue
        for x in applies:
            for tt in (targets if x == "ALL" else [norm_target(x)]):
                exempt.add((tt, c))

    missing = grid - covered - exempt
    print(f"  门禁1 覆盖对账: {len(covered)}/{total} 格已覆盖，类级豁免 {len(exempt & grid)} 格，缺 {len(missing)} 格")
    by_t = {}
    for t, c in sorted(missing):
        by_t.setdefault(t, []).append(c)
    for t, cs in list(by_t.items())[:8]:
        print(f"    缺 {t}: {'/'.join(cs)}")
    if len(by_t) > 8:
        print(f"    其余 {len(by_t) - 8} 个目标同样有缺口")
    if missing:
        problems.append((1, BLOCK, f"门禁1：{len(missing)} 格未覆盖（无 F 条目、无 N/A 理由、无类级豁免）"))

    print(f"  门禁1 根因唯一: {len(ref_owner)} 个 path:line 主键，重复 {len(dup_groups)} 组"
          f"（开放 MAIN {len(main_ids)} 条 / 已修复 {len(fixed_ids)} 条 / 观察 {len(obs_ids)} 条）")

    # ---- 密度天花板：没有分母，任何条数都自洽 ----
    loc = 0
    seen_paths = set()
    for u in idx["units"]:
        if u["id"] in set(targets) and u.get("path") and u["path"] not in seen_paths:
            seen_paths.add(u["path"])
            loc += file_loc(u["path"])
    if loc >= MIN_LOC_FOR_RATIO:
        ratio = len(main_ids) / (loc / 1000.0)
        print(f"  门禁1 密度: MAIN {len(main_ids)} 条 / {loc} 行 = {ratio:.1f} 条/KLOC"
              f"（警戒 {RATIO_WARN:.0f}，上限 {RATIO_BLOCK:.0f}）| OBSERVATION {len(obs_ids)} 条不计入")
        if ratio > RATIO_BLOCK:
            problems.append((1, BLOCK,
                             f"门禁1 密度失控：{ratio:.1f} 条/KLOC 超过 {RATIO_BLOCK:.0f}，"
                             f"判定为重复计数而非真实缺陷密度，退回阶段1按 path:line 归并"))
        elif ratio > RATIO_WARN:
            problems.append((1, WARN, f"门禁1 密度偏高：{ratio:.1f} 条/KLOC，复核是否同一根因多次落格"))
    else:
        print(f"  门禁1 密度: 分母 {loc} 行 < {MIN_LOC_FOR_RATIO}，样本过小不作判定"
              f"（MAIN {len(main_ids)} / OBSERVATION {len(obs_ids)}）")


def gate2(fcat, rcat):
    if rcat is None:
        problems.append((2, BLOCK, "门禁2：RequirementCatalog 缺失"))
        return
    reqs = rcat.get("entries", [])
    # R.from 只能指向真实存在的 FOUND 条目（含已修复的历史条目）
    all_found = {e.get("id") for e in (fcat or {}).get("entries", []) if e.get("status") == "FOUND"}
    # 只有「MAIN 且未修复」的条目必须被约束处理：OBSERVATION 独立成节，FIXED 已闭环
    required = {e.get("id") for e in (fcat or {}).get("entries", [])
                if e.get("status") == "FOUND"
                and e.get("tier", "MAIN") == "MAIN"
                and not str(e.get("disposition", "")).startswith("FIXED")}
    linked = set()
    for r in reqs:
        rid = r.get("id", "?")
        if r.get("provenance") not in PROVENANCE:
            problems.append((2, BLOCK, f"{rid}: provenance 缺失或非法（{r.get('provenance')}）"))
        if r.get("control") not in CONTROL:
            problems.append((2, BLOCK, f"{rid}: control 必须 ∈ {sorted(CONTROL)}"))
        for f in r.get("from", []):
            if all_found and norm_target(f) not in all_found:
                problems.append((2, BLOCK, f"{rid}: 来源 {f} 不在 FailureCatalog 中"))
            linked.add(norm_target(f))
        # 可判定性：范围/触发/判定标准/时间限制 四要素
        for fld in ("scope", "trigger", "criterion"):
            if not str(r.get(fld, "")).strip():
                problems.append((2, BLOCK, f"{rid}: 缺 {fld}（无法判定通过/失败）"))
        if r.get("provenance") == "ASSUMPTION" and not str(r.get("falsifier", "")).strip():
            problems.append((2, BLOCK, f"{rid}: ASSUMPTION 必须给 falsifier"))
        vm = str(r.get("verification_method", "")).strip()
        if not vm:
            problems.append((2, BLOCK, f"{rid}: 无验证方法"))
        elif vm not in VERIFY_METHODS:
            problems.append((2, BLOCK,
                             f"{rid}: 验证手段不在枚举内（{vm}），必须属于 {sorted(VERIFY_METHODS)}"))
        elif not str(r.get("command", "")).strip():
            # CODE_REVIEW 同样必须可复现：写不出命令 = "我看过了"，不是验证
            problems.append((2, BLOCK,
                             f"{rid}: {vm} 必须给可复现命令（grep/dotnet test/复现程序）；"
                             f"人工阅读不得作为 command"))
        # 接受风险不是免检通道：必须写清谁接受、什么条件下重开
        if r.get("control") == "ACCEPT":
            for fld in ("risk_owner", "review_condition"):
                if not str(r.get(fld, "")).strip():
                    problems.append((2, BLOCK, f"{rid}: control=ACCEPT 但缺 {fld}"))
        if r.get("verifiable") is False and r.get("control") != "ACCEPT":
            problems.append((2, WARN, f"{rid}: 标记为不可验证却未转为 ACCEPT"))
    # 覆盖方向：每条开放 MAIN 的 F 必须被至少一条 R 处理（prevent/detect/recover/accept）
    unhandled = [f for f in required if f not in linked] if required else []
    if unhandled:
        problems.append((2, BLOCK, f"门禁2：{len(unhandled)} 条开放 MAIN 故障未被任何约束处理: {sorted(unhandled)[:6]}"))
    print(f"  门禁2 约束合法: {len(reqs)} 条约束，来源覆盖 {len(linked & required)}/{len(required)} 条开放 MAIN")


def gate3(rcat, vrep):
    if vrep is None:
        problems.append((3, BLOCK, "门禁3：VerificationReport 缺失"))
        return
    results = vrep.get("results", [])
    reqs = {r.get("id"): r for r in (rcat or {}).get("entries", [])}
    seen = {}
    for v in results:
        rid = v.get("req", "?")
        st = v.get("status")
        if st not in VSTATUS:
            problems.append((3, BLOCK, f"{v.get('id')}: status 必须是 {sorted(VSTATUS)}，实际 {st}"))
        if rid not in reqs:
            problems.append((3, BLOCK, f"{v.get('id')}: req {rid} 不存在于 RequirementCatalog"))
        seen.setdefault(rid, []).append(st)
        ev = v.get("evidence")
        if st in ("PASS", "FAIL"):
            if not ev:
                problems.append((3, BLOCK, f"{v.get('id')}: {st} 必须附证据路径"))
            elif not os.path.exists(os.path.join(ROOT, ev.replace("/", os.sep))) and not os.path.exists(
                    os.path.join(BASE, ev.replace("/", os.sep))):
                problems.append((3, BLOCK, f"{v.get('id')}: 证据文件不存在: {ev}"))
        if st == "PASS":
            prov = reqs.get(rid, {}).get("provenance")
            if prov == "CODE_AS_IS":
                problems.append((3, BLOCK, f"{v.get('id')}: CODE_AS_IS 约束不得判 PASS（现状合理化）"))
    no_result = [r for r in reqs if r not in seen]
    if no_result:
        problems.append((3, BLOCK, f"门禁3：{len(no_result)} 条约束没有任何验证记录: {no_result[:6]}"))
    tally = {}
    for sts in seen.values():
        for s in sts:
            tally[s] = tally.get(s, 0) + 1
    print(f"  门禁3 验证收口: {len(seen)}/{len(reqs)} 条约束有记录 | 状态分布 {tally}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gate", type=int, default=0)
    ap.add_argument("--scope", default="", help="scope json 路径，含 targets 数组，只对账该子集")
    a = ap.parse_args()

    only = None
    if a.scope and os.path.exists(a.scope):
        with open(a.scope, encoding="utf-8") as f:
            only = json.load(f).get("targets")

    idx = load("index.json")
    if idx is None:
        print("索引缺失，无法对账")
        return 2
    fcat = load("FailureCatalog.json", required=False)
    rcat = load("RequirementCatalog.json", required=False)
    vrep = load("VerificationReport.json", required=False)

    print("== 门禁检查 ==")
    if a.gate in (0, 1):
        gate1(idx, fcat, only)
    if a.gate in (0, 2):
        gate2(fcat, rcat)
    if a.gate in (0, 3):
        gate3(rcat, vrep)

    # ---- 产物成本：规程的纸面量不许远超被审代码本身（上轮实测 7:1，明显失衡）----
    try:
        art_bytes = sum(os.path.getsize(os.path.join(ART, n)) for n in
                        ("FailureCatalog.json", "RequirementCatalog.json", "VerificationReport.json")
                        if os.path.exists(os.path.join(ART, n)))
        src_bytes = sum(os.path.getsize(os.path.join(ROOT, (u.get("path") or "").replace("/", os.sep)))
                        for u in idx["units"]
                        if u.get("path") and (not only or u["id"] in set(only))
                        and os.path.exists(os.path.join(ROOT, u["path"].replace("/", os.sep))))
        if src_bytes:
            r = art_bytes / src_bytes
            print(f"  产物成本: {art_bytes / 1024:.1f} KB 产物 / {src_bytes / 1024:.1f} KB 源码 = {r:.1f} : 1")
            if r > 3:
                problems.append((0, WARN,
                                 f"产物体积是源码的 {r:.1f} 倍（>3 倍）：报告已成本体，压缩条目、合并同类项"))
    except OSError:
        pass

    blocks = [p for p in problems if p[1] == BLOCK]
    warns = [p for p in problems if p[1] == WARN]
    print(f"\n结果: {len(blocks)} 阻断 / {len(warns)} 警告")
    for g, sev, msg in problems[:40]:
        print(f"  [{sev}] G{g} {msg}")
    if len(problems) > 40:
        print(f"  ... 另有 {len(problems) - 40} 条")
    return 1 if blocks else 0


if __name__ == "__main__":
    sys.exit(main())

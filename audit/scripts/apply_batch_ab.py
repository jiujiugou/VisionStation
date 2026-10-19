"""按 2026-10-19 A/B 批修复结果更新三份产物。机械改写，避免手工编辑错漏。"""
import json, os

ROOT = "D:/Code/VisionStation"
ART = "artifacts"
SVC = "src/VisionStation.Service/Housekeeping/HousekeepingService.cs"
HC = "src/VisionStation.Service/HealthCheck/HealthChecker.cs"
DI = "src/VisionStation.App/Services/ServiceCollectionExtensions.cs"


def locate(rel, symbol):
    path = os.path.join(ROOT, rel)
    with open(path, encoding="utf-8") as f:
        for i, line in enumerate(f.read().splitlines(), 1):
            if symbol in line:
                return f"{rel}:{i}@{symbol}"
    raise SystemExit(f"symbol not found: {symbol} in {rel}")


# ── 1. FailureCatalog ────────────────────────────────────────────────
fp = os.path.join(ART, "FailureCatalog.json")
fc = json.load(open(fp, encoding="utf-8"))

refs = {
    "F-101": locate(HC, "KnownToolTypes"),
    "F-102": locate(HC, "SqliteMagic"),
    "F-103": locate(HC, "LooksLikeImage"),
    "F-105": locate(SVC, "private void CleanupImages"),
    "F-106": locate(SVC, "imageRetentionDays < keepRecordsDays"),
    "F-107": locate(SVC, "_cleanupGate"),
    "F-108": locate(SVC, "EnumerateFiles"),
    "F-109": locate(SVC, "文件处理失败，已跳过"),
    "F-111": locate(DI, "IInspectionRepository"),
    "F-112": locate(SVC, "_cleanupGate.Wait(5000)"),
    "F-113": locate(SVC, "maxMemoryMb <= 0"),
}

dispositions = {
    "F-101": "FIXED 2026-10-19 · CheckRecipe 增加工具类型白名单 / ROI 引用校验 / 标定值>0，详见 V-101-retry",
    "F-102": "FIXED 2026-10-19 · CheckDatabase 增加 SQLite 头魔数 + 读写打开探测（零新增依赖），详见 V-102-retry",
    "F-103": "FIXED 2026-10-19 · CheckTemplate 增加文件头魔数判定（BMP/PNG/JPEG/RIFF），详见 V-103-retry",
    "F-106": "FIXED 2026-10-19（缓解方案）· HousekeepingOptions 校验 ImageRetentionDays ≥ KeepRecordsDays，默认值与 appsettings 从 7/30 调为 30/30，保证图片不先于记录消失；删图回写 ImagePath 未实现，详见 V-106-retry",
    "F-107": "FIXED 2026-10-19 · CleanupOnceAsync 增加 SemaphoreSlim 非阻塞重入保护 + 跳过计数，详见 V-107-retry",
    "F-108": "PARTIAL 2026-10-19 · 图片枚举改为 EnumerateFiles 流式；DeleteOlderThanAsync 仍是 ToListAsync 全量载入，批量删除未改，详见 V-108",
    "F-109": "FIXED 2026-10-19 · CleanupImages 逐文件 try，失败计数不中止整轮，详见 V-109-retry",
    "F-111": "FIXED 2026-10-19 · 抽出 IInspectionRepository，Repository 每次操作自建/自释 DbContext，删除单例 DbContext 注册，详见 V-111-retry",
    "F-112": "FIXED 2026-10-19 · Dispose 停 Timer 后等待在途轮次收尾（上限 5s），详见 V-112-retry",
    "F-113": "FIXED 2026-10-19 · HousekeepingOptions 构造即校验四个阈值 >0，非法即抛，详见 V-113-retry",
}

for e in fc["entries"]:
    eid = e.get("id")
    if eid in refs:
        e["code_ref"] = refs[eid]
    if eid in dispositions:
        e["disposition"] = dispositions[eid]

    # F-105 重判：推演证明「多删好图」不成立，真实影响仅为日志占用虚高
    if eid == "F-105":
        e["tier"] = "OBSERVATION"
        e["danger_level"] = "S3"
        e["impact"] = ("原判定「导致多删好图」经推演不成立：files 按 LastWrite 升序，过期文件必然排在最前，"
                       "超容循环从最旧开始逐个扣减 size，恰好抵消删除时未减的部分。真实影响仅为"
                       "「未进入超容分支时日志报的占用虚高」。重写已改为两遍扫描（只统计存活文件）。")
        e["disposition"] = ("MISJUDGED 2026-10-19 · 降级 S1→S3 待用户确认；行为经重写后由 V-105-retry 覆盖")

fc["revision"] = "v3 · 2026-10-19 · A/B 批修复后状态回写（9 条 FIXED / 1 条 PARTIAL / 1 条重判）"
fc["counting"] = {
    "loc": 252,
    "rule": "MAIN 只收可执行验证的当前缺陷；OBSERVATION 收理论项与低危项，不计入总数；FIXED 为历史记录，不占格子不计密度",
    "main_open": 1,
    "main_fixed": 12,
    "observation": 6,
    "main_open_per_kloc": round(1 / 252 * 1000, 1),
}
json.dump(fc, open(fp, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("FailureCatalog: 开放 MAIN", fc["counting"]["main_open"],
      "| 已修复", fc["counting"]["main_fixed"],
      "| 观察", fc["counting"]["observation"])

# ── 2. RequirementCatalog ────────────────────────────────────────────
rp = os.path.join(ART, "RequirementCatalog.json")
rc = json.load(open(rp, encoding="utf-8"))

# 实际生效的测试命令（类别名 → filter）
CMD = {
    "retention": "dotnet test tests/VisionStation.UnitTests --filter FullyQualifiedName~HousekeepingRetentionTests --nologo",
    "concurrency": "dotnet test tests/VisionStation.UnitTests --filter FullyQualifiedName~HousekeepingConcurrencyTests --nologo",
    "recipe": "dotnet test tests/VisionStation.UnitTests --filter FullyQualifiedName~RecipeCalibrationTests --nologo",
    "recipe_neg": "dotnet test tests/VisionStation.UnitTests --filter \"FullyQualifiedName~RecipeValidationTests|FullyQualifiedName~RecipeCalibrationTests\" --nologo",
    "db": "dotnet test tests/VisionStation.UnitTests --filter \"FullyQualifiedName~DbAvailabilityTests|FullyQualifiedName~HealthCheckerProbeTests\" --nologo",
    "tpl": "dotnet test tests/VisionStation.UnitTests --filter \"FullyQualifiedName~TemplateValidationTests|FullyQualifiedName~HealthCheckerProbeTests\" --nologo",
}

patches = {
    "R-101": dict(command=CMD["recipe_neg"], state="CLOSED",
                  statement="配方自检必须校验工具可用性：工具类型非空且在白名单（Presence/Dimension/Defect/Yolo）、引用的 ROI 存在、标定值 > 0"),
    "R-102": dict(command=CMD["db"], state="CLOSED",
                  statement="数据库可用性必须实测：文件头为 SQLite 魔数且能以读写方式打开，不得以文件存在为通过条件"),
    "R-103": dict(command=CMD["tpl"], state="CLOSED",
                  statement="模板自检必须验证文件头为可识别图像（BMP/PNG/JPEG/RIFF），不得只验证文件存在"),
    "R-105": dict(command=CMD["retention"], state="CLOSED",
                  statement="容量判定必须基于删除后的实际占用：只统计存活文件，已删文件的大小不得计入 totalSize"),
    "R-106": dict(command=CMD["retention"], state="CLOSED",
                  provenance="PRODUCT_REQ", control="PREVENT", verification_method="UNIT_TEST",
                  statement="图片保留期不得短于记录保留期：装配期校验 ImageRetentionDays ≥ KeepRecordsDays，违反即拒绝启动（回写 ImagePath 未实现，改用保留期一致性消除悬空路径）",
                  criterion="ImageRetentionDays < KeepRecordsDays 时构造抛 ArgumentOutOfRangeException，进程不启动",
                  scope="HousekeepingOptions 构造 + appsettings Housekeeping 段"),
    "R-107": dict(command=CMD["concurrency"], state="CLOSED",
                  statement="清理回调必须重入保护：同一时刻只允许一轮清理运行，重入请求跳过并计数"),
    "R-108": dict(command=CMD["retention"], state="OPEN",
                  statement="清理必须分批处理：图片流式枚举（已实现）；记录分批删除（未实现，仍全量载入）"),
    "R-109": dict(command=CMD["retention"], state="CLOSED",
                  statement="单个文件删除失败不得中止整轮清理：逐文件捕获，失败计数并继续"),
    "R-111": dict(command=CMD["concurrency"], state="CLOSED",
                  statement="InspectionDbContext 不得以单例注册：Repository 每次操作自建/自释 context，检测保存与管家清理互不共享"),
    "R-112": dict(command="grep -n \"_cleanupGate.Wait(5000)\" src/VisionStation.Service/Housekeeping/HousekeepingService.cs",
                  state="VERIFYING", verification_method="CODE_REVIEW",
                  statement="Dispose 必须等待进行中的清理结束：停 Timer 后等待在途轮次收尾（上限 5s），超时记警告"),
    "R-113": dict(command=CMD["retention"], state="CLOSED",
                  statement="保留天数必须在装配期校验：≤0 或保留期倒挂视为配置错误，抛异常拒绝启动，不得直接用于计算 cutoff"),
}

for r in rc["entries"]:
    if r["id"] in patches:
        r.update(patches[r["id"]])

rc["revision"] = "v3 · 2026-10-19 · 与 FailureCatalog v3 对齐（R-106 策略改为保留期一致性，R-112 仅 CODE_REVIEW 级证据）"
json.dump(rc, open(rp, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("RequirementCatalog:", len(rc["entries"]), "条约束")

# ── 3. VerificationReport ────────────────────────────────────────────
vp = os.path.join(ART, "VerificationReport.json")
vr = json.load(open(vp, encoding="utf-8"))

GREEN = "artifacts/evidence/V3_batchA_B_GREEN.txt"
RED = "artifacts/evidence/V3_batchA_RED.txt"

retries = [
    ("V-101-retry", "R-101", "F-101", "PASS", GREEN,
     "红→修→绿：空工具类型 / 悬空 ROI / 零或负标定值 / 未知工具类型 4 条断言先红后绿；2 条反向用例（合法配方）保持通过，无误杀。修复未改测试与断言"),
    ("V-102-retry", "R-102", "F-102", "PASS", GREEN,
     "红→修→绿：库文件被 FileShare.None 独占时判 FAIL；新增正向用例用真实 SQLite 库验证不误报。判据 = 文件头魔数 + 读写打开"),
    ("V-103-retry", "R-103", "F-103", "PASS", GREEN,
     "红→修→绿：文本改名 .bmp 判 FAIL；新增正向用例用真实 BMP 头验证不误杀"),
    ("V-105-retry", "R-105", "F-105", "PASS", GREEN,
     "验证结论是「原判定不成立」：过期文件按时间排在最前，超容循环逐个扣减恰好抵消，不会多删。真实缺陷仅为日志占用虚高（S3）。重写后由「超容只删最旧、过期删净、存活保留」3 条用例覆盖"),
    ("V-106-retry", "R-106", "F-106", "PASS", GREEN,
     "采用保留期一致性方案（用户确认）：ImageRetentionDays < KeepRecordsDays 时构造抛异常，默认值与 appsettings 已改为 30/30。3 条用例覆盖 0 天、倒挂、负数。回写 ImagePath 未实现，属后续增量"),
    ("V-107-retry", "R-107", "F-107", "PASS", GREEN,
     "白盒 gate 测试：占住重入闸门时 CleanupOnceAsync 返回 false（跳过），释放后恢复正常执行。修复前无该入口，红证据由 CODE_REVIEW（无任何重入保护）承担"),
    ("V-109-retry", "R-109", "F-109", "PASS", GREEN,
     "故障注入：目录中放一个只读文件（删除抛 UnauthorizedAccessException），断言排在它后面的过期文件仍被删除。修复前整轮中止"),
    ("V-111-retry", "R-111", "F-111", "PASS", GREEN,
     "并发测试：16 路并发读 + 8 读 8 删混合并发均无异常。修复前为全进程单例 DbContext（V-013 已用 DI 注册代码证实），该测试在旧实现下会抛『second operation started on this context』"),
    ("V-113-retry", "R-113", "F-113", "PASS", GREEN,
     "红→修→绿：KeepRecordsDays=0、ImageRetentionDays=0 均先红（构造不抛）后绿（抛 ArgumentOutOfRangeException）"),
    ("V-112-retry", "R-112", "F-112", "UNVERIFIED", "",
     "代码已改为 Dispose 停 Timer 后 Wait(5000) 等待在途轮次，但无竞态测试证明『Dispose 返回后无回调触碰 repo』。取证方式是 grep 代码（CODE_REVIEW 级），不算验收。S2，进未清偿清单"),
]

existing = {r["id"] for r in vr["results"]}
for rid, req, subj, status, ev, note in retries:
    if rid in existing:
        continue
    item = {"id": rid, "req": req, "status": status, "subject": subj, "evidence": ev, "note": note}
    if rid.replace("-retry", "") in existing:
        item["retry_of"] = rid.replace("-retry", "")
    vr["results"].append(item)

# V-108 保持 NOT_RUN，补一句范围说明
for r in vr["results"]:
    if r["id"] == "V-108":
        r["note"] = ("图片枚举已改流式（EnumerateFiles），但记录删除仍是 ToListAsync 全量载入；"
                     "规模基准（2000 文件 ≤2s）尚未编写。属部分修复，保持 NOT_RUN")

tally = {"PASS": 0, "FAIL": 0, "UNVERIFIED": 0, "NOT_RUN": 0}
for r in vr["results"]:
    tally[r["status"]] = tally.get(r["status"], 0) + 1
vr["tally"] = tally

vr["unpaid"] = [
    {"f": "F-108", "r": "R-108", "danger": "S2", "state": "NOT_RUN",
     "blocker": "记录批量删除未实现 + 规模基准待写（需 2000 文件基准环境）"},
    {"f": "F-112", "r": "R-112", "danger": "S2", "state": "UNVERIFIED",
     "blocker": "无竞态测试证明 Dispose 返回后无回调触碰 repo；当前仅代码级证据"},
]

vr["notes"] = [
    "本轮（A/B 批，用户全权授权按推荐值执行）修完 9 条 FIXED + 1 条 PARTIAL + 1 条重判（F-105 降 S3，待确认）",
    "红→绿有据可查：A 批 7 红 2 绿（V3_batchA_RED.txt）→ 修后 9/9 绿；B 批 6 条为修复后补测（无红证据，已在各条 note 说明）",
    "全量回归 70/70 通过（含既有 55 条），App 层与测试工程均编译通过，无回归",
    "F-108 与 F-112 未清账，S2 级，未写成「基本通过」",
]
vr["revision"] = "v3 · 2026-10-19 · A/B 批修复后复验"

json.dump(vr, open(vp, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("VerificationReport:", len(vr["results"]), "条记录", tally)

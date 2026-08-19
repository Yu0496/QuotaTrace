# Codex 用量统计器：一次性修复、重算与校准任务书

> 项目：`F:/Project/QuotaStatistics`  
> 目标：修复当前 Codex token / API 等值统计中已确认和已暴露的全部可靠性问题，并完成数据库迁移、历史全量重算、回归测试和真实数据校验。  
> 执行对象：Codex / IDE Agent  
> 核验日期：2026-08-19

---

## 0. 这次不是继续审计，而是直接修复

上一轮审计已经确认当前统计**不能完全信任**，最主要的虚高来源是：

```text
Codex total_token_usage 是整个 session 的累计 counter
但当前 parser 按 model 分别保存 previous counter
```

当前错误代码位于：

```text
F:/Project/QuotaStatistics/src/UsageTray/Providers/Codex/CodexJsonlParser.cs
```

当前逻辑类似：

```csharp
var counters = new Dictionary<string, Counter>();
...
counters.TryGetValue(effectiveModel, ...)
```

这是错误的。

正确原则：

```text
“新增了多少 token”由 session 全局累计 counter 决定。
“新增 token 属于哪个模型”由该 event 当时的 effective model 决定。
```

模型切换不能建立新的累计 baseline。

本轮允许直接修改源代码、测试、SQLite schema/migration、pricing schema、Diagnostics，并在测试通过后备份数据库、迁移数据库和全量重算历史。

---

# 1. 两个必须长期保留的真实 Golden Session

## 1.1 单模型短 Session

```text
C:/Users/xiong/.codex/archived_sessions/
rollout-2026-08-13T21-57-20-019ffb69-c293-7f01-a81b-be85256432f2.jsonl
```

真实结果：

```text
第一条累计：
Input    18,297
Cached    9,984
Output       402

最终累计：
Input   185,286
Cached  157,952
Output    2,518

正确 delta 总和：
Input   185,286
Cached  157,952
Output    2,518
```

---

## 1.2 Sol → Terra → Sol 长 Session

```text
C:/Users/xiong/.codex/archived_sessions/
rollout-2026-07-11T02-18-37-019f4d40-afae-74b1-8c32-89927f0860e0.jsonl
```

模型发生：

```text
gpt-5.6-sol
→ gpt-5.6-terra
→ gpt-5.6-sol
```

正确全局累计：

```text
Input    100,366,344
Cached    97,597,440
Output       247,824
```

修复前 SQLite 错误值：

```text
Input    157,400,039
Cached   152,896,256
Output       414,180
```

旧短上下文价格口径下：

```text
正确：$64.5191346
旧程序：$86.6037532
虚高约 34.2%
```

注意：本任务还要加入 GPT-5.6 长上下文定价，所以 `$64.5191346` 只用于验证旧价格口径下的 parser 修复。最终成本可能更高。

**Golden Token Totals 必须永久固定为：**

```text
100,366,344
97,597,440
247,824
```

---

# 2. 本轮必须一次解决的全部问题

1. 模型切换导致累计 token 重复计入。
2. 模型切回时再次重复累计。
3. Counter rewind/reset/compaction。
4. `last_token_usage` 重播导致潜在重复统计。
5. 同一 `session_id` 多路径导致的重复风险。
6. active session 移入 `archived_sessions` 后重复/残留。
7. JSONL 删除后数据库旧贡献不清理。
8. Refresh / Full Rescan / Restart 必须幂等。
9. 保持 Cached Input 不 double count。
10. 检测并支持 Cache Write token（当前 schema 若存在）。
11. `reasoning_output_tokens` 不 double count。
12. GPT-5.6 >272K input 的 Long Context 价格。
13. Standard API 等值、API Fast/Flex、ChatGPT Fast credit multiplier 必须分离。
14. 删除危险的 `gpt-5*` 通用价格 fallback。
15. `codex-auto-review` 等未知模型不能乱套价格。
16. 日期/模型/项目/session 汇总必须一致。
17. 增加完全独立的 Reference Parser。
18. 尽可能用官方 Codex `account/usage/read` 做只读交叉验证。
19. parser/schema 升级后自动使旧错误缓存失效并重建。
20. 不回归 Antigravity 功能。

---

# 3. 修复核心：Session Global Counter

错误：

```text
Dictionary<Model, Counter>
```

改为 session/logical-session 的单一累计状态：

```text
SessionGlobalCounter
```

模型只负责 delta 归属。

例：

```text
Sol    total=100
Sol    total=200
Terra  total=300
Terra  total=400
Sol    total=500
```

正确：

```text
Sol    +100
Sol    +100
Terra  +100
Terra  +100
Sol    +100

总计 = 500
Sol   = 300
Terra = 200
```

不能因为第一次出现 Terra 就把 300 全算给 Terra，也不能切回 Sol 后把 500-旧 Sol baseline 再算一次。

建议新增：

```text
CodexCounterState
CodexCounterEpoch
CodexUsageNormalizer
```

---

# 4. Global Counter 算法

伪代码：

```text
previous = null
epoch = 0

for snapshot in chronological_order:

    current = snapshot.total_token_usage

    if previous == null:
        delta = current
        previous = current
        record(delta)
        continue

    if current == previous:
        delta = 0
        continue

    if all cumulative counters are monotonic >= previous:
        delta = current - previous
        previous = current
        record(delta)
        continue

    if any cumulative counter decreases:
        epoch += 1
        previous = current

        // 这是 rewind/reset baseline，不把 current 整体重新计入。
        delta = 0

        // 只有存在独立且已验证的 per-request usage 时，
        // 才允许记录 reset event 上的真实新增 usage。
```

**重要：**

- 第一个 snapshot：`delta=current`，因为 0 到第一条的使用真实发生过。
- 中途 rewind：默认 `delta=0`，建立新 epoch。
- 不允许中途 rewind 时把较小的 `current` 当成一整笔新 usage。

---

# 5. `last_token_usage` 不能直接相加

Codex JSONL 可能有：

```text
payload.info.last_token_usage
```

它不能作为账本直接 `SUM()`。

Codex 存在这种行为：

```text
只更新 rate limit
→ total_token_usage 没变化
→ 又发 token_count
→ last_token_usage 仍重复上一笔非零值
```

所以必须遵守：

```text
total cumulative 未推进
=> usage delta 必须为 0
=> 即使 last_token_usage 非零也不能再记
```

`total_token_usage` 是账本 truth。

`last_token_usage` 只用于：

- 验证当前 delta 是否对应一笔完整 request；
- 判断 request-level input；
- 判断 >272K long context；
- cache read/write/request shape；
- service tier（如果日志可靠提供）。

---

# 6. `last_token_usage` 与 delta 的一致性

每次 cumulative 推进后比较：

```text
deltaInput
deltaCached
deltaCacheWrite
deltaOutput
```

与：

```text
last_token_usage
```

若字段一致：

```text
RequestUsageQuality = Exact
```

可以将 `last_token_usage.input_tokens` 作为该 request 的 input size。

若不一致：

```text
RequestUsageQuality = AggregateOrUnknown
```

此时：

- Global delta 仍可用于 token 账本；
- 不要强行把它当单 request；
- 不要凭它触发长上下文价格；
- CostQuality 要降级。

---

# 7. Raw Snapshot 模型

建议：

```csharp
public sealed record CodexTokenSnapshot(
    string SessionId,
    DateTimeOffset Timestamp,
    string? EffectiveModel,
    string? ServiceTier,
    TokenCounter Total,
    TokenCounter? Last,
    long? ModelContextWindow,
    string SourcePath,
    int SourceLine,
    string EventKey
);
```

`TokenCounter` 至少准备：

```text
InputTokens
CachedInputTokens
CacheWriteTokens
OutputTokens
ReasoningOutputTokens
```

字段不存在时明确标记 unavailable，不要伪造。

---

# 8. Cache Write：这次必须查清

执行 Agent 搜真实 JSONL和当前 Codex schema/源码：

```text
cache_write
cache_write_tokens
cache_write_input_tokens
cache_creation
cache_creation_input_tokens
```

官方 GPT-5.6 当前价格包含 Cache Write，因此如果 Codex 当前日志已经记录它，应纳入统计。

若确认：

```text
InputTokens 包含 CachedInput + CacheWrite
```

则：

```text
NormalInput =
    InputTokens
    - CachedInputTokens
    - CacheWriteTokens
```

成本：

```text
NormalInput  * InputPrice
Cached       * CachedPrice
CacheWrite   * CacheWritePrice
Output       * OutputPrice
```

如果旧日志根本没有 Cache Write 分类：

- 不把所有普通 input 当 cache write；
- 也不要未经验证宣称 cache write=0；
- 标记 `CostQuality=CacheWriteUnavailable`；
- token 仍然保留。

---

# 9. Cached Input Double Count

上一轮这一项已经 PASS，必须保持。

如果 `InputTokens` 是包含 cached 的总 input：

```text
NormalInput = Input - Cached - CacheWrite
```

不能：

```text
Input * normal rate
+ Cached * cached rate
```

---

# 10. Reasoning Output

检查：

```text
reasoning_output_tokens
```

是否为 `output_tokens` 的 detail/subset。

若是，则计费 output：

```text
OutputTokens
```

不是：

```text
OutputTokens + ReasoningOutputTokens
```

添加回归测试，防止 double count。

---

# 11. GPT-5.6 Long Context：按真实 request 定价

截至 2026-08-19，官方规则：

```text
Prompts with >272K input tokens
```

整个请求：

```text
Input        2x
Cached Input 2x
Cache Write  2x
Output       1.5x
```

官方：

```text
https://developers.openai.com/api/docs/pricing
https://developers.openai.com/api/docs/models/gpt-5.6-sol
```

**实现当天重新核对官方价格；若已变化，以当天官方值为准。**

不能用 session 累计总量判断。

最可靠判断：

```text
Global delta 与 last_token_usage 完全一致
```

则：

```text
last_token_usage.input_tokens
```

作为单 request input。

阈值测试：

```text
271,999 -> Short
272,000 -> Short
272,001 -> Long
```

---

# 12. 当前 GPT-5.6 Standard 价格结构

截至核验日：

```text
Sol Short:
Input         5.00
Cached        0.50
Cache Write   6.25
Output       30.00

Sol Long:
Input        10.00
Cached        1.00
Cache Write  12.50
Output       45.00

Terra Short:
Input         2.00
Cached        0.20
Cache Write   2.50
Output       12.00

Terra Long:
Input         4.00
Cached        0.40
Cache Write   5.00
Output       18.00

Luna Short:
Input         0.20
Cached        0.02
Cache Write   0.25
Output        1.20

Luna Long:
Input         0.40
Cached        0.04
Cache Write   0.50
Output        1.80
```

单位：

```text
USD / 1M tokens
```

不要把 long-context multiplier 散落硬编码到 CostCalculator；价格表直接支持 Short / Long 两套价格。

---

# 13. PricingRule 建议

```csharp
public sealed record TokenPriceSet(
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal? CacheWritePerMillion,
    decimal OutputPerMillion
);

public sealed record PricingRule(
    string Provider,
    string ModelPattern,
    MatchMode MatchMode,
    TokenPriceSet StandardShort,
    TokenPriceSet? StandardLong,
    long? LongContextInputThreshold,
    string SourceUrl,
    DateOnly LastVerifiedAt
);
```

价格变更后只重算 cost，不要求重扫 token raw logs。

---

# 14. API 等值主指标口径

主界面继续使用：

```text
API 等值（Standard）
```

定义：

> 同一批真实 token 如果按对应模型 Standard API 价格调用，大约需要多少钱。

主指标**不要**应用：

- ChatGPT Codex Fast 2.5x credit multiplier；
- API Fast 2x；
- Flex 0.5x；
- Sub2API 分组/用户倍率。

这些如果以后需要，只能作为独立指标。

---

# 15. Fast / Flex / Credit multiplier 分离

当前官方规则中：

```text
Codex ChatGPT Fast GPT-5.6:
credits 约 2.5x Standard

API Fast GPT-5.6:
API price 约 2x Standard
```

二者不是一个指标。

若 JSONL 能可靠得到 `service_tier`，可以在 Diagnostics/详情中额外算：

```text
Observed Tier API Equivalent
```

但不能覆盖主 `Standard API Equivalent`。

如果未来做：

```text
Codex Credit Weighted Equivalent
```

也必须第三个独立显示。

---

# 16. 删除危险价格 wildcard

移除：

```text
gpt-5*
```

这种宽泛 fallback。

允许：

```text
gpt-5.6-sol
gpt-5.6-terra
gpt-5.6-luna
```

以及官方明确 alias，例如：

```text
gpt-5.6 -> gpt-5.6-sol
```

Snapshot/prefix 只有官方明确属于同 pricing family 才匹配。

未知模型：

```text
Tokens 正常统计
Cost = N/A
UnpricedTokens += ...
```

---

# 17. `codex-auto-review`

当前出现：

```text
codex-auto-review
```

不要猜是 Sol/Terra/Luna。

只有找到官方明确映射才定价。

否则主界面：

```text
已定价 API 等值：$X
另有 Y tokens 未定价
```

不要把 `$X` 描述成完整总成本。

---

# 18. 同一个 session_id 多路径：必须这次彻底解决

数据库已发现：

```text
一个 session_id 对应 26 个 path
```

本轮不能继续留 `NEEDS VERIFICATION`。

但不能直接：

```text
UNIQUE(session_id)
```

因为多文件可能是：

- exact duplicate；
- active → archived 副本；
- prefix；
- partial overlap；
- continuation；
- resumed segment；
- rewind/fork；
- 真正不同的 segment。

---

# 19. 推荐改为 Logical Session Pipeline

Codex 使用统计改成：

```text
source files
    ↓
raw token snapshots
    ↓
group by session_id
    ↓
event-level dedupe
    ↓
chronological sort
    ↓
global counter delta
    ↓
model/date/project attribution
    ↓
logical session usage
```

`source_files` 仍然负责：

- 文件发现；
- size；
- mtime；
- parser cache；
- session_id；
- source lifecycle。

但最终 usage contribution 应按 logical session 生成，而不是不同 path 各自产生一份可能重叠的 `file_usage` 再直接求和。

---

# 20. EventKey / Snapshot Fingerprint

优先：

```text
官方稳定 event id
```

若没有，用：

```text
SHA256(
    session_id
  + precise timestamp
  + total input
  + total cached
  + total cache_write
  + total output
  + event type
)
```

不包含 prompt / response 正文。

相同 EventKey 在：

```text
sessions
archived_sessions
额外目录
```

只计一次。

如果 EventKey 相同但关键 usage/model 字段冲突：

```text
DataConflict
```

不能 silently merge。

---

# 21. 26-path session 必须输出分类报告

对当前最大的碰撞组输出：

```text
session_id
path
file size
SHA256
first timestamp
last timestamp
first counter
last counter
token event count
unique event count
overlap count
```

分类：

```text
Exact duplicate
Prefix duplicate
Partial overlap
Continuation
Independent segment
Unknown
```

然后验证新算法只计真实 unique usage 一次。

---

# 22. 多路径合并

同 `session_id` 的所有 snapshots：

```text
Union by EventKey
Sort by timestamp
```

相同 event：

- 一个 model 为空、另一个非空 → 用非空；
- 两个非空且冲突 → Diagnostics 报冲突。

Source priority 只用于冲突：

```text
active sessions
> archived_sessions
> extra roots
```

但 archived 中存在 active 没有的独有 event 时仍然保留。

---

# 23. active → archived 移动

必须保留 `archived_sessions` 扫描。

测试：

```text
Before:
sessions/A.jsonl

After:
archived_sessions/A.jsonl
```

刷新后：

```text
Logical session token 完全不变
Cost 完全不变
```

不能为了去重直接停止扫描 archived。

---

# 24. 删除/移动旧 Source 的 stale contribution

当前删除 JSONL 后旧贡献不会清理，需要修。

建议：

```text
scan_generation_id
```

完整成功扫描某 root：

- 发现文件标记 `last_seen_generation`；
- 扫描完成后找本 root 未 seen 的旧 source；
- 删除/标记 source；
- 将相关 logical session 加入 recompute queue。

**只有 root 本轮扫描成功时才清 missing。**

磁盘/权限错误时不能把整个 root 当删除。

---

# 25. 数据库建议

可以新增 logical-session 聚合表，例如：

```sql
CREATE TABLE codex_session_usage (
    session_id            TEXT NOT NULL,
    local_date            TEXT NOT NULL,
    project_key           TEXT NOT NULL DEFAULT '',
    model_id              TEXT NOT NULL DEFAULT '',
    input_tokens          INTEGER NOT NULL,
    cached_input_tokens   INTEGER NOT NULL,
    cache_write_tokens    INTEGER NOT NULL DEFAULT 0,
    output_tokens         INTEGER NOT NULL,
    request_count         INTEGER NOT NULL DEFAULT 0,
    long_context_requests INTEGER NOT NULL DEFAULT 0,
    data_quality          INTEGER NOT NULL,
    cost_quality          INTEGER NOT NULL,
    PRIMARY KEY(
        session_id,
        local_date,
        project_key,
        model_id
    )
);
```

也可在现有结构上实现，只要最终能证明：

```text
同一 logical session 的重叠 source 永远只统计一次
```

---

# 26. Parser / Schema Version 必须升级

当前旧 DB 内已有错误解析结果。

提升：

```text
Codex parser version
database/schema version（如需要）
```

新版本首次启动：

1. 备份：
   ```text
   usage.db.bak-YYYYMMDD-HHMMSS
   ```
2. 保留：
   - settings；
   - project aliases；
   - Antigravity 数据；
   - quota snapshots。
3. invalidated：
   - 旧 Codex file/session usage；
   - 旧 Codex parser state/cache。
4. 自动 full rebuild Codex。
5. 不要求用户手动删除 DB。

---

# 27. Full Rebuild 要原子化

避免：

```text
删旧统计
→ 重建中崩溃
→ 用户只剩半库/0
```

可：

- per-session atomic replace；
- 或 temporary rebuild tables 完成后 swap。

失败 session：

```text
保留 error
不能无声消失
```

---

# 28. 增量性能

修复后仍保持轻量：

```text
source file 未变化
=> 不重新 raw parse

source file 变化
=> parse 该 source
=> recompute 受影响 logical session
```

不需要每次刷新全量重扫 287+ files。

---

# 29. 模型/日期/项目归属

模型：

```text
model change event
→ currentModel 更新
→ 下一次真实 cumulative advance 的 delta 归 currentModel
```

日期：

```text
event UTC timestamp
→ TimeZoneInfo.Local
→ local_date
```

不要按 sessions 文件夹日期归属。

项目：

- 保留当前 CWD/project 逻辑；
- 一个 logical session 不应因重复 source 被归到两个项目；
- metadata 冲突要 Diagnostics。

---

# 30. 永久增加独立 Reference Parser

新增测试专用：

```text
tests/ReferenceCodexParser.cs
```

不得调用：

- 正式 `CodexJsonlParser`
- `UsageAggregator`
- `PricingService`
- SQLite

只实现最简单 truth：

```text
读取 JSONL
→ cumulative total
→ 单 global previous
→ delta
```

用它和正式 parser 对比。

---

# 31. 必须新增的回归测试

至少覆盖以下场景。

### A. 单模型真实短 session

预期：

```text
185,286 / 157,952 / 2,518
```

### B. Sol → Terra → Sol 真实长 session

预期：

```text
100,366,344 / 97,597,440 / 247,824
```

正式 parser == Reference parser。

### C. 人工模型切换

```text
Sol   total 100
Sol   total 200
Terra total 300
Terra total 400
Sol   total 500
```

预期：

```text
Sol=300
Terra=200
Total=500
```

### D. rate-limit-only last 重播

```text
total=100 last=100
total=100 last=100
total=150 last=50
```

预期总 usage：

```text
150
```

不是 250。

### E. rewind

```text
100
200
300
120  // reset baseline
170
```

预期：

```text
100+100+100+0+50=350
```

### F. Long context boundary

```text
271999 short
272000 short
272001 long
```

### G. Cache Write

确认 normal/cached/write/output 四类不重复。

### H. Reasoning

若：

```text
output=1000
reasoning=600
```

且 reasoning 是 subset，最终 output 仍计 1000。

### I. exact duplicate paths

两个路径完整内容一致，只计一次。

### J. prefix

```text
A: e1,e2
B: e1,e2,e3
```

最终 e1-e3 各一次。

### K. partial overlap

```text
A: e1,e2,e3
B: e2,e3,e4
```

最终 e1-e4 各一次。

### L. continuation

同 session_id 不重叠、counter 连续，全部保留。

### M. moved active -> archived

移动前后统计完全一致。

### N. deleted source

成功扫描 root 后旧 contribution 消失。

### O. Full Rescan / Restart

```text
Refresh
Refresh
Full Rebuild
Restart
Refresh
```

无新 usage 时总数完全一致。

### P. Unknown model

Token 保留，Cost=N/A。

### Q. no broad wildcard

未来未知 `gpt-5.x-*` 不能被 `gpt-5*` 自动估价。

---

# 32. 汇总一致性

任意日期范围：

```text
SUM(ByDate.Input)
= SUM(ByModel.Input)
= SUM(ByProject.Input)
= SUM(BySession.Input)
= Dashboard.Input
```

Cached / CacheWrite / Output 同理。

已定价 Cost：

```text
ByDate
ByModel
ByProject
Dashboard
```

必须一致。

未定价：

```text
UnpricedTokens
```

单独列出。

---

# 33. CostQuality

建议：

```csharp
public enum CostQuality
{
    Exact,
    CacheWriteUnavailable,
    RequestShapeUnavailable,
    LongContextUncertain,
    UnpricedModel
}
```

UI 不要把所有不确定 usage 混成一个看起来精确到分的金额。

建议：

```text
已定价 API 等值：$123.45
另有 2.4M tokens 未定价
部分旧请求缺少 Cache Write 分类
```

---

# 34. 官方 Codex usage 交叉验证

当前官方 Codex app-server 提供：

```text
account/usage/read
```

官方文档：

```text
https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md
```

当前说明它可以读取：

- account token-activity summary；
- daily buckets；
- 指定 `threadId` 时的 thread usage；
- estimated credits；
- optional cost；
- usage breakdown。

如果本机 Codex 版本支持，增加：

```text
CodexOfficialUsageVerifier
```

**只用于 Diagnostics / Cross-check。**

不能再加进本地 JSONL usage，否则又形成双数据源重复。

---

# 35. 官方验证的安全边界

优先：

```text
调用本机 codex app-server 的已有登录状态
```

不要：

- 读取 `auth.json` 后自己发 token；
- 保存 OAuth access token；
- 将官方 usage 加进本地 token totals。

接口不可用：

```text
Verifier=N/A
```

不影响主统计。

---

# 36. Official vs Local 报告

按可比窗口显示：

```text
Local JSONL
Official Account Usage
Difference
```

并说明合理差异可能来自：

- cloud Codex；
- code review；
- ephemeral session；
- auto-review；
- 当前仍未写盘 session；
- 官方计量覆盖范围不同。

如果支持 `account/usage/read(threadId)`，优先用前面两个 Golden Session 的 UUID 做 thread-level 对比。

---

# 37. 单 Session 审计模式永久保留

Diagnostics 提供一个简单审计导出：

```text
Session ID
Source paths
Timestamp
Model
Epoch
Raw total
Previous total
Delta
Last usage
RequestUsageQuality
Short/Long
Pricing rule
Cost
EventKey
```

不要求漂亮 UI，可以导出文本/CSV。

它以后用于快速排查 Codex schema 变化。

---

# 38. 迁移前后必须生成对比文件

迁移前：

```text
diagnostics/pre-fix-baseline.json
```

迁移后：

```text
diagnostics/post-fix-result.json
```

包含：

```text
Today
7d
30d
Month

Input
Cached
CacheWrite
Output
Priced USD
Unpriced Tokens
```

不保存 prompt/response。

---

# 39. 最终尽可能量化每类修正

报告：

```text
Model-switch correction       -$X
Multi-path dedupe              -$Y
Stale source cleanup           -$Z
Long-context pricing           +$A
Cache-write pricing            +$B
Pricing mapping correction     +/-$C
Unknown model removed from USD -$D
```

无法单独拆分时写：

```text
Not independently measurable
```

不要编造。

---

# 40. 26-path session 必须实际调查并结案

找到那个：

```text
1 session_id -> 26 paths
```

逐 path 输出：

```text
path
size
SHA256
first timestamp
last timestamp
first counter
last counter
event count
unique event count
overlap count
```

最终必须明确属于：

```text
Exact duplicate / Prefix / Overlap / Continuation / Independent / Mixed
```

并证明新算法处理后没有重复或漏算。

本轮不接受继续写：

```text
NEEDS VERIFICATION
```

---

# 41. 真实长 Session 最终硬验收

对：

```text
019f4d40-afae-74b1-8c32-89927f0860e0
```

必须输出：

```text
Raw final cumulative
Reference parser
Formal parser
DB logical-session aggregate
```

Token 必须一致：

```text
Input    100,366,344
Cached    97,597,440
Output       247,824
```

Cost 则按修复后的：

- short/long context；
- cache write 可得性；
- exact model pricing；

重新计算。

---

# 42. default-pricing.json 迁移

如果价格 schema 从：

```text
Input / Cached / Output
```

升级到：

```text
Short
Long
CacheWrite
```

必须兼容旧用户 pricing。

迁移：

1. 旧 Input/Cached/Output → `StandardShort`。
2. exact 已知 GPT-5.6 可以补官方 CacheWrite/Long。
3. 自定义未知模型缺失字段保持 null，不猜。
4. 更新 `schemaVersion`。
5. 保留用户自定义规则。
6. `LastVerifiedAt` 和 `SourceUrl` 写入。

---

# 43. 不要自动抓网页价格

工具保持轻量。

这次只更新本地内置价格。

Agent 实施当天访问：

```text
https://developers.openai.com/api/docs/pricing
```

重新核对 Sol/Terra/Luna 和 Long Context。

若与本文不同：

```text
以实施当天官方数据为准
```

并在最终报告说明变化。

---

# 44. 数据库安全

迁移真实 DB 前：

```text
dotnet test
dotnet build -c Release
```

全部 PASS。

生产 DB：

```text
先备份
再迁移
```

测试使用临时 DB，不能拿生产 DB 做破坏性测试。

原始 Codex JSONL 永不修改/删除。

---

# 45. Antigravity 不得回归

如果修改共享：

```text
PricingService
UsageRepository
Dashboard
DB schema
```

必须运行所有 Antigravity 测试。

确认：

- quota；
- history；
- API 等值；
- project；
- tray；

不受影响。

---

# 46. 完成后的 Health 信息

Diagnostics 建议显示：

```text
Codex parser version
Last full rebuild
Source files
Logical sessions
Duplicate source groups
Deduplicated token events
Counter resets
Model metadata conflicts
Long-context exact requests
Request-shape uncertain events
Unpriced models/tokens
Official verifier status
Official/local difference
```

---

# 47. 最终 PASS 矩阵

最终逐项给出：

```text
[PASS/FAIL] Global cumulative delta
[PASS/FAIL] Model switch attribution
[PASS/FAIL] Switch-back attribution
[PASS/FAIL] Counter rewind
[PASS/FAIL] Repeated last_token_usage
[PASS/FAIL] Cached double count
[PASS/FAIL] Cache Write handling
[PASS/FAIL] Reasoning output double count
[PASS/FAIL] Long-context request detection
[PASS/FAIL] Long-context pricing
[PASS/FAIL] Exact model pricing
[PASS/FAIL] No broad gpt-5 wildcard
[PASS/FAIL] Exact duplicate sources
[PASS/FAIL] Prefix overlap
[PASS/FAIL] Partial overlap
[PASS/FAIL] Continuation
[PASS/FAIL] Active -> archived move
[PASS/FAIL] Deleted source cleanup
[PASS/FAIL] Full rescan idempotence
[PASS/FAIL] Restart idempotence
[PASS/FAIL] Date/model/project/session totals
[PASS/FAIL] Real short Golden Session
[PASS/FAIL] Real model-switch Golden Session
[PASS/FAIL] 26-path session classification
[PASS/FAIL/N/A] Official account usage cross-check
[PASS/FAIL/N/A] Official thread usage cross-check
[PASS/FAIL] Antigravity regression
```

---

# 48. 最终报告必须回答

1. 模型切换 bug 是否彻底修复？
2. 修复后当前 7 天 Input/Cached/CacheWrite/Output 各是多少？
3. 修复后当前 7 天 **Standard API 等值**是多少？
4. 相比修复前变化多少美元、多少百分比？
5. 26-path 同 session 到底是什么情况？此前是否真实重复？
6. Long Context 加价使当前 7 天成本增加多少？
7. Cache Write 对成本的影响是多少；多少旧日志无法精确分类？
8. 还有多少 token 因未知模型而未定价？
9. 与官方 Codex usage 可比数据的差异是多少？
10. 当前主界面的 API 等值现在可以给出什么可信度评级？

---

# 49. 不接受的“完成”

以下任何一种都不算完成：

- 只把 `Dictionary<Model, Counter>` 改成一个 Counter，但不补测试。
- 修 parser 后继续使用旧 SQLite 错误缓存。
- 直接 `UNIQUE(session_id)` 导致 continuation 丢失。
- 为去重直接停止扫描 `archived_sessions`。
- 把 `last_token_usage` 全部相加。
- 只看 `delta >272K` 就当 long request，而没验证 delta 对应单 request。
- 继续使用 `gpt-5*` 价格 fallback。
- 未知模型偷偷套 Sol/Terra/Luna。
- 官方 usage 再加到 JSONL totals。
- 测试 PASS，却不再跑两个真实 Golden Session。
- 修复后不给出 Before/After 数字。

---

# 50. 推荐实施顺序

```text
Phase 1
Global counter + delta + last_token_usage guard + epoch

Phase 2
Logical session + multi-path event dedupe

Phase 3
Source move/delete lifecycle

Phase 4
CacheWrite + reasoning + request shape

Phase 5
Long-context + pricing schema + exact model matching

Phase 6
DB backup/migration/parser-version invalidation

Phase 7
Full historical rebuild

Phase 8
Golden session verification

Phase 9
Official account/thread usage cross-check

Phase 10
Before/After final report + Antigravity regression
```

---

# 51. 最终原则

修复后，每一个 Codex API 等值必须能追溯：

```text
Raw JSONL
→ Logical Session
→ Deduplicated Token Snapshot
→ Global Cumulative Delta
→ Model / Date / Project Attribution
→ Verified Request Shape
→ Short / Long Context
→ Exact Pricing Rule
→ API Equivalent
```

任何：

```text
未知
缺字段
无法验证
未定价
```

都必须显式标记。

这次目标不是得到一个“好看的美元数字”，而是让这个数字真正可以拿来和 Sub2API、官方 usage、其他账号进行比较。

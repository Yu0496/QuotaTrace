# Codex + Antigravity 轻量用量统计托盘 —— Agent 开发规格 V2

> **目标平台**：Windows 10/11  
> **建议技术栈**：C# / .NET 8 / WinForms  
> **资料与上游源码核验日期**：2026-08-19  
> **面向执行者**：Codex、Gemini Flash 等编码 Agent  
> **目标**：做一个足够轻量、以系统托盘为核心的本地统计器，用于查看 Codex 与 Google Antigravity 的使用量、时间趋势、API 等值成本、模型统计、项目统计和额度状态。  
> **V2 核心要求**：**Antigravity 的“API 等值美元”与 Codex 一样属于正式目标功能，不再是可选探索项。**优先回溯本地历史真实 token；若历史格式不足，则至少通过官方 CLI status-line / transcript 元数据对启用后的使用进行精确增量记录。  
> **原则**：本地优先、尽量离线、统计口径可验证；quota 百分比只用于额度展示，绝不参与美元换算。

---

# V2 相对上一版的关键变更

本版把 Antigravity API 等值从“尽量实现”提升为**正式验收项**。

截至 2026-08-19，Google Antigravity 官方 CLI 文档已经明确公开 status-line JSON schema。该 payload 包含：

```text
cwd
conversation_id
transcript_path
model.id
workspace.current_dir
workspace.project_dir
context_window.total_input_tokens
context_window.total_output_tokens
context_window.current_usage.input_tokens
context_window.current_usage.output_tokens
context_window.current_usage.cache_creation_input_tokens
context_window.current_usage.cache_read_input_tokens
quota
plan_tier
execution_mode
```

官方来源：

```text
https://antigravity.google/docs/cli/statusline
https://antigravity.google/docs/cli/commands/statusline
https://antigravity.google/docs/cli/commands/usage
https://antigravity.google/docs/cli/credits
https://antigravity.google/docs/plans
```

因此 V2 的实现策略是：

```text
Antigravity 历史/当前真实 token
        ↓
按 conversation + model + project 去重/差分
        ↓
provider/model 对应 API 价格
        ↓
API 等值（按当前价格估算）
```

同时保持：

```text
Antigravity quota %
        ↓
只显示剩余额度 / reset
        ↓
绝不转换成 token 或 USD
```

**最低交付线**：

- Codex API 等值必须可回溯。
- Antigravity API 等值必须至少能对“启用统计后的真实使用”进行精确记录。
- 如果本地历史记录中存在可验证 token 字段，则必须继续实现历史回溯。
- 如果 Antigravity Desktop/IDE 的历史格式确实不提供可验证 token，而 CLI status-line 可以精确记录，则 UI 必须显示“精确统计自 YYYY-MM-DD HH:mm 起”，不能伪装成完整历史。
- 若执行 Agent 还没有找到任何可验证的 Antigravity token 来源，则**任务不能以‘功能完成’结案**；只能提交明确 blocker、已验证的数据源和下一步调查结果。


# 0. 给执行 Agent 的硬性工作方式

请直接实现，不要先大规模重构需求，不要擅自扩展成“全平台 AI 用量管理器”。

实施时严格按以下顺序：

1. 先建立最小可运行 WinForms 托盘程序。
2. 先完成 Codex 本地历史统计，并用真实 Codex JSONL 做验证。
3. 再完成统一统计/项目/价格层。
4. 完成 Antigravity 本地 quota，作为独立的额度显示层。
5. **必须完成 Antigravity 精确 token 采集链路**：先查官方 transcript / 本地 brain/conversation 历史，再查 2.x SQLite，最后用官方 CLI status-line 做可靠的前向增量记录。
6. **必须接入 Antigravity API 等值美元计算**；只有真实 token 或经过验证的累计 token 差分才能进入 CostCalculator。
7. 每完成一个阶段先运行测试，再进入下一阶段。
8. 对上游内部接口使用独立 Provider 封装，不把反向工程字段散落在 UI 中。
9. 遇到未知模型、未知 JSON 字段、Antigravity 新版本格式变化时，要“降级显示”，不要猜数据。
10. 禁止为了“看起来功能完整”而用 quota 百分比推算 token 或美元。
11. 最终交付可直接 `dotnet build` / `dotnet publish` 的项目，以及 README。

---

# 1. 产品定位

这是一个**单用户、本机、本地数据优先**的小工具。

核心用途：

- 我今天 / 最近 7 天 / 最近 30 天 / 本月用了多少 Codex。
- 这些 token 如果按照当前配置的官方 API 单价调用，相当于多少钱。
- 哪个模型最贵。
- 哪个项目最耗 token / API 等值成本。
- 每天的消耗趋势是什么。
- Antigravity 当前各模型/模型池还剩多少额度、何时刷新。
- **Antigravity 同样必须显示时间/API 等值/模型/项目统计。**优先回溯本地真实 token；若历史源不足，则从启用官方 status-line 记录器的时间点起提供精确前向统计，并明确覆盖起始时间。
- 程序平时隐藏在系统托盘，不需要一个沉重的常驻 UI。

**不是账单系统。**

所有“API 等值金额”在 UI 中统一叫：

> **API 等值（按当前价格估算）**

不能叫“实际消费”“账单”“花费”，因为 Codex/Antigravity 订阅内使用并没有真的产生这笔 API 账单。

---

# 2. 功能范围

## 2.1 第一优先级：必须完成

### A. Windows 托盘

程序启动后默认隐藏主窗口，仅显示系统托盘图标。

托盘行为：

- 左键：打开/隐藏主面板。
- 右键菜单：
  - 打开仪表盘
  - 立即刷新
  - 开机启动（勾选）
  - 设置
  - 退出
- Tooltip 建议：
  - 有 Codex 精确数据时：`Codex 今日 $4.82 | 7天 $21.37`
  - 有 Antigravity quota 时追加：`AG Gemini 64%`
- 单实例运行；第二次启动时激活已有实例。
- 退出必须真正释放 `NotifyIcon`，不留下幽灵托盘图标。

### B. Codex 本地历史统计

读取 Codex 自己的本地 session JSONL。

至少支持：

- 默认用户目录下 Codex sessions。
- `CODEX_HOME`。
- 设置中手工添加额外 sessions 根目录。
- 不要求程序常驻：几天后重新打开也能补扫历史。
- 增量扫描，不要每次从头解析全部历史。

统计：

- Input tokens
- Cached input tokens
- Output tokens
- 总 tokens（展示口径见后文）
- Session 数
- 按模型
- 按日期
- 按项目
- API 等值成本
- 今日
- 最近 7 天
- 最近 30 天
- 本自然月
- 自定义日期范围

### C. 项目统计

Codex：

- 从 session 元数据中获取工作目录/CWD。
- 将同一工作目录归到同一项目。
- 默认项目名称使用目录最后一级，例如：
  - `D:\Games\CardGame` → `CardGame`
- 设置页允许用户给项目改显示名/别名。
- 原始 canonical path 仍保留，避免同名目录误合并。
- 无法识别的 session 放入“未归类”，不得丢弃。

Antigravity：

- 若本地会话记录能可靠关联 Project/CWD，则使用真实 Project/CWD。
- Antigravity 2.x 的 Project 可能包含多个文件夹，因此 `ProjectKey` 不应只设计成“单一路径”。
- 如果只能知道会话但无法可靠知道项目，则显示“未归类”，不要按时间猜项目。

### D. API 等值价格

价格必须可编辑，不能把所有单价硬编码在业务逻辑里。

支持：

- Provider + 模型匹配
- Input / Cache Read / Cache Write / Output 单价
- 单位固定为 USD / 1M tokens
- 对没有独立 Cache Write 价格的模型允许该字段为空，并按 provider 规则处理
- 当前价格配置更新时间
- 来源 URL
- 设置页编辑
- JSON 导入/导出

统一价格模型至少支持：

```text
Input
CacheRead
CacheWrite
Output
```

Codex 若日志语义为 `InputTokens` 已包含 cached input：

```text
cached = min(cacheRead, input)
nonCachedInput = input - cached

cost =
    nonCachedInput / 1_000_000 * inputPrice
  + cached         / 1_000_000 * cacheReadPrice
  + output         / 1_000_000 * outputPrice
```

Antigravity 官方 status-line 当前还暴露：

```text
cache_creation_input_tokens
cache_read_input_tokens
```

因此对 Claude 等存在“缓存写入价 / 缓存读取价”差异的 API，PricingRule 要允许分别配置：

```text
cacheWritePerMillionUsd
cacheReadPerMillionUsd
```

只有经过验证的 token 分类才能参与这部分计费。

**注意：不要把 cached input 再额外加到 input 上，也不要把 cache creation/read 与 total input 重复计费。**
Codex 的统计语义参考 Win-CodexBar 成熟实现；Antigravity 的语义必须用真实 status-line/transcript 样本验证。

未知模型：

- token 仍正常统计。
- API 等值显示 `—` 或“价格未配置”。
- 不允许默认偷偷套 GPT-4o 或任意其他模型价格。
- 设置页提示用户补充模型价格。

### E. Antigravity quota

至少实现当前本地 quota 查询：

- 自动发现 Antigravity 2.x App / IDE 的 local `language_server`。
- 兼容 `agy` CLI 在本机存在/运行时的本地服务。
- 仅访问 `127.0.0.1`。
- 获取：
  - 计划/套餐（如果本地接口返回）
  - 模型或模型池
  - remaining fraction
  - reset time
  - 5h / weekly 等可用窗口（若 `RetrieveUserQuotaSummary` 返回）
- 保存 quota snapshots，用来画历史变化。
- Antigravity 没运行时：
  - 不报致命错误。
  - 显示“离线”。
  - 显示最后一次成功快照和时间。
- 不要求使用 Google OAuth。
- 第一版不要读浏览器 Cookie。
- 第一版不要读取/保存 Google refresh token。

### F. Antigravity 精确 token + API 等值：必须完成

Antigravity 必须和 Codex 一样提供：

- 今日 / 7 天 / 30 天 / 本月 / 自定义时间
- Input / Cached / Output
- 按模型
- 按项目
- API 等值美元
- 数据覆盖起始时间
- 数据质量标签

**美元只能来自真实 token。**

数据源优先级：

1. 官方 `transcript_path` 指向的 conversation transcript / brain JSONL（若其中含可验证 usage event）。
2. Antigravity 2.x 本地 `brain/`、`conversations/`、SQLite `.db` 等历史数据。
3. 官方 Antigravity CLI status-line JSON 的精确累计/当前 usage 字段，作为前向增量记录器。
4. 其他本机官方/可验证数据结构。
5. quota 只能用于 quota，**永远不能用于 token/API 美元换算**。

官方 status-line 当前明确提供：

```text
conversation_id
transcript_path
model.id
workspace.current_dir
workspace.project_dir

context_window.total_input_tokens
context_window.total_output_tokens

context_window.current_usage.input_tokens
context_window.current_usage.output_tokens
context_window.current_usage.cache_creation_input_tokens
context_window.current_usage.cache_read_input_tokens
```

实现要求：

- 先做真实样本验证，确认 `total_*` 是单调累计还是上下文窗口统计。
- 若是单调累计，按 `conversation_id` 保存上次累计值并做 delta。
- `current_usage` 可用于拆分最新调用的普通 input / cache creation / cache read，但必须通过样本验证其语义。
- status-line 会在 agent state 变化时重复调用，必须去重，不能每次收到相同 totals 都记一笔。
- 模型切换时必须按模型分别归属。
- `workspace.project_dir` / `cwd` 用于项目归类。
- `transcript_path` 必须加入历史回溯 discovery；如果 transcript 本身包含每次 API usage，应优先解析 transcript，而不是依赖工具常驻。

**完成标准：**

- 若本地历史能回溯：完整补算已有 Antigravity 历史。
- 若只能前向记录：从用户启用 recorder 的时间开始精确统计，并在 UI 显示起始时间。
- 如果仅完成 quota、没有任何真实 token → USD 链路，则本功能不算完成。

---

# 3. 明确不做的内容

为了保持轻量，第一版不要做：

- Electron。
- WebView 前端框架。
- 多账户 OAuth 管理。
- 云同步。
- 登录系统。
- 遥测/Analytics。
- 自动上传日志。
- 浏览器 Cookie 抓取。
- Telegram/Discord 通知。
- 复杂动画。
- 插件市场。
- 多平台兼容。
- 自动抓网页更新价格。
- 远程 Antigravity Cloud Code API。
- 通过代理拦截 Codex / Antigravity 请求。
- 将用户 prompt/response 正文保存到本工具数据库。
- 从 quota 百分比反推 token 数。

---

# 4. 技术选型

## 4.1 主程序

使用：

```text
C#
.NET 8
WinForms
```

理由：

- `NotifyIcon` 原生可用。
- Windows 专用，无需跨平台 UI 框架。
- 比 Electron/Tauri+Web 前端更简单。
- 用户主要需求是托盘、表格、少量图表。
- 可发布成单独 Windows 小程序。
- 与 Unity/C# 开发习惯一致。

## 4.2 依赖控制

允许的主要第三方 NuGet：

```text
Microsoft.Data.Sqlite
System.Management   // 仅在需要用 WMI 查进程命令行时
```

尽量不要再加重量级依赖。

图表：

- 不引入完整图表框架。
- 自己写一个 `SparklineControl` / `DailyBarChartControl`。
- 使用 WinForms `OnPaint` + GDI+ 即可。
- 只需要简单折线/柱状图。

JSON：

```text
System.Text.Json
```

HTTP：

```text
HttpClient
```

---

# 5. 推荐解决方案结构

```text
UsageTray/
├─ UsageTray.sln
├─ src/
│  └─ UsageTray/
│     ├─ UsageTray.csproj
│     ├─ Program.cs
│     │
│     ├─ App/
│     │  ├─ AppPaths.cs
│     │  ├─ SingleInstance.cs
│     │  ├─ StartupManager.cs
│     │  └─ AppSettings.cs
│     │
│     ├─ Core/
│     │  ├─ ProviderKind.cs
│     │  ├─ DataQuality.cs
│     │  ├─ TokenUsage.cs
│     │  ├─ UsageBucket.cs
│     │  ├─ QuotaSnapshot.cs
│     │  ├─ ProjectInfo.cs
│     │  ├─ PricingRule.cs
│     │  └─ DateRange.cs
│     │
│     ├─ Providers/
│     │  ├─ IUsageProvider.cs
│     │  ├─ Codex/
│     │  │  ├─ CodexProvider.cs
│     │  │  ├─ CodexSessionLocator.cs
│     │  │  ├─ CodexJsonlParser.cs
│     │  │  ├─ CodexProjectResolver.cs
│     │  │  └─ CodexModels.cs
│     │  │
│     │  └─ Antigravity/
│     │     ├─ AntigravityProvider.cs
│     │     ├─ AntigravityProcessDiscovery.cs
│     │     ├─ AntigravityPortDiscovery.cs
│     │     ├─ AntigravityLocalApi.cs
│     │     ├─ AntigravityQuotaParser.cs
│     │     ├─ AntigravityHistoryLocator.cs
│     │     ├─ AntigravityHistoryParser.cs
│     │     └─ AntigravityModels.cs
│     │
│     ├─ Data/
│     │  ├─ UsageDatabase.cs
│     │  ├─ DatabaseMigrations.cs
│     │  └─ UsageRepository.cs
│     │
│     ├─ Pricing/
│     │  ├─ PricingService.cs
│     │  ├─ PricingMatcher.cs
│     │  └─ default-pricing.json
│     │
│     ├─ Services/
│     │  ├─ RefreshCoordinator.cs
│     │  ├─ UsageAggregator.cs
│     │  ├─ ProjectService.cs
│     │  └─ DiagnosticsService.cs
│     │
│     └─ UI/
│        ├─ TrayApplicationContext.cs
│        ├─ MainForm.cs
│        ├─ MainForm.Designer.cs
│        ├─ SettingsForm.cs
│        ├─ SettingsForm.Designer.cs
│        ├─ Controls/
│        │  ├─ MetricCard.cs
│        │  ├─ DailyBarChartControl.cs
│        │  └─ QuotaBarControl.cs
│        └─ ViewModels/
│           └─ DashboardSnapshot.cs
│
├─ tests/
│  └─ UsageTray.Tests/
│     ├─ CodexJsonlParserTests.cs
│     ├─ PricingServiceTests.cs
│     ├─ UsageAggregatorTests.cs
│     ├─ ProjectResolverTests.cs
│     ├─ AntigravityQuotaParserTests.cs
│     └─ Fixtures/
│
├─ LICENSE
└─ README.md
```

不要为了“架构漂亮”再拆十几个 class library。

---

# 6. 统一数据模型

## 6.1 DataQuality

```csharp
public enum DataQuality
{
    Exact,          // 原始日志/官方本地元数据直接提供 token
    Derived,        // 从精确累计值做差分得到
    QuotaOnly,      // 只有额度百分比/重置时间
    Unavailable
}
```

任何 UI 统计都要知道数据质量。

---

## 6.2 TokenUsage

建议：

```csharp
public sealed record TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens
);
```

显示总 token 时：

```text
DisplayedTotalTokens = InputTokens + OutputTokens
```

因为 cached input 已包含在 input 的计费语义里时，不应再次相加。

如果某个 Provider 的原始语义不同，必须在 Provider 内归一化后再写入统一模型。

---

## 6.3 UsageBucket

建议按“来源文件 + 本地日期 + 项目 + 模型”聚合，而不是把所有原始 prompt 保存进数据库：

```csharp
public sealed record UsageBucket(
    ProviderKind Provider,
    DateOnly LocalDate,
    string? ProjectKey,
    string? ModelId,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    int RequestCount,
    DataQuality Quality
);
```

---

## 6.4 QuotaSnapshot

```csharp
public sealed record QuotaSnapshot(
    ProviderKind Provider,
    DateTimeOffset CapturedAt,
    string ModelOrPoolId,
    string DisplayLabel,
    double? RemainingFraction,
    DateTimeOffset? ResetAt,
    string WindowKind,       // session / 5h / weekly / unknown
    string Source            // antigravity-app / agy / ide
);
```

`RemainingFraction` 合法范围必须验证为 `0..1`。

---

# 7. SQLite 数据设计

数据库位置：

```text
%LOCALAPPDATA%\UsageTray\usage.db
```

设置：

```text
%LOCALAPPDATA%\UsageTray\settings.json
```

价格：

```text
%LOCALAPPDATA%\UsageTray\pricing.json
```

日志：

```text
%LOCALAPPDATA%\UsageTray\logs\
```

日志默认只保存程序运行信息和异常，不写 prompt/response 正文、不写 OAuth token/CSRF token。

---

## 7.1 source_files

```sql
CREATE TABLE source_files (
    provider            TEXT NOT NULL,
    path                TEXT NOT NULL,
    file_size           INTEGER NOT NULL,
    mtime_utc_ticks     INTEGER NOT NULL,
    parsed_bytes        INTEGER NOT NULL DEFAULT 0,
    parser_version      INTEGER NOT NULL DEFAULT 1,
    session_id          TEXT NULL,
    project_key         TEXT NULL,
    last_model          TEXT NULL,
    parser_state_json   TEXT NULL,
    last_error          TEXT NULL,
    PRIMARY KEY(provider, path)
);
```

---

## 7.2 file_usage

不要只把汇总存在内存。

每个来源文件保存自己的贡献：

```sql
CREATE TABLE file_usage (
    provider             TEXT NOT NULL,
    source_path          TEXT NOT NULL,
    local_date           TEXT NOT NULL,
    project_key          TEXT NOT NULL DEFAULT '',
    model_id             TEXT NOT NULL DEFAULT '',
    input_tokens         INTEGER NOT NULL,
    cached_input_tokens  INTEGER NOT NULL,
    output_tokens        INTEGER NOT NULL,
    request_count        INTEGER NOT NULL DEFAULT 0,
    data_quality         INTEGER NOT NULL,
    PRIMARY KEY(
        provider,
        source_path,
        local_date,
        project_key,
        model_id
    )
);
```

这样如果源文件被修改：

1. 在事务内删掉该文件旧 `file_usage`。
2. 重新解析。
3. 插入新结果。
4. 更新 `source_files`。

不会出现重复累计。

---

## 7.3 quota_snapshots

```sql
CREATE TABLE quota_snapshots (
    provider             TEXT NOT NULL,
    captured_at_utc      TEXT NOT NULL,
    model_or_pool_id     TEXT NOT NULL,
    label                TEXT NOT NULL,
    remaining_fraction   REAL NULL,
    reset_at_utc         TEXT NULL,
    window_kind          TEXT NOT NULL,
    source               TEXT NOT NULL
);
```

为了轻量：

- Antigravity quota 每 1~2 分钟刷新时，不必每次都写数据库。
- 建议最多每 5 分钟落一次样本。
- 如果 quota 数值或 reset 时间发生明显变化，可立即额外落一次。
- 默认保留 90 天。
- 90 天以前可按“每天最后一条”压缩。

---

## 7.4 project_aliases

```sql
CREATE TABLE project_aliases (
    provider       TEXT NOT NULL,
    project_key    TEXT NOT NULL,
    display_name   TEXT NOT NULL,
    PRIMARY KEY(provider, project_key)
);
```

---

# 8. Codex 数据源实现

## 8.1 主要借鉴项目

### Win-CodexBar

仓库：

```text
https://github.com/nesszer/Win-CodexBar
```

重点文件：

```text
rust/src/cost_scanner.rs
rust/src/codex_costs.rs
```

重点借鉴：

### `cost_scanner.rs`

借鉴其：

- Codex sessions 目录发现。
- 日期目录扫描。
- JSONL token event 解析。
- UTC timestamp → 本地日期处理。
- 对日期扫描范围额外前后 padding 一天。
- 文件 `mtime + size + parsed_bytes` 缓存。
- 未变化文件跳过。
- 增长中的 JSONL 从上次 offset 续扫。
- 保存 `last_model` / `last_totals` 以便增量解析。
- 避免重复统计累计 token。
- 模型维度 token 聚合。
- 扫描取消/异常容错思想。

**不要只写一个 `foreach line => sum(input_tokens)`。**

当前 Codex 日志中可能出现累计 token 报告。正确做法是参考上游成熟 parser 的“累计值 → delta”策略，否则极易重复统计。

### `codex_costs.rs`

借鉴：

- 模型规范化。
- cached token 计费处理。
- 按模型统计。
- Standard/Fast 模式分类思路。
- pricing completeness / unknown model 思路。
- 单元测试方法。

但本项目对“未知价格”采取更保守策略：

> 上游某些路径为了兼容会 fallback 到其他模型价格；本项目不要 fallback。未知模型直接 Cost=N/A。

---

## 8.2 Codex sessions 根目录

至少依次检查：

1. `CODEX_HOME` 下的 sessions。
2. 用户 home 默认 Codex sessions。
3. 用户手工配置目录。

不要把默认路径只写死成一个字符串。

实现：

```text
CodexSessionLocator.GetCandidateRoots()
```

去重：

- 使用 canonical full path。
- Windows path comparer 用 `OrdinalIgnoreCase`。

---

## 8.3 日期目录

当前 Codex sessions 通常按：

```text
YYYY/MM/DD/*.jsonl
```

组织。

扫描“本地日期范围”时，因为 JSONL timestamp 是 UTC，而用户按本地日历看统计：

- 范围两端至少多扫一天。
- 真正归属日期必须按 event timestamp 转换到 `TimeZoneInfo.Local` 后决定。

不要仅凭文件夹日期归属。

---

## 8.4 增量扫描

第一次：

```text
offset = 0
full parse
```

文件未变化：

```text
mtime 相同
size 相同
parsed_bytes >= size
=> 完全跳过
```

文件增长：

```text
size > oldSize
parsed_bytes > 0
offset 位于完整行边界
保存有 parser state
=> 从 parsed_bytes 继续
```

文件缩短/被重写/offset 不安全：

```text
=> 删除该文件旧缓存并从 0 重扫
```

### 行边界

保存 offset 时必须位于完整 JSONL 行末。

如果程序上次在半行中断：

- 回退到上一个 `\n`。
- 不解析半条 JSON。

---

## 8.5 JSON 容错

不要为整份 JSONL 建一个刚性的完整 DTO。

推荐：

```csharp
using JsonDocument
```

按需要读取：

- `type`
- `timestamp`
- model
- token count fields
- session metadata
- cwd/project-related metadata

未知字段忽略。

单行损坏：

- 记录 debug warning。
- 跳过该行。
- 不让整份 session 报废。

---

# 9. Codex 项目识别

这是本项目相对 CodexBar 需要额外做好的部分。

## 9.1 识别规则

Agent 在真实本机 JSONL 上先做一次 schema discovery：

1. 读取 3~5 个最近 Codex session。
2. 搜索下列语义字段：
   - `cwd`
   - `working_directory`
   - `workspace`
   - `project`
   - `repo`
   - session metadata 中等价字段
3. 输出 debug 级 schema report，但不要输出 prompt 正文。
4. 找到稳定字段后实现 parser。

优先：

```text
session 开始时的真实 CWD
```

作为 `ProjectKey`。

如果 session 中 CWD 后续发生变化：

- MVP 使用 session 第一条有效 CWD。
- 不要把同一 session 强拆成多个项目。

---

## 9.2 ProjectKey

不要直接用显示名当 key。

例如：

```text
ProjectKey = NormalizeFullPath(cwd)
DisplayName = DirectoryInfo(ProjectKey).Name
```

如果未来要支持 Git：

可加可选增强：

```text
若 cwd 在 Git repo 内，可以把 Git root 作为 project root
```

但这不是第一版阻塞项。

---

## 9.3 用户自定义别名

例如：

```text
D:\Unity\CardGame    => 把牌打空！
D:\Work\Site         => 官网
```

统计仍绑定 canonical path。

---

# 10. Pricing 系统

## 10.1 default-pricing.json 示例结构

```json
{
  "schemaVersion": 1,
  "lastVerifiedAt": "2026-08-19",
  "rules": [
    {
      "provider": "Codex",
      "modelPattern": "gpt-example",
      "matchMode": "Exact",
      "inputPerMillionUsd": 0,
      "cacheReadPerMillionUsd": 0,
      "cacheWritePerMillionUsd": null,
      "outputPerMillionUsd": 0,
      "sourceUrl": "https://openai.com/api/pricing/"
    }
  ]
}
```

上面只是结构示例，不要把 `0` 当真实价格。

执行 Agent 在实现时应根据官方页面重新核对默认价格，并写入实际值。

官方优先：

```text
OpenAI:
https://openai.com/api/pricing/

Google Gemini API:
https://ai.google.dev/gemini-api/docs/pricing

Anthropic:
https://docs.anthropic.com/en/docs/about-claude/pricing
```

---

## 10.2 匹配优先级

建议：

```text
Exact
Prefix
Contains
```

优先级：

```text
Exact > longest Prefix > longest Contains
```

不要使用一个过于宽泛的规则先吃掉所有新模型。

---

## 10.3 价格修改

设置页：

```text
模型
Input $/1M
Cached $/1M
Output $/1M
来源
```

按钮：

- 保存
- 恢复内置默认
- 导入 JSON
- 导出 JSON

价格变化后：

- 不需要重扫日志。
- 直接基于已经保存的 token 重新计算 UI。
- UI 明示“按当前价格估算”。

这样不会把历史统计绑死在旧价格上。

---

# 11. Antigravity：必须理解的数据源差异

Antigravity 与 Codex 不同。

Codex 有相对成熟的本地 JSONL token 历史。

Antigravity 当前至少存在三个不同概念：

1. **Quota**：剩余百分比、5h/weekly、reset。
2. **Conversation/Project 本地历史**。
3. **真实 token usage**。

三者不要混在一起。

---

# 12. Antigravity quota —— 主要借鉴 Win-CodexBar

## 12.1 上游文件

```text
https://github.com/nesszer/Win-CodexBar/blob/main/rust/src/providers/antigravity/mod.rs
https://github.com/nesszer/Win-CodexBar/blob/main/rust/src/providers/antigravity/tests.rs
```

同时参考原始 CodexBar 文档：

```text
https://github.com/steipete/CodexBar/blob/main/docs/antigravity.md
```

---

## 12.2 当前推荐的来源优先级

本项目第一版：

```text
Antigravity 2.x App local language_server
        ↓
agy CLI local server（若已经运行）
        ↓
Antigravity IDE local language_server
        ↓
无
```

不做远程 OAuth fallback。

原因：

- 用户只需要本机小工具。
- local source 足够轻。
- 避免保存 Google 凭据。
- 安全边界清晰。

---

## 12.3 进程发现

不要只按固定 exe 完整路径寻找。

Antigravity 版本升级、x64/ARM64、App/IDE/CLI 可能不同。

Windows 使用 WMI 查询进程：

```text
Win32_Process
ProcessId
Name
ExecutablePath
CommandLine
```

查找符合 Antigravity `language_server` 特征的进程。

从 CommandLine 解析：

```text
--csrf_token
--extension_server_csrf_token
--extension_server_port
--https_server_port
```

两种写法都支持：

```text
--flag value
--flag=value
```

App/IDE：

- 通常存在 CSRF token。
- 请求本地接口时需要 header。

`agy`：

- 同类 local server 可不需要 CSRF。
- 不要强行要求 token。

**CSRF token 只保存在当前刷新调用的内存中。**
不要写数据库、日志、settings。

---

# 13. Antigravity 实际 API 端口发现

这是非常重要的坑。

**不要假设 `--extension_server_port` 就是实际 quota API 端口。**

当前上游已经专门修过这个问题：

- language server 会绑定随机 localhost 端口。
- `extension_server_port` 可能属于另一服务。
- 真实 Connect/gRPC HTTP API 端口应当通过进程实际 listen ports + endpoint probe 找。

推荐：

### 方案 A：优先直接枚举 PID 的 TCP listen ports

如果 Agent 能可靠实现 Windows `GetExtendedTcpTable` P/Invoke，优先用它。

输入：

```text
PID
```

输出：

```text
该 PID 正在 LISTEN 的本地 TCP ports
```

这是最轻量的长期方案。

### 方案 B：MVP fallback

若 P/Invoke 实现风险过高，可在**刷新时单次**调用 PowerShell：

```powershell
Get-NetTCPConnection -OwningProcess <PID> -State Listen
```

解析 LocalPort。

不要常驻 PowerShell，不要每秒调用。

---

## 13.1 API 探针

对候选端口请求：

```text
https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUnleashData
```

可将：

```text
HTTP 200
HTTP 401
```

都视为“这个端口像正确的 language server API”。

真正 fetch 时再带正确 CSRF。

---

## 13.2 TLS

Antigravity 本地 language server 使用 self-signed certificate。

允许：

```text
仅对 127.0.0.1
跳过 certificate validation
```

不允许写一个全局：

```csharp
return true;
```

然后拿这个 HttpClient 去访问互联网。

实现单独的：

```text
AntigravityLoopbackHttpClient
```

要求：

- Base host 固定 `127.0.0.1`
- 不接受用户输入 hostname
- 禁止 redirect
- 2~8 秒 timeout
- invalid cert bypass 仅此 client

---

# 14. Antigravity quota endpoint

优先尝试当前更丰富的：

```text
RetrieveUserQuotaSummary
```

路径：

```text
https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary
```

若失败再 fallback：

```text
GetUserStatus
GetCommandModelConfigs
```

其中已验证的 `GetUserStatus`：

```text
POST
https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus
```

Headers：

```text
Content-Type: application/json
Connect-Protocol-Version: 1
```

App/IDE 如需要：

```text
X-Codeium-Csrf-Token: <token>
```

Body metadata 可参考上游：

```json
{
  "metadata": {
    "ideName": "antigravity",
    "extensionName": "antigravity",
    "ideVersion": "unknown",
    "locale": "en"
  }
}
```

---

# 15. Antigravity quota 解析

`GetUserStatus` 当前重要字段语义：

```text
userStatus
planStatus.planInfo
cascadeModelConfigData.clientModelConfigs[]
```

每个 model config 关注：

```text
label
modelId / id
quotaInfo.remainingFraction
quotaInfo.resetTime
```

不要依赖数组位置，例如：

```text
第一个 = Claude
第二个 = Gemini
```

这种写法已有实际误映射案例。

必须依据：

```text
label
modelId
```

分类。

最简单规则：

```text
label 包含 claude                => Claude
label 包含 gemini + pro          => Gemini Pro
label 包含 gemini + flash        => Gemini Flash
其他                             => Other
```

但 UI 的底层数据要保留原 model id / label，不要只保存分组名。

对于 `RetrieveUserQuotaSummary`：

优先保留服务端直接给出的 quota pool/group，不要把所有 raw model 强行重新构造。

---

# 16. Antigravity quota 历史

与 Codex 不同：

- quota endpoint 只告诉“现在”。
- 关闭本工具期间通常无法补回每一分钟历史。

因此：

```text
Codex 历史：可补扫
Antigravity quota 历史：从本工具第一次记录后开始
```

UI 要允许显示：

> 统计自 2026-xx-xx 开始

不要假装有安装前历史。

---

# 17. Antigravity 精确 token 历史 —— 必须优先调查

这里不是可选研究项，而是 API 等值功能的数据基础。

## 17.1 第一优先：官方 transcript / brain 日志

官方 CLI status-line payload 当前给出：

```text
transcript_path
```

官方示例路径形态：

```text
~/.gemini/antigravity/brain/<conversation_id>/.system_generated/logs/transcript.jsonl
```

执行 Agent 应先检查：

```text
~/.gemini/antigravity/brain/
~/.gemini/antigravity/conversations/
```

以及 status-line 实际返回的 `transcript_path`。

目标：

- 判断 transcript 是否包含每次模型调用的 usage 元数据。
- 若存在：
  - timestamp
  - model
  - input/output
  - cache read/write
  - conversation id
  - project/cwd
  则优先实现 transcript parser。
- transcript 是 JSONL 时，沿用 Codex 的：
  - 增量 offset
  - size/mtime
  - 单文件 contribution replacement
  - 损坏行容错
  - 不保存正文
  的扫描框架。

如果 transcript usage 足够完整，这是比反向解析 SQLite 更优先的数据源。

---

## 17.2 第二优先：Antigravity 2.x 本地 conversation SQLite / DB

重点参考：

```text
https://github.com/unchase/antigravity-storage-manager
```

已知该项目支持 Antigravity 2.0 后的 SQLite `.db` 会话格式，并公开说明其本地目录包括：

```text
~/.gemini/antigravity/brain/
~/.gemini/antigravity/conversations/
```

它还实现了模型 quota/request/token 相关统计，可用于定位 Antigravity 的本地数据结构。

重点搜索源码关键词：

```text
token
usage
input
output
cached
cache
thinking
conversation
SQLite
.db
model
modelId
requestCount
brain
transcript
```

只借本地 parser/statistics 思路，不引入：

- Drive Sync
- OAuth
- Proxy
- Telegram
- profile switching

### SQLite discovery

只读打开：

```text
Mode=ReadOnly
```

先：

```sql
SELECT name, type
FROM sqlite_master
WHERE type IN ('table', 'view');
```

再：

```text
PRAGMA table_info(...)
```

寻找 usage/model/timestamp/project。

若关键数据是 blob/protobuf：

- 参考上游 parser。
- 必须有真实 fixture。
- 不允许仅靠字符串 grep 后猜数字。

---

## 17.3 Discovery 输出

`DiagnosticsService.DiscoverAntigravityHistory()` 至少输出：

```text
Brain root: ...
Conversation root: ...
Transcript files: ...
DB files: ...
PB files: ...

Usage events detected: YES/NO
Input tokens: YES/NO
Output tokens: YES/NO
Cache read: YES/NO
Cache write: YES/NO
Model: YES/NO
Timestamp: YES/NO
Conversation id: YES/NO
Project/CWD: YES/NO

Selected history source:
Transcript / SQLite / PB / None
```

不输出 prompt/response 正文。

---

# 18. Antigravity CLI 官方 status-line —— 必须实现的可靠前向来源

官方文档：

```text
https://antigravity.google/docs/cli/statusline
https://antigravity.google/docs/cli/commands/statusline
```

截至 2026-08-19，官方 schema 明确包含：

```text
cwd
session_id
conversation_id
transcript_path
model.id
model.display_name
workspace.current_dir
workspace.project_dir
version

context_window.total_input_tokens
context_window.total_output_tokens
context_window.context_window_size
context_window.used_percentage
context_window.remaining_percentage

context_window.current_usage.input_tokens
context_window.current_usage.output_tokens
context_window.current_usage.cache_creation_input_tokens
context_window.current_usage.cache_read_input_tokens

quota
agent_state
plan_tier
execution_mode
```

官方说明 status-line 在 agent state 变化时执行自定义命令，并把 JSON payload 通过 stdin 传入脚本。

因此本项目必须提供：

```text
AntigravityStatusRecorder
```

作为**前向精确统计 fallback**。

## 18.1 配置方式

设置页：

```text
Antigravity CLI 精确统计

状态：未配置 / 已配置 / 正常记录 / 错误

[安装统计桥接]
[复制手工配置命令]
[测试]
[卸载桥接]
```

优先使用官方命令：

```text
/statusline <本项目生成的 recorder 命令>
```

或者生成用户可复制的 `settings.json` 片段。

不得静默覆盖用户已有 statusLine 配置。

如果已有自定义 status line：

- 默认不覆盖。
- 提供 wrapper/chain 方案：
  - recorder 先记录 JSON。
  - 再调用用户原来的 status-line 命令。
  - 将原命令 stdout 原样返回给 Antigravity。
- 无法安全 chain 时提示用户选择，不擅自替换。

---

## 18.2 Recorder 输入处理

每次 stdin 收到一个 JSON payload：

提取：

```text
Timestamp = 本机接收时间
ConversationId
TranscriptPath
ModelId
ModelDisplayName
Cwd
ProjectDir

TotalInput
TotalOutput

CurrentInput
CurrentOutput
CacheCreationInput
CacheReadInput

AgentState
ExecutionMode
```

不要保存：

```text
email
prompt
response
tool output
```

---

## 18.3 去重与 delta

status-line 可能在一次 API 调用期间被执行多次。

按：

```text
conversation_id
```

保存 last state：

```text
lastTotalInput
lastTotalOutput
lastModel
lastPayloadFingerprint
lastRecordedAt
```

基本规则：

```text
若 totalInput / totalOutput 均未变化：
    不记 usage

若 totals 增加：
    deltaInput  = newTotalInput  - oldTotalInput
    deltaOutput = newTotalOutput - oldTotalOutput
```

如果 totals 减小：

可能发生：

- rewind
- conversation compaction
- CLI schema 变化
- reset

此时：

- 不允许生成负 usage。
- 新建 `CounterEpoch`。
- 从新基线继续。
- Diagnostics 记录一次 counter reset。

---

## 18.4 Cache 拆分验证

不能看到：

```text
current_usage.cache_read_input_tokens
```

就直接当作本次账单。

执行 Agent必须做真实实验：

### Test A：首次简单请求

记录：

```text
old totals
new totals
current_usage
```

### Test B：在同一 conversation 继续一轮

再次记录。

验证：

```text
deltaTotalInput
deltaTotalOutput
current_usage.input_tokens
current_usage.output_tokens
cache_creation_input_tokens
cache_read_input_tokens
```

目标是确认：

- `total_*` 是否单调累计。
- `current_usage` 是最近一次 API call、当前 context，还是其他口径。
- cache read/write 是否包含于 input。
- model switch 后 totals 的语义。

只有验证后才能形成 `AntigravityUsageNormalizer`。

如果 cache 拆分暂时无法可靠确认：

- Input/Output 总量仍可统计。
- API 等值如果该模型的 cached 与 normal 价格不同，则标记 `CostQuality=Incomplete`，**不要用普通 input 价格偷算成“精确美元”**。

---

# 19. Antigravity API 等值 —— 正式功能

## 19.1 两个不同数字必须分离

UI 中永久区分：

```text
Quota / 剩余额度
```

和：

```text
API 等值（按当前价格估算）
```

前者来自：

```text
remaining_fraction
reset_time
requests/tokens remaining
```

后者来自：

```text
真实 usage token × 对应 API price
```

禁止：

```text
64% quota = $64
50% weekly = 已使用某固定美元
quota delta × 某经验系数
```

---

## 19.2 模型 → API 价格来源

Antigravity 可使用不同模型族，因此价格表必须按**实际模型提供方**映射。

官方价格来源优先：

```text
Google Gemini API:
https://ai.google.dev/gemini-api/docs/pricing

Anthropic Claude:
https://docs.anthropic.com/en/docs/about-claude/pricing

OpenAI:
https://openai.com/api/pricing/
```

如果 Antigravity 某模型没有公开、直接可比的 API SKU：

```text
API 等值 = N/A
价格状态 = 无可比公开 API 价格
```

不得拿“名字最像”的模型价格硬套。

---

## 19.3 PricingRule

建议扩展：

```csharp
public sealed record PricingRule(
    string Provider,
    string ModelPattern,
    MatchMode MatchMode,
    decimal InputPerMillionUsd,
    decimal? CacheReadPerMillionUsd,
    decimal? CacheWritePerMillionUsd,
    decimal OutputPerMillionUsd,
    string SourceUrl,
    DateOnly LastVerifiedAt
);
```

如果不同 context 长度有价格阶梯：

MVP 可以加入：

```text
PricingTier
MinInputTokens
MaxInputTokens
```

如果暂时不做阶梯：

- 对会触发价格阶梯的模型显示 warning。
- 不声称金额“精确到 API 账单”。

---

## 19.4 Cost 计算

归一化后的 usage：

```text
TotalInput
CacheReadInput
CacheWriteInput
Output
```

定义：

```text
normalInput =
    TotalInput
    - CacheReadInput
    - CacheWriteInput
```

只有在 Provider schema 已确认 `TotalInput` 包含 cache 分类时才这样减。

然后：

```text
cost =
    normalInput     * inputPrice
  + cacheReadInput  * cacheReadPrice
  + cacheWriteInput * cacheWritePrice
  + output          * outputPrice
```

全部除以：

```text
1_000_000
```

如果 provider 的 cache write 仍按普通 input 计费：

```text
cacheWritePrice = inputPrice
```

如果某 provider 的原始 token 语义不同：

在 Provider 内先 normalization，不要修改统一 UI 口径。

---

## 19.5 API 等值的数据质量

增加：

```csharp
public enum CostQuality
{
    ExactTokenSplit,      // 输入/缓存/输出拆分都已验证
    ExactTokensNoCache,   // 模型不需要 cache 差价或明确没有 cache
    PartialPrice,         // token 精确，但价格结构/阶梯无法完整映射
    Unavailable
}
```

UI：

```text
$31.42   精确 token / 当前 API 价格
```

或：

```text
$—       缓存计费拆分尚未验证
```

不使用 `$0.00` 表示未知。

---

# 20. Antigravity AI Credits 与 API 等值的关系

Antigravity 官方 Plans/CLI 文档说明：

- baseline quota 是订阅计划额度。
- AI Credits 用于 baseline quota 之外的 overage。
- overage credit consumption 按对应 consumption pricing 处理。
- CLI `/credits` 可以查看余额和 consumption history。

官方：

```text
https://antigravity.google/docs/plans
https://antigravity.google/docs/cli/credits
```

本项目第一版可以把 AI Credits 作为**可选附加显示**，但不要把它和 API 等值混为同一个数字。

推荐 UI：

```text
Antigravity

API 等值（按公开 API 价格）   $38.71
Baseline quota               64% 剩余
AI Credits                   42 remaining   // 若能可靠取得
```

规则：

- API 等值用于回答“这些真实 token 若按 API 调用，大约值多少钱”。
- AI Credits 用于回答“Antigravity overage credit 账户还剩多少”。
- 未确认“1 credit = 固定 USD”时，不把 credit 余额直接转换为美元。
- baseline quota 百分比永不换美元。

---

# 21. Antigravity 项目识别

Antigravity 2.x 的 Project 可能由一个或多个 folders 组成。

因此建议：

```csharp
ProjectKey = stable project id if available
```

如果本地数据没有稳定 project id：

```text
Canonical sorted folder list
=> SHA256
=> ProjectKey
```

显示名：

```text
官方 Project name（优先）
否则单文件夹用 folder name
否则“FolderA + FolderB”
```

如果当前 conversation 只知道 CWD：

```text
先按 CWD 项目归类
DataQuality 仍可为 Exact
```

不要根据“哪个项目窗口刚才在前台”推断费用归属。

---

# 22. 统一 Provider 接口

不要让 UI 知道 Codex JSONL 或 Antigravity LSP 细节。

建议：

```csharp
public interface IUsageProvider
{
    ProviderKind Kind { get; }

    Task<ProviderAvailability> DetectAsync(
        CancellationToken cancellationToken);

    Task<ProviderRefreshResult> RefreshAsync(
        RefreshContext context,
        CancellationToken cancellationToken);
}
```

返回：

```csharp
public sealed record ProviderRefreshResult(
    IReadOnlyList<UsageBucket> Usage,
    IReadOnlyList<QuotaSnapshot> Quotas,
    IReadOnlyList<string> Warnings,
    DateTimeOffset RefreshedAt
);
```

Codex：

```text
Usage = 有
Quota = 第一版可无
```

Antigravity：

```text
Usage = 能验证真实 token 时有
Quota = 有
```

---

# 23. 刷新策略

## 23.1 程序启动

启动后：

1. 立即显示托盘。
2. 后台读取 SQLite 旧统计，UI 可立即打开。
3. 后台扫描 Codex 新增 JSONL。
4. 检测 Antigravity 是否运行。
5. 若运行，取 quota。
6. 若发现 Antigravity 本地历史，扫描新增历史。
7. 更新 UI。

不要让启动窗口卡住等扫描。

---

## 23.2 Codex

程序运行时：

方案：

```text
FileSystemWatcher + debounce 5 秒
```

监控 sessions 根目录。

或者：

```text
每 5 分钟轻量刷新
```

二者选一即可。

推荐：

- FileSystemWatcher 负责即时。
- 每 10 分钟做一次低频校验，防止 watcher 丢事件。

---

## 23.3 Antigravity quota

仅当检测到 Antigravity/agy process 时：

```text
默认 90 秒刷新一次
```

设置允许：

```text
60 / 90 / 120 / 300 秒
```

App 不运行：

```text
10 分钟后再检测
```

或仅在用户打开面板/手动刷新时检测。

不要每秒 probe ports。

---

# 24. 主界面设计

建议尺寸：

```text
760 x 560 左右
```

不要做复杂 Dashboard。

---

## 24.1 顶部

Provider 过滤：

```text
[全部] [Codex] [Antigravity]
```

日期：

```text
[今天] [7天] [30天] [本月] [自定义]
```

右侧：

```text
最后刷新 03:41    [刷新]
```

---

## 24.2 第一行 Metric Cards

“全部”页建议：

```text
API 等值
$ 31.42
按当前价格估算

Input
12.6 M

Cached
8.3 M

Output
1.8 M
```

若 Antigravity 的历史回溯暂时缺失，但 status-line recorder 已启用：

```text
API 等值
$12.43
精确统计自 2026-08-19 13:30 起
```

若 recorder 尚未启用、当前只有 quota：

```text
API 等值
—
尚未建立真实 token 采集
```

此状态只允许出现在配置/诊断阶段，**不属于 V2 最终验收通过状态**。绝不能显示 `$0.00`，因为 `$0.00` 会误导成“真实为零”。

---

## 24.3 趋势图

一个简单图即可：

```text
每日 API 等值
```

X：

```text
日期
```

Y：

```text
USD
```

Tooltip：

```text
8/18
Codex     $7.20
AG        $2.13  // 仅真实 token 可用时
合计      $9.33
```

如果 AG 只有 quota：

不画成美元曲线。

Antigravity quota 可在下面单独画小 sparkline。

---

## 24.4 模型表

```text
模型                 Input    Cached   Output    API 等值
GPT-5.6 Sol          4.2M     2.8M     0.6M      $...
Gemini ...           ...
```

未知价格：

```text
$ —
```

并显示小提示：

```text
未配置价格
```

---

## 24.5 项目表

```text
项目                  Provider      Tokens       API 等值
把牌打空！            Codex         ...          ...
孔中窥日              Codex         ...          ...
某项目                 Antigravity   ...          ...
未归类                 ...           ...          ...
```

支持点击项目查看：

- 按日期
- 按模型

不用做多层导航，简单弹出/切换过滤即可。

---

# 25. Antigravity quota 区域

显示：

```text
Antigravity   Pro
状态：在线
来源：App Local

Gemini Models
5h      [██████░░░░] 64% 剩余   2h 14m
Weekly  [████████░░] 82% 剩余   4d 7h

Claude / GPT
5h      [████░░░░░░] 41% 剩余   1h 52m
Weekly  [███████░░░] 71% 剩余   4d 7h
```

如果只有旧 per-model payload：

```text
Gemini 3.1 Pro
Gemini Flash
Claude ...
```

显示实际可取得的数据，不编造 weekly。

---

# 26. Settings

只做必要内容。

## General

```text
☑ 开机启动
☑ 启动后隐藏到托盘
刷新间隔：90 秒
数据保留：90 天
```

## Codex

```text
自动路径：...
额外 Session 目录：
[添加]
[删除]

[重新扫描全部 Codex]
```

## Antigravity

```text
状态：
App / IDE / agy / 未检测到

数据源：
Quota         可用
历史 Token    可用 / 不可用
Project       可用 / 不可用

[重新检测]
```

## Pricing

DataGrid 编辑价格。

## Projects

```text
原路径/Project ID           显示名
D:\...\CardGame             把牌打空！
```

## Diagnostics

只显示：

- Provider 状态
- 路径
- 文件数量
- parser version
- 最近错误
- Antigravity 端口
- quota source

CSRF/OAuth/token 不显示。

---

# 27. 系统托盘实现要求

WinForms：

```text
ApplicationContext
NotifyIcon
ContextMenuStrip
```

不要为了托盘启动一个一直可见的 MainForm。

建议：

```text
TrayApplicationContext
```

持有：

- `NotifyIcon`
- `MainForm?`
- `RefreshCoordinator`
- `CancellationTokenSource`

窗口关闭按钮默认：

```text
Hide()
```

不是 Exit。

真正 Exit 只来自：

```text
托盘 -> 退出
```

---

# 28. 开机启动

使用当前用户级：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

不需要管理员权限。

value 指向当前 exe。

关闭开机启动时删除自己的 value。

---

# 29. 单实例

用：

```text
Mutex
```

例如：

```text
UsageTray.SingleInstance
```

第二实例：

- 不再启动 scanner。
- 可简单退出。
- 如果方便，可用 NamedPipe 通知第一实例打开主窗口。
- NamedPipe 是增强项，不应拖慢第一版。

---

# 30. 轻量目标

这是验收目标，不是绝对物理保证：

- 空闲时不持续高 CPU。
- 没有刷新时 CPU 接近 0。
- 不进行 1 秒轮询。
- 不启动 Chromium/WebView。
- 不长期挂 PowerShell 子进程。
- 不把全量历史反复扫一遍。
- 内存目标：常规空闲尽量控制在约 40~80 MB 范围。
- 单次刷新完成后释放文件 handle / HTTP response / SQLite command。
- UI 图表只在数据变化或重绘时计算。

---

# 31. 安全要求

## 31.1 默认无外网依赖

第一版业务功能应可以在 Windows Firewall 禁止该 exe 外网后正常工作：

- Codex 本地历史：正常。
- Antigravity local quota：正常。
- Antigravity local history：正常。

价格只使用本地配置。

---

## 31.2 Antigravity loopback

任何关闭 TLS 校验的 HttpClient：

```text
只能访问 127.0.0.1
```

同时：

```text
AllowAutoRedirect = false
```

不能让认证 header 跟随 redirect。

---

## 31.3 凭据

第一版不需要：

- Codex `auth.json`
- OpenAI OAuth
- Google OAuth
- 浏览器 Cookie

如果仅做本地历史 + Antigravity local LSP，这些都不是必需项。

---

## 31.4 会话隐私

扫描 session/conversation 时：

只提取：

- 时间
- model
- usage
- session id
- cwd/project metadata

不要保存：

- 用户 prompt
- 模型回答
- 源代码正文
- tool output 正文

如果 parser 为识别 schema 临时读取全文：

- 只在内存。
- 不进入日志。
- 测试 fixture 使用人工脱敏样本。

---

# 32. 错误处理

## Codex JSONL 某一行损坏

```text
跳过该行
继续
warning count +1
```

## 某一 session 无 token

```text
不计 token
不报致命错误
```

## 无 model id

```text
model = "Unknown"
token 仍保留
cost = N/A
```

## Pricing 缺失

```text
token 正常
cost = N/A
```

## Antigravity 没运行

```text
Provider = Offline
显示最后成功快照
```

## Antigravity 内部 API 变了

```text
Quota unavailable
保留历史
Diagnostics 写 endpoint/parser error
主程序继续工作
```

## SQLite 锁

Antigravity 自己的 DB：

```text
只读打开
短 timeout
失败后稍后重试
```

不要复制/覆盖 Antigravity 原数据库。

---

# 33. 上游代码借鉴清单

以下是执行 Agent 应该主动查看的项目。

---

## 33.1 Win-CodexBar —— 第一参考

```text
https://github.com/nesszer/Win-CodexBar
```

License：请在实现时再次核对仓库 LICENSE。

### 文件 1

```text
rust/src/cost_scanner.rs
```

借鉴：

- 本地历史扫描
- file cache
- resume offset
- Codex JSONL
- daily/model aggregation
- UTC/local date padding

### 文件 2

```text
rust/src/codex_costs.rs
```

借鉴：

- token → API cost
- cached input 逻辑
- model normalization
- per-model aggregate
- pricing completeness tests

### 文件 3

```text
rust/src/providers/antigravity/mod.rs
```

借鉴：

- Antigravity process detection
- flag parsing
- real listening port discovery
- loopback endpoint probe
- CSRF handling
- `GetUserStatus`
- model classification
- quota parsing

### 文件 4

```text
rust/src/providers/antigravity/tests.rs
```

借鉴：

- 测试 fixture
- quota parser tests
- port/process parsing tests

### Tray

```text
rust/src/tray/
```

只看交互思路，不复制技术栈。

本项目使用 WinForms `NotifyIcon`。

---

## 33.2 原版 CodexBar Antigravity 文档

```text
https://github.com/steipete/CodexBar/blob/main/docs/antigravity.md
```

重点：

- Antigravity App / agy / IDE 数据源优先级。
- `RetrieveUserQuotaSummary`。
- `GetUserStatus` fallback。
- App/CLI 与 IDE payload 丰富度差异。
- 不要通过 UI scraping 获取 quota。

---

## 33.3 antigravity-usage —— 第二参考

```text
https://github.com/skainguyen1412/antigravity-usage
```

重点目录：

```text
src/local/
```

重点文件名：

```text
connect-client.ts
local-parser.ts
port-detective.ts
port-prober.ts
process-detector.ts
```

借鉴：

- local-only 模式。
- process → port → local endpoint 的拆层。
- Antigravity 不登录额外账户也能查询 local LSP 的思路。

---

## 33.4 Antigravity Storage Manager —— 历史/Token 第一参考

```text
https://github.com/unchase/antigravity-storage-manager
```

重点：

- `~/.gemini/antigravity/conversations/`
- 2.x `.db`
- 旧 `.pb`
- Token Usage
- request usage
- model mapping
- conversation/project metadata
- SQLite/PB 解析

只借本地 parser/statistics。

不要引入它的：

- Drive Sync
- Proxy
- OAuth
- Telegram
- profile switching

---

## 33.5 antigravity-panel —— quota history 思路参考

```text
https://github.com/n2ns/antigravity-panel
```

可参考：

- quota snapshot history
- 14-day trend
- downsample

本项目不需要复制其扩展/UI。

---

## 33.6 官方 Antigravity CLI —— V2 第一手依据

```text
https://antigravity.google/docs/cli/statusline
https://antigravity.google/docs/cli/commands/statusline
https://antigravity.google/docs/cli/features
https://antigravity.google/docs/cli/commands/usage
https://antigravity.google/docs/cli/credits
https://antigravity.google/docs/plans
```

重点：

- status line custom script。
- agent state 变化时向自定义命令 stdin 写入 JSON。
- `conversation_id`。
- `transcript_path`。
- CWD / workspace project_dir。
- active model。
- `context_window.total_input_tokens`。
- `context_window.total_output_tokens`。
- `current_usage.input_tokens/output_tokens`。
- `cache_creation_input_tokens`。
- `cache_read_input_tokens`。
- quota / reset。
- AI Credits 与 baseline quota 的区别。

这条来源是 Antigravity API 等值的第一手数据依据，优先级高于任何 quota 百分比推算。

---

# 34. 开源代码使用规则

如果直接复制上游非微小实现：

1. 先检查其当前 LICENSE。
2. 保留许可证要求的 attribution / copyright。
3. 在本项目 `THIRD_PARTY_NOTICES.md` 写：
   - 项目
   - URL
   - License
   - 借鉴/复制的文件
4. 优先“理解后用 C# 重写”而不是把 Rust/TS 逐行机械翻译。

不要复制与本项目无关的大块代码。

---

# 35. 推荐的实现阶段

---

## Phase 0 —— 建项目

完成：

- .NET 8 WinForms
- TrayApplicationContext
- MainForm
- Settings
- SQLite 初始化
- 单实例
- README

验收：

```text
启动 -> 只出现 tray
左键 -> MainForm
关闭 MainForm -> 回 tray
退出 -> 进程结束
```

---

## Phase 1 —— Codex Scanner

完成：

- Session locator
- JSONL parser
- full scan
- incremental scan
- daily/model aggregation
- SQLite cache
- DateRange

先不要做漂亮 UI。

写 console/debug dump 验证：

```text
Today
7d
30d
By Model
```

### Golden Test

用真实 session 复制后人工脱敏，只保留：

- timestamp
- model
- token fields
- cwd

存到：

```text
tests/Fixtures/codex/
```

至少覆盖：

1. 单 event。
2. 多 event。
3. 累计 token 变化。
4. cached input。
5. model change。
6. 文件追加。
7. 截断后重写。
8. JSON 损坏一行。
9. UTC 日期跨本地午夜。

---

## Phase 2 —— Pricing + API 等值

完成：

- pricing.json
- matching
- calculation
- model table
- current-price recompute
- unknown model warning

测试：

```text
1M input
400k cached
1M output
```

确保：

```text
600k * inputPrice
+ 400k * cachedPrice
+ 1M * outputPrice
```

不要：

```text
1M inputPrice
+ 400k cachedPrice
```

造成 cached double billing。

---

## Phase 3 —— Project

完成：

- Codex cwd discovery
- project resolver
- aliases
- by-project statistics

验收：

```text
同一路径多个 session -> 同一项目
同名不同路径 -> 两个项目
未知 cwd -> 未归类
```

---

## Phase 4 —— Dashboard

完成：

- Today / 7d / 30d / Month / Custom
- Metric cards
- daily chart
- model table
- project table
- provider filter

此阶段 Codex 已经是完整产品。

---

## Phase 5 —— Antigravity quota

完成：

- process discovery
- flags
- listen ports
- endpoint probe
- Get quota
- snapshot storage
- UI quota bars
- offline last snapshot

测试 fixture 不需要真实服务器：

把上游响应做成脱敏 JSON fixture。

至少测试：

- Gemini Pro
- Gemini Flash
- Claude
- 无 quotaInfo
- invalid remainingFraction
- resetTime null
- 数组顺序改变
- App CSRF
- agy no-CSRF

---

## Phase 6 —— Antigravity History + Transcript

执行 Agent 必须做实际 discovery：

```text
Brain root
Conversation root
Transcript
SQLite
PB
```

输出：

```text
Tokens: YES/NO
Cache split: YES/NO
Model: YES/NO
Timestamp: YES/NO
Conversation ID: YES/NO
Project: YES/NO
```

优先顺序：

```text
Transcript usage
> Conversation/SQLite usage
> 其他可验证历史
```

只要其中一条能回溯真实 token，就实现：

```text
AntigravityHistoryParser
```

并接入：

```text
UsageBucket
Pricing
Project
Dashboard
```

---

## Phase 7 —— Antigravity status-line Recorder（P0，不再可选）

无论历史 parser 是否完整，都实现官方 status-line recorder，原因：

- 它有明确官方 schema。
- 可以提供未来 usage 的稳定精确采集。
- 它给出 conversation/model/workspace/token 元数据。
- 它还能给出 `transcript_path`，帮助进一步回溯。

完成：

- 生成 recorder helper。
- 接收 stdin JSON。
- conversation totals 去重/delta。
- cache read/write 语义验证。
- CWD/project。
- model。
- coverage start time。
- 安全 chain 已有 status line。
- 与历史 DB/transcript 去重。

验收：

```text
发起两轮 Antigravity CLI 模型调用
=> 工具产生两次或正确聚合后的 usage
=> 重复 status-line state update 不重复计费
=> 项目归类正确
=> 模型正确
=> API 等值正确
```

---

## Phase 8 —— Antigravity API 等值（P0）

完成：

- provider/model pricing
- cache read/write
- current pricing recompute
- CostQuality
- daily/model/project API-equivalent views
- coverage start label

至少使用两个不同模型做 fixture：

```text
Gemini 类
Claude 类（如果本机有）
```

没有公开可比 API 价格的模型：

```text
Token 正常
Cost = N/A
```

不得让一个未知价格模型把全局合计悄悄变成错误金额。

全局合计若存在 N/A 模型：

UI 建议：

```text
已定价 API 等值：$31.42
另有 1.2M tokens 未计价
```

而不是显示一个貌似完整的 `$31.42`。

---

# 36. Antigravity DB 与 CLI 去重

如果未来两条精确来源同时存在，必须解决重复。

Provider 内设置 source priority：

```text
Historical DB > CLI live capture
```

或者按 session id + timestamp 去重。

第一版最简单：

```text
检测到可回溯 DB token history
=> 不启用 CLI live token ingestion

DB 不提供 token
=> 才允许 CLI live capture
```

避免双算。

---

# 37. 统计查询

`UsageRepository` 提供：

```text
GetSummary(range, provider?)
GetDaily(range, provider?)
GetByModel(range, provider?)
GetByProject(range, provider?)
GetProjectDetail(projectKey, range)
GetLatestQuotas(provider)
GetQuotaHistory(provider, range)
```

UI 不自己拼 SQL。

---

# 38. DashboardSnapshot

每次刷新先在后台计算：

```csharp
public sealed class DashboardSnapshot
{
    public DateRange Range { get; init; }
    public decimal? ApiEquivalentUsd { get; init; }

    public long InputTokens { get; init; }
    public long CachedTokens { get; init; }
    public long OutputTokens { get; init; }

    public IReadOnlyList<DailyUsageView> Daily { get; init; }
    public IReadOnlyList<ModelUsageView> Models { get; init; }
    public IReadOnlyList<ProjectUsageView> Projects { get; init; }
    public IReadOnlyList<QuotaView> Quotas { get; init; }

    public IReadOnlyList<string> Warnings { get; init; }
}
```

再一次性切 UI thread 更新。

不要每扫一行 JSON 就刷新 UI。

---

# 39. 自定义日期

Date range 基于本地日历：

```text
From 00:00:00 Local
To   23:59:59.999 Local
```

数据库存日期 bucket：

```text
YYYY-MM-DD
```

event 原始 timestamp：

如果需要保存，只存 UTC。

---

# 40. 性能细节

## 扫描

- `FileStream` + `StreamReader` 顺序读。
- 不把一个 500MB JSONL 一次 `ReadAllText`。
- JSON 一行一行处理。
- SQLite batch transaction。
- 解析结果先本地 accumulate，最后批量 replace 该 file buckets。

## UI

- DataGridView 开启双缓冲（必要时 subclass）。
- chart 点数多于约 120 天时按日聚合，不画每条 event。
- 不在 Paint 中查数据库。

---

# 41. Diagnostics

需要一个“诊断”按钮，方便未来 Antigravity 格式变化。

可复制到剪贴板：

```text
UsageTray version
Windows version
.NET version

Codex
Detected roots: ...
JSONL files: ...
Last scan: ...
Last parser error: ...
Unknown models: ...

Antigravity
App detected: yes/no
IDE detected: yes/no
agy detected: yes/no
API port: ...
Quota endpoint used: ...
History root: ...
History format: ...
History token source: available/unavailable
Last error: ...
```

禁止包含：

- CSRF token
- OAuth token
- 邮箱（默认省略）
- prompt
- response
- 源代码正文

---

# 42. 日志等级

Release 默认：

```text
Info
```

日志轮转：

```text
最多 5 个文件
每个 1~2 MB
```

Debug 才记录：

- 被跳过的损坏 JSON line number
- endpoint probe 过程
- parser schema details

即使 Debug 也不要记录 secret/header value。

---

# 43. UI 中的数据可信度

建议用小标签：

```text
精确
本地差分
配额
不可用
```

不必满屏显示，但详情/Tooltip 中能看到。

例如 Antigravity：

```text
API 等值：—
原因：当前 Antigravity 数据源仅返回额度，不返回历史 token。
```

这比显示一个错误的美元数字更重要。

---

# 44. README 必须写清楚

README 至少包括：

## What it does

Codex + Antigravity 本地 usage tray。

## Privacy

```text
Default local-only
No telemetry
No browser cookies
No prompt upload
No OAuth required for core features
```

## Codex source

```text
Local session JSONL
```

## Antigravity source

```text
Local language server for quota
Local conversation/official CLI metadata for tokens when available
```

## API Equivalent

说明：

```text
不是实际账单
按 pricing.json 的当前 API 单价估算
```

## Antigravity limitation

如果当前版本未能取得历史 token，要明确写：

```text
Quota tracking is exact from the local quota source;
API-equivalent cost is only shown when exact token metadata is available.
```

---

# 45. 验收测试

最终必须逐条过。

---

## 45.1 托盘

- [ ] 启动后不弹主窗口。
- [ ] 托盘图标存在。
- [ ] 左键可显示/隐藏。
- [ ] 右键刷新有效。
- [ ] 关闭窗口不退出。
- [ ] 托盘退出后进程消失。
- [ ] 不出现第二实例。

---

## 45.2 Codex

- [ ] 不运行本工具时产生的 Codex session，之后打开能够补统计。
- [ ] 重复刷新不会重复累计。
- [ ] JSONL 增长时只处理新增部分或安全重算单文件。
- [ ] cached input 不 double count。
- [ ] UTC 跨日本地归属正确。
- [ ] model change 不串模型。
- [ ] 今天/7天/30天/本月正确。
- [ ] 自定义日期正确。
- [ ] 未知模型 token 仍显示。
- [ ] 未知模型 cost 不瞎估。

---

## 45.3 Project

- [ ] 同一路径可聚合。
- [ ] 同名不同 path 不误合。
- [ ] 项目别名可保存。
- [ ] 未识别项目进入“未归类”。
- [ ] 项目统计之和与总统计一致。

---

## 45.4 Pricing

- [ ] 编辑价格后无需重扫日志。
- [ ] UI 立即重算。
- [ ] cached 公式正确。
- [ ] 导入/导出可用。
- [ ] 错误 JSON 不破坏现有 pricing。

---

## 45.5 Antigravity quota

- [ ] App 运行时能检测。
- [ ] 实际 API port 不依赖 `extension_server_port`。
- [ ] 只连接 127.0.0.1。
- [ ] self-signed TLS 只对 loopback 放行。
- [ ] CSRF 不落盘。
- [ ] Model mapping 不依赖数组顺序。
- [ ] quota % 正确。
- [ ] reset time 正确。
- [ ] App 关闭后显示 offline + last snapshot。
- [ ] quota history 从本工具安装后逐步产生。

---

## 45.6 Antigravity token + API 等值

这是 V2 的正式验收项：

- [ ] 至少一种真实 token 来源在目标机器上可用。
- [ ] 优先检查并支持官方 `transcript_path` / brain history。
- [ ] 能回溯的历史 token 已回溯。
- [ ] 官方 status-line recorder 可以记录启用后的前向 usage。
- [ ] `conversation_id` 去重正确。
- [ ] status-line 重复刷新不会重复累计。
- [ ] totals reset/rewind 不产生负 token。
- [ ] 时间正确。
- [ ] 模型正确。
- [ ] 项目正确或进入未归类。
- [ ] 不由 quota 反推 token。
- [ ] cache read/write 语义已经用真实样本验证，或明确标记 Cost 不完整。
- [ ] API 等值使用正确 provider/model price。
- [ ] 当前价格修改后不重扫历史即可重算。
- [ ] Transcript / DB / CLI 多来源不会重复统计。
- [ ] 若只能从启用 recorder 后开始，UI 明确显示精确统计起始时间。
- [ ] 未知模型价格显示 N/A，不按其他模型价格猜。
- [ ] 全局 Cost 合计不会掩盖“存在未定价 token”。

**仅有 quota、没有 Antigravity token/API 等值，不算通过本项验收。**

---

## 45.7 Privacy

- [ ] 数据库不含 prompt 正文。
- [ ] 日志不含 prompt 正文。
- [ ] 日志不含 CSRF。
- [ ] 不读取浏览器 Cookie。
- [ ] 核心功能不要求 OAuth。
- [ ] 禁止外网后本地功能仍工作。

---

# 46. 最终交付物

执行完成后必须提供：

```text
UsageTray.sln
完整源码
tests
README.md
THIRD_PARTY_NOTICES.md（如果借用上游代码）
LICENSE
默认 pricing.json
```

发布：

```powershell
dotnet restore
dotnet test
dotnet build -c Release
```

并提供至少一种 publish：

```powershell
dotnet publish src/UsageTray/UsageTray.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false
```

如果用户希望完全免安装 .NET Runtime，再额外提供：

```powershell
dotnet publish src/UsageTray/UsageTray.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

注意：

- framework-dependent 通常更小。
- self-contained 更方便但体积明显更大。
- “轻量”优先看运行开销和依赖，而不是强求 EXE 几 MB。

---

# 47. 建议最终 UI 文案

主标题：

```text
AI Usage Tray
```

Provider：

```text
Codex
Antigravity
```

统计：

```text
API 等值
按当前价格估算

输入
缓存输入
输出

按日期
按模型
按项目

额度
剩余
重置
```

状态：

```text
精确
仅配额
价格未配置
离线
未归类
```

---

# 48. 开发过程中最容易犯的 10 个错误

1. **直接把每个 Codex `token_count` 累加。**  
   可能把累计值重复统计。必须参考成熟 parser 的 delta/cache 逻辑。

2. **把 cached input 又加一次。**  
   会重复计费。

3. **根据 Codex session 文件夹日期做本地日历统计。**  
   UTC/本地时区会造成跨日错误。

4. **未知模型套默认模型单价。**  
   会让 API 等值“看起来完整但实际错误”。

5. **把 Antigravity quota 百分比换算美元。**  
   没有可靠数学关系。API 等值必须来自真实 token；官方 status-line 已提供可用于精确采集的 token 元数据。

6. **把 `extension_server_port` 当 quota API port。**  
   Antigravity 2.x 中不可靠，应枚举 PID listen ports 再 probe。

7. **关闭 TLS 校验后允许任意 hostname。**  
   必须限定 127.0.0.1。

8. **Antigravity 模型按返回数组顺序贴标签。**  
   服务端顺序/模型集合会变化。

9. **为了项目统计保存完整对话。**  
   没必要，也扩大隐私风险。

10. **每次刷新全量重扫历史。**  
    必须缓存每个 source file 的 size/mtime/offset/贡献。

---

# 49. Agent 的完成判定

不要以“程序能启动”作为完成。

真正完成至少意味着：

```text
Codex：
历史 token 精确
时间统计可用
模型统计可用
项目统计可用
API 等值可用
不常驻也可补扫

Antigravity：
本地 quota 可用
quota history 可用
真实 token 采集链路可用
历史可回溯则完成历史回溯
至少 status-line 前向精确统计可用
按时间/模型/项目统计可用
API 等值可用
统计覆盖起始时间明确
App/IDE/agy 失败能降级
quota 永不参与美元反推

App：
托盘可用
轻量
SQLite 增量缓存
无遥测
无浏览器 Cookie
核心功能本地运行
```

---

# 50. 如果工期有限，优先级

绝对优先：

```text
P0  Tray
P0  Codex scanner
P0  Pricing
P0  时间统计
P0  项目统计
P0  Antigravity quota
P0  Antigravity transcript/history discovery
P0  Antigravity CLI status-line recorder
P0  Antigravity API 等值
P1  Antigravity Desktop/IDE 完整历史回溯的额外格式兼容
P2  AI Credits 附加展示
P2  更漂亮图表
```

如果 Antigravity Desktop/IDE 某个历史格式无法稳定解析，可以先保证官方 status-line 的**前向精确统计**按期完成；但不能退回到“只有 quota、没有 API 等值”的旧目标。

---

# 51. 给 Agent 的最后一句执行要求

先把这个项目做成一个**可信的小仪表**，而不是一个功能很多但数字来源说不清的“AI 管理平台”。

对于每一个显示给用户的数字，都必须能回答：

```text
它来自哪个本地文件/本地接口？
它是原始值、差分值还是估算值？
如果是美元，使用了哪条价格规则？
如果数据源不可用，为什么不是 0？
```

只要这四个问题能稳定回答，并且 **Codex 与 Antigravity 两边的 API 等值都已经有真实 token 数据链路**，V2 第一版才算成功。


---

> **规格版本**：V2 — Antigravity API Equivalent Required  
> **核验日期**：2026-08-19

# CODE_INDEX: 系统代码总框架与模块索引

本文档是 QuotaTrace（Codex + Antigravity 本地用量与额度统计器）的代码全景导航与模块索引。在进行开发、调试或审查任务前，请先阅读本索引快速定位模块与链路，实际逻辑以最新源码为准。

---

## 1. 工程概览与解决方案结构

- **解决方案**: [`UsageTray.sln`](UsageTray.sln)
- **目标框架**: .NET 8.0 Windows (`net8.0-windows`)
- **交付目录**: [`publish/`](publish)

```mermaid
graph TD
    Entry[Program.cs / Main] --> TrayCtx[TrayApplicationContext]
    TrayCtx --> Coord[RefreshCoordinator]
    Coord --> CodexP[CodexProvider]
    Coord --> AntiP[AntigravityProvider]
    Coord --> Price[PricingService]
    Coord --> Agg[UsageAggregator]
    Agg --> Projector[QuotaProjector]
    AntiP --> Pools[AntigravityQuotaEstimator]
    Pools --> Projector
    Coord --> Repo[UsageRepository]
    Repo --> DB[(SQLite: usage.db)]
    Coord --> MemOpt[MemoryOptimizer]
    TrayCtx --> MainForm[MainForm]
    TrayCtx --> QuotaPopup[QuotaPopupForm / QuotaSummaryControl]
    TrayCtx --> Settings[SettingsForm]
```

### 1.1 项目清单

| 项目 / 目录 | 输出类型 | 核心职责 |
| :--- | :--- | :--- |
| [`src/UsageTray/`](src/UsageTray) | WinExe (WinForms) | 主应用程序，包含托盘交互、高 DPI 界面、数据解析、SQLite 存储、计价引擎与调度。 |
| [`tests/UsageTray.Tests/`](tests/UsageTray.Tests) | Class Library (xUnit) | 单元与回归测试套件，涵盖计价、Protobuf 解码与损坏输入拒绝、Antigravity 令牌端口隔离、会话归一化、额度周期预估、Codex 周历史重构、抖动聚合与提前重置防分裂、分模型独立测算、锚点切片用量防丢失、跨设备断层样本过滤、周历史集成、GPT-6.2 Astra/Sol 与 GPT-6.1 Sol/Luna/Terra 及 Gemini 3.8 Flash N / Gemini 4 Hydrogen / Carbon / Neon / Orchestrator (Carbon/Argon) & Worker 规则及迁移去重、`PricingViewerForm` 高 DPI 防截断布局、Antigravity 取消 Argon 独立额度并入 Gemini 主池与废弃快照过滤清理、Pro/Ultra Carbon 变体完整支持、Antigravity `agy` CLI `statusLine` 被动同步与手动倒计时/日期校准全链路测试（220 项测试）。 |
| [`tools/AntigravityStatusRecorder/`](tools/AntigravityStatusRecorder) | Exe (Console) | 独立控制台工具，读取官方 status-line stdin JSON 并记录到本地存储。 |
| [`tools/CodexAudit/`](tools/CodexAudit) | Exe (Console) | 离线 Codex 审计与 CSV 导出工具。 |

---

## 2. 模块索引与源码路径

### 2.1 应用程序与生命周期 (`UsageTray.App`)

负责全局路径、配置持久化、国际化多语言管理、开机自启、单实例互斥以及维护 CLI 入口。

| 类名 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`I18n`](src/UsageTray/App/I18n.cs) | `src/UsageTray/App/I18n.cs` | 全局轻量强类型国际化模块，支持自动检测（`auto`）与中英文切换（`zh-CN`/`en-US`），提供动态语言切换事件。 |
| [`AppPaths`](src/UsageTray/App/AppPaths.cs) | `src/UsageTray/App/AppPaths.cs` | 解析 `%LOCALAPPDATA%\UsageTray` 下的数据库、配置、价格与日志绝对路径。 |
| [`AppSettings`](src/UsageTray/App/AppSettings.cs) | `src/UsageTray/App/AppSettings.cs` | 定义用户配置模型及 JSON 读写，包含 `EnableCodex`/`EnableAntigravity`、语言设置、全量扫描周期与窗口尺寸/列宽记忆。 |
| [`SingleInstance`](src/UsageTray/App/SingleInstance.cs) | `src/UsageTray/App/SingleInstance.cs` | 基于系统互斥体（Mutex）确保进程单实例运行。 |
| [`StartupManager`](src/UsageTray/App/StartupManager.cs) | `src/UsageTray/App/StartupManager.cs` | 读写 Windows 注册表 `Run` 键，实现开机自启并提供标准交付产物路径规范化与自动修正同步机制。 |
| [`CodexMaintenanceCli`](src/UsageTray/App/CodexMaintenanceCli.cs) | `src/UsageTray/App/CodexMaintenanceCli.cs` | 命令行维护入口，支持数据库备份、全量重建与归一化诊断。 |

### 2.2 核心领域模型 (`UsageTray.Core`)

定义通用的基础数据结构、枚举、数据质量标识及计价规则。

| 类名 / 契约 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`ProviderKind`](src/UsageTray/Core/ProviderKind.cs) | `src/UsageTray/Core/ProviderKind.cs` | 提供商枚举（`Codex`、`Antigravity`）及其存储转换。 |
| [`TokenUsage`](src/UsageTray/Core/TokenUsage.cs) | `src/UsageTray/Core/TokenUsage.cs` | 记录 Input、CacheRead、CacheWrite、Output、ThinkingOutput、长上下文等 Token 明细。 |
| [`UsageBucket`](src/UsageTray/Core/UsageBucket.cs) | `src/UsageTray/Core/UsageBucket.cs` | 聚合维度的核心用量桶（Provider/Model/Project/Date），承载 Token 统计与订阅参考金额计算。 |
| `CodexQuotaPools` | `src/UsageTray/Core/CodexQuotaPools.cs` | 集中识别标准与 Reserve 模型池，供解析与周期查询复用。 |
| [`QuotaSnapshot`](src/UsageTray/Core/QuotaSnapshot.cs) | `src/UsageTray/Core/QuotaSnapshot.cs` | 配额快照模型（含 `QuotaSources`：`antigravity-local`、`antigravity-statusline`、`antigravity-manual`），记录 5 小时与周配额比例、重置时间戳、过期状态（`IsResetPassed`）、来源语义（`IsManualCalibration`/`IsStatusLineSync`）、过期 5h 隐藏判定（`IsExpiredStaleAntigravityFiveHour`）及自适应陈旧判断（`IsStale`：手动校准不过期，CLI 同步 24 小时，桌面/历史快照 15 分钟）。 |
| [`TokenSpeedEstimate`](src/UsageTray/Core/TokenSpeedEstimate.cs) | `src/UsageTray/Core/TokenSpeedEstimate.cs` | 速率预估模型，记录未命中 Prefill、缓存读取与输出解码速度及样本数与格式化。 |

| [`PricingRule`](src/UsageTray/Core/PricingRule.cs) | `src/UsageTray/Core/PricingRule.cs` | 单个模型的定价规则定义（输入、缓存读、缓存写、输出单价、长上下文阶梯及核验日期）。 |
| [`DateRange`](src/UsageTray/Core/DateRange.cs) | `src/UsageTray/Core/DateRange.cs` | 时间范围区间（今天、7天、30天、本月、全部、本次周额度、自定义区间）。 |
| [`DataQuality`](src/UsageTray/Core/DataQuality.cs) | `src/UsageTray/Core/DataQuality.cs` | 数据质量与可信度标志（`Exact`、`PricingMissing`、`CacheWriteUnavailable` 等）。 |
| [`ProjectInfo`](src/UsageTray/Core/ProjectInfo.cs) | `src/UsageTray/Core/ProjectInfo.cs` | 项目标识与显示名称映射。 |

### 2.3 数据持久化与仓储 (`UsageTray.Data`)

管理本地 SQLite 数据库连接、表结构迁移、原始会话与聚合桶的 CRUD。

| 类名 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`UsageDatabase`](src/UsageTray/Data/UsageDatabase.cs) | `src/UsageTray/Data/UsageDatabase.cs` | SQLite 连接工厂与连接池管理，配置 `PRAGMA cache_size = -2000` 限制内存。 |
| [`DatabaseMigrations`](src/UsageTray/Data/DatabaseMigrations.cs) | `src/UsageTray/Data/DatabaseMigrations.cs` | 数据库版本管理与 DDL 迁移（`sources`、`raw_snapshots`、`usage_buckets`、`quotas` 等表）。 |
| [`UsageRepository`](src/UsageTray/Data/UsageRepository.cs) | `src/UsageTray/Data/UsageRepository.cs` | 提供高效的批量写入、增量查询、按模型池与窗口归一化复合键（`NormalizeAntigravityPoolWindowKey` 隔离每池每窗口最新来源）获取最新快照（过滤 Spark / Argon / `unknown*` 残留并包含 Reserve 动态活跃触发检测）、各池 Token 窗口隔离（支持 Gemini 与 3P 双池独立周周期）及已删除源清理。 |

### 2.4 定价与抓取体系 (`UsageTray.Pricing`)

负责模型价格的匹配计算、长上下文阶梯判定、固定非促销参考基准内嵌、订阅倍率与安全迁移。

| 类名 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`PricingService`](src/UsageTray/Pricing/PricingService.cs) | `src/UsageTray/Pricing/PricingService.cs` | 维护内存价格文档（Schema 16），提供通配符与别名精确匹配、模型阶梯加价计算、精确键优先热升级及重复键自动去重。 |
| [`PricingMatcher`](src/UsageTray/Pricing/PricingMatcher.cs) | `src/UsageTray/Pricing/PricingMatcher.cs` | 模型名称规范化、别名映射与模式匹配引擎。 |
| [`PricingUpdateService`](src/UsageTray/Pricing/PricingUpdateService.cs) | `src/UsageTray/Pricing/PricingUpdateService.cs` | 合并软件内置参考基准，保留自定义规则；不抓取 API 促销价覆盖订阅参考。 |

### 2.5 数据提供商 (`UsageTray.Providers`)

负责从磁盘文件、数据库与本地服务中提取真实 Token、会话元数据与配额。

#### 2.5.1 Antigravity 提供商 (`UsageTray.Providers.Antigravity`)

| 类名 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`AntigravityProvider`](src/UsageTray/Providers/Antigravity/AntigravityProvider.cs) | `src/UsageTray/Providers/Antigravity/AntigravityProvider.cs` | 统一驱动 Antigravity 历史解析、状态流记录、配额探测及已删除源清理；当桌面端隐藏 CSRF 令牌且已存在有效手动校准或 CLI 同步周快照时不产生误报警告，否则提示通过校准弹窗或主动使用 `agy` CLI 同步。 |
| [`AntigravitySqliteHistoryParser`](src/UsageTray/Providers/Antigravity/AntigravitySqliteHistoryParser.cs) | `src/UsageTray/Providers/Antigravity/AntigravitySqliteHistoryParser.cs` | 从 `conversations/*.db` 读取 `gen_metadata` 与 `steps`，提取 Protobuf 结构化 Token 与时间戳；显示名缺少原始模型 ID 时按具体 Claude 版本与 Orchestrator/Worker 模式推断。 |
| [`AntigravityProtobufReader`](src/UsageTray/Providers/Antigravity/AntigravityProtobufReader.cs) | `src/UsageTray/Providers/Antigravity/AntigravityProtobufReader.cs` | 基于 `ReadOnlySpan<byte>` 与 `ref struct` 的流式零分配 Protobuf Wire 解码器；拒绝无效 Varint、字段号及越界长度。 |
| [`AntigravityQuotaParser`](src/UsageTray/Providers/Antigravity/AntigravityQuotaParser.cs) | `src/UsageTray/Providers/Antigravity/AntigravityQuotaParser.cs` | 解析 `RetrieveUserQuotaSummary` 嵌套结构及 `agy` CLI `statusLine` JSON `quota` 节点（兼容 `groups`、字典与数组形态），提取 Gemini 与 3P 配额池并过滤已下线的 Argon 与 `disabled` 项。 |
| [`AntigravityLocalApi`](src/UsageTray/Providers/Antigravity/AntigravityLocalApi.cs) | `src/UsageTray/Providers/Antigravity/AntigravityLocalApi.cs` | 与 loopback 本地 language_server 通信，发送 CSRF Token 获取实时配额。 |
| [`AntigravityPortDiscovery`](src/UsageTray/Providers/Antigravity/AntigravityPortDiscovery.cs) | `src/UsageTray/Providers/Antigravity/AntigravityPortDiscovery.cs` | 基于进程 PID 探测 language_server 动态绑定的 HTTP/HTTPS 端口，不枚举无关 loopback 服务；Provider 将 CSRF Token 与同一进程的端口配对。 |
| [`AntigravityQuotaEstimator`](src/UsageTray/Providers/Antigravity/AntigravityQuotaEstimator.cs) | `src/UsageTray/Providers/Antigravity/AntigravityQuotaEstimator.cs` | 提供 Antigravity 双池严格互斥匹配（Gemini / 3P，将 Carbon、Neon、Hydrogen 与原 Argon 系列统一归入 Gemini 主池并阻断遗留 Argon 快照混入）、全周期宏观基准外推（区分手动校准/CLI 同步外推注记）与常识物理下限防倒挂。 |
| [`AntigravityProjectResolver`](src/UsageTray/Providers/Antigravity/AntigravityProjectResolver.cs) | `src/UsageTray/Providers/Antigravity/AntigravityProjectResolver.cs` | 从 summary 数据库与 trajectory blob 中解析项目物理根路径。 |
| [`AntigravityStatusRecorder`](src/UsageTray/Providers/Antigravity/AntigravityStatusRecorder.cs) | `src/UsageTray/Providers/Antigravity/AntigravityStatusRecorder.cs` | 官方 status-line stdin 增量处理器；在启用历史 DB 优先回溯或无 `conversation_id` 时仍先提取并按 5 分钟/变动阈值节流写入 `antigravity-statusline` 配额快照（不主动静默启动 CLI）。 |
| [`AntigravityManualQuotaHelper`](src/UsageTray/Providers/Antigravity/AntigravityManualCalibration.cs) | `src/UsageTray/Providers/Antigravity/AntigravityManualCalibration.cs` | 手动额度校准计算与配置助手，支持按周倒计时（天/小时/分）与重置日期互相换算、校验并构建 `antigravity-manual` 周快照以及生成 `agy` CLI `statusLine` JSON 配置片段。 |

#### 2.5.2 Codex 提供商 (`UsageTray.Providers.Codex`)

| 类名 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`CodexProvider`](src/UsageTray/Providers/Codex/CodexProvider.cs) | `src/UsageTray/Providers/Codex/CodexProvider.cs` | 管理 Codex 会话文件扫描、0 变更短路检查、增量/全量解析及快照归一化。 |
| [`CodexJsonlParser`](src/UsageTray/Providers/Codex/CodexJsonlParser.cs) | `src/UsageTray/Providers/Codex/CodexJsonlParser.cs` | 逐行容错解析 session JSONL（ParserVersion 12），过滤新版 CLI 调试级 `token_usage_record`，绑定权威 `token_count`，忽略已下线的 Spark 辅助模型配额并提取主力通用额度（`codex-weekly`）与 Reserve；采用变动增量采集与去重保留单文件内完整的时序配额快照，彻底杜绝单文件末行覆盖导致中间快照丢失。 |
| [`CodexUsageNormalizer`](src/UsageTray/Providers/Codex/CodexUsageNormalizer.cs) | `src/UsageTray/Providers/Codex/CodexUsageNormalizer.cs` | 跨文件多分支会话归一化引擎，处理全局累计计数器差分、时间倒序重排与回退 Epoch。 |
| [`CodexSessionLocator`](src/UsageTray/Providers/Codex/CodexSessionLocator.cs) | `src/UsageTray/Providers/Codex/CodexSessionLocator.cs` | 发现 `CODEX_HOME` 及默认 `~/.codex/sessions` 下的活动与归档会话文件。 |

### 2.6 服务层与聚合调度 (`UsageTray.Services`)

| 类名 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`RefreshCoordinator`](src/UsageTray/Services/RefreshCoordinator.cs) | `src/UsageTray/Services/RefreshCoordinator.cs` | 刷新主调度器，协调并发锁、增量/全量扫描、价格更新、手动校准持久化（`GetLatestQuotas`、`SaveManualAntigravityQuotas`）与快照广播。 |
| `QuotaProjector` | `src/UsageTray/Services/QuotaProjection.cs` | Codex/Antigravity 共用的同周期快照差分外推（手动校准 2 小时/CLI 10 分钟/本地 60 秒重置漂移容差，手动与 CLI 样本豁免 36 小时样本断层截断，并区分显示“按手动校准外推”与“按 CLI 同步样本外推”）；引入 2% API 抖动容忍（`JitterTolerance`）；提供 `EstimateModelProjections` 微区间切片归因算法。 |
| [`UsageAggregator`](src/UsageTray/Services/UsageAggregator.cs) | `src/UsageTray/Services/UsageAggregator.cs` | 聚合引擎，按日期区间/双额度周周期汇总 Token、计算订阅参考金额及缓存命中率；主力通用模型与 Reserve 备用额度双通道解耦聚合；构建周历史周期时按 `ResetAt` 12 小时容差聚类消除并发会话交替导致的虚假分裂，并建立 100% 完整额度周期基准；调度主力池分模型测算并分发至视图模型；集成 Antigravity 分模型推算周满额计算（切片单模型外推、所属模型池满额回退绑定、全周期宏观基准校准与常识下限防倒挂）；为历史周周期切片计算独立分模型测算并在当前周期不足 5% 时平滑继承。 |
| [`UsageViews`](src/UsageTray/Services/UsageViews.cs) | `src/UsageTray/Services/UsageViews.cs` | 视图数据模型，包含 `DashboardSnapshot`（支持分池订阅参考金额、双周周期列表 `CodexWeeklyCycles` 与分模型周满额测算 `CodexModelProjections`）、`ModelUsageView`（携带单模型推算周满额）、`ModelQuotaProjectionView` 及 `CodexHistoricalCycleView`（包含 `ModelProjections` 分模型测算列表、100% 周期额度基准与全周期消耗比例）等。 |
| [`MemoryOptimizer`](src/UsageTray/Services/MemoryOptimizer.cs) | `src/UsageTray/Services/MemoryOptimizer.cs` | 执行 LOH 压缩、GC 及 Windows 原生 `SetProcessWorkingSetSize` 深度回收常驻内存。 |
| [`DiagnosticsService`](src/UsageTray/Services/DiagnosticsService.cs) | `src/UsageTray/Services/DiagnosticsService.cs` | 输出提供商状态、数据库统计与解析诊断报告。 |
| [`ProjectService`](src/UsageTray/Services/ProjectService.cs) | `src/UsageTray/Services/ProjectService.cs` | 项目名称别名与路径美化服务。 |
| [`AppUpdateService`](src/UsageTray/Services/AppUpdateService.cs) | `src/UsageTray/Services/AppUpdateService.cs` | 软件自动更新服务，负责 GitHub 最新 Release 检测、语义版本对比、安装包资产优选匹配、后台异步检测、流式下载进度汇报、临时目录解压及无缝自更新批处理脚本触发。 |

### 2.7 用户界面与交互控件 (`UsageTray.UI`)

采用自适应高 DPI 设计、自绘渲染及几何/矢量高清图标。

| 类名 / 控件 | 路径 | 核心职责 |
| :--- | :--- | :--- |
| [`TrayApplicationContext`](src/UsageTray/UI/TrayApplicationContext.cs) | `src/UsageTray/UI/TrayApplicationContext.cs` | 托盘主上下文，管理托盘图标、右键菜单（集成“校准 Antigravity 额度...”与“检查更新...”）、气泡升级通知点击、单击/双击防冲突计时器及定时刷新。 |
| [`MainForm`](src/UsageTray/UI/MainForm.cs) | `src/UsageTray/UI/MainForm.cs` | 主仪表盘窗口，展示多通道订阅参考金额卡片、周期标识（含手动/CLI 来源标签）、趋势图表、模型表（含各模型「推算周满额」列与细化原因 Tooltip）、项目表及带顶部状态与“校准额度”入口的额度面板；采用异步独立加载（Task.Run）、请求序列防竞态与 DataGridView 双缓冲。 |
| [`AntigravityManualQuotaDialog`](src/UsageTray/UI/AntigravityManualQuotaDialog.cs) | `src/UsageTray/UI/AntigravityManualQuotaDialog.cs` | Antigravity 手动校准对话框，支持按周倒计时（天/小时/分）或精确重置日期+剩余百分比录入 Gemini 及可选 Claude/GPT 周额度并实时校验。 |
| [`UpdateDialog`](src/UsageTray/UI/UpdateDialog.cs) | `src/UsageTray/UI/UpdateDialog.cs` | 独立软件升级对话框，展示新版本号、发布日期与更新日志说明，提供【在软件内直接升级】（带下载与解压进度条）、【前往 GitHub 下载】（网页跳转）及【稍后提醒】等操作。 |
| [`QuotaPopupForm`](src/UsageTray/UI/QuotaPopupForm.cs) | `src/UsageTray/UI/QuotaPopupForm.cs` | 单击托盘弹出的高清额度摘要窗口，直接宿主承载 `QuotaSummaryControl` 保证与主窗口 100% 同源同布，支持拖拽移动锁定、主动点击/Esc 关闭、失焦防误关及工作区限高滚动。 |
| [`QuotaSummaryControl`](src/UsageTray/UI/Controls/QuotaSummaryControl.cs) | `src/UsageTray/UI/Controls/QuotaSummaryControl.cs` | 复用的额度摘要自绘控件（主窗口额度 Tab 与悬浮窗共用；智能隐藏过期超 12 小时的历史 Antigravity 5h 残留快照，区分“手动校准”/“CLI 同步”/“待校准”徽标而不再误报红字“离线”，并在主窗口展开各模型独立测算分支树）。 |
| [`CodexHistoryControl`](src/UsageTray/UI/Controls/CodexHistoryControl.cs) | `src/UsageTray/UI/Controls/CodexHistoryControl.cs` | 独立的 Codex 周历史主面板页面控件，展示过去数次区间的周额度（非固定 7 天动态判定、进行中/已重置状态高亮、标准 100% 基准额度变化 `100% → {minRem:P0}`、订阅参考金额、满额预估、第 9 列「模型独立测算」及 4 行底部明细面板）。 |
| [`DailyBarChartControl`](src/UsageTray/UI/Controls/DailyBarChartControl.cs) | `src/UsageTray/UI/Controls/DailyBarChartControl.cs` | 自绘每日用量柱状图，支持动态 DPI 刻度与图例排版。 |
| [`SettingsForm`](src/UsageTray/UI/SettingsForm.cs) | `src/UsageTray/UI/SettingsForm.cs` | 可缩放的设置窗口，包含常规配置、Antigravity 配额同步说明与按钮（手动校准与复制 `agy` CLI `statusLine` 配置）、模型价格查看/更新基准、软件关于与升级检查。 |
| [`PricingViewerForm`](src/UsageTray/UI/PricingViewerForm.cs) | `src/UsageTray/UI/PricingViewerForm.cs` | 独立模型价格查看窗口，采用 `AutoScaleMode.Dpi` 与动态字体测量防止高 DPI 截断，支持按提供商筛选、实时搜索及 4 位小数单价展示。 |
| [`DateRangeDialog`](src/UsageTray/UI/DateRangeDialog.cs) | `src/UsageTray/UI/DateRangeDialog.cs` | 自定义起止日期选择对话框。 |
| [`QuotaDisplayFormatter`](src/UsageTray/UI/QuotaDisplayFormatter.cs) | `src/UsageTray/UI/QuotaDisplayFormatter.cs` | 托盘紧凑单行 Tooltip 与弹窗文本格式化工具，自动折叠过期 5h 快照、优先显示 `AG 周 XX%` / `AG 待校准` 并区分手动校准/CLI 同步状态说明。 |
| [`TimeFormatter`](src/UsageTray/UI/TimeFormatter.cs) | `src/UsageTray/UI/TimeFormatter.cs` | 时间与重置倒计时格式化工具，生成“X天X小时X分后”相对时间。 |
| [`AppIcon`](src/UsageTray/UI/AppIcon.cs) | `src/UsageTray/UI/AppIcon.cs` | 内嵌资源高清图标加载器及几何矢量兜底绘制。 |
| [`WindowGeometryPersistence`](src/UsageTray/UI/WindowGeometryPersistence.cs) | `src/UsageTray/UI/WindowGeometryPersistence.cs` | 窗口尺寸与主页各表格栏位宽度自动持久化与恢复。 |

---

## 3. 关键调用链与数据流

### 3.1 增量与全量刷新数据流

```mermaid
sequenceDiagram
    participant Timer as RefreshTimer / User
    participant Coord as RefreshCoordinator
    participant Codex as CodexProvider
    participant Anti as AntigravityProvider
    participant DB as UsageRepository
    participant Agg as UsageAggregator
    participant UI as MainForm / QuotaPopup
    
    Timer->>Coord: RefreshAsync(forceFullScan)
    Coord->>Codex: RefreshAsync(context)
    Note over Codex: 比对文件 mtime/hash<br/>0变更短路或解析增量 JSONL
    Codex->>DB: 写入 RawSnapshots & Buckets
    Coord->>Anti: RefreshAsync(context)
    Note over Anti: 流式解码 Protobuf Wire<br/>探测 Local Language Server
    Anti->>DB: 写入 Buckets & Quotas
    Coord->>Agg: BuildSnapshot()
    Agg->>DB: 聚合时间范围 / 周额度周期
    Agg-->>Coord: 返回最新 DashboardSnapshot
    Coord->>Coord: MemoryOptimizer.TrimMemory()
    Coord->>UI: 触发 SnapshotChanged 事件更新界面
```

### 3.2 订阅参考金额计算公式

- **Sub2API 三段式输入口径**:
  $$\text{未命中输入 (Input)} = \text{TotalInput} - \text{CacheRead} - \text{CacheWrite}$$
- **基础计费公式**:
  $$\text{Cost} = (\text{Input} \times P_{in} + \text{CacheRead} \times P_{cr} + \text{CacheWrite} \times P_{cw} + \text{Output} \times P_{out}) / 10^6$$
- **长上下文阶梯判定**:
  单次 Request 命中阈值（如 Gemini Pro > 200K Tokens，GPT-6 / GPT-5.6 > 272K Tokens）时，按长上下文独立单价结算。
- **满额预估价值推算**:
  $$\text{EstimatedFullUsd} = \frac{\text{两次快照间本机参考金额}}{\text{初始剩余比例} - \text{末次剩余比例}}$$

---

## 4. 维护与扩展准则

1. **新增模型价格**: 修改 [`Pricing/default-pricing.json`](src/UsageTray/Pricing/default-pricing.json) 并升级 `PricingService.DefaultDocumentVersion`。
2. **数据库结构变更**: 在 [`DatabaseMigrations.cs`](src/UsageTray/Data/DatabaseMigrations.cs) 增加递增迁移脚本，切勿直接修改历史迁移步骤。
3. **解析器升级**: 若修改了 Token 提取算法，递增对应 Provider 的 `ParserVersion`，使系统自动清理旧缓存并触发一次全量重新计算。
4. **测试覆盖**: 任何涉及数据解析、计价或周窗口计算的代码修改，必须在 [`tests/UsageTray.Tests`](tests/UsageTray.Tests) 补充对应的单元测试。

## 5. 手动验证产物治理

`tools/artifacts.py` 是本地同步运行、登记输入及清理命令的薄入口；`tools/artifact_runtime.py` 管理活动范围，`tools/artifact_clean.py` 实施逐文件预览和交互清理，`tools/artifact-clean.json` 声明可丢弃根目录。根目录 `清理验证产物.bat` 调用交互清理；现有 CI、应用及采集路径未接入。具体边界和用法见项目文档。

`tools/run_preview_test.py` 仅在已登记 run 内将 `TEST_PREVIEW_DIR` 指向 work，以运行现有可选离屏预览测试。

# AI Usage Tray

这是一个 Windows 10/11 本地托盘统计器，读取 Codex session JSONL 和 Antigravity 本地历史/status-line 元数据，展示真实 token、时间、模型、项目以及“API 等值（按当前价格估算）”。

## 使用方式

```powershell
dotnet restore
dotnet test
dotnet build -c Release
dotnet run --project src/UsageTray/UsageTray.csproj
```

Antigravity CLI 的 status-line recorder 入口：

```powershell
echo '{"conversation_id":"...","model":{"id":"gemini-2.5-flash"},"context_window":{"total_input_tokens":10,"total_output_tokens":2}}' |
  dotnet run --project tools/AntigravityStatusRecorder/AntigravityStatusRecorder.csproj
```

也可以把编译后的 `AntigravityStatusRecorder.exe` 配置为官方 status-line 的自定义命令。程序不会静默覆盖已有 status-line；接入前请保留原有命令并按需手工 chain。

## 数据来源与口径

- Codex：本地 `CODEX_HOME/sessions`、默认 `~/.codex/sessions` 和设置中追加的目录。累计 token 会按模型做差分，损坏行单独跳过。
- Antigravity 历史：优先扫描 `~/.gemini/antigravity/conversations` 与 `brain` 下的 JSON/JSONL；完整 JSON 对象/数组会解析为多个事件。JSON 历史没有可验证 token 时，再以只读方式检查 SQLite 的显式 token 字段；未知 SQLite/PB 结构不会猜数字。
- Antigravity 前向 token：读取官方 status-line stdin JSON，按 `conversation_id` 去重并处理 totals 增量；totals 回退时建立新 epoch，不产生负 token。
- Antigravity quota：只查询本机 loopback 的本地接口，并保存最近快照。quota 百分比和 reset 时间绝不转换成 token 或美元。
- 价格：`%LOCALAPPDATA%\UsageTray\pricing.json`，首次运行从 `Pricing/default-pricing.json` 复制；主界面“获取最新价格”按钮会手动读取允许的 OpenAI/Gemini 官方 HTTPS 定价页，解析失败时保留旧规则，启动时不自动联网。

API 等值不是实际账单。未知模型、未配置价格或尚未验证的 cache read/write 语义显示为 `—`，但 token 仍保留在统计中；全局数字会额外提示未计价 token 数量。

## 隐私

核心功能本地运行，不读取浏览器 Cookie、不要求 OAuth、不上传遥测，不保存 prompt、response、源代码正文、tool output、CSRF 或 OAuth token。价格更新只访问固定官方 HTTPS 定价页面，不上传本地用量数据。数据库位于 `%LOCALAPPDATA%\UsageTray\usage.db`。

## 当前限制

Antigravity 具体内部历史/本地 language server 格式可能随版本变化。JSON/JSONL 或 SQLite 中有可验证 usage 时会记录；未知 SQLite/PB schema 会保守降级到“不可用”，不会用 quota 反推 API 等值。自动价格读取依赖官方页面结构，无法识别时需要在设置页手动编辑价格文件。status-line cache 拆分默认需要用户用真实样本验证后，在设置页启用对应开关。

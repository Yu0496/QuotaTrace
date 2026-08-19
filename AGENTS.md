## 核心规则
1. **维护文档** — 对功能性修改，必须同步修改/完善位于根目录的 `项目文档.md` 对应内容，并在底部的“更新日志”记录修改时间与内容。
2. **语言规范** — 任何供用户阅读的规则、计划、文档或对话，都必须使用中文。
3. **及时纠错** — 如果用户或计划有错误或模糊不清，应在执行前向用户指出。
4. **个人知识库** — 在用户明确要求，或 Agent 判断需要并征得用户确认后，遵循下列规则使用个人知识库。

## 个人知识库

使用时读取：

```text
KB_ROOT = D:/LnowledgeBase
```

调用时禁止全库扫描，按顺序读取：

```text
D:/LnowledgeBase/AGENTS.md
→ D:/LnowledgeBase/00-KB-System/GENERATED-INDEX.md
→ 相关大类 index.md
→ 命中的少量具体文档
```

如需把本项目经验、决策或排错记录写入知识库，必须先读取：

```text
D:/LnowledgeBase/00-KB-System/05-AGENT-MAINTENANCE-PROTOCOL.md
D:/LnowledgeBase/00-KB-System/CONTRIBUTING-GUIDE.md
```

不得在本项目 `AGENTS.md` 中自行定义知识库写入流程。
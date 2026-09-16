# 贡献指南

## 提交规范（Conventional Commits）

所有提交必须使用如下前缀格式：

```
<type>(<scope>): <subject>

[可选 body：动机与影响]
[可选 footer: BREAKING CHANGE 等]
```

### type 类型表

| type | 含义 | 示例 |
|---|---|---|
| `feat` | 新功能 | `feat(ws): 支持ClientAssertion代持凭证建连` |
| `fix` | 缺陷修复 | `fix(ws): 修复StartAsync跨await持有门闩导致的自死锁` |
| `refactor` | 结构调整（不改行为） | `refactor(channel): FeishuChannel按职责拆分partial文件` |
| `perf` | 性能优化 | `perf(core): token缓存读路径去锁` |
| `test` | 仅测试改动 | `test(e2e): 本地mock链路补WS收事件项` |
| `docs` | 仅文档 | `docs: PARITY回填WS死锁修复证据` |
| `style` | 格式（不改逻辑） | `style: 统一文件头usings顺序` |
| `chore` | 构建/工具/杂务 | `chore(codegen): apis.json补录vc尾部资源` |
| `ci` | CI 配置 | `ci: 新增GitHubActions测试矩阵` |

### scope 约定

按模块取值：`core` / `auth` / `event` / `card` / `ws` / `channel` / `im` / `contact` / `authen` / `codegen` / `e2e` / `parity`；跨模块省略 scope。

### 书写规则

- subject 用中文（与仓库主语言一致），不超过 50 字，结尾不加句号
- 一个提交只做一件事；重构与功能修复不混在同一个提交
- 破坏性变更在 body 里写 `BREAKING CHANGE: <说明>`，并升主版本

## 代码风格

- 目标框架 net8.0；开启 `<Nullable>enable</Nullable>` 与 `TreatWarningsAsErrors`
- 单文件建议 ≤ 400 行；超出按职责拆分（partial 分文件 / DTO 独立 / 嵌套类提升）
- 公开 API 必须有 XML 注释（`/// <summary>`，中文，说明语义而非实现）
- 生成代码（`*.g.cs`）由 `tools/FeishuSdk.CodeGen` 产出，不手工编辑；改模板后重新生成
- 与 Go 版（larksuite/oapi-sdk-go）的行为对齐记录在 `PARITY.md`，改动涉及协议行为时同步更新

## 提交前检查单

```bash
dotnet build -c Release        # 零警告零错误
dotnet test  -c Release        # 全部通过
```

真机 E2E（需凭证）：`./run-e2e.sh`

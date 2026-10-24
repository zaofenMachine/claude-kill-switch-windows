# Claude Kill Switch for Windows

面向 **Claude Desktop 和独立 Firefox** 的定向断网保护参考实现：让指定程序只能连接本机专用代理；代理不可用时连接失败，而不是尝试直连。

本仓库提供可复用源码、默认拒绝的模板和人工部署步骤，**不是一键安装器**。克隆仓库或只配置 Clash 分组，不会自动建立系统级保护。

## 工作方式

```text
Claude Desktop ── 固定 HTTP 代理 ──┐
                                ├─ 127.0.0.1:17987 ─ Claude-Safe ─ 手动选择的白名单节点
独立 Firefox ─── 固定 SOCKS5 ────┘
           │
           └─ 其他 IPv4 地址 / IPv6 / 其他 TCP 端口 / UDP：按程序路径由 WFP 阻断
```

- **独立入口**：Mihomo 的 `mixed` listener 绑定 `127.0.0.1:17987`，指定 `Claude-Safe`，不跟随普通代理组。
- **精确白名单**：名称、类型、服务器、端口匹配且没有代理链、重名冲突的叶节点才进入分组。分组为手动 `select`，默认和空分组后备均为 `REJECT`。
- **系统级阻断**：为审计过的可执行文件生成 simplewall 配置，最终由持久化 Windows Filtering Platform 规则阻断非专用代理连接。
- **登录交接**：可注册 Claude Safe Firefox。Desktop 外部登录时临时将它设为默认浏览器，成功回到 Desktop 后可以切回。
- **更新维护**：Desktop 包版本限制与内置引擎的 EXE 路径限制分开处理。在已审计的引擎根目录内，仅已保护的路径可启动，更新生成新路径时先拒绝，维护规则后再放行。

TUN 或系统代理开关不决定固定代理的地址；但切换 TUN、重载配置或停止内核可能中断连接。外部 MCP 工具、虚拟机、系统代理进程不会因为由 Claude 启动就自动继承路径规则。

## 从这里开始

1. 阅读 [边界与架构](docs/architecture.md) 和 [部署步骤](docs/deployment.md)。
2. 自行安装官方依赖；在 Windows PowerShell 5.1 初始化本地目录并编译本仓库的辅助程序：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Initialize-Local.ps1
   powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Build-Helpers.ps1
   ```

3. 编辑被 Git 忽略的 `private/approved-nodes.json`。模板的名单为空，生成的专用分组只能 `REJECT`。
4. 运行 `node scripts/generate-extension.mjs`，审阅 `private/generated/Script.js`，按部署文档安装到 Clash Verge 的全局扩展。
5. 完成 WFP、Desktop 固定代理和可选浏览器配置；执行 [验收](docs/testing.md) 后再使用真实账号。

**生成配置不会安装规则。** 不要跳过第 5 步，也不要将生成文件或订阅提交到仓库。

## 可用工具

| 工具 | 用途 | 是否更改系统网络设置 |
| --- | --- | --- |
| `scripts/generate-extension.mjs` | 生成本地全局扩展 | 否 |
| `scripts/Inspect-Desktop.ps1` | 枚举已安装包及可执行路径，写本地清单 | 否 |
| `scripts/Inspect-DesktopRuntime.ps1` | 核对运行中 claude.exe 的实际/内核路径、包身份及签名 | 否 |
| `scripts/prepare_firewall.py` | 生成待审阅的 simplewall 配置 | 否 |
| `scripts/engine_gate.py` | 准备内置引擎的 EXE 启动限制；拒绝自动覆盖已有 EXE 策略 | 否 |
| `scripts/compare_wfp.py` | 与本机已审计基线比较 WFP 规则 | 否 |
| `scripts/verify_wfp_extension.py` | 验证旧规则保留，新增路径仅复制已审计的阻断范围 | 否 |
| `scripts/check-proxy.mjs` | 无账号检查专用代理出口国家，隐藏明文出口 IP | 否；会访问 Cloudflare |
| `scripts/Register-ClaudeBrowser.ps1` | `Check` 检查、`Register` 注册、`Remove` 撤销专用浏览器入口 | Register/Remove 改当前用户注册表；不自动设为默认 |
| 根目录三个 CMD | 打开专用浏览器、出口测试页、Windows 默认应用设置 | 打开应用或设置页 |

构建不下载任何第三方二进制。浏览器入口会检查锁定策略文件的哈希，失败时停止，不回退普通浏览器。

## 文档

- [部署与依赖](docs/deployment.md)
- [保护范围与限制](docs/architecture.md)
- [测试步骤与已知证据](docs/testing.md)
- [应用更新及回滚](docs/maintenance.md)
- [内置引擎独立更新的保护](docs/engine-updates.md)
- [哪些内容没有上传，以及如何在本机准备](docs/private-materials.md)
- [第三方组件](THIRD_PARTY.md)

## 验证仓库源码

Node.js 22+、Python 3.10+；这些测试不停止代理、不安装防火墙、不访问 Claude 账号。

```powershell
node --test tests/allowlist.test.mjs
python -m unittest discover -s tests -p 'test_*.py'
python scripts/check-publication.py
```

最后一个命令检查 Git 索引中的文件，不能替代人工审查。CI 额外检查 PowerShell 语法和编译 C#，不会自动运行实际故障注入。

## 状态

最初实机验证环境包含 Windows 10 Pro 22H2、Clash Verge 2.5.2、Mihomo 1.19.29、simplewall 3.9.1、Claude Desktop 2.42304.0.0；这是历史证据，**不代表所有后续版本已兼容**。公开版经过脱敏和生成器调整，离线测试通过不等于在新机器完成了联网验收。

本项目不证明节点信誉、账号安全或全系统零泄漏，也不改变服务方的使用条件。仓库暂未指定开源许可证；第三方组件遵循各自许可且不随仓库分发。

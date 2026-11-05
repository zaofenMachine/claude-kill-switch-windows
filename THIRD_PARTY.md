# 第三方组件

以下项目是运行或部署时可能需要的依赖，不随此仓库分发，遵循各自许可。请从官方来源取得并核验签名或官方校验值；本仓库不提供第三方安装包镜像。

| 组件 | 官方来源 | 用途 |
| --- | --- | --- |
| Clash Verge Rev | https://github.com/clash-verge-rev/clash-verge-rev | 管理订阅和全局扩展 |
| Mihomo | https://github.com/MetaCubeX/mihomo | 专用 listener 与手动选择组 |
| simplewall | https://github.com/henrypp/simplewall | 创建并管理持久化 WFP 过滤器 |
| Firefox | https://www.mozilla.org/firefox/ | 可选独立浏览器 |
| Firefox 管理员文档 | https://firefox-admin-docs.mozilla.org/ | 验证企业策略行为 |
| Claude Desktop | https://claude.com/download | 目标应用，按安装版本核对代理与包路径 |
| Node.js | https://nodejs.org/ | 配置生成、只读检查、离线测试 |
| Python | https://www.python.org/ | XML/配置生成和比对；公开工具仅使用标准库 |
| .NET 10 | https://dotnet.microsoft.com/ | 独立 Windows 管理器的构建与运行 |
| Windows App SDK 2.6.5 / WinUI 3 | https://github.com/microsoft/WindowsAppSDK | Fluent 桌面界面及窗口能力；NuGet 锁定版本和内容哈希 |
| YamlDotNet 16.3.0 | https://github.com/aaubry/YamlDotNet | 管理器的 YAML 解析/生成；NuGet 锁定版本及内容哈希，MIT 许可见 LICENSES/YamlDotNet-MIT.txt |

构建管理器会通过 NuGet 获取 YamlDotNet、Windows App SDK 及其依赖，自包含发布目录还包含 .NET 运行时。应完整保留发布目录中的组件和许可文件；源码仓库不提交第三方二进制。Windows App SDK 的源码和再分发条款以官方包及其 [LICENSE](https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE) 为准。

Windows PowerShell、.NET Framework 编译器和 WFP/AppLocker API 属于 Windows 平台能力；具体可用性依 Windows 版本与安装状态而定。

本仓库不是 Anthropic、Mozilla、Microsoft、Clash Verge、Mihomo 或 simplewall 的官方项目。项目名称仅用于说明适用对象。

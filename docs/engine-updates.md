# 内置引擎独立更新保护

## 为什么仅限制 Desktop 包版本还不够

一次实机审计发现：Desktop 主包仍是已批准版本，但主进程启动了用户缓存中的独立 `claude-code` 引擎。该引擎没有包身份，原先按安装包枚举出的 WFP 路径不包含它。

还发现 CIM 报告的逻辑路径与内核实际文件路径不同。MSIX 可能把用户目录映射到包的 `LocalCache`；仅复制进程列表里看起来正确的路径，可能生成无法匹配进程的规则。

本方案为当前已审计引擎增加相同的网络阻断，并在明确的引擎根目录下拒绝其他 EXE 路径启动。新版本换路径时，保持拒绝，直到先补齐网络规则再加入精确启动例外。

## 本地准备

1. 保留原 Desktop Appx 规则与原 WFP 基线；备份本地及有效 AppLocker 策略、simplewall 配置、当前保护清单。
2. 运行 `Inspect-DesktopRuntime.ps1`。检查 `actualPath`、`nativePath`、`packageIdentity`、签名、哈希及父进程。它仅枚举名为 claude.exe 的实例，不能替代完整进程树审计。
3. 将已经核实的实际引擎路径添加到 `private/targets.json`，并重新准备 WFP 配置。旧目标必须全部保留。
4. 从空模板准备本地请求：

   ```powershell
   Copy-Item templates\engine-approval.example.json private\engine-approval.json
   Get-AppLockerPolicy -Local -Xml | Set-Content private\applocker-before.xml -Encoding utf8
   Get-AppLockerPolicy -Effective -Xml | Set-Content private\applocker-effective-before.xml -Encoding utf8
   ```

   两个导出命令在 Windows PowerShell 5.1 中运行，必要时提升权限。请求包含两个数组：`roots` 是明确的 `claude-code` 根目录，`approvedExecutables` 是已审计的实际 exe 完整路径。逻辑根目录和物理缓存根目录如果不同，应分别评估；不要为没有对应 WFP 的逻辑路径添加启动例外。

5. 运行 `python scripts/engine_gate.py`，得到 `private/generated/engine-gate-blocked.xml` 与 `engine-gate-approved.xml`。生成器不安装策略。

生成器要求本地与有效策略一致，并拒绝任何已有 EXE 集合，避免用“其他 EXE 全部允许”意外放宽他人的应用白名单。已有门禁的升级维护也需要先审阅当前策略、识别自己拥有的规则并合并，不能重新导入过时快照。

## 策略含义

候选保留所有已有非 EXE 集合，新增 EXE 集合：

- 其他可执行文件保持允许。这只适用于之前没有 EXE 集合的环境。
- 每个指定引擎根目录添加一条递归路径拒绝规则。
- `blocked` 阶段没有例外；`approved` 阶段只为已完成 WFP 验证的精确 exe 路径设置例外。

新目录、新文件名即使仍有有效签名，也不会自动获得放行。原路径内替换程序仍受到该路径的 WFP 约束。路径门禁不是哈希防篡改措施，也不阻止有人把程序移到门禁范围外；管理员、恶意代码和新存储布局不在这项保证内。

## 应用与验收顺序

1. 对候选执行 `Test-AppLockerPolicy`：当前批准引擎、其他正常程序应允许；放在模拟新版本目录中的无账号诊断程序应拒绝。
2. 维护窗口内先安装 blocked 阶段。它限制新的引擎启动，不主动终止现有进程。等待 `Get-AppLockerPolicy -Effective` 与完整候选一致，不能把刚调用 Set-AppLockerPolicy 的返回当成立刻生效。
3. 更新 WFP 配置并导出当前过滤器。确认所有旧过滤器的条件、动作、层、权重、flags 保留，新增规则只是已审计参照程序的规则换成了目标引擎应用身份。
4. 可使用 `verify_wfp_extension.py` 做完整比对。本地 spec 包含 `referenceAppId` 和 `addedAppIds`，值来自已审计的 WFP 导出与运行进程的实际内核路径：

   ```powershell
   python scripts/verify_wfp_extension.py private/wfp-before.xml private/wfp-after.xml private/wfp-extension-spec.json
   ```

   工具只比较 simplewall provider；还需独立核对其他 provider 没有意外变化。
5. 验证通过后安装 approved 阶段，再次等待有效策略完全一致。若任何验证失败，保留 blocked 状态并修复，不先放开未经验证的引擎。
6. 实际尝试启动模拟新路径中的无账号诊断程序，预期 Windows 策略拒绝。批准引擎仅运行 `--version` 做启动烟测，不发送聊天。新路径诊断程序应使用自己的探针，不能替换真实引擎文件来测试。
7. 更新本机审计基线与保护清单。运行时覆盖审计使用实际进程路径；旧的固定路径计数和故障注入脚本不能继续冒充完整覆盖。

## 一次实机结果与边界

本次扩展从 9 个路径、81 条阻断变为 10 个路径、90 条阻断。新增引擎的 9 条规则与已审计探针范围一致，原规则和另外 408 条其他 provider 过滤器的比较内容一致。比对忽略重新生成的 filter ID/key 与不透明 providerData。

离线评估中 152 个现有可执行文件允许；实际模拟新路径返回 Win32 错误 1260；批准引擎的 `--version` 成功。用户确认当前 Desktop 会话正常。此过程没有触发真实升级、重启 Desktop 或发送账号测试请求，不等于覆盖所有未来引擎布局、其他 MCP 程序或启动最早阶段。

该实机安装包装脚本与快照只保存在原机器，公开仓库提供路径检查、策略生成、WFP 增量验证及测试源码；没有把依赖特定版本和私有基线的管理员安装脚本直接发布成通用安装器。

## 参考

- [Microsoft：AppLocker 的规则集合](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-applocker-rule-collections)
- [Microsoft：路径条件及其限制](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-the-path-rule-condition-in-applocker)
- [Microsoft：规则例外与拒绝优先级](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-applocker-rule-exceptions)

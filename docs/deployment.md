# 人工部署步骤

公开版不执行防火墙安装、AppLocker 强制策略或代理内核停止。先完成所有准备，再按顺序启用；中途未完成时不要假定已有完整保护。

## 1. 环境与本地目录

准备 Windows、Windows PowerShell 5.1、Node.js 22+、Python 3.10+、Clash Verge/Mihomo；可选独立 Firefox 和 simplewall portable。下载来源见 [第三方组件](../THIRD_PARTY.md)。本仓库没有第三方安装包。

将仓库放在长期固定的本地目录。WFP 匹配绝对路径，后续搬目录需要维护规则。

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Initialize-Local.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Build-Helpers.ps1
```

初始化脚本限制 `private/`、`runtime/`、`reports/`、`backups/` 的目录 ACL 为当前用户、SYSTEM 和管理员。它不会改代理、注册表或防火墙。不要将真实数据存入 templates、docs 或 src。

保存现有配置：Clash 全局扩展、当前生成配置、当前订阅选择、系统代理、Desktop 相关策略、WFP 状态、AppLocker 有效/本地策略及服务状态。所有原始导出放入本地私有目录。

## 2. 批准节点并生成扩展

编辑 `private/approved-nodes.json`。只从自己的订阅读取四个身份字段，**不要复制整个代理定义或订阅 URL**。

```json
{
  "expectedCountry": "SG",
  "pins": [
    { "name": "My approved node", "type": "vless", "server": "node.example.invalid", "port": 443 }
  ]
}
```

示例域名不可用，必须替换成真实身份。名称、类型和服务器使用精确匹配；端口使用整数。

```powershell
node scripts/generate-extension.mjs
```

输出为 `private/generated/Script.js`。先保存原全局扩展，然后在 Clash Verge 的全局扩展脚本中安装这份内容；如果已有其他全局逻辑，先合并，不能直接覆盖。查看生成配置，确认：

- `Claude-Safe` 只含 `REJECT` 和被批准的可用叶节点。
- listener 为 TCP `127.0.0.1:17987`，`udp: false`，`proxy: Claude-Safe`。
- 其他代理组、TUN、DNS、订阅及普通选择保持原意。
- 用安装中的 Mihomo `-t` 检查完整候选配置后再重载；同时确认磁盘上供重启读取的配置与当前运行配置一致。

这一步可能影响现有连接，先保存工作。不要把别人的备份配置覆盖到自己的订阅上。随后在 Claude-Safe 里手动选节点；不选择时保持 REJECT。

## 3. 审阅 Desktop 路径

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Inspect-Desktop.ps1
```

在本机阅读 `private/desktop-inventory.json`。将实际需要保护的 Desktop 主程序及相关辅助 exe，连同受保护探针、可选独立 Firefox，填入 `private/targets.json` 的 `targets` 数组。使用真实绝对路径。**不要加入 ControlProbe.exe**，它用于对照。

枚举不会自动审计所有辅助程序；检查签名、实际版本和用途。传统安装包与 MSIX 路径结构可能不同，脚本不识别时应停止并人工确认。

## 4. 生成并安装路径级阻断

```powershell
python scripts/prepare_firewall.py
```

输出在 `private/generated/simplewall/`：`profile.xml`、`simplewall.ini` 和 `plan.json`。脚本验证目标文件存在，但**不会安装规则**。

先查看现有 simplewall 配置和 WFP provider。如果已有 simplewall 策略，必须合并审阅，不能覆盖。在无既有配置的专用 portable 实例中，工具关闭时将两份配置放入其实际可执行文件目录；以管理员身份启动官方工具，按其界面启用过滤。

启用前逐条确认：全局入站/出站阻断关闭；内部默认规则与内置封锁列表关闭；规则只关联审阅过的路径；没有一般回环豁免；四条自定义规则范围与生成清单一致。不要通过关闭 Windows 防火墙或第三方安全软件来“修复”安装问题。

以管理员身份导出 WFP：

```powershell
netsh.exe wfp show state file=.\private\wfp-after.xml
```

核查实际过滤器的应用身份、方向、层、地址、端口、协议、BLOCK 动作及持久化标志，并比较其他 provider 是否受到意外影响。审核通过的快照才可另存为 `private/wfp-audited-baseline.xml`。之后可以用：

```powershell
python scripts/compare_wfp.py private/wfp-audited-baseline.xml private/wfp-after.xml
```

比对工具只确认与已审阅基线一致，**不能替你完成首次基线审计**。安装未达预期时，先关闭目标应用，按本机备份恢复自己拥有的规则。

## 5. Desktop 固定代理

保存工作，从托盘彻底退出 Desktop。先检查并备份当前用户与整机的 `SOFTWARE\Policies\Claude`。如果整机策略或现有用户策略存在冲突，按管理来源合并，不能盲目覆盖。

在已确认本机版本支持该策略后，为当前用户设置字符串值：

```text
HKCU\SOFTWARE\Policies\Claude
egressProxyUrl = http://127.0.0.1:17987
```

重新打开 Desktop，核对启动日志确实采用该代理，再检查真实进程连接。这里没有提供直接修改注册表的默认运行脚本，以免无意覆盖已有管理策略。请结合 [官方网络代理文档](https://claude.com/docs/third-party/claude-desktop/network-proxy) 和本机安装版本确认适用范围；新版产品的代理继承行为可能不同。

## 6. 可选专用 Firefox

单独安装/解包 Firefox 到 `runtime/firefox/core/firefox.exe`，不要复用日常浏览器配置目录。将 `templates/firefox-policies.json` **逐字节复制**到 `runtime/firefox/core/distribution/policies.json`。不要重新格式化该文件，启动器会验证其 SHA-256。

在 Firefox 的 `about:policies` 确认代理及相关限制实际生效，并将这个 firefox.exe 路径纳入第 4 步保护。独立 profile 位于 `runtime/claude-browser-profile`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Register-ClaudeBrowser.ps1 -Mode Register
```

注册只创建当前用户的浏览器候选，不强行修改默认关联。双击“设置-Claude-登录浏览器.cmd”，在 Windows 设置里选 **Claude Safe Firefox**。默认关联正确不代表 WFP 已部署。

用“打开-Claude-出口测试页.cmd”检查地区；它固定使用专用 Firefox，不依赖默认浏览器。网络检查可使用：

```powershell
node scripts/check-proxy.mjs
```

## 7. 登录与后续使用

登录期间让系统 HTTP/HTTPS 默认浏览器保持 Claude Safe Firefox。Desktop 打开的外部认证页应进入它；浏览器询问打开 `claude://` 回调时，确认目标是安装的 Desktop。返回 Desktop 并确认登录成功后，可以切回原默认浏览器。邮件软件自带的内置浏览器不受这项默认关联保证。

不要为测试主动退出已正常使用的账号。先完成 [无账号验收](testing.md)，真实认证留到实际需要登录时确认。版本限制、升级流程和撤销步骤见 [维护文档](maintenance.md)。

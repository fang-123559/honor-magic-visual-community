# v0.4.6 测试证书安装指南

如果点击「一键安装」后看到红色的「操作未完整完成」和错误码 `0x800B0109`，表示 Windows 在验证安装包时遇到了证书信任问题。v0.4.6 第一次安装到一台电脑时，需要这台电脑先信任本版的测试证书。

让大家点了「一键安装」，直到报错才知道还要手动装证书，是我们的引导没有讲清楚。给大家添麻烦了，对不起。**下一个版本会简化安装流程。**

## 第一步　下载并核对本版证书

在 v0.4.6 发布页的 **Assets** 中下载 `MagicCommunity.Native.Local.cer`。它和安装 ZIP 分开提供。[下载 v0.4.6 公钥证书](https://github.com/littletaro97-arch/honor-magic-visual-community/releases/download/v0.4.6/MagicCommunity.Native.Local.cer)

双击这个文件，打开「详细信息」，找到「指纹」。对照下面这行，显示时的大小写或空格可以忽略。指纹一致再继续。

```text
4F030B9E4CF34E129A8578DB66603E81EBE405A0
```

## 第二步　把证书装到「本地计算机 → 受信任人」

1. 切回证书窗口的「常规」，点「安装证书」。在「存储位置」选 **本地计算机**。Windows 弹出授权提示时，需要这台电脑的管理员确认。
2. 选「将所有的证书都放入下列存储」，点「浏览」，选择 **受信任人（Trusted People）**。
3. 点「确定」→「下一步」→「完成」。看到导入成功的提示后，关闭向导。

这一步对同一台电脑上的 Windows 账户都生效。如果之前选了「当前用户」，请按上面的位置重新导入。

## 第三步　返回安装器重试

如果仍在红色报错页，点右下角「返回检查」，再点「一键安装」。如果已经关闭安装器，解压下载的 ZIP，重新运行里面的 `MagicCommunity-Setup-0.4.6-x64.exe` 即可。[v0.4.6 安装包与发布说明](https://github.com/littletaro97-arch/honor-magic-visual-community/releases/tag/v0.4.6)

### 已经导入，仍看到同一个错误？

在**报错的那台电脑**上打开 PowerShell，粘贴这一行。它只检查证书所在的位置。

```powershell
Test-Path 'Cert:\LocalMachine\TrustedPeople\4F030B9E4CF34E129A8578DB66603E81EBE405A0'
```

- 返回 `False`，请回到第二步，核实选的是「本地计算机 → 受信任人」。
- 返回 `True`，请重试一次。若仍然失败，请在反馈里附上此次错误码、安装窗口给出的 `ActivityId` 和 Windows 版本，以便我们查清原因。[反馈安装问题](https://github.com/littletaro97-arch/honor-magic-visual-community/issues)

本指南适用于 v0.4.6 测试包。关于这一错误码和证书存储位置，也可参考[微软的 MSIX 安装排障说明](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide)。

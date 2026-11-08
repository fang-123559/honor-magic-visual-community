# 第三方来源与许可记录

项目原创软件代码采用根目录 `LICENSE` 中的 AGPL-3.0-only。本文件记录独立适用的第三方来源和权利边界；任何第三方素材、组件或商标均不因项目源代码许可而重新授权。

## 第三方内容

### 荣耀（HONOR）专有素材

| 路径 | 内容 | 来源 |
| --- | --- | --- |
| `assets/honor-icons/` | 402 项应用图标（233 PNG / 157 SVG / 6 ICO） | 从 HONOR MagicAnimation、MagicTaskbarApp 的 Resources 提取 |
| `resources/transitions/` | 进场动画（`entrance.mp4`、帧序列、logo 帧） | 荣耀开机/进场动画素材 |
| `assets/community/` | 更新窗口应用图标 | 用户提供的原型内嵌图 |
| `assets/honor-fonts/` | HONOR Sans CN Light、Regular、Medium；应用私有加载 | 用户要求嵌入的本机 Windows Fonts 原始文件；哈希见该目录 manifest.json |
| `Assets/Logo*.png` | 应用图标 | 参考荣耀官方视觉 |

**这些素材的著作权归荣耀（HONOR）所有，本项目不拥有其版权，也不以项目源代码许可重新授权。项目负责人已确认取得这些素材对外再分发许可；素材仅在该单独授权范围内随公开仓库和发行物分发。**

授权文件及其具体限制不随公开仓库发布。字体仍采用应用私有加载，不会安装到 Windows 或修改字体注册表；来源与逐文件 SHA-256 见 `assets/honor-fonts/manifest.json`，详见 `assets/honor-fonts/NOTICE.md`。

### 源自 Seelen UI 的内容与 SDK（按各自上游许可）

| 路径 | 内容 |
| --- | --- |
| `assets/native-shell/` 中的 Dock 默认图标与顶栏/Dock 界面翻译 | 复制自 Seelen UI 源码 |
| `experiments/native-host-patch/` | 固定版本 Seelen 2.8.9 的上游修改源码 |
| `widgets/desktop/` | 当前 Widget 构建导入 `@seelen-ui/lib` 2.8.4 SDK；构建还读取上游样式 |

相关许可全文见 `assets/native-shell/licenses/Seelen-LICENSE` 与 `widgets/desktop/LICENSE-Seelen-SDK.txt`；依赖和版本说明见 `widgets/desktop/THIRD-PARTY-NOTICES.md`。

**截至 v0.4.9：Windows 桌面宿主使用独立 Win32/.NET 实现，不再启动 Seelen UI 宿主程序；桌面 Widget 构建仍导入并打包 `@seelen-ui/lib` 2.8.4，且复用上游样式。** 因此宿主已独立，但当前源码构建仍含 Seelen UI 来源组件。该 SDK、样式、图标、翻译和上游实验源码适用各自上游许可，不因根目录 AGPL 许可改变。


### 第三方开源组件

见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

---

## 商标

「HONOR」「荣耀」「magic 视界」及其图形标识是其权利人的商标。

本项目是社区独立实现，与荣耀（HONOR）不存在隶属、合作或背书关系，不代表官方立场，也不提供官方支持。

本项目的许可状态不构成任何商标授权。再分发本项目的修改版本时，请不要使用上述商标作为来源标识，也不要让用户误以为该版本来自荣耀官方。

「Magic 视界社区版」是本项目自用的名称，用于说明设计来源与技术渊源。

---

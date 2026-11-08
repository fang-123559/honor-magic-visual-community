# 第三方组件与许可

项目原创软件代码采用根目录 `LICENSE` 中的 AGPL-3.0-only。此清单记录不由该项目许可覆盖、而是分别适用各自许可或授权的第三方内容。
各项的完整许可全文保存在对应目录，或可在所列链接处获取。

---

## 运行时

### .NET 10 / Microsoft.WindowsDesktop.App

- 许可：MIT
- 版权：.NET Foundation and Contributors
- 链接：https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- 说明：本项目以自包含方式发布，安装包内包含 `dotnet/` 目录下的 .NET 运行时（`Microsoft.NETCore.App` 10.0.13、`Microsoft.WindowsDesktop.App` 10.0.13）。按 MIT 要求保留版权与许可声明。
- 本项目使用的 WPF 属于 `Microsoft.WindowsDesktop.App` 的一部分。

---

## 图标

顶栏、Dock 与系统弹窗的原生图标由 **react-icons 5.7.0** 的既有资源导出，图标几何未改动，仅将填充/描边统一适配为跟随系统主题的 `currentColor`。
参考资源见 `assets/native-shell/`，导出脚本 `tools/dev/export-toolbar-reference.mjs`。

| 图标集 | 许可 | 链接 |
| --- | --- | --- |
| Font Awesome 5 | CC BY 4.0 | https://fontawesome.com/ |
| Font Awesome 6 | CC BY 4.0 | https://fontawesome.com/ |
| Ionicons 4 | MIT | https://ionicons.com/ |
| Ionicons 5 | MIT | https://ionicons.com/ |
| Material Design Icons | Apache-2.0 | https://github.com/google/material-design-icons |
| GitHub Octicons | MIT | https://github.com/primer/octicons |
| Lucide | ISC | https://lucide.dev/ |
| Game Icons | CC BY 3.0 | https://game-icons.net/ |
| Ant Design Icons | MIT | https://github.com/ant-design/ant-design-icons |
| Bootstrap Icons | MIT | https://github.com/twbs/icons |
| Remix Icon | Apache-2.0 | https://github.com/Remix-Design/RemixIcon |
| Heroicons | MIT | https://github.com/tailwindlabs/heroicons |
| Heroicons 2 | MIT | https://github.com/tailwindlabs/heroicons |
| BoxIcons | MIT | https://github.com/atisawd/boxicons |
| css.gg | MIT | https://github.com/astrit/css.gg |
| VS Code Icons | CC BY 4.0 | https://github.com/microsoft/vscode-codicons |
| Tabler Icons | MIT | https://github.com/tabler/tabler-icons |
| Phosphor Icons | MIT | https://github.com/phosphor-icons/core |
| Icons8 Line Awesome | MIT | https://icons8.com/line-awesome |

> CC BY 4.0 / CC BY 3.0 的图标在再分发时需要保留署名。上面这张表即为署名清单，修改版本请一并保留。

---

## 日历与本地化

### Moment.js

- 许可：MIT
- 链接：https://github.com/moment/moment/blob/develop/LICENSE
- 用途：日历卡片的本地化数据
- 许可全文：`assets/native-shell/licenses/moment-LICENSE`

---

## 数据与服务

### Open-Meteo

- 许可：CC BY 4.0
- 链接：https://open-meteo.com/
- 用途：天气数据

### GeoNames（via Open-Meteo）

- 许可：CC BY 4.0
- 链接：https://www.geonames.org/
- 用途：城市检索

公共免费端点按非商业用途使用，具体条款以供应商官网为准。

---

## Seelen UI

- `@seelen-ui/lib` 2.8.4：AGPL-3.0-only
- 链接：https://github.com/eythaann/Seelen-UI
- 本项目中的位置：`widgets/desktop/src.js`、`widgets/desktop/grid-src.js` 导入 SDK；`widgets/desktop/build.mjs` 构建桌面 Widget 并读取 `experiments/native-host-patch/` 中的上游样式；`assets/native-shell/` 保留源自上游的 Dock 图标和界面翻译；`experiments/native-host-patch/` 与 `references/seelen-v2.8.9/` 保留上游源码与许可证
- 许可全文：`widgets/desktop/LICENSE-Seelen-SDK.txt`、`assets/native-shell/licenses/Seelen-LICENSE`

v0.4.9 的桌面窗口宿主使用独立 Win32/.NET 实现，不再启动 Seelen UI 宿主程序；桌面 Widget 构建仍导入并打包 `@seelen-ui/lib` 2.8.4，且复用上游样式。因此宿主已独立，但当前仓库的 Widget 构建仍包含 Seelen UI 来源组件。根目录 AGPL-3.0-only 仅覆盖项目原创软件代码，第三方来源部分仍保留各自版权和许可。范围说明见 [NOTICE.md](NOTICE.md)。

---

## 荣耀（HONOR）素材

不是开源组件。图标、进场动画、应用图标与 HONOR Sans CN 字体的著作权归荣耀所有。项目负责人已确认取得对外再分发许可；这些素材依授权范围单独再分发，不纳入本项目 AGPL 许可。
字体仅按用户要求加入本地构建，采用应用私有加载；来源和逐文件 SHA-256 见 `assets/honor-fonts/manifest.json`，权利说明见 `assets/honor-fonts/NOTICE.md`。
详见 [NOTICE.md](NOTICE.md) 与 `assets/honor-icons/NOTICE.md`。

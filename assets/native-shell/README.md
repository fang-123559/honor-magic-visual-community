# Native toolbar and Dock assets

本目录是原生顶栏和 Dock 的项目内资产源。全部 SVG、数据表及许可证均为普通文件副本；不使用符号链接、目录联接、快捷方式或运行时远程下载。

## 内容

- `icons/`：194 个 SVG，包括内置模块、菜单、蓝牙分类、社区电池、Dock 开始/桌面/回收站/缺省媒体图标。
- `manifest.json`：每个 SVG 的文件路径及 SHA256。
- `strings.json`：73 种语言的顶栏及 Dock 文案。
- `calendar.json`：140 种 Moment locale 的月份、星期、日期格式、数字与序数数据。原生格式器不执行 JavaScript。
- `licenses/`、`NOTICE.md`：复制的上游许可证和出处。出处网址是归属信息，渲染及生成不从网址读取资产。

## 日常生成

在仓库根目录执行 `node tools/dev/export-toolbar-reference.mjs`。该模式仅使用本目录普通文件生成 `native-toolbar-icons.h`、`native-shell-strings.h`、`native-calendar-data.h`，不加载 Seelen、React、react-icons、Moment 或共享依赖。C++ 编译直接使用入库头文件，无需生成步骤。

仅在有意更新参照版本时使用 `--refresh-reference`；此模式只读锁定的参考依赖，将新资产内容复制进本目录，并更新 Rust 的蓝牙与文案数据文件。更新后审查差异、清单哈希及归属信息。

Windows 应用图标、用户头像、媒体封面及用户自定义图标属于运行时数据，通过现有认证接口读取和有界缓存，不把用户数据加入本目录。资源在项目内独立保存不能单独证明后台实现独立。当前 Magic.System.Native/1 选择使用独立 C++ 系统后台；旧 Magic.System/1 与 Seelen 宿主适配仍作为兼容/回滚分支保留，不能据目录名称断言已经清除。部分 Dock SVG 和翻译数据仍来源于 Seelen，具体归属见 NOTICE.md 与 licenses/；弹窗身份改名不改变这些来源声明。


## 后续图标替换来源

用户指定的荣耀图标库已登记于 [后续图标替换来源](../../docs/development/后续图标替换来源-20261026.md)。后续优先从该库选择合适的内置图标及完整状态组，按现有UI风格逐步替换；实际采用的资源仍须收入项目普通资产文件及清单，运行时不依赖该外部目录。本轮仅登记来源，不替换或加载。


## 已接入荣耀双套图标

原生顶栏与Dock现在优先使用[社区版荣耀资产](../honor-icons/README.md)，设置中可即时切换白底与通透。该源是项目普通文件，运行时不访问外部库；未映射的第三方、动态托盘及自定义图标保留实际来源。旧目录中的历史资源及翻译出处按NOTICE保留，不因更换默认图标而删除许可声明。

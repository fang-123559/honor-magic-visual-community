# Windows 菜单图标来源

这些 PNG 是 2026-10-24 从本机 Windows 11 CommandStore 注册的系统图标资源导出的项目内栅格副本；来源模块、资源索引及对应命令见 manifest.json。压缩图标缺少注册 Icon 时使用本机 imageres.dll 的压缩文件资源。

导出工具：tools/dev/export-shell-menu-icons.ps1。PNG 以 NativeShellMenu.* 名称嵌入程序集；运行显示不依赖这些源 DLL 的外部链接。本目录不包含 DLL 或字体文件，也不代表现代 Explorer 所选 Fluent 图形与这些注册图标像素相同。

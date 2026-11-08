# 独立系统后台开发边界

本目录的核心只使用 C++ 标准库、Windows SDK 与 Windows API。不得包含或链接 experiments/native-host-patch、seelen_core、Tauri、WebView2、上游 Hook 或资源模板；不得从 Seelen 复制实现。

系统托盘没有公开的全量图标枚举接口，而用户明确要求完整自绘。允许仅在 tray-shell-compat.cpp 中隔离 Windows Shell COM 兼容接口的 ABI 声明；该接口并非 Microsoft 稳定公开契约，必须单独说明来源、版本风险和探测结果。不支持时明确失败，不借旧宿主补功能。只注册/取消只读回调，不调用更改 Windows 托盘偏好的接口；涉及用户图标动作仍须核对实时 HWND/PID/创建时间与图标身份。

自绘图标采集由相邻 src/tray-capture 的自有 DLL 使用 Windows WH_CALLWNDPROCRET，定向附加经核对的 Explorer 任务栏线程；兼容 Shell_NotifyIcon 的 WM_COPYDATA 数据布局，不改写 Explorer 代码、不使用上游 Hook。共享队列、图标与 PNG 有硬上限；正常退出主动断开，异常后只清理经 PID/创建时间核对的失效属性。诊断只添加/删除自有测试图标，不能点击其他应用或隐藏正式任务栏。

音频默认输出切换的 Windows 私有 IPolicyConfig ABI 仅允许在 audio-policy-compat.cpp 声明；必须先核对当前活动的输出设备，明确接口不可用状态，禁止通过旧宿主兜底。

逐模块迁移阶段按实际实现报告，不能以健康检查、进程名或协议名称宣布托盘/通知/配置完成。隔离检查只启动自有后台和测试窗口，不接管桌面、不隐藏任务栏、不改变现场布局/权限。正式集成与加载遵守根 AGENTS.md。使用 tools/dev/check-native-system.ps1 构建并运行隔离检查。

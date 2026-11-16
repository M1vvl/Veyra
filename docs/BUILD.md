# uilicd

Windows 11 x64 桌面 Dock、右侧控制中心与映射收纳盒。主程序使用 C# / WPF / D3D11，玻璃遵循 Background → Glass → Content 六步骤管线。

## 构建独立 EXE

需要 Windows、.NET 8 SDK 与 PowerShell。首次还原依赖需要访问 NuGet；SDK 仅用于构建，最终 EXE 自带运行时。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

默认输出 `dist\uilicd.exe`。也可指定 SDK 和输出位置：

```powershell
.\build.ps1 -DotnetPath 'C:\路径\dotnet.exe' -OutputDirectory 'D:\输出目录'
```

构建会先编译项目，重新生成并校验嵌入式光学着色器，再发布 Windows x64 单文件。保留 WPF / WinRT 所需元数据，不进行裁剪或压缩。原生运行库在首次运行时由 .NET 自动释放到系统临时目录，无需额外安装 .NET。

## 代码结构

- `src/uilicd`：完整主程序项目，包括图标、XAML、原生接口和着色器。
- `src/uilicd/SettingsWindow*.cs`：设置页与控件；`Branding.cs`：产品名称与图标。
- `src/uilicd/Dock`、`Shell`、`Services`、`DesktopOrganization`：Dock、托盘、系统控制与收纳模块（以实际目录为准）。
- `src/uilicd/Rendering`：原生光学渲染与窗口背景采集。
- `scripts`：构建与图标格式转换；原始品牌图片位于 `Assets/uilicd-original.png`。
- `optics-webgl`：独立的 1080×1920 Three.js 光学核对场景；运行方法见其 README，不是主程序运行依赖。
- `licenses`、`THIRD_PARTY_NOTICES.md`：第三方许可与版权说明；WitchDrawer 适配代码另附原始许可。

源码不含个人收纳内容、缓存、SDK、node_modules、历史截图或无关项目。NuGet 与 npm 依赖分别由项目文件和 package-lock.json 还原。

## 设置兼容与恢复

为保留现有数据，内部命名空间、单实例互斥锁与 `%LOCALAPPDATA%\GlassDock` 数据目录继续兼容旧版；所有产品显示名称与可执行文件名为 uilicd。首次正常运行会迁移本软件自己的旧自启注册项，并保留 Windows 对它的启用 / 禁用状态，不修改其他软件的自启项。

映射收纳盒始终保存原文件路径，不移动源文件。关闭 uilicd 会恢复原生任务栏；必要时可执行 `uilicd.exe --restore-taskbar`。

## 定向验证

```powershell
.\dist\uilicd.exe --settings-preview settings-report.json
```

该模式验证设置玻璃、图标、控件、重置和透明度，生成报告与截图，不写入个人设置。正常启动直接运行 `uilicd.exe`；托盘菜单可打开设置。

# Veyra

[下载 Windows 单文件版](https://github.com/M1vvl/Veyra/releases/latest) · [直接下载 1.0.3 EXE](https://github.com/M1vvl/Veyra/releases/download/v1.0.3/Veyra.exe)

Veyra 是采用 Liquid Glass 材质的 Windows 桌面工具，提供 Dock、右侧状态栏、控制中心和映射收纳盒。

打开发布页，在 Assets 中下载 `Veyra.exe`，保存到自己的文件夹后双击运行。程序自带运行时，首次启动会自动展开依赖并短暂等待。支持 Windows 11 x64。

同时下载并解压 `Veyra-1.0.3-Licenses.zip`，保存随附的第三方许可。`SHA256SUMS.txt` 提供文件校验值。

仓库 Code 页面提供完整开发源码，可直接运行的软件在 Releases。构建需要 Windows 和 .NET 8 SDK，在源码根目录运行以下命令。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出文件为 `dist\Veyra.exe`。完整构建说明见 [docs/BUILD.md](docs/BUILD.md)。

## 功能

- Liquid Glass Dock，支持拖入应用、运行窗口识别和磁吸放大。
- 右侧状态栏与控制中心，支持音量、亮度、网络、输入法、日历和隐藏托盘。
- 映射收纳盒，保存文件和文件夹的绝对路径引用，支持独立命名、拖放排序、自动隐藏、固定与四边网格调整。
- 收纳盒收起后，同列下方盒子自动补位，展开时恢复间距。
- 设置支持玻璃透明度、Dock 尺寸、图标大小、磁吸和材质刷新率，并提供独立重置。
- 最大化或全屏应用覆盖桌面时暂停相关渲染，返回桌面后恢复。
- 音乐灵动岛支持旋转封面、逐字高亮与长歌词分段、展开过渡，以及歌词、进度条和律动条独立配色。关闭组件会停止媒体读取并释放专用资源。进度拖动需要播放器支持系统媒体定位接口，网易云与 QQ 音乐提供本机后台适配。
- 网络页同步连接状态，已连接 Wi-Fi 置顶，其余附近网络按信号强度排列，并显示以太网连接。附近 Wi-Fi 扫描需要 Windows 位置访问权限，受限时可从网络页进入授权设置。

## 使用

启动后，通过 Veyra 托盘菜单打开设置。桌面应用、文件与文件夹可拖入 Dock 或收纳盒。

设置和映射引用保存在本机用户目录 `%LOCALAPPDATA%\GlassDock`，兼容早期版本。收纳盒保留源文件位置。退出 Veyra 时恢复 Windows 任务栏。需要手动恢复时运行以下命令。

```powershell
.\Veyra.exe --restore-taskbar
```

## 免责声明

本项目自有代码沿用仓库的 MIT 许可。第三方组件分别遵循各自许可，详见 [LICENSE](LICENSE)、[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 [licenses](licenses)。

收纳盒吸附几何适配自 WitchDrawer，其相关代码遵循 PolyForm Noncommercial 1.0.1。包含该组件的分发与使用须遵守其非商业许可及 Required Notice，具体条款见 [WitchDrawer 许可](src/Veyra/ThirdParty/WitchDrawer/LICENSE.txt) 与 [版权说明](src/Veyra/ThirdParty/WitchDrawer/NOTICE.md)。

本软件按现状提供。在适用法律允许的范围内，作者和贡献者不对其适用性、可靠性或准确性作出明示或暗示的保证，也不承担使用本软件产生的损失责任。使用者应遵守所在地法律及相关第三方许可。

## 隐私政策

Veyra 当前版本不包含用户数据收集、遥测或自动上传模块。偏好、Dock 固定项、收纳盒路径引用和诊断日志保存在本机用户目录。

音量、亮度、输入法、网络与托盘功能通过本机 Windows 接口交互。使用网络连接或打开第三方应用时，相关通信与数据处理受 Windows、网络服务及对应应用的隐私政策约束。

启用音乐灵动岛后，组件通过 Windows 媒体会话读取播放信息。网易云后台时间轴缺失时，在本机以只读方式识别播放进度。歌词优先读取本地 LRC，随后按歌曲 ID、标题或歌手向网易云、QQ 音乐接口查询，相关服务会收到查询内容。关闭灵动岛后停止这些读取和请求。

开发构建需要从 NuGet 获取依赖，独立光学核对场景的构建需要 npm 依赖。

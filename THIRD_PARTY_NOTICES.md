# Third-party notices

The notification-area host protocol and callback behavior were implemented with reference to **ManagedShell**, Copyright Cairo Development Team and contributors, licensed under Apache License 2.0.

Sources: https://github.com/cairoshell/ManagedShell, particularly `TrayService.cs`, `NotifyIcon.cs` and `NativeMethods.Shell32.cs`.

uilicd contains its own WPF presentation, packet validation, lifecycle and Explorer forwarding implementation. It does not ship the ManagedShell runtime package. The Apache-2.0 license is included in `licenses/ManagedShell-Apache-2.0.txt`.

The source project also references Vortice.Direct3D11, Vortice.DXGI, Vortice.D3DCompiler and their package dependencies. Their license notices are included in `licenses`. The self-contained executable includes the Microsoft .NET / Windows Desktop runtime; its notices are included with the distribution.

`src/uilicd/ThirdParty/WitchDrawer` includes an adapted snapping geometry implementation, its original PolyForm Noncommercial 1.0.0 license and required notice. The original development checkout uses `src/GlassDock` for this same project.

The independent `optics-webgl` scene bundles Three.js 0.187.0 under the MIT license. Its license is included in `optics-webgl/THREE-LICENSE.txt`. esbuild 0.28.2 is a build dependency recorded in that scene's package lockfile.

The read-only NetEase playback-clock discovery algorithm in `Music/NeteaseBackgroundClock.cs` is adapted from maker114/netease-lyric-api, Copyright (c) 2026 maker114, under the MIT license. Its license is included in `src/uilicd/ThirdParty/NetEaseClock/LICENSE.txt` and `licenses/NetEaseClock-MIT.txt`.

# Liquid Glass WebGL2 核对场景

该场景按 1080×1920 单 Canvas 核对六步骤光学管线。桌面应用中的原生 WPF/D3D11 管线由主项目负责。

运行以下命令后打开 `http://127.0.0.1:4173`。

```powershell
npm ci --ignore-scripts
npm run build
npm start
```

Three.js 固定为 0.187.0，esbuild 固定为 0.28.2。依赖版本与完整性记录在 `package-lock.json`，构建后的 `dist` 可交给任意本地静态服务器运行。

`pipeline.js` 管理单 Renderer 与 Background、Glass、Content 三个 Scene。背景先绘入 `paintComposeRT`，Three.js 在 render 返回前完成该 RenderTarget 的 mip 层级。玻璃读取这份背景，文字与图标在最终 Content Scene 绘制。绘制缓冲区始终为 1080×1920，CSS 仅等比缩放显示。

`glass-shaders.js` 实现 aspect 校正 SDF、quintic 高度、四点法线、双界面折射、三路边缘采样与外半倒角 GGX 光照。高度平台的法线采样支持在 t=1 归零。Inner/Outer 位移取自绿色物理光路，RGB 色散仅作用于边缘。折射、外部阴影、白色染色与高光按顺序合成。

最新边缘深度参数中，大表面厚度为 16 px、倒角过渡为 30 px、背景距离为 48 px。Dock 使用 16/25/44 px，内部卡片使用 12/21/38 px。参数按各表面高度归一化一次。GGX 光强系数为 .70，环境 Fresnel 系数为 .60，两者仍仅由曲率与外半倒角权重支持。IOR 为 1.46，感知粗糙度为 .32，中央常量平台保持零法线梯度。

点击“灯光旋转”或按 1 切换四点灯光。点击“切换背景”或按 2 改变程序化测试背景。按钮命中通过 Three.js Raycaster，文字图标使用最后一层中的纹理。灯光以 4 Hz 更新，显式输入请求下一显示帧。隐藏页面暂停渲染，背景静止时复用已有 RT。

核对场景的构建代码与许可完整保留；历史截图和现场诊断报告未纳入源码包。

版本依据为 [Three.js npm 元数据](https://registry.npmjs.org/three/latest)，RenderTarget 行为核对了安装包中的 `src/renderers/WebGLRenderer.js`。Three.js 许可保存在 `THREE-LICENSE.txt`。

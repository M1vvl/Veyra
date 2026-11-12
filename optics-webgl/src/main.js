import { GlassPipeline } from './pipeline.js';
import { addBackground } from './background.js';
import { addLabel, addIcon, attachInteractions } from './content.js';

const canvas = document.getElementById('optics');
try {
  const pipeline = new GlassPipeline(canvas);
  const background = addBackground(pipeline.background);
  pipeline.addSurface({ x: 124, y: 420, width: 832, height: 1080, radius: 64, thickness: 16, transition: 30, distance: 48 });
  pipeline.addSurface({ x: 164, y: 1160, width: 752, height: 176, radius: 36, thickness: 12, transition: 21, distance: 38 });
  pipeline.addSurface({ x: 164, y: 920, width: 752, height: 204, radius: 36, thickness: 12, transition: 21, distance: 38 });
  pipeline.addSurface({ x: 164, y: 680, width: 752, height: 204, radius: 36, thickness: 12, transition: 21, distance: 38 });
  pipeline.addSurface({ x: 124, y: 170, width: 832, height: 148, radius: 48, thickness: 16, transition: 25, distance: 44 });

  const labels = pipeline.content;
  addLabel(labels, { text: 'Liquid Glass', x: 130, y: 1670, width: 820, height: 88, fontSize: 64 });
  addLabel(labels, { text: 'BACKGROUND   /   GLASS   /   CONTENT', x: 132, y: 1590, width: 820, height: 46, fontSize: 24 });
  addLabel(labels, { text: '控制中心', x: 170, y: 1380, width: 700, height: 64, fontSize: 44 });
  addLabel(labels, { text: '声音', x: 204, y: 1250, width: 300, height: 52, fontSize: 32 });
  addLabel(labels, { text: '48%', x: 736, y: 1250, width: 130, height: 52, fontSize: 28, align: 'center' });
  addLabel(labels, { text: '━━━━━━━━━━━━', x: 204, y: 1190, width: 650, height: 50, fontSize: 34 });
  addLabel(labels, { text: '材质核对', x: 204, y: 1040, width: 620, height: 52, fontSize: 32 });
  const lights = addLabel(labels, { text: '◉  灯光旋转', x: 204, y: 956, width: 300, height: 72, fontSize: 30 });
  const backdrop = addLabel(labels, { text: '◈  切换背景', x: 536, y: 956, width: 300, height: 72, fontSize: 30 });
  addLabel(labels, { text: '中心平面', x: 204, y: 790, width: 620, height: 52, fontSize: 32 });
  addLabel(labels, { text: 'IOR 1.46   ·   粗糙度 0.32', x: 204, y: 720, width: 650, height: 52, fontSize: 28 });
  addLabel(labels, { text: '外半倒角高光 · 双界面折射', x: 170, y: 498, width: 730, height: 56, fontSize: 28 });
  ['⌘', '⌕', '♪', '✦', '▤', '⚙'].forEach((symbol, index) => addIcon(labels, {
    symbol, x: 172 + index * 124, y: 206, size: 78,
    color: ['#62b7e4', '#6f8ccb', '#b99ada', '#ce9eb8', '#dea571', '#7bb3bd'][index],
  }));
  lights.userData.activate = () => {
    pipeline.animateLights = !pipeline.animateLights;
    canvas.setAttribute('aria-label', `灯光旋转${pipeline.animateLights ? '已开启' : '已停止'}。按 1 切换灯光，按 2 切换背景。`);
  };
  backdrop.userData.activate = () => {
    background.uniforms.uAlternate.value = 1 - background.uniforms.uAlternate.value;
    pipeline.invalidate(true);
  };
  const removeInteractions = attachInteractions(canvas, pipeline.camera, [lights, backdrop], () => pipeline.invalidate());
  window.glassOptics = {
    diagnostics: pipeline.diagnostics,
    render: () => pipeline.invalidate(true),
    dispose: () => { removeInteractions(); pipeline.dispose(); },
  };
  pipeline.renderer.compile(pipeline.background, pipeline.camera);
  pipeline.renderer.compile(pipeline.glass, pipeline.camera);
  pipeline.renderer.compile(pipeline.content, pipeline.camera);
  pipeline.invalidate(true);
  if (new URLSearchParams(location.search).has('verify')) {
    setTimeout(() => {
      const bounds = canvas.getBoundingClientRect();
      const click = (x, y) => canvas.dispatchEvent(new PointerEvent('pointerup', {
        clientX: bounds.left + x / 1080 * bounds.width,
        clientY: bounds.top + (1 - y / 1920) * bounds.height,
        bubbles: true,
      }));
      const lightsBefore = pipeline.animateLights;
      click(354, 992);
      const lightsAfter = pipeline.animateLights;
      const backgroundBefore = background.uniforms.uAlternate.value;
      click(686, 992);
      const backgroundAfter = background.uniforms.uAlternate.value;
      pipeline.frame(performance.now() + 1000);
      const report = {
        ...pipeline.diagnostics,
        drawingBuffer: [pipeline.renderer.getContext().drawingBufferWidth, pipeline.renderer.getContext().drawingBufferHeight],
        interactions: { lightsBefore, lightsAfter, backgroundBefore, backgroundAfter },
        surfaceCount: pipeline.materials.length,
      };
      const output = document.createElement('output');
      output.id = 'verification';
      output.hidden = true;
      output.textContent = JSON.stringify(report);
      document.body.append(output);
    }, 1200);
  }
  window.addEventListener('pagehide', event => {
    if (!event.persisted) window.glassOptics.dispose();
  });
} catch (error) {
  canvas.hidden = true;
  const message = document.getElementById('error');
  message.hidden = false;
  message.textContent = error.message;
  console.error(error);
}

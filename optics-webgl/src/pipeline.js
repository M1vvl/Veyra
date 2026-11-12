import * as THREE from 'three';
import { vertexShader, fragmentShader } from './glass-shaders.js';

export const WIDTH = 1080;
export const HEIGHT = 1920;

export class GlassPipeline {
  constructor(canvas) {
    const context = canvas.getContext('webgl2', { alpha: false, antialias: true, powerPreference: 'low-power' });
    if (!context) throw new Error('此场景需要 WebGL2。');
    this.renderer = new THREE.WebGLRenderer({ canvas, context, antialias: true });
    this.renderer.setPixelRatio(1);
    this.renderer.setSize(WIDTH, HEIGHT, false);
    this.renderer.autoClear = false;
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.camera = new THREE.OrthographicCamera(0, WIDTH, HEIGHT, 0, 0.1, 10);
    this.camera.position.z = 5;
    this.background = new THREE.Scene();
    this.glass = new THREE.Scene();
    this.content = new THREE.Scene();
    this.paintComposeRT = new THREE.WebGLRenderTarget(WIDTH, HEIGHT, {
      generateMipmaps: true,
      minFilter: THREE.LinearMipmapLinearFilter,
      magFilter: THREE.LinearFilter,
      depthBuffer: false,
      stencilBuffer: false,
      type: THREE.UnsignedByteType,
    });
    this.paintComposeRT.texture.colorSpace = THREE.LinearSRGBColorSpace;
    this.materials = [];
    this.backgroundDirty = true;
    this.dirty = true;
    this.animateLights = true;
    this.disposed = false;
    this.lastFrame = 0;
    this.lightTime = 0;
    this.frameRequest = 0;
    this.frameTimer = 0;
    this.diagnostics = { frameSize: [WIDTH, HEIGHT], order: [], frames: 0, backgroundFrames: 0 };
    this.diagnostics.shaderErrors = [];
    const debugInfo = context.getExtension('WEBGL_debug_renderer_info');
    this.diagnostics.renderer = debugInfo ? context.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL) : context.getParameter(context.RENDERER);
    this.diagnostics.webglVersion = context.getParameter(context.VERSION);
    this.renderer.debug.onShaderError = (gl, program, vertex, fragment) => {
      this.diagnostics.shaderErrors.push({
        program: gl.getProgramInfoLog(program),
        vertex: gl.getShaderInfoLog(vertex),
        fragment: gl.getShaderInfoLog(fragment),
      });
    };
    this.backgroundDisplay = new THREE.Scene();
    const material = new THREE.MeshBasicMaterial({ map: this.paintComposeRT.texture, depthTest: false, toneMapped: false });
    const plane = new THREE.Mesh(new THREE.PlaneGeometry(WIDTH, HEIGHT), material);
    plane.position.set(WIDTH / 2, HEIGHT / 2, 0);
    this.backgroundDisplay.add(plane);
    this.visibilityListener = () => {
      if (document.hidden) {
        cancelAnimationFrame(this.frameRequest);
        clearTimeout(this.frameTimer);
        this.frameRequest = 0;
        this.frameTimer = 0;
      } else this.invalidate();
    };
    document.addEventListener('visibilitychange', this.visibilityListener);
  }

  addSurface({ x, y, width, height, radius = 40, thickness = 14, transition = 24, distance = 40, tint = 0.035 }) {
    const padding = 28;
    const material = new THREE.RawShaderMaterial({
      glslVersion: THREE.GLSL3,
      vertexShader,
      fragmentShader,
      uniforms: {
        paintComposeRT: { value: this.paintComposeRT.texture },
        uCanvasSize: { value: new THREE.Vector2(WIDTH, HEIGHT) },
        uSurfaceSize: { value: new THREE.Vector2(width, height) },
        uSurfaceOrigin: { value: new THREE.Vector2(x, y) },
        uPadding: { value: padding },
        uCornerRadius: { value: radius / height },
        uNormalTransition: { value: transition / height },
        uGlassThickness: { value: thickness / height },
        uBackgroundDistance: { value: distance / height },
        uIOR: { value: 1.46 },
        uDispersion: { value: 0.009 },
        uRoughness: { value: 0.32 },
        uTint: { value: tint },
        uTime: { value: 0 },
      },
      depthTest: false,
      depthWrite: false,
      transparent: true,
    });
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(width + padding * 2, height + padding * 2), material);
    mesh.position.set(x + width / 2, y + height / 2, 0);
    mesh.renderOrder = this.materials.length;
    this.glass.add(mesh);
    this.materials.push(material);
    return mesh;
  }

  invalidate(background = false) {
    this.dirty = true;
    this.backgroundDirty ||= background;
    // Explicit input takes the next display frame, independently of the slower light cadence.
    clearTimeout(this.frameTimer);
    this.frameTimer = 0;
    if (!this.frameRequest && !this.frameTimer && !document.hidden && !this.disposed) {
      this.frameRequest = requestAnimationFrame(time => this.frame(time));
    }
  }

  frame(time) {
    this.frameRequest = 0;
    if (document.hidden || this.disposed) return;
    const interval = this.animateLights && !this.dirty ? 250 : 1000 / 30;
    if (time - this.lastFrame < interval) {
      this.frameTimer = setTimeout(() => {
        this.frameTimer = 0;
        this.frameRequest = requestAnimationFrame(nextTime => this.frame(nextTime));
      }, interval - (time - this.lastFrame));
      return;
    }
    this.lastFrame = time;
    if (!this.dirty && !this.animateLights) return;
    const renderer = this.renderer;
    this.diagnostics.order = [];
    if (this.backgroundDirty) {
      renderer.setRenderTarget(this.paintComposeRT);
      renderer.clear();
      renderer.render(this.background, this.camera);
      // WebGLRenderer.render finalizes generateMipmaps=true RTs before returning.
      this.backgroundDirty = false;
      this.diagnostics.backgroundFrames++;
    }
    this.diagnostics.order.push('BACKGROUND paintComposeRT + mip chain');
    renderer.setRenderTarget(null);
    renderer.clear();
    renderer.render(this.backgroundDisplay, this.camera);
    if (this.animateLights) this.lightTime = time / 1000;
    for (const material of this.materials) material.uniforms.uTime.value = this.lightTime;
    renderer.render(this.glass, this.camera);
    this.diagnostics.order.push('GLASS SDF / height / refraction / three paths / BRDF');
    renderer.clearDepth();
    renderer.render(this.content, this.camera);
    this.diagnostics.order.push('CONTENT icons / text / hit targets');
    this.diagnostics.frames++;
    this.diagnostics.glError = renderer.getContext().getError();
    this.dirty = false;
    if (this.animateLights && !this.frameRequest && !this.frameTimer) {
      this.frameTimer = setTimeout(() => {
        this.frameTimer = 0;
        this.frameRequest = requestAnimationFrame(nextTime => this.frame(nextTime));
      }, 250);
    }
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frameRequest);
    clearTimeout(this.frameTimer);
    document.removeEventListener('visibilitychange', this.visibilityListener);
    for (const scene of [this.background, this.glass, this.content, this.backgroundDisplay]) {
      scene.traverse(object => {
        object.geometry?.dispose();
        if (object.material) {
          object.material.map?.dispose();
          object.material.dispose();
        }
      });
    }
    this.paintComposeRT.dispose();
    this.renderer.dispose();
  }
}

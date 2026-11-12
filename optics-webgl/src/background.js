import * as THREE from 'three';
import { WIDTH, HEIGHT } from './pipeline.js';
import { vertexShader } from './glass-shaders.js';

export function addBackground(scene) {
  const material = new THREE.RawShaderMaterial({
    glslVersion: THREE.GLSL3,
    vertexShader,
    fragmentShader: `
      precision highp float;
      in vec2 vUv;
      out vec4 outputColor;
      uniform float uAlternate;
      void main() {
        vec2 point = vUv * vec2(1080.0, 1920.0);
        vec3 low = mix(vec3(0.035, 0.075, 0.12), vec3(0.22, 0.08, 0.03), uAlternate);
        vec3 high = mix(vec3(0.18, 0.42, 0.48), vec3(0.55, 0.28, 0.12), uAlternate);
        vec3 color = mix(low, high, vUv.y);
        vec2 grid = abs(fract(point / 72.0 - 0.5) - 0.5) / fwidth(point / 72.0);
        color += vec3(0.06) * (1.0 - min(min(grid.x, grid.y), 1.0));
        float stripe = 1.0 - smoothstep(9.0, 12.0, abs(point.x - 310.0 - 95.0 * sin(point.y / 155.0)));
        color = mix(color, vec3(0.62, 0.78, 0.30), stripe * 0.82);
        float circle = 1.0 - smoothstep(125.0, 128.0, length(point - vec2(800.0, 790.0)));
        color = mix(color, vec3(0.75, 0.26, 0.14), circle * 0.85);
        float line = 1.0 - smoothstep(3.0, 5.0, abs(point.y - (0.25 * point.x + 1040.0)));
        color = mix(color, vec3(0.95), line);
        outputColor = vec4(color, 1.0);
      }`,
    uniforms: { uAlternate: { value: 0 } },
    depthTest: false,
    depthWrite: false,
  });
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(WIDTH, HEIGHT), material);
  mesh.position.set(WIDTH / 2, HEIGHT / 2, 0);
  scene.add(mesh);
  return material;
}

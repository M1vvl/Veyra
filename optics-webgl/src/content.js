import * as THREE from 'three';

export function addLabel(scene, { text, x, y, width, height, fontSize = 32, color = '#f2f8ff', align = 'left' }) {
  const canvas = document.createElement('canvas');
  canvas.width = Math.ceil(width * 2);
  canvas.height = Math.ceil(height * 2);
  const context = canvas.getContext('2d');
  context.scale(2, 2);
  context.font = `600 ${fontSize}px system-ui, sans-serif`;
  context.fillStyle = color;
  context.textBaseline = 'middle';
  context.textAlign = align;
  context.fillText(text, align === 'center' ? width / 2 : 0, height / 2, width);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.generateMipmaps = false;
  texture.minFilter = THREE.LinearFilter;
  const material = new THREE.MeshBasicMaterial({ map: texture, transparent: true, depthTest: false, depthWrite: false, toneMapped: false });
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(width, height), material);
  mesh.position.set(x + width / 2, y + height / 2, 0);
  scene.add(mesh);
  return mesh;
}

export function addIcon(scene, { symbol, x, y, color = '#86d2ed', size = 76 }) {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 160;
  const context = canvas.getContext('2d');
  const gradient = context.createLinearGradient(0, 0, 160, 160);
  gradient.addColorStop(0, color);
  gradient.addColorStop(1, '#26394c');
  context.fillStyle = gradient;
  context.beginPath();
  context.roundRect(8, 8, 144, 144, 32);
  context.fill();
  context.fillStyle = '#f5fbff';
  context.font = '600 76px system-ui, sans-serif';
  context.textBaseline = 'middle';
  context.textAlign = 'center';
  context.fillText(symbol, 80, 85);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  const material = new THREE.MeshBasicMaterial({ map: texture, transparent: true, depthTest: false, toneMapped: false });
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(size, size), material);
  mesh.position.set(x + size / 2, y + size / 2, 0);
  scene.add(mesh);
  return mesh;
}

export function attachInteractions(canvas, camera, targets, invalidate) {
  const raycaster = new THREE.Raycaster();
  const pointer = new THREE.Vector2();
  const activate = event => {
    const bounds = canvas.getBoundingClientRect();
    pointer.set((event.clientX - bounds.left) / bounds.width * 2 - 1, 1 - (event.clientY - bounds.top) / bounds.height * 2);
    raycaster.setFromCamera(pointer, camera);
    const hit = raycaster.intersectObjects(targets, false)[0];
    if (hit) {
      hit.object.userData.activate();
      invalidate();
    }
  };
  canvas.addEventListener('pointerup', activate);
  const keyboard = event => {
    if (event.key === '1' || event.key === '2') {
      targets[Number(event.key) - 1]?.userData.activate();
      invalidate();
      event.preventDefault();
    }
  };
  canvas.addEventListener('keydown', keyboard);
  return () => {
    canvas.removeEventListener('pointerup', activate);
    canvas.removeEventListener('keydown', keyboard);
  };
}

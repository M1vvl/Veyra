export const vertexShader = `
precision highp float;
in vec3 position;
in vec2 uv;
uniform mat4 modelViewMatrix;
uniform mat4 projectionMatrix;
out vec2 vUv;
void main() {
  vUv = uv;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}`;

export const fragmentShader = `
precision highp float;
precision highp sampler2D;
in vec2 vUv;
out vec4 outputColor;
uniform sampler2D paintComposeRT;
uniform vec2 uCanvasSize;
uniform vec2 uSurfaceSize;
uniform vec2 uSurfaceOrigin;
uniform float uPadding;
uniform float uCornerRadius;
uniform float uNormalTransition;
uniform float uGlassThickness;
uniform float uBackgroundDistance;
uniform float uIOR;
uniform float uDispersion;
uniform float uRoughness;
uniform float uTint;
uniform float uTime;

vec3 linearToDisplay(vec3 color) {
  return mix(12.92 * color, 1.055 * pow(max(color, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055,
    step(vec3(0.0031308), color));
}

// O2. All SDF and derivative coordinates are normalized by surface height.
vec2 correctedPosition(vec2 uv) {
  return (uv - 0.5) * vec2(uSurfaceSize.x / uSurfaceSize.y, 1.0);
}
vec2 positionToUv(vec2 point) {
  return point / vec2(uSurfaceSize.x / uSurfaceSize.y, 1.0) + 0.5;
}
float circleSdf(vec2 point, float radius) { return length(point) - radius; }
float roundedRectSdf(vec2 point, vec2 halfSize, float radius) {
  vec2 q = abs(point) - halfSize + radius;
  return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
}
float polynomialSmoothMin(float first, float second, float width) {
  float h = clamp(0.5 + 0.5 * (second - first) / max(width, 0.00001), 0.0, 1.0);
  return mix(second, first, h) - width * h * (1.0 - h);
}
float glassSurfaceSdf(vec2 point) {
  vec2 halfSize = vec2(uSurfaceSize.x / uSurfaceSize.y, 1.0) * 0.5;
  return roundedRectSdf(point, halfSize, min(uCornerRadius, min(halfSize.x, halfSize.y)));
}

// O3. The saturated quintic has an exactly constant central plateau.
float quintic(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }
float surfaceHeight(vec2 point) {
  float interior = max(-glassSurfaceSdf(point), 0.0);
  float bevel = max(uNormalTransition * 0.82, 0.0001);
  return uGlassThickness * quintic(clamp(interior / bevel, 0.0, 1.0));
}
vec3 surfaceNormal(vec2 point, float height, out float curvature) {
  float stepSize = clamp(uNormalTransition * 0.13, 0.003, 0.012);
  float left = surfaceHeight(point - vec2(stepSize, 0.0));
  float right = surfaceHeight(point + vec2(stepSize, 0.0));
  float down = surfaceHeight(point - vec2(0.0, stepSize));
  float up = surfaceHeight(point + vec2(0.0, stepSize));
  float bevelT = clamp(max(-glassSurfaceSdf(point), 0.0) / max(uNormalTransition * 0.82, 0.0001), 0.0, 1.0);
  float plateauSupport = 1.0 - smoothstep(0.8, 1.0, bevelT);
  vec2 gradient = vec2(right - left, up - down) / (2.0 * stepSize) * plateauSupport;
  curvature = abs(right + left + up + down - 4.0 * height) / (stepSize * stepSize) * plateauSupport;
  return normalize(vec3(-gradient, 1.0));
}

// O4. Front interface, travel to the planar back face, then the background plane.
vec3 safeRefract(vec3 incident, vec3 normal, float eta) {
  vec3 ray = refract(incident, normal, eta);
  return dot(ray, ray) > 0.000001 ? normalize(ray) : vec3(0.0, 0.0, -1.0);
}
vec2 traceRay(vec2 point, float height, vec3 normal, float ior) {
  vec3 insideRay = safeRefract(vec3(0.0, 0.0, -1.0), normal, 1.0 / ior);
  float glassPath = height / max(-insideRay.z, 0.025);
  vec3 backPoint = vec3(point, height) + insideRay * glassPath;
  vec3 outsideRay = safeRefract(insideRay, vec3(0.0, 0.0, 1.0), ior);
  float airPath = uBackgroundDistance / max(-outsideRay.z, 0.025);
  return positionToUv((backPoint + outsideRay * airPath).xy);
}
vec3 sampleBackground(vec2 surfaceUv, float lod) {
  vec2 backgroundUv = (uSurfaceOrigin + surfaceUv * uSurfaceSize) / uCanvasSize;
  return textureLod(paintComposeRT, clamp(backgroundUv, vec2(0.0), vec2(1.0)), lod).rgb;
}
vec3 sampleSpectrum(vec2 baseUv, vec2 rayR, vec2 rayG, vec2 rayB, float lod) {
  return vec3(sampleBackground(baseUv + rayR - rayG, lod).r,
    sampleBackground(baseUv, lod).g, sampleBackground(baseUv + rayB - rayG, lod).b);
}

// O6. Dielectric GGX, height-correlated Smith, and Schlick.
float distributionGGX(float NoH, float alpha) {
  float alpha2 = alpha * alpha;
  float denominator = NoH * NoH * (alpha2 - 1.0) + 1.0;
  return alpha2 / max(3.14159265359 * denominator * denominator, 0.00001);
}
float smithHeightCorrelated(float NoV, float NoL, float alpha) {
  float alpha2 = alpha * alpha;
  float lambdaV = NoL * sqrt(NoV * NoV * (1.0 - alpha2) + alpha2);
  float lambdaL = NoV * sqrt(NoL * NoL * (1.0 - alpha2) + alpha2);
  return 0.5 / max(lambdaV + lambdaL, 0.00001);
}
float schlick(float cosine, float f0) { return f0 + (1.0 - f0) * pow(1.0 - cosine, 5.0); }

void main() {
  // O1. The pipeline has completed the background render and its mip chain.
  vec2 localPixels = vUv * (uSurfaceSize + 2.0 * uPadding) - uPadding;
  vec2 uv = localPixels / uSurfaceSize;
  vec2 point = correctedPosition(uv);

  // O2. Contour coverage derives from the SDF, separately from surface height.
  float sdf = glassSurfaceSdf(point);
  float aa = max(fwidth(sdf), 0.5 / uSurfaceSize.y);
  float mask = 1.0 - smoothstep(-aa, aa, sdf);

  // O3. Normals are sampled from the noise-free height field.
  float interior = max(-sdf, 0.0);
  float bevel = max(uNormalTransition * 0.82, 0.0001);
  float bevelT = clamp(interior / bevel, 0.0, 1.0);
  float height = surfaceHeight(point);
  float curvature;
  vec3 normal = surfaceNormal(point, height, curvature);

  // O4. Dispersion has three actual optical paths.
  vec2 rayR = traceRay(point, height, normal, max(1.001, uIOR - uDispersion));
  vec2 rayG = traceRay(point, height, normal, uIOR);
  vec2 rayB = traceRay(point, height, normal, uIOR + uDispersion);

  // O5. Face, inward compression, outward roll. Dispersion stays on the rims.
  vec2 opticalCenter = vec2(0.5);
  vec2 faceUv = mix(opticalCenter + (uv - opticalCenter) * 0.975, rayG, 0.08);
  vec2 rimDirection = length(normal.xy) > 0.00001 ? normalize(normal.xy) : vec2(0.0);
  vec2 correctedToUv = vec2(uSurfaceSize.y / uSurfaceSize.x, 1.0);
  float rimDisplacement = length((rayG - uv) * vec2(uSurfaceSize.x / uSurfaceSize.y, 1.0)) * 0.7;
  vec2 innerUv = rayG - rimDirection * rimDisplacement * correctedToUv;
  vec2 outerUv = uv + rimDirection * rimDisplacement * 1.28 * correctedToUv;
  float faceWeight = smoothstep(0.36, 1.0, bevelT);
  float outerWeight = (1.0 - faceWeight) * (1.0 - smoothstep(0.08, 0.52, bevelT));
  float innerWeight = max(0.0, 1.0 - faceWeight - outerWeight);
  vec3 weights = vec3(faceWeight, innerWeight, outerWeight);
  weights /= max(weights.x + weights.y + weights.z, 0.0001);
  vec3 refracted = sampleBackground(faceUv, 2.3) * weights.x
    + sampleSpectrum(innerUv, rayR, rayG, rayB, 0.55) * weights.y
    + sampleSpectrum(outerUv, rayR, rayG, rayB, 3.0) * weights.z;

  // O6. Complete refraction -> outside shadow -> white tint -> reflection.
  float shadowDistance = max(sdf, 0.0) * uSurfaceSize.y;
  float shadowSupport = max(uPadding - 1.0, 0.5);
  float shadowTail = 1.0 - smoothstep(shadowSupport * 0.5, shadowSupport, shadowDistance);
  float externalShadow = exp(-shadowDistance / 7.0) * 0.18 * shadowTail;
  vec3 shadowed = refracted;
  vec3 material = mix(shadowed, vec3(0.96, 0.98, 1.0), clamp(uTint, 0.0, 1.0));
  vec3 view = vec3(0.0, 0.0, 1.0);
  float NoV = clamp(dot(normal, view), 0.0, 1.0);
  float f0 = pow((uIOR - 1.0) / (uIOR + 1.0), 2.0);
  float alpha = max(uRoughness * uRoughness, 0.02);
  float outerBevelSupport = 1.0 - smoothstep(0.32, 0.50, bevelT);
  float support = outerBevelSupport * clamp(curvature * bevel * 0.7, 0.0, 1.0);
  vec3 reflection = vec3(0.0);
  for (int index = 0; index < 4; index++) {
    float angle = float(index) * 1.57079632679 + uTime * 0.035;
    float z = 0.28 + 0.043 * float(index);
    float xy = sqrt(1.0 - z * z);
    vec3 light = vec3(cos(angle) * xy, sin(angle) * xy, z);
    vec3 halfVector = normalize(view + light);
    float NoL = clamp(dot(normal, light), 0.0, 1.0);
    float NoH = clamp(dot(normal, halfVector), 0.0, 1.0);
    float VoH = clamp(dot(view, halfVector), 0.0, 1.0);
    vec3 lightColor = index % 2 == 0 ? vec3(1.0, 0.94, 0.87) : vec3(0.86, 0.94, 1.0);
    reflection += lightColor * distributionGGX(NoH, alpha)
      * smithHeightCorrelated(NoV, NoL, alpha) * schlick(VoH, f0) * NoL * 0.70;
  }
  material += (reflection + schlick(NoV, f0) * 0.60 * vec3(0.86, 0.93, 1.0)) * support;
  // The interior carries the fully refracted scene. Only silhouette and shadow use alpha.
  float opacity = mask + externalShadow * (1.0 - mask);
  vec3 premultiplied = linearToDisplay(max(material, vec3(0.0))) * mask;
  outputColor = vec4(opacity > 0.0 ? premultiplied / opacity : vec3(0.0), opacity);
}`;

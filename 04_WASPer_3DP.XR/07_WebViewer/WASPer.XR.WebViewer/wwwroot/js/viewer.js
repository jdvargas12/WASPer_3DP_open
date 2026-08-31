import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import * as BufferGeometryUtils from "three/addons/utils/BufferGeometryUtils.js";
import { createLiveJobConnection } from "./live-link.js";

// Role -> color. Jobs now carry the active WASPer palette as compact viewer
// metadata; the browser can also temporarily override it from the selector.
let ROLE_COLOR = {
  undefined: 0x808088,
  shell: 0x4da3ff,
  infill: 0xffa64d,
  partition: 0x9a6bff,
  support: 0xff5c5c,
  transition: 0x4dffb0,
};
let wasperJobPalette = { ...ROLE_COLOR };
const BUILTIN_PALETTES = {
  wasperBlue: { label: "WASPer Blue", colors: [0x3d9ddd, 0x3d9ddd, 0x3d9ddd, 0x3d9ddd, 0x3d9ddd, 0x3d9ddd] },
  roleClassic: { label: "Roles - Classic", colors: [0xe1534a, 0xf7bbbd, 0x92c5de, 0xae7dbe, 0xee9e41, 0x8c8c8c] },
  roleVivid: { label: "Roles - Vivid", colors: [0xc62828, 0xff9800, 0x009688, 0x7c4dff, 0xffc107, 0x424242] },
  roleBright: { label: "Roles - Bright", colors: [0xff205c, 0xffd60a, 0x00e5ff, 0x9b4dff, 0x39ff14, 0x232323] },
  roleColorBlind: { label: "Roles - Color Blind", colors: [0xd55e00, 0x0072b2, 0x009e73, 0xcc79a7, 0xf0e442, 0x666666] },
  neutralGray: { label: "Neutral Gray", colors: [0x8c8c8c, 0x8c8c8c, 0x8c8c8c, 0x8c8c8c, 0x8c8c8c, 0x8c8c8c] },
  clayRawGrayRedware: { label: "Clay - Raw Gray Redware", colors: Array(6).fill(0x8a8880) },
  clayFiredGrayRedware: { label: "Clay - Fired Gray Redware", colors: Array(6).fill(0x9a492d) },
  clayRawRedEarthenware: { label: "Clay - Raw Red Earthenware", colors: Array(6).fill(0x914d39) },
  clayFiredRedEarthenware: { label: "Clay - Fired Red Earthenware", colors: Array(6).fill(0xc45833) },
  clayRawBuffEarthenware: { label: "Clay - Raw Buff Earthenware", colors: Array(6).fill(0xae946d) },
  clayFiredBuffEarthenware: { label: "Clay - Fired Buff Earthenware", colors: Array(6).fill(0xdab880) },
  clayRawWhiteStoneware: { label: "Clay - Raw White Stoneware", colors: Array(6).fill(0xcdc8b8) },
  clayFiredWhiteStoneware: { label: "Clay - Fired White Stoneware", colors: Array(6).fill(0xe8dec7) },
  clayRawPinkClay: { label: "Clay - Raw Pink", colors: Array(6).fill(0xd3aa9e) },
  clayFiredPinkClay: { label: "Clay - Fired Pink", colors: Array(6).fill(0xe59d93) },
};
const ROLE_KEYS = ["shell", "infill", "partition", "support", "transition", "undefined"];
const ROLE_NAME = {
  undefined: "Undefined", shell: "Shell", infill: "Infill",
  partition: "Partition", support: "Support", transition: "Transition",
};
const paletteSelect = document.getElementById("paletteSelect");
const fromWasperOption = new Option("From WASPer", "wasper");
paletteSelect.add(fromWasperOption);
for (const [key, palette] of Object.entries(BUILTIN_PALETTES)) {
  paletteSelect.add(new Option(palette.label, key));
}

function roleColorsFromArray(colors) {
  return Object.fromEntries(ROLE_KEYS.map((role, index) => [role, colors[index]]));
}

function roleColorsFromViewerStyle(style) {
  if (!style) return null;
  const colors = [
    style.shellColor, style.infillColor, style.partitionColor,
    style.supportColor, style.transitionColor, style.undefinedColor,
  ].map(Number);
  return colors.every(color => Number.isInteger(color) && color >= 0 && color <= 0xffffff)
    ? roleColorsFromArray(colors)
    : null;
}

function refreshLegendColors() {
  for (const row of document.querySelectorAll("#legend .row[data-role]")) {
    const color = ROLE_COLOR[row.dataset.role] ?? ROLE_COLOR.undefined;
    const swatch = row.querySelector(".swatch");
    if (swatch) swatch.style.background = `#${color.toString(16).padStart(6, "0")}`;
  }
}

function applySelectedPalette() {
  const builtIn = BUILTIN_PALETTES[paletteSelect.value];
  ROLE_COLOR = builtIn ? roleColorsFromArray(builtIn.colors) : { ...wasperJobPalette };
  disposeMeshCaches();
  resetMaterialCaches();
  refreshLegendColors();
  rebuildContent();
}

function applyJobViewerStyle(style) {
  const colors = roleColorsFromViewerStyle(style);
  if (colors) {
    wasperJobPalette = colors;
    const name = String(style.paletteName || "Custom").replace(/([a-z])([A-Z])/g, "$1 $2");
    fromWasperOption.textContent = `From WASPer (${name})`;
  } else {
    fromWasperOption.textContent = "From WASPer (legacy file)";
  }
  if (paletteSelect.value === "wasper") ROLE_COLOR = { ...wasperJobPalette };
}

paletteSelect.addEventListener("change", applySelectedPalette);
const TRAVEL_COLOR = 0x555a68;
const IS_MOBILE_DEVICE = window.matchMedia("(pointer: coarse)").matches;
const meshQualitySelect = document.getElementById("meshQuality");
const CONTINUOUS_MESH_AUTO_LIMIT = 20000;
let meshRenderMode = "fast";
let meshQualityChosenByUser = false;
meshQualitySelect.value = meshRenderMode;
const MOBILE_MAX_PIXEL_RATIO = 1.25;
const DESKTOP_MAX_PIXEL_RATIO = 2;

const viewport = document.getElementById("viewport");

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x14151a);

const camera = new THREE.PerspectiveCamera(45, window.innerWidth / window.innerHeight, 0.1, 100000);
camera.position.set(150, -250, 200);
camera.up.set(0, 0, 1); // WASPer coordinates are Z-up (see coordinates.upAxis in the job).

// Large fabrication jobs can span several thousand model units while their deposited beads are
// only a few units thick. A conventional depth buffer loses precision across that range, and a
// scale-dependent near plane clips the model as soon as the camera moves in for inspection.
// Logarithmic depth preserves both the full-object view and close bead-level navigation.
const renderer = new THREE.WebGLRenderer({
  antialias: !IS_MOBILE_DEVICE,
  alpha: true,
  logarithmicDepthBuffer: true,
});
renderer.setPixelRatio(Math.min(
  window.devicePixelRatio || 1,
  IS_MOBILE_DEVICE ? MOBILE_MAX_PIXEL_RATIO : DESKTOP_MAX_PIXEL_RATIO));
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.xr.enabled = true; // M9 (WebXR, AR first pass, 2026-08-19) -- see arButton below
// three.js defaults to 'local', which real-device testing found this phone's WebXR/ARCore
// implementation rejects outright ("This device does not support the requested reference space
// type"). 'local-floor' (floor-relative -- Y=0 at the floor, which also happens to be a more
// useful origin for placing a model on a real surface than 'local' anyway) worked instead.
renderer.xr.setReferenceSpaceType("local-floor");
viewport.appendChild(renderer.domElement);

const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.dampingFactor = 0.08;

scene.add(new THREE.AmbientLight(0xffffff, 0.55));
const key = new THREE.DirectionalLight(0xffffff, 0.8);
key.position.set(1, -1, 2);
scene.add(key);

// contentGroup is the single transform root for desktop and AR placement. Its
// children have separate lifecycles: context changes with the loaded job,
// process geometry changes during playback, and helpers are viewer overlays.
const contentGroup = new THREE.Group();
scene.add(contentGroup);
const contextGroup = new THREE.Group();
contextGroup.name = "WASPer context";
let showContext = true;
contextGroup.visible = showContext;
const processGroup = new THREE.Group();
processGroup.name = "WASPer process";
const helpersGroup = new THREE.Group();
helpersGroup.name = "WASPer helpers";
contentGroup.add(contextGroup, processGroup, helpersGroup);

let contextSignature = null;

function contextMeshSignature(meshes) {
  return (meshes || []).map(mesh => {
    const vertices = mesh.vertices || [];
    const indices = mesh.triangleIndices || [];
    const first = vertices[0];
    const last = vertices[vertices.length - 1];
    return `${mesh.id || ""}:${vertices.length}:${indices.length}:` +
      `${mesh.red},${mesh.green},${mesh.blue},${mesh.alpha}:` +
      `${first ? `${first.x},${first.y},${first.z}` : ""}:` +
      `${last ? `${last.x},${last.y},${last.z}` : ""}`;
  }).join("|");
}

function disposeContextGroup() {
  for (const child of contextGroup.children.slice()) {
    child.geometry?.dispose();
    child.material?.dispose();
    contextGroup.remove(child);
  }
}

function buildContextMeshes(job) {
  const meshes = job.contextMeshes || [];
  const signature = contextMeshSignature(meshes);
  if (signature === contextSignature) return;

  disposeContextGroup();
  contextSignature = signature;

  for (const source of meshes) {
    const vertices = source.vertices || [];
    const indices = source.triangleIndices || [];
    if (vertices.length === 0 || indices.length < 3) continue;

    const positions = new Float32Array(vertices.length * 3);
    for (let i = 0; i < vertices.length; i++) {
      const vertex = vertices[i];
      positions[i * 3] = vertex.x;
      positions[i * 3 + 1] = vertex.y;
      positions[i * 3 + 2] = vertex.z;
    }

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    geometry.setIndex(indices);

    const normals = source.normals || [];
    if (normals.length === vertices.length) {
      const normalValues = new Float32Array(normals.length * 3);
      for (let i = 0; i < normals.length; i++) {
        const normal = normals[i];
        normalValues[i * 3] = normal.x;
        normalValues[i * 3 + 1] = normal.y;
        normalValues[i * 3 + 2] = normal.z;
      }
      geometry.setAttribute("normal", new THREE.BufferAttribute(normalValues, 3));
    } else {
      geometry.computeVertexNormals();
    }

    const alpha = Math.max(0, Math.min(255, source.alpha ?? 255)) / 255;
    const material = new THREE.MeshStandardMaterial({
      color: new THREE.Color(
        (source.red ?? 170) / 255,
        (source.green ?? 174) / 255,
        (source.blue ?? 182) / 255),
      opacity: alpha,
      transparent: alpha < 0.999,
      depthWrite: alpha >= 0.999,
      roughness: 0.78,
      metalness: 0,
      side: THREE.DoubleSide,
    });
    const mesh = new THREE.Mesh(geometry, material);
    mesh.name = source.id || "WASPer context mesh";
    contextGroup.add(mesh);
  }
}

// ---- M10 (Spatial Registration, added 2026-08-19) ----
// A dashed wireframe of the job's full bounding box, parented under contentGroup rather than
// added to `scene` directly -- being a child means it automatically inherits every transform
// contentGroup already gets for free (AR placement/scale/pinch-rotate, desktop's identity
// transform), no separate position/rotation/scale bookkeeping needed anywhere else in the file.
// It lives in helpersGroup, so process playback can rebuild processGroup without special-case
// preservation logic. Visible by default so spatial scale is immediately legible. The box is
// rebuilt when Context changes: it encloses print geometry alone when Context is hidden and the
// combined print/context scene when Context is visible.
let showBoundingBox = true;
let bboxHelper = null;

function disposeBoundingBox() {
  if (!bboxHelper) return;
  helpersGroup.remove(bboxHelper);
  for (const child of bboxHelper.children) {
    if (child.isSprite) {
      // Sprite.geometry is a single buffer three.js shares across every Sprite instance
      // internally -- deliberately not disposed here, only what's actually unique per label
      // (the material and its CanvasTexture).
      child.material.map?.dispose();
      child.material.dispose();
    } else {
      child.geometry?.dispose();
      child.material?.dispose();
    }
  }
  bboxHelper = null;
}

// A camera-facing text label (a Sprite backed by a small canvas-drawn texture) -- three.js has no
// built-in text rendering without loading a separate font asset, and this technique needs none.
// worldHeight sizes it in scene units (see callers) rather than pixels, so it reads at a
// consistent relative size regardless of the job's real-world scale.
function createDimensionLabel(text, worldHeight) {
  const fontPx = 64; // canvas-space resolution, independent of worldHeight -- stays crisp either way
  const canvas = document.createElement("canvas");
  const ctx = canvas.getContext("2d");
  const font = `600 ${fontPx}px -apple-system, Segoe UI, Roboto, sans-serif`;
  ctx.font = font;
  const textWidth = ctx.measureText(text).width;
  const padX = fontPx * 0.3;
  const padY = fontPx * 0.18;
  canvas.width = Math.ceil(textWidth + padX * 2);
  canvas.height = Math.ceil(fontPx + padY * 2);
  ctx.font = font; // resizing the canvas above resets all 2D context state, font included
  ctx.textBaseline = "middle";
  ctx.fillStyle = "rgba(20,21,26,0.78)";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "#ffb84d";
  ctx.fillText(text, padX, canvas.height / 2);

  const texture = new THREE.CanvasTexture(canvas);
  texture.minFilter = THREE.LinearFilter; // avoids mipmap generation, which needs power-of-two dims
  const material = new THREE.SpriteMaterial({ map: texture, depthTest: false });
  // depthTest:false so the label stays legible even when it'd otherwise be occluded by the
  // model's own geometry from the current angle -- these are annotations, not part of the actual
  // print, so reading like an always-on-top HUD callout is the intent, not a bug.
  const sprite = new THREE.Sprite(material);
  sprite.scale.set(worldHeight * (canvas.width / canvas.height), worldHeight, 1);
  return sprite;
}

// `box` is computeBounds(job)'s result -- the job's full extent (every branch, every point,
// independent of Path/Mesh/time-slider state), already in the same coordinate space Path/Mesh
// geometry is built in, so no conversion is needed to line the wireframe up with the real
// geometry. Dash/gap size (and the dimension labels' own size, below) are a fraction of the box's
// own longest dimension rather than a fixed number of scene units, so both read consistently
// whether the job is a 50mm test coupon or a 2m installation -- and, since contentGroup's *scale*
// (not this geometry) is what changes in AR, they stay proportionally consistent there too.
function buildBoundingBox(box) {
  disposeBoundingBox();
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const maxDim = Math.max(size.x, size.y, size.z, 1e-6);

  const geometry = new THREE.EdgesGeometry(new THREE.BoxGeometry(size.x, size.y, size.z));
  geometry.translate(center.x, center.y, center.z);
  const material = new THREE.LineDashedMaterial({
    color: 0xffb84d, dashSize: maxDim * 0.02, gapSize: maxDim * 0.012,
  });
  const wireframe = new THREE.LineSegments(geometry, material);
  wireframe.computeLineDistances(); // required for LineDashedMaterial to actually render as dashes

  bboxHelper = new THREE.Group();
  bboxHelper.add(wireframe);

  // Dimension labels ("the dimensions of the BBox should show on the BBox", 2026-08-19): one per
  // axis, each at the midpoint of an edge on the box's lower/min corner, nudged outward so it
  // doesn't overlap the wireframe itself. X/Y sit on the base plane (WASPer's Z-up convention --
  // matches "on the base plane" from the original request), Z is the height label on a vertical
  // edge. Billboarded (THREE.Sprite always faces the camera) so all three stay legible from any
  // AR viewing angle, not just square-on.
  const round = v => Math.round(v * 100) / 100;
  const labelSize = maxDim * 0.06;
  const offset = maxDim * 0.03;

  const xLabel = createDimensionLabel(String(round(size.x)), labelSize);
  xLabel.position.set(center.x, box.min.y - offset, box.min.z);
  bboxHelper.add(xLabel);

  const yLabel = createDimensionLabel(String(round(size.y)), labelSize);
  yLabel.position.set(box.min.x - offset, center.y, box.min.z);
  bboxHelper.add(yLabel);

  const zLabel = createDimensionLabel(String(round(size.z)), labelSize);
  zLabel.position.set(box.min.x - offset, box.min.y - offset, center.z);
  bboxHelper.add(zLabel);

  bboxHelper.visible = showBoundingBox;
  helpersGroup.add(bboxHelper);
}

// job.metadata.coordinates carries the units the job's raw numbers are actually in (mirrors
// CoordinateFrame in WASPer.XR.Core) -- shown when present rather than assumed, since a plain
// number alone ("210 x 180 x 65") doesn't say whether that's mm, m, or something else.
function formatBoundingBoxDims(box, job) {
  const size = box.getSize(new THREE.Vector3());
  const round = v => Math.round(v * 100) / 100;
  const units = job.metadata && job.metadata.coordinates && job.metadata.coordinates.units;
  const suffix = units ? ` ${units}` : "";
  return `${round(size.x)} × ${round(size.y)} × ${round(size.z)}${suffix}`;
}

// ---- M9 (WebXR, AR first pass, 2026-08-19) ----
// Deliberately hand-rolled rather than using three/addons/webxr/ARButton.js: that helper injects
// its own independently-styled floating button, which would sit alongside (and visually clash
// with) this page's already-deliberate control layout/CSS. Requesting the session directly gives
// full control over both the button (arButton, a normal element in this page's own layout) and
// exactly which features are requested.
//
// AR support requires two things neither of which this code can do anything about if missing:
// (1) an AR-capable device/browser (ARCore on Android via Chrome, generally) -- feature-detected
// below, arButton just stays hidden if unsupported; (2) a secure context (HTTPS, or
// localhost/127.0.0.1) -- WebXR is unavailable entirely over a plain http://<lan-ip> URL like
// the ones Sm01's Mobile Access QR codes point at, even on otherwise-capable hardware. For local
// dev/testing without setting up TLS, Chrome (desktop and Android) has a flag for exactly this:
// chrome://flags/#unsafely-treat-insecure-origin-as-secure -- enable it, add this page's
// http://<lan-ip>:5252 origin to the list, relaunch Chrome.
// Radii halved from the original 0.06/0.08m (2026-08-19 follow-up: "make the blue circle
// smaller, is too big") -- an 8cm-radius ring read as oversized against most real tabletop-scale
// placement targets.
const reticle = new THREE.Mesh(
  new THREE.RingGeometry(0.03, 0.04, 32).rotateX(-Math.PI / 2),
  new THREE.MeshBasicMaterial({ color: 0x4da3ff })
);
reticle.matrixAutoUpdate = false;
reticle.visible = false;
scene.add(reticle);
// This viewer places printing jobs on horizontal work surfaces. ARCore can also return hits on
// walls and other steep planes; accepting those makes the placement ring appear vertical and
// competes with the intended tabletop/floor hit. Keep poses whose local Y normal is within 25
// degrees of WebXR world-up. Reused temporaries avoid allocating objects every XR frame.
const AR_HORIZONTAL_MIN_ALIGNMENT = Math.cos(THREE.MathUtils.degToRad(25));
const arHitMatrix = new THREE.Matrix4();
const arHitQuaternion = new THREE.Quaternion();
const arHitNormal = new THREE.Vector3();
const arWorldUp = new THREE.Vector3(0, 1, 0);

// Z-up (WASPer's convention, see camera.up above) to Y-up (WebXR's "local-floor" reference
// space, which is gravity-aligned like every other AR framework) -- applied to contentGroup only
// when placing it in AR; the desktop/OrbitControls view never touches this and stays Z-up.
const AR_UP_CONVERSION = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), -Math.PI / 2);
// Starting point only, not a hard target -- every job has different proportions, so no single
// constant reads right for all of them (real-device feedback 2026-08-19: the first value tried,
// 0.4, ran a bit large on an actual desk). Pinch-to-resize (below) is what actually lets this be
// corrected per-job instead of chasing a better guess here.
const AR_TARGET_FOOTPRINT_METERS = 0.3;
const AR_PINCH_MIN_SCALE_FACTOR = 0.2; // relative to whatever scale a pinch gesture started from
const AR_PINCH_MAX_SCALE_FACTOR = 5;

let xrSession = null;
let hitTestSource = null;
let previousSceneBackground = null;
// Cached once per AR session (cleared in onArSessionEnded) rather than remeasured on every tap:
// re-tapping the reticle is meant to reposition, not silently undo a pinch resize by reapplying
// the auto-fit scale on top of it -- see onArSelect.
let arNativeMaxDimension = null;
let arScaleAdjustedByUser = false;
// Rotation around the world's vertical axis, layered on top of AR_UP_CONVERSION -- unlike scale
// there's no "auto" value to fall back to (0 is as good a default as any), so this is just applied
// on every placement/re-placement as-is, no separate "has the user touched this" flag needed the
// way arScaleAdjustedByUser gates scale. See applyArOrientation below.
let arUserYawRadians = 0;

// M10 spatial registration (2026-08-19, reworked into a drag-to-adjust rectangle later the same
// day -- see the long comment above startCalibration for the full history). While isCalibrating is
// true, onArSelect routes taps into the calibration flow instead of normal freeform placement.
// Once isCalibrated is true, the calibrated position stays authoritative and scene taps no longer
// reposition it, while the explicit scale/rotate controls remain available for deliberate
// fine-tuning. Cleared on session start/end alongside the other AR-session-scoped state above.
// isCalibrated locks in once Place Object has actually revealed contentGroup at a calibrated
// transform. Use Clear Calibration (arCalibrateButton) to deliberately return to tap-to-place.
let isCalibrating = false;
let isCalibrated = false;
// Normal freeform placement is one-shot. Previously every later XR 'select' event copied the
// moving reticle transform back to contentGroup, so an incidental canvas/UI tap could make a
// correctly placed model jump elsewhere. Reposition explicitly unlocks one additional tap.
let isPlacementLocked = false;
// Fit-to-Screen toggle (2026-08-19 follow-up): off by default, since true real-world scale is
// calibration's whole point -- but some jobs are large enough that seeing the whole thing up
// close on a phone screen means backing away several metres, so this lets the user opt into the
// same auto-fit-to-a-tabletop-footprint sizing freeform placement already always uses instead.
// Read every frame by updateCalibrationTracking while isCalibrating is true.
let arFitToScreen = false;

const arButton = document.getElementById("arButton");
const arHint = document.getElementById("arHint");
const arScaleSlider = document.getElementById("arScaleSlider");
const arRotateSlider = document.getElementById("arRotateSlider");
const arShowUiButton = document.getElementById("arShowUiButton");
const arCalibrateButton = document.getElementById("arCalibrateButton");
const arRepositionButton = document.getElementById("arRepositionButton");
const arFitScreenButton = document.getElementById("arFitScreenButton");
const arPlaceObjectButton = document.getElementById("arPlaceObjectButton");
const arOverlay = document.getElementById("arOverlay");
const arCollapseButton = document.getElementById("arCollapseButton");

if (navigator.xr) {
  navigator.xr.isSessionSupported("immersive-ar").then(supported => {
    if (supported) {
      arButton.style.display = "";
      // 2026-08-19 follow-up: lets #controls (desktop) reserve room above itself for arButton --
      // see the body.ar-supported CSS rule -- only once support is actually confirmed, rather
      // than unconditionally leaving an empty gap for every desktop browser that can't show AR
      // at all (support is feature-detected async, so this can't just be a plain CSS rule keyed
      // off #arButton's own display, which isn't set until this callback runs anyway).
      document.body.classList.add("ar-supported");
    }
  }).catch(() => { /* leave hidden -- isSessionSupported itself can reject, same as reporting false */ });
}

arButton.addEventListener("click", () => {
  if (xrSession) {
    xrSession.end();
    return;
  }
  navigator.xr.requestSession("immersive-ar", {
    requiredFeatures: ["hit-test"],
    // dom-overlay (added 2026-08-19, alongside #arOverlay above) is what actually lets arHint/
    // arScaleSlider -- and arButton itself, for exiting -- render and receive taps during the
    // session; it's optional because plain tap-to-place/pinch-to-resize (touch events on the
    // canvas) don't depend on it and keep working even where it's unsupported. root: document.body
    // rather than just #arOverlay so arButton (a sibling, not a descendant of #arOverlay) is
    // covered too -- body.ar-active's CSS is still what decides which of body's children are
    // actually visible during the session.
    optionalFeatures: ["local-floor", "dom-overlay"],
    domOverlay: { root: document.body },
  }).then(onArSessionStarted).catch(err => {
    console.error("Could not start AR session", err);
    // Surfaced on the button itself (2026-08-19), not just console.error: a phone has no easy
    // way to see devtools output without a USB-tethered remote debugging session, so a generic
    // "AR unavailable" left no way to tell a missing-camera-permission rejection apart from a
    // genuinely-unsupported-hit-test one from here.
    arButton.textContent = "AR failed: " + (err.name || "") + " " + (err.message || err);
  });
});

async function onArSessionStarted(session) {
  xrSession = session;
  previousSceneBackground = scene.background;
  scene.background = null; // let the camera passthrough show instead of the desktop-view navy fill
  // Hidden until the first placement tap (onArSelect, below) -- contentGroup otherwise keeps
  // whatever transform it last had (identity, on a fresh load), which would render right at the
  // camera's own local-floor origin the instant passthrough starts, before the user has scanned
  // for a surface or tapped anything. Restored in onArSessionEnded for the desktop view.
  contentGroup.visible = false;
  arHint.style.display = "";
  arScaleSlider.value = 1;
  arRotateSlider.value = 0;
  arUserYawRadians = 0;
  isCalibrating = false;
  isCalibrated = false;
  isPlacementLocked = false;
  arCalibrateButton.textContent = "Calibrate";
  arCalibrateButton.classList.remove("active");
  arFitToScreen = false;
  arFitScreenButton.classList.remove("active");
  arPlaceObjectButton.style.display = "none";
  arRepositionButton.style.display = "none";
  document.body.classList.remove("ar-ui-visible"); // Show UI starts off each session, not carried over
  arShowUiButton.textContent = "Show UI";
  session.addEventListener("end", onArSessionEnded);
  // AR already has the camera passthrough plus hit testing to process. Rendering its framebuffer
  // below native resolution is a substantial mobile GPU saving and is preferable to dropped
  // interaction frames; desktop/non-XR rendering keeps its independent pixel-ratio policy above.
  renderer.xr.setFramebufferScaleFactor(0.75);
  await renderer.xr.setSession(session);
  arButton.textContent = "Exit AR";
  document.body.classList.add("ar-active");
}

function onArSessionEnded() {
  xrSession = null;
  hitTestSource = null;
  reticle.visible = false;
  scene.background = previousSceneBackground;
  arButton.textContent = "Enter AR";
  document.body.classList.remove("ar-active");
  document.body.classList.remove("ar-ui-visible");
  arNativeMaxDimension = null; // next session starts from a fresh auto-fit measurement
  arScaleAdjustedByUser = false;
  arUserYawRadians = 0;
  isCalibrating = false;
  isCalibrated = false;
  isPlacementLocked = false;
  arCalibrateButton.textContent = "Calibrate";
  arCalibrateButton.classList.remove("active");
  arFitToScreen = false;
  arFitScreenButton.classList.remove("active");
  arPlaceObjectButton.style.display = "none";
  arRepositionButton.style.display = "none";
  // Back to the normal desktop transform -- OrbitControls/resetCamera never expect contentGroup
  // itself to carry a transform, only the camera does on that side.
  contentGroup.position.set(0, 0, 0);
  contentGroup.quaternion.identity();
  contentGroup.scale.set(1, 1, 1);
  contentGroup.visible = true; // restore for the desktop view
}

// Toggled independently from the desktop toggleUiButton/body.simple-ui (own CSS, see the
// body.ar-active.ar-ui-visible rules above) so switching one doesn't silently flip the other for
// the next desktop or AR session.
arShowUiButton.addEventListener("click", () => {
  const visible = document.body.classList.toggle("ar-ui-visible");
  arShowUiButton.textContent = visible ? "Hide UI" : "Show UI";
});

// Collapse toggle (2026-08-19 follow-up) -- see #arCollapseButton/#arOverlay.collapsed CSS.
// Independent of arShowUiButton above: that one hides the *desktop-style* chrome (HUD, Path/
// Mesh/BBox, timeline) during AR; this one only shrinks #arOverlay itself (the calibration/scale/
// rotate panel), which stays relevant/needed regardless of whether the rest of the UI is showing.
arCollapseButton.addEventListener("click", () => {
  const collapsed = arOverlay.classList.toggle("collapsed");
  arCollapseButton.textContent = collapsed ? "▲" : "▼"; // caret up (expand) / down (collapse)
  arCollapseButton.title = collapsed ? "Expand this panel" : "Collapse this panel";
});

renderer.xr.addEventListener("sessionstart", () => {
  const session = renderer.xr.getSession();
  session.requestReferenceSpace("viewer").then(viewerSpace => {
    session.requestHitTestSource({ space: viewerSpace }).then(source => {
      hitTestSource = source;
    });
  });
  session.addEventListener("select", onArSelect);
});

// Fixes a real bug found 2026-08-19: tapping Hide UI, Path/Mesh/BBox, Play, etc. during an AR
// session was also re-placing/repositioning contentGroup, as if the reticle had been tapped.
// domOverlay's root is document.body (see requestSession above), which per spec means every tap
// -- including ones that land on our own overlay buttons/sliders, not just the 3D canvas -- fires
// a 'beforexrselect' event on that root *before* the corresponding XRSession 'select' event (the
// one onArSelect listens for, above). Chrome does NOT automatically suppress 'select' for taps on
// interactive dom-overlay elements; the page has to do that itself by calling preventDefault()
// here, which is exactly what was missing -- every button tap was quietly also counting as a
// placement tap on the scene. Only suppressed for taps that did NOT land on the canvas itself
// (renderer.domElement): actual tap-to-place/reposition/calibration-anchor taps on the open scene
// still need to reach onArSelect/onArSelect's calibration branch/finishCornerAdjustment normally.
document.body.addEventListener("beforexrselect", event => {
  if (event.target !== renderer.domElement) event.preventDefault();
});

// Measured once per AR session (see arNativeMaxDimension above), scale temporarily zeroed out
// during the measurement so an earlier placement's scale can never compound into the reading --
// then restored, since this is meant to be a read, not a side effect.
function measureArNativeMaxDimension() {
  const previousScale = contentGroup.scale.x;
  contentGroup.scale.set(1, 1, 1);
  contentGroup.updateMatrixWorld(true);
  const box = new THREE.Box3().setFromObject(contentGroup);
  const size = box.getSize(new THREE.Vector3());
  contentGroup.scale.setScalar(previousScale);
  return Math.max(size.x, size.y, size.z, 1e-6);
}

// Composes the fixed Z-up -> Y-up conversion with arRotateSlider's world-vertical-axis yaw, in
// that order: AR_UP_CONVERSION establishes the upright orientation first, then yawQuat spins it
// around the *world's* Y axis on top of that (not the model's own local axis, which after
// up-conversion no longer points the way you'd expect) -- q.multiply(other) in three.js applies
// `other` first and `q` second, so yawQuat.multiply(AR_UP_CONVERSION) is exactly that order.
// Called from onArSelect (every placement/re-placement) and directly from arRotateSlider's own
// handler (so dragging it updates the already-placed model immediately, no re-tap needed).
function applyArOrientation() {
  const yaw = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), arUserYawRadians);
  contentGroup.quaternion.copy(yaw).multiply(AR_UP_CONVERSION);
}

// Re-tappable by design (not a one-shot placement): if the first tap lands somewhere awkward,
// tapping the reticle again elsewhere just moves the model rather than requiring an Exit/re-Enter
// round trip. Re-tapping only ever changes position/orientation, never scale, once
// arScaleAdjustedByUser is set (by a pinch gesture, below) -- otherwise every reposition tap
// would silently discard a manual resize by reapplying the auto-fit footprint on top of it.
// Rotation (arUserYawRadians) has no separate "auto" value to protect, so it's simply reapplied
// as-is on every tap via applyArOrientation -- re-tapping was never going to reset it either way.
function onArSelect() {
  if (!reticle.visible) return;
  // M10, simplified further 2026-08-19 (second pass, same day): taps no longer do anything during
  // calibration. Real-device testing of the tap-to-set-position version above found Place Object
  // frequently placed the model at a stale/wrong spot -- if the user never happened to tap after
  // the reticle settled on its final position, contentGroup's transform was whatever the last tap
  // (or nothing) had left it at. The reticle is already a continuous live reference the instant
  // it's visible, so there's no reason to gate contentGroup's transform behind a tap at all --
  // updateCalibrationTracking (called every frame from updateArHitTest) now keeps contentGroup
  // glued to the reticle for as long as calibration is active, and Place Object simply reveals
  // whatever that live position currently is. A tap during calibration is now a no-op.
  if (isCalibrating) return;
  if (isCalibrated) return;
  if (isPlacementLocked) return;

  if (arNativeMaxDimension == null) arNativeMaxDimension = measureArNativeMaxDimension();
  if (!arScaleAdjustedByUser) {
    contentGroup.scale.setScalar(AR_TARGET_FOOTPRINT_METERS / arNativeMaxDimension);
    arScaleSlider.value = 1; // matches the auto-fit scale just applied above -- see the slider's own handler
  }

  contentGroup.position.setFromMatrixPosition(reticle.matrix);
  applyArOrientation();
  contentGroup.visible = true; // first tap after entering AR is what actually reveals the model
  arHint.style.display = "none"; // no longer needed once something is placed
  isPlacementLocked = true;
  reticle.visible = false;
  arRepositionButton.style.display = "";
}

arRepositionButton.addEventListener("click", () => {
  // Keep the object visible at its current transform while the user searches for a new surface.
  // Exactly one subsequent scene tap is accepted; onArSelect locks it again immediately.
  isPlacementLocked = false;
  arRepositionButton.style.display = "none";
  arHint.textContent = "Point at a surface, then tap to reposition";
  arHint.style.display = "";
});

// M10 spatial registration -- detects a single placement point (the hit-test reticle, same as
// freeform placement) at true real-world scale, then requires an explicit Place Object tap to
// reveal it there. Simplified 2026-08-19 down to this from a much more elaborate history tried
// earlier the same day: a 3-freehand-tap flow (imprecise -- small pixel-level tap error compounded
// into a visibly-off alignment), then an auto-detected-plane-plus-2-snapped-taps flow (kept
// failing to lock onto anything, most likely because a real scan rarely produces a clean,
// job-sized rectangle), then a one-anchor-tap-plus-drag-2-corners flow (still not working
// reliably on-device even after that). None of the plane-detection/rectangle-fitting machinery
// those needed is used any more -- just the same reticle-based tap freeform placement already
// relies on.

// Every other position in this viewer (mesh/path geometry, computeBounds, the bounding box) is in
// the job's own raw units -- mm for a typical WASPer job -- with metresPerUnit (job.metadata.
// coordinates, mirrors CoordinateFrame.MetresPerUnit in WASPer.XR.Core) never applied anywhere
// else in the file, since the freeform AR placement above deliberately ignores real-world scale
// anyway (AR_TARGET_FOOTPRINT_METERS auto-fits to a tabletop-sized footprint regardless of what
// the job's numbers actually mean). Calibration is different: it's meant to be a true 1:1
// real-world alignment, so it's the one place that needs this conversion.
function jobMetresPerUnit(job) {
  const value = job && job.metadata && job.metadata.coordinates && job.metadata.coordinates.metresPerUnit;
  return (typeof value === "number" && value > 0) ? value : 1;
}

function startCalibration() {
  if (!currentJob) return;
  isCalibrating = true;
  isCalibrated = false;
  isPlacementLocked = false;
  arRepositionButton.style.display = "none";
  // Explicitly hidden here (2026-08-19) rather than just left as-is: if a freeform tap had
  // already placed and revealed contentGroup before Calibrate was tapped, leaving .visible
  // untouched would let it keep rendering at its old spot throughout calibrating, then jump
  // straight to the new calibrated transform the instant the first tap lands here -- defeating
  // the whole point of gating the reveal behind Place Object, below.
  contentGroup.visible = false;
  // Fresh baseline each time Calibrate is (re)started (2026-08-19 follow-up, "scale and rotate
  // sliders are not affecting the object"): otherwise a multiplier fine-tuned against a previous
  // calibration attempt's baseline (or against freeform's auto-fit footprint, if that ran first
  // this session) would get silently carried over and applied on top of *this* attempt's true
  // scale/auto-fit -- see arBaselineScale/updateCalibrationTracking.
  arScaleAdjustedByUser = false;
  arScaleSlider.value = 1;
  // Shown as a placement reference the moment Calibrate starts.
  showBoundingBox = true;
  if (bboxHelper) bboxHelper.visible = true;
  modeButtons.bbox.classList.add("active");
  // Force Path on too (2026-08-19 follow-up, "Place Object not placing the object"): Path keeps
  // the complete toolpath visible during playback, using translucent lines for pending segments,
  // unlike Mesh (which only shows what's printed *up to* currentTime and can legitimately be empty if a live-pushed
  // job hasn't started printing yet, or is paused at 0% via an external sim_par). Simple UI's
  // mobile default (see the matchMedia block far below) turns Path off out of the box, which is
  // exactly the device this feature is used on -- so calibrating against a job with little/no
  // print progress yet could end up with nothing in contentGroup to actually see, making Place
  // Object look like it isn't working even though the transform was set correctly. Forcing Path
  // on here guarantees a visible, complete reference for as long as the job has any geometry at
  // all, regardless of playback position -- left on afterward rather than restored on
  // cancel/clear, same as BBox above (the user can toggle it back off from #modePath any time).
  if (!showPath) {
    showPath = true;
    modeButtons.path.classList.add("active");
    rebuildContent();
  }
  arCalibrateButton.textContent = "Cancel Calibration";
  arCalibrateButton.classList.add("active");
  arPlaceObjectButton.style.display = "none";
  arHint.style.display = "";
  arHint.textContent = "Point camera at a surface to detect placement position.";
}

function cancelCalibration() {
  isCalibrating = false;
  arCalibrateButton.classList.remove("active");
  arCalibrateButton.textContent = isCalibrated ? "Clear Calibration" : "Calibrate";
  arPlaceObjectButton.style.display = "none";
  if (!isCalibrated) {
    arHint.textContent = "Point at a surface, then tap to place";
    arHint.style.display = contentGroup.visible ? "none" : "";
  }
}

// Finishing action for calibration: reveals contentGroup at whatever placement point the last tap
// in onArSelect set (position/orientation/true-scale already applied there, just hidden), then
// locks the placement in (isCalibrated) the same way the old Confirm Calibration step used to.
arPlaceObjectButton.addEventListener("click", () => {
  contentGroup.visible = true;
  arPlaceObjectButton.style.display = "none";
  arHint.style.display = "none";
  arHint.textContent = "Point at a surface, then tap to place"; // restored for if calibration is cleared
  isCalibrating = false;
  isCalibrated = true;
  isPlacementLocked = true;
  // Hide the placement target immediately. Do not wait for the next XR hit-test frame to
  // observe the locked state: on some Android devices that left the last blue ring rendered
  // after placement. A new calibration attempt is what makes it eligible to appear again.
  reticle.visible = false;
  arCalibrateButton.textContent = "Clear Calibration";
  arCalibrateButton.classList.add("active");
});

arFitScreenButton.addEventListener("click", () => {
  arFitToScreen = !arFitToScreen;
  arFitScreenButton.classList.toggle("active", arFitToScreen);
  // Reset the fine-tune baseline (2026-08-19 follow-up), same reasoning as startCalibration's own
  // reset: a multiplier dialed in against true scale doesn't mean anything applied to the auto-fit
  // footprint, or vice versa, so switching baselines snaps back to a clean 1x rather than carrying
  // a now-meaningless multiplier over. updateCalibrationTracking picks up both changes on the very
  // next frame while isCalibrating; otherwise this simply takes effect next time Calibrate starts.
  arScaleAdjustedByUser = false;
  arScaleSlider.value = 1;
  if (currentJob && (contentGroup.visible || isCalibrating)) {
    contentGroup.scale.setScalar(arBaselineScale());
  }
});

arCalibrateButton.addEventListener("click", () => {
  if (isCalibrating) {
    cancelCalibration();
    return;
  }
  if (isCalibrated) {
    // Leaves contentGroup exactly where the calibration put it -- freeform controls (tap/pinch/
    // sliders) simply resume being able to adjust it from that point on. Also covers clearing a
    // calibration that was confirmed but never actually placed (Place Object still pending) --
    // hides that button too so it doesn't linger for a calibration that no longer applies.
    isCalibrated = false;
    isPlacementLocked = true;
    arCalibrateButton.textContent = "Calibrate";
    arCalibrateButton.classList.remove("active");
    arPlaceObjectButton.style.display = "none";
    arRepositionButton.style.display = "";
    return;
  }
  startCalibration();
});

// Click-and-drag alternative to the two-finger pinch below, added 2026-08-19 alongside dom-overlay
// -- both just set contentGroup's scale as a multiplier on top of arBaselineScale(), so dragging
// the slider after a pinch (or vice versa) continues from wherever the other one left off rather
// than the two fighting over separate state.
//
// 2026-08-19 follow-up ("scale and rotate sliders are not affecting the object"): this used to
// hardcode AR_TARGET_FOOTPRINT_METERS / arNativeMaxDimension as the baseline and bail out
// entirely if arNativeMaxDimension was still null -- correct for freeform (which always measures
// it on the first tap), but during true-scale calibration arNativeMaxDimension is never measured
// at all, so the slider silently did nothing the whole time it was calibrating. Routed through
// arBaselineScale() instead, which picks true scale or the auto-fit footprint depending on
// what's actually active right now, and only measures arNativeMaxDimension lazily when the
// auto-fit path actually needs it -- so this works in every mode, calibrating or not.
arScaleSlider.addEventListener("input", () => {
  contentGroup.scale.setScalar(arBaselineScale() * Number(arScaleSlider.value));
  arScaleAdjustedByUser = true;
});

// Spins the already-placed model in place -- works before a first placement too (harmless, just
// not visible yet, contentGroup.visible is still false at that point).
//
// 2026-08-19 follow-up: no longer gated on isCalibrated. That guard dated back to the original
// plane-detection calibration (a full 3D rotation solved from the detected surface's normal, which
// a yaw-only slider really would have skewed) -- calibration has been a single reticle point plus
// this exact same user-yaw mechanism freeform placement already used since the 2026-08-19
// simplification above, so there's no longer a separate "solved" rotation left to protect here.
arRotateSlider.addEventListener("input", () => {
  arUserYawRadians = THREE.MathUtils.degToRad(Number(arRotateSlider.value));
  applyArOrientation();
});

// Two-finger pinch to resize the placed model, the same gesture as pinch-zooming a photo --
// added 2026-08-19 after real-device feedback that the auto-fit footprint above ran a bit large
// in practice: no single constant reads right for every job's proportions, so this is what
// actually lets it be corrected per-job rather than chasing a better guess in code. Plain touch
// events on the canvas (not the WebXR 'select'/input-source APIs) because handheld AR's own taps
// already surface as ordinary DOM touch events -- Chrome synthesizes 'select' from a single quick
// tap, and a deliberate two-finger gesture doesn't trigger that, so there's no conflict with
// onArSelect's tap-to-place/reposition above.
let arPinchStartDistance = null;
let arPinchStartScale = 1;

function arTouchDistance(touches) {
  const dx = touches[0].clientX - touches[1].clientX;
  const dy = touches[0].clientY - touches[1].clientY;
  return Math.hypot(dx, dy);
}

renderer.domElement.addEventListener("touchstart", event => {
  if (!xrSession || isCalibrating || event.touches.length !== 2) return;
  arPinchStartDistance = arTouchDistance(event.touches);
  arPinchStartScale = contentGroup.scale.x;
});

renderer.domElement.addEventListener("touchmove", event => {
  if (!xrSession || isCalibrating || event.touches.length !== 2 || arPinchStartDistance == null) return;
  event.preventDefault();
  const factor = arTouchDistance(event.touches) / arPinchStartDistance;
  const clampedFactor = THREE.MathUtils.clamp(factor, AR_PINCH_MIN_SCALE_FACTOR, AR_PINCH_MAX_SCALE_FACTOR);
  contentGroup.scale.setScalar(arPinchStartScale * clampedFactor);
  arScaleAdjustedByUser = true;
  // Keep the slider synchronized against the active baseline: true scale after calibrated
  // placement, or the auto-fit footprint during normal freeform placement.
  arScaleSlider.value = contentGroup.scale.x / Math.max(arBaselineScale(), 1e-9);
}, { passive: false });

renderer.domElement.addEventListener("touchend", event => {
  if (event.touches.length < 2) arPinchStartDistance = null;
});

// Shared by updateCalibrationTracking, arScaleSlider's own handler, and freeform placement's
// onArSelect -- the scale any of those three would apply *before* a manual slider/pinch
// adjustment (arScaleAdjustedByUser) is layered on top. True real-world scale while calibrating
// or after calibrated placement (unless Fit to Screen is on), or the same auto-fit-to-a-tabletop
// footprint sizing everywhere else -- lazily measuring/caching arNativeMaxDimension the first
// time it is actually needed, same as onArSelect always has.
// 2026-08-19 follow-up ("scale and rotate sliders are not affecting the object"): pulled out of
// updateCalibrationTracking's body so arScaleSlider can multiply against whichever baseline is
// actually active right now instead of being hardcoded to freeform's auto-fit footprint -- see
// that handler below for why the hardcoded version silently did nothing during true-scale
// calibration.
function arBaselineScale() {
  if ((isCalibrating || isCalibrated) && !arFitToScreen) return jobMetresPerUnit(currentJob);
  if (arNativeMaxDimension == null) arNativeMaxDimension = measureArNativeMaxDimension();
  return AR_TARGET_FOOTPRINT_METERS / arNativeMaxDimension;
}

// Continuously glues contentGroup to the live reticle for as long as calibration is active --
// added 2026-08-19 (second pass) to replace the old tap-to-set-position calibration flow, which
// left Place Object revealing whatever stale transform the last tap (or none at all) had set.
// Called every frame from updateArHitTest, after reticle.visible has just been refreshed, so it
// always reflects this frame's hit-test result rather than a frame-old one. Place Object's own
// visibility is driven purely by reticle.visible here -- "as soon as the circle is visible on the
// screen" -- rather than by any tap having happened.
//
// 2026-08-19 follow-up ("scale and rotate sliders are not affecting the object"): scale used to be
// unconditionally recomputed here every single frame, which meant any manual adjustment from
// arScaleSlider (or a pinch) made *during* calibration was overwritten again on the very next
// frame before it ever had a chance to read as "working". Now gated behind !arScaleAdjustedByUser
// -- position (and orientation, which is cheap and has no separate "auto" value worth protecting,
// same as freeform) still track the reticle every frame regardless, only the scale baseline stops
// once the user has taken over. startCalibration/the Fit to Screen toggle both reset
// arScaleAdjustedByUser back to false, below, so switching baselines snaps back to a clean 1x
// starting point rather than carrying over a multiplier tuned for a totally different baseline.
function updateCalibrationTracking() {
  if (!reticle.visible) {
    arPlaceObjectButton.style.display = "none";
    arHint.textContent = "Point camera at a surface to detect placement position.";
    return;
  }
  contentGroup.position.setFromMatrixPosition(reticle.matrix);
  applyArOrientation();
  if (!arScaleAdjustedByUser) contentGroup.scale.setScalar(arBaselineScale());
  arPlaceObjectButton.style.display = "";
  arHint.textContent = "Move the phone to adjust, then tap Place Object to show it here.";
}

function updateArHitTest(frame) {
  if (!hitTestSource) return;
  const referenceSpace = renderer.xr.getReferenceSpace();
  const results = frame.getHitTestResults(hitTestSource);
  const acceptsPlacement = isCalibrating || (!isCalibrated && !isPlacementLocked);
  if (acceptsPlacement && results.length > 0) {
    const pose = results[0].getPose(referenceSpace);
    arHitMatrix.fromArray(pose.transform.matrix);
    arHitQuaternion.setFromRotationMatrix(arHitMatrix);
    arHitNormal.set(0, 1, 0).applyQuaternion(arHitQuaternion).normalize();
    const isHorizontal = Math.abs(arHitNormal.dot(arWorldUp)) >= AR_HORIZONTAL_MIN_ALIGNMENT;
    reticle.visible = isHorizontal;
    if (isHorizontal) reticle.matrix.copy(arHitMatrix);
  } else {
    reticle.visible = false;
  }
  if (isCalibrating) updateCalibrationTracking();
}

let currentJob = null;
let pendingExternalSimulationParameter = null;
// Path and Mesh are independent on/off layers, not exclusive modes. Path is
// the whole job as thin lines, split by playback into opaque printed and
// translucent pending segments. Mesh is solid bead geometry up to the same
// time. Path starts enabled while the heavier Mesh layer is opt-in.
let showPath = true;
let showMesh = false;
let currentTime = 0;
let sceneScale = 10; // updated once the job's bounds are known; drives travel-line dash sizing
const roleVisibility = new Map(); // role -> bool, toggled via the legend's eye icons

let currentCameraBounds = null;

function resetCamera() {
  if (!currentJob) return;
  const visibleBounds = computeVisibleBounds(currentJob);
  if (!visibleBounds) return;
  frameCamera(visibleBounds);
}
document.getElementById("resetCamera").addEventListener("click", resetCamera);

const toggleUiButton = document.getElementById("toggleUiButton");
toggleUiButton.addEventListener("click", () => {
  const simplified = document.body.classList.toggle("simple-ui");
  toggleUiButton.textContent = simplified ? "Full UI" : "Simple UI";
});

// #hud drag-to-resize (added 2026-08-19) -- see the #hud/#hudResizeHandle CSS comments above for
// why (some KPI values are far wider than any one fixed panel width reads comfortably). Pointer
// Events rather than separate mouse/touch listeners: one code path covers both a mouse drag and a
// finger drag on a touchscreen device running the Full UI (the handle is hidden entirely at the
// phone breakpoint, where #hud already spans edge-to-edge instead of having a fixed width to drag).
const hud = document.getElementById("hud");
const hudResizeHandle = document.getElementById("hudResizeHandle");
hudResizeHandle.addEventListener("pointerdown", event => {
  event.preventDefault();
  hudResizeHandle.setPointerCapture(event.pointerId);
  const startX = event.clientX;
  const startWidth = hud.getBoundingClientRect().width;

  function onPointerMove(moveEvent) {
    const next = startWidth + (moveEvent.clientX - startX);
    // Mirrors #hud's own CSS min-width/max-width -- clamped here too so dragging past either
    // limit doesn't feel like it "runs out" with no visual explanation of why.
    const maxWidth = Math.min(window.innerWidth * 0.7, 640);
    hud.style.width = Math.min(Math.max(next, 220), maxWidth) + "px";
  }
  function onPointerUp() {
    hudResizeHandle.removeEventListener("pointermove", onPointerMove);
    hudResizeHandle.removeEventListener("pointerup", onPointerUp);
  }
  hudResizeHandle.addEventListener("pointermove", onPointerMove);
  hudResizeHandle.addEventListener("pointerup", onPointerUp);
});

function frameCamera(box) {
  const size = new THREE.Vector3();
  const center = new THREE.Vector3();
  box.getSize(size);
  box.getCenter(center);

  // Fit a sphere around the complete job against the narrower of the camera's horizontal and
  // vertical fields of view. The previous max-dimension * 3.2 approximation could crop tall jobs
  // on wide screens and wide jobs on phones because it ignored aspect ratio and box diagonal.
  const sphere = box.getBoundingSphere(new THREE.Sphere());
  const radius = Math.max(sphere.radius, 0.5);
  const verticalFov = THREE.MathUtils.degToRad(camera.fov);
  const horizontalFov = 2 * Math.atan(Math.tan(verticalFov / 2) * Math.max(camera.aspect, 1e-6));
  const limitingFov = Math.min(verticalFov, horizontalFov);
  const distance = (radius / Math.sin(limitingFov / 2)) * 1.15;
  sceneScale = Math.max(size.x, size.y, size.z, 1) * 0.5;

  const viewDirection = new THREE.Vector3(1, -1, 0.8).normalize();
  const position = center.clone().addScaledVector(viewDirection, distance);
  camera.position.copy(position);
  camera.zoom = 1;
  // Keep the near plane close enough for bead-level inspection even when the overall job spans
  // thousands of units. logarithmicDepthBuffer above prevents the large near/far ratio from
  // degrading depth precision, while the generous far plane avoids clipping after zooming out.
  camera.near = Math.max(radius * 1e-6, 0.001);
  camera.far = Math.max(distance + radius * 20, radius * 100, 1000);
  camera.updateProjectionMatrix();

  controls.target.copy(center);
  controls.update();
  currentCameraBounds = box.clone();
}

let platformGrid = null;

function disposePlatform() {
  if (!platformGrid) return;
  platformGrid.parent?.remove(platformGrid);
  platformGrid.geometry?.dispose();
  platformGrid.material?.dispose();
  platformGrid = null;
}

function buildPlatform(box) {
  const size = new THREE.Vector3();
  const center = new THREE.Vector3();
  box.getSize(size);
  box.getCenter(center);

  const span = Math.max(size.x, size.y, 10) * 1.4;
  const divisions = 20;
  const grid = new THREE.GridHelper(span, divisions, 0x3a3d4a, 0x24262f);
  grid.rotation.x = Math.PI / 2; // GridHelper is XZ by default; WASPer's ground plane is XY (Z-up).
  // Nudge the platform slightly below the path's lowest point rather than
  // exactly at it -- coincident planes z-fight, and a path running through
  // y=0 (the grid's own centerline, as the sample fixture's does) would
  // otherwise render invisibly on top of a grid line instead of above it.
  const zOffset = Math.max(size.x, size.y, size.z, 10) * 0.01;
  grid.position.set(center.x, center.y, box.min.z - zOffset);
  // Keep the reference grid under the same transform root as the WASPer geometry. In desktop
  // view contentGroup is identity, while AR applies the required Z-up -> Y-up conversion to the
  // whole job. Adding the grid directly to scene left it in WASPer's XY plane, which is vertical
  // in WebXR's Y-up world even though the placed object itself was correctly oriented.
  helpersGroup.add(grid);
  platformGrid = grid;
}

function toVec3(p) {
  return new THREE.Vector3(p.x, p.y, p.z);
}

// ---- Path layer: print segments are sorted and batched by role. Each role uses one geometry
// with two draw groups: printed segments use the palette color at full opacity, while pending
// segments use the same color translucently. Playback only changes the group ranges. ----

const PENDING_PATH_OPACITY = 0.18;
let pathPlaybackEntries = [];

function updatePathPlayback(time) {
  for (const entry of pathPlaybackEntries) {
    const printedCount = upperBound(entry.times, time);
    const printedVertices = printedCount * 2;
    const totalVertices = entry.times.length * 2;
    entry.geometry.clearGroups();
    if (printedVertices > 0) entry.geometry.addGroup(0, printedVertices, 0);
    if (printedVertices < totalVertices) {
      entry.geometry.addGroup(printedVertices, totalVertices - printedVertices, 1);
    }
  }
}

function buildPathMode(job, time) {
  const segmentsByRole = new Map();
  for (const segment of job.segments) {
    if (segment.type !== "print" || roleVisibility.get(segment.role) === false) continue;
    if (!segmentsByRole.has(segment.role)) segmentsByRole.set(segment.role, []);
    segmentsByRole.get(segment.role).push(segment);
  }

  pathPlaybackEntries = [];
  for (const [role, segments] of segmentsByRole) {
    segments.sort((a, b) => a.startTimeSeconds - b.startTimeSeconds);
    const positions = new Float32Array(segments.length * 6);
    const times = new Float64Array(segments.length);
    for (let i = 0; i < segments.length; i++) {
      const segment = segments[i];
      const offset = i * 6;
      positions[offset] = segment.from.x;
      positions[offset + 1] = segment.from.y;
      positions[offset + 2] = segment.from.z;
      positions[offset + 3] = segment.to.x;
      positions[offset + 4] = segment.to.y;
      positions[offset + 5] = segment.to.z;
      times[i] = segment.startTimeSeconds;
    }
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    const color = ROLE_COLOR[role] ?? ROLE_COLOR.undefined;
    const printedMaterial = new THREE.LineBasicMaterial({ color });
    const pendingMaterial = new THREE.LineBasicMaterial({
      color,
      transparent: true,
      opacity: PENDING_PATH_OPACITY,
      depthWrite: false,
    });
    const lines = new THREE.LineSegments(geometry, [printedMaterial, pendingMaterial]);
    lines.userData.ownsMaterial = true;
    processGroup.add(lines);
    pathPlaybackEntries.push({ geometry, times });
  }
  updatePathPlayback(time);
}

// ---- Ghost mode (TEMPORARILY DISABLED -- not wired to a UI button; tested
// slower than Mesh mode in a real browser on the real study, likely fragment-
// shader overdraw from thousands of overlapping impostor boxes rather than
// anything wrong with the batching itself -- kept here, untouched, in case
// batch-size/impostor-tightness tuning brings it back below Mesh mode). GPU
// raymarch, porting WASPer_PrintPathSegmentRenderer.cs's
// actual technique (the "Pp18" renderer) instead of building any per-segment
// CPU mesh at all. Per segment: a small procedural impostor box is drawn
// (36 hardcoded vertices, indexed by gl_VertexID, no vertex buffer), and the
// fragment shader raymarches an implicit swept-superellipse field in world
// space to find the exact bead surface, writing gl_FragDepth so it composites
// correctly with everything else in the scene. Per-segment data (endpoint
// centers, width/height axes, half-extents, cap radius/flags) is packed into
// an RGBA32F texture and read via texelFetch -- one instanced draw call
// (THREE.InstancedMesh) covers an entire batch of up to ~2048 segments,
// matching the native renderer's own texture-width-driven batch cap. This is
// the actual reason Pp18 exists: shows the exact bead shape at any zoom with
// almost no CPU-side geometry construction, unlike Mesh mode's real tubes.
//
// Ported faithfully field-for-field from the native GLSL (both shaders below
// mirror WasperPrintPathSegmentRenderer.cs's VertexShader/FragmentShader
// nearly verbatim) and from WASPer_PrintPathPreview.cs's texel packing
// layout. One deliberate deviation: the native shader reconstructs its ray
// from a `noperspective in vec2 vNdc` varying, a desktop-GLSL-only
// interpolation qualifier with no equivalent in WebGL2's GLSL ES 300. The
// standard web substitute -- reconstructing NDC directly from gl_FragCoord,
// which is already exact per-pixel screen position, no interpolation needed
// -- replaces it below; every other line of the raymarch is unchanged. ----

const GHOST_FLOATS_PER_SEGMENT = 24; // 6 RGBA32F texels, matching WasperPrintPathPreviewBatch.FloatsPerSegment
const GHOST_MAX_SEGMENTS_PER_BATCH = 2048; // matches native's MaxSegmentsPerBatch (texture-width safety margin)
const GHOST_AMBIENT = 0.6;
const GHOST_SHADE_STRENGTH = 0.4;
// Bead cross-section profile exponent, matching Pp18's superellipse exponent (2 = ellipse, 4 =
// squarer/rounded-rect-like, 6 = squircle) -- shared by both Ghost mode (uProfileExponent, below)
// and Mesh mode (buildMeshMode, further down). Used to be a live <select> in #controls (removed
// 2026-08-19: rarely touched, and being the widest item in that row made it the one most likely
// to push the row onto a second line -- which, on top of AR's own #controls-height-dependent
// positioning math added the same day, made the row's height less predictable than it needed to
// be). Hardcoded back to its old default; flip back to a <select> feeding this via a "change"
// listener (as it briefly was) if per-job control over this ever actually gets used.
const PROFILE_EXPONENT = 4;

const GHOST_VERTEX_SHADER = `precision highp float;
precision highp int;
precision highp sampler2D;

uniform sampler2D uSegments;
uniform mat4 uWorldToClip;

flat out int vSegment;

const vec3 cube[36] = vec3[36](
    vec3(-1.0,-1.0,-1.0), vec3( 1.0,-1.0,-1.0), vec3( 1.0, 1.0,-1.0),
    vec3(-1.0,-1.0,-1.0), vec3( 1.0, 1.0,-1.0), vec3(-1.0, 1.0,-1.0),
    vec3(-1.0,-1.0, 1.0), vec3( 1.0, 1.0, 1.0), vec3( 1.0,-1.0, 1.0),
    vec3(-1.0,-1.0, 1.0), vec3(-1.0, 1.0, 1.0), vec3( 1.0, 1.0, 1.0),
    vec3(-1.0,-1.0,-1.0), vec3(-1.0,-1.0, 1.0), vec3( 1.0,-1.0, 1.0),
    vec3(-1.0,-1.0,-1.0), vec3( 1.0,-1.0, 1.0), vec3( 1.0,-1.0,-1.0),
    vec3(-1.0, 1.0,-1.0), vec3( 1.0, 1.0, 1.0), vec3(-1.0, 1.0, 1.0),
    vec3(-1.0, 1.0,-1.0), vec3( 1.0, 1.0,-1.0), vec3( 1.0, 1.0, 1.0),
    vec3(-1.0,-1.0,-1.0), vec3(-1.0, 1.0, 1.0), vec3(-1.0,-1.0, 1.0),
    vec3(-1.0,-1.0,-1.0), vec3(-1.0, 1.0,-1.0), vec3(-1.0, 1.0, 1.0),
    vec3( 1.0,-1.0,-1.0), vec3( 1.0,-1.0, 1.0), vec3( 1.0, 1.0, 1.0),
    vec3( 1.0,-1.0,-1.0), vec3( 1.0, 1.0, 1.0), vec3( 1.0, 1.0,-1.0)
);

vec4 segmentTexel(int segment, int offset) {
    return texelFetch(uSegments, ivec2(segment * 6 + offset, 0), 0);
}

void main() {
    int segment = gl_InstanceID;
    vec4 t0 = segmentTexel(segment, 0);
    vec4 t1 = segmentTexel(segment, 1);
    vec4 t2 = segmentTexel(segment, 2);
    vec4 t3 = segmentTexel(segment, 3);
    vec4 t4 = segmentTexel(segment, 4);
    vec4 t5 = segmentTexel(segment, 5);

    vec3 local = cube[gl_VertexID];
    bool atA = local.z < 0.0;
    vec3 base = atA ? t0.xyz : t1.xyz;
    vec3 w = normalize(atA ? t2.xyz : t4.xyz);
    vec3 h = normalize(atA ? t3.xyz : t5.xyz);
    vec3 n = normalize(cross(h, w));
    float hw = atA ? t0.w : t1.w;
    float hh = atA ? t2.w : t3.w;
    float ext = t4.w + 0.1 * max(hw, hh);

    vec3 center = base + n * (atA ? -ext : ext);
    vec3 world = center + w * (local.x * hw * 1.2) + h * (local.y * hh * 1.2);
    gl_Position = uWorldToClip * vec4(world, 1.0);
    vSegment = segment;
}`;

const GHOST_FRAGMENT_SHADER = `precision highp float;
precision highp int;
precision highp sampler2D;

uniform sampler2D uSegments;
uniform mat4 uClipToWorld;
uniform mat4 uWorldToClip;
uniform vec2 uViewportSize;
uniform vec3 uColor;
uniform vec3 uLightDirection;
uniform float uAmbient;
uniform float uShadeStrength;
uniform float uProfileExponent;

flat in int vSegment;

out vec4 fragColor;

vec3 gA; vec3 gB; vec3 gWA; vec3 gHA; vec3 gWB; vec3 gHB; vec3 gNA; vec3 gNB;
float gHwA; float gHwB; float gHhA; float gHhB; float gCap;
bool gCapA; bool gCapB;

float superellipse(float value) {
    return pow(abs(value), uProfileExponent);
}

float superellipseGradient(float value) {
    float magnitude = pow(abs(value), uProfileExponent - 1.0);
    return value < 0.0 ? -magnitude : magnitude;
}

vec4 segmentTexel(int segment, int offset) {
    return texelFetch(uSegments, ivec2(segment * 6 + offset, 0), 0);
}

vec3 worldPoint(vec2 ndc, float z) {
    vec4 world = uClipToWorld * vec4(ndc, z, 1.0);
    return world.xyz / world.w;
}

bool clipPositiveHalfSpace(
    float origin,
    float slope,
    inout float tEnter,
    inout float tExit,
    inout bool entryWasClipped)
{
    const float ParallelEpsilon = 1e-7;
    if (abs(slope) < ParallelEpsilon)
        return origin >= 0.0;

    float crossing = -origin / slope;
    if (slope > 0.0)
    {
        if (crossing > tEnter)
        {
            tEnter = crossing;
            entryWasClipped = true;
        }
    }
    else
    {
        tExit = min(tExit, crossing);
    }
    return tExit > tEnter;
}

float beadField(vec3 p)
{
    float dA = dot(p - gA, gNA);
    float dB = dot(gB - p, gNB);
    if (dA < 0.0)
    {
        vec3 q = p - gA;
        float x = dot(q, gWA) / gHwA;
        float y = dot(q, gHA) / gHhA;
        float radial = superellipse(x) + superellipse(y);
        if (!gCapA) return radial - 1.0;
        float z = dA / gCap;
        return radial + superellipse(z) - 1.0;
    }
    if (dB < 0.0)
    {
        vec3 q = p - gB;
        float x = dot(q, gWB) / gHwB;
        float y = dot(q, gHB) / gHhB;
        float radial = superellipse(x) + superellipse(y);
        if (!gCapB) return radial - 1.0;
        float z = dB / gCap;
        return radial + superellipse(z) - 1.0;
    }
    float s = dA / max(dA + dB, 1e-9);
    vec3 c = mix(gA, gB, s);
    vec3 w = normalize(mix(gWA, gWB, s));
    vec3 h = normalize(mix(gHA, gHB, s));
    float hw = mix(gHwA, gHwB, s);
    float hh = mix(gHhA, gHhB, s);
    vec3 q = p - c;
    float x = dot(q, w) / hw;
    float y = dot(q, h) / hh;
    return superellipse(x) + superellipse(y) - 1.0;
}

vec3 beadNormal(vec3 p)
{
    float dA = dot(p - gA, gNA);
    float dB = dot(gB - p, gNB);
    if (dA < 0.0)
    {
        vec3 q = p - gA;
        float x = dot(q, gWA) / gHwA;
        float y = dot(q, gHA) / gHhA;
        vec3 n = gWA * (superellipseGradient(x) / gHwA)
            + gHA * (superellipseGradient(y) / gHhA);
        if (gCapA)
        {
            float z = dA / gCap;
            n += gNA * (superellipseGradient(z) / gCap);
        }
        return normalize(n);
    }
    if (dB < 0.0)
    {
        vec3 q = p - gB;
        float x = dot(q, gWB) / gHwB;
        float y = dot(q, gHB) / gHhB;
        vec3 n = gWB * (superellipseGradient(x) / gHwB)
            + gHB * (superellipseGradient(y) / gHhB);
        if (gCapB)
        {
            float z = dB / gCap;
            n -= gNB * (superellipseGradient(z) / gCap);
        }
        return normalize(n);
    }

    float s = clamp(dA / max(dA + dB, 1e-9), 0.0, 1.0);
    vec3 c = mix(gA, gB, s);
    vec3 w = normalize(mix(gWA, gWB, s));
    vec3 h = mix(gHA, gHB, s);
    h = normalize(h - w * dot(h, w));
    float hw = mix(gHwA, gHwB, s);
    float hh = mix(gHhA, gHhB, s);
    vec3 q = p - c;
    float x = dot(q, w) / hw;
    float y = dot(q, h) / hh;
    return normalize(
        w * (superellipseGradient(x) / hw)
        + h * (superellipseGradient(y) / hh));
}

void main()
{
    vec4 t0 = segmentTexel(vSegment, 0);
    vec4 t1 = segmentTexel(vSegment, 1);
    vec4 t2 = segmentTexel(vSegment, 2);
    vec4 t3 = segmentTexel(vSegment, 3);
    vec4 t4 = segmentTexel(vSegment, 4);
    vec4 t5 = segmentTexel(vSegment, 5);

    gA = t0.xyz;  gHwA = max(t0.w, 1e-6);
    gB = t1.xyz;  gHwB = max(t1.w, 1e-6);
    gWA = normalize(t2.xyz);  gHhA = max(t2.w, 1e-6);
    gHA = normalize(t3.xyz);  gHhB = max(t3.w, 1e-6);
    gWB = normalize(t4.xyz);  gCap = max(t4.w, 1e-6);
    gHB = normalize(t5.xyz);
    float flags = t5.w;
    gCapA = mod(flags, 2.0) >= 1.0;
    gCapB = flags >= 2.0;
    gNA = normalize(cross(gHA, gWA));
    gNB = normalize(cross(gHB, gWB));

    // gl_FragCoord is exact per-pixel screen position -- reconstructing the
    // ray from it, instead of an interpolated NDC varying, is what stands in
    // for the native shader's noperspective qualifier (see comment above).
    vec2 ndc = (gl_FragCoord.xy / uViewportSize) * 2.0 - 1.0;
    vec3 ro = worldPoint(ndc, -1.0);
    vec3 rd = normalize(worldPoint(ndc, 1.0) - ro);

    vec3 mid = 0.5 * (gA + gB);
    float radius = 0.5 * length(gB - gA)
        + 1.25 * max(max(gHwA, gHwB), max(gHhA, gHhB)) + gCap;
    vec3 oc = ro - mid;
    float qb = dot(oc, rd);
    float qc = dot(oc, oc) - radius * radius;
    float disc = qb * qb - qc;
    if (disc < 0.0) discard;
    float root = sqrt(disc);
    float tEnter = max(-qb - root, 0.0);
    float tExit = -qb + root;
    if (tExit <= tEnter) discard;

    bool entryWasClipped = false;
    float jointOverlap = 0.20 * max(
        max(gHwA, gHwB),
        max(gHhA, gHhB));
    if (!gCapA && !clipPositiveHalfSpace(
            dot(ro - gA, gNA) + jointOverlap,
            dot(rd, gNA),
            tEnter,
            tExit,
            entryWasClipped))
        discard;
    if (!gCapB && !clipPositiveHalfSpace(
            dot(gB - ro, gNB) + jointOverlap,
            -dot(rd, gNB),
            tEnter,
            tExit,
            entryWasClipped))
        discard;

    const int Steps = 32;
    float dt = (tExit - tEnter) / float(Steps);
    float prevT = tEnter;
    float prevF = beadField(ro + rd * tEnter);
    float hitT = prevF < 0.0 && !entryWasClipped ? tEnter : -1.0;

    for (int i = 1; i <= Steps && hitT < 0.0; i++)
    {
        float t = tEnter + dt * float(i);
        float f = beadField(ro + rd * t);
        if (prevF > 0.0 && f <= 0.0)
        {
            float lo = prevT;
            float hi = t;
            for (int j = 0; j < 8; j++)
            {
                float m = 0.5 * (lo + hi);
                if (beadField(ro + rd * m) <= 0.0) hi = m;
                else lo = m;
            }
            hitT = 0.5 * (lo + hi);
            break;
        }
        prevT = t;
        prevF = f;
    }
    if (hitT < 0.0) discard;

    vec3 hit = ro + rd * hitT;
    vec3 normal = beadNormal(hit);
    if (dot(normal, normal) < 1e-18) normal = -rd;
    normal = normalize(normal);
    if (dot(normal, rd) > 0.0) normal = -normal;

    vec3 lightDirection = normalize(uLightDirection);
    float diffuse = max(dot(normal, lightDirection), 0.0);
    vec3 halfDirection = normalize(lightDirection - rd);
    float specular = pow(max(dot(normal, halfDirection), 0.0), 32.0);
    vec3 shaded = uColor * clamp(uAmbient + uShadeStrength * diffuse, 0.0, 1.0)
        + vec3(0.18 * specular);
    fragColor = vec4(shaded, 1.0);
    vec4 clip = uWorldToClip * vec4(hit, 1.0);
    gl_FragDepth = 0.5 * (clip.z / clip.w) + 0.5;
}`;

function writeGhostTexel(data, offset, v, w) {
  data[offset] = v.x; data[offset + 1] = v.y; data[offset + 2] = v.z; data[offset + 3] = w;
}

// Packs one segment's 6 texels, matching WASPer_PrintPathPreview.cs's exact
// layout: t0 A/halfWidthA, t1 B/halfWidthB, t2 WA/halfHeightA, t3 HA/halfHeightB,
// t4 WB/capRadius, t5 HB/capFlags. capA/capB (ellipsoid end caps) are true only
// at a true stroke start/end on an open branch -- internal joints and closed
// loops get none, matching "closed loops wrap without caps" in the native
// builder's own comment. Returns false if the branch has no frame data for
// either endpoint (segment silently dropped from Ghost mode in that case).
function packGhostSegment(data, offset, branch, segment) {
  const startFrame = pointFrame(branch, segment.pointIndex - 1);
  const endFrame = pointFrame(branch, segment.pointIndex);
  if (!startFrame || !endFrame) return false;

  const capA = segment.pointIndex - 1 === 0 && !branch.closed;
  const capB = segment.pointIndex === branch.positions.length - 1 && !branch.closed;
  const capRadius = Math.min(startFrame.halfWidth, startFrame.halfHeight, endFrame.halfWidth, endFrame.halfHeight);
  const capFlags = (capA ? 1 : 0) + (capB ? 2 : 0);

  writeGhostTexel(data, offset, startFrame.center, startFrame.halfWidth);
  writeGhostTexel(data, offset + 4, endFrame.center, endFrame.halfWidth);
  writeGhostTexel(data, offset + 8, startFrame.widthAxis, startFrame.halfHeight);
  writeGhostTexel(data, offset + 12, startFrame.intoMaterial, endFrame.halfHeight);
  writeGhostTexel(data, offset + 16, endFrame.widthAxis, capRadius);
  writeGhostTexel(data, offset + 20, endFrame.intoMaterial, capFlags);
  return true;
}

const ghostLightDirection = new THREE.Vector3(1, -1, 2).normalize(); // matches the scene's own key light

function createGhostBatchMesh(role, data, count) {
  const texture = new THREE.DataTexture(
    data.subarray(0, count * GHOST_FLOATS_PER_SEGMENT), count * 6, 1, THREE.RGBAFormat, THREE.FloatType);
  texture.magFilter = THREE.NearestFilter;
  texture.minFilter = THREE.NearestFilter;
  texture.wrapS = THREE.ClampToEdgeWrapping;
  texture.wrapT = THREE.ClampToEdgeWrapping;
  texture.needsUpdate = true;

  const material = new THREE.RawShaderMaterial({
    glslVersion: THREE.GLSL3,
    vertexShader: GHOST_VERTEX_SHADER,
    fragmentShader: GHOST_FRAGMENT_SHADER,
    side: THREE.DoubleSide, // native disables cull face -- the impostor box can be entered by the camera
    uniforms: {
      uSegments: { value: texture },
      uWorldToClip: { value: new THREE.Matrix4() },
      uClipToWorld: { value: new THREE.Matrix4() },
      uViewportSize: { value: new THREE.Vector2(1, 1) },
      uColor: { value: new THREE.Color(ROLE_COLOR[role] ?? ROLE_COLOR.undefined) },
      uLightDirection: { value: ghostLightDirection },
      uAmbient: { value: GHOST_AMBIENT },
      uShadeStrength: { value: GHOST_SHADE_STRENGTH },
      uProfileExponent: { value: PROFILE_EXPONENT },
    },
  });

  // No real attributes are read in the vertex shader (it indexes a
  // hardcoded cube[] by gl_VertexID, matching the native renderer's own
  // "empty VBO, only there to make the draw legal" approach) -- this
  // position attribute exists purely so three.js knows the geometry has 36
  // vertices per instance.
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.Float32BufferAttribute(new Float32Array(36 * 3), 3));

  const mesh = new THREE.InstancedMesh(geometry, material, count);
  // Per-instance transforms are irrelevant here (world position comes from
  // the segment texture, not instanceMatrix), so the bounds three.js would
  // compute from identity instance matrices are meaningless -- skip culling
  // instead of risking the whole batch vanishing at the wrong camera angle.
  mesh.frustumCulled = false;
  mesh.userData.isGhostBatch = true;
  return mesh;
}

function buildGhostMode(job) {
  const segmentsByRole = new Map();
  const travelPairs = [];

  for (const segment of job.segments) {
    if (segment.type !== "print") {
      travelPairs.push([toVec3(segment.from), toVec3(segment.to)]);
      continue;
    }
    if (!segmentsByRole.has(segment.role)) segmentsByRole.set(segment.role, []);
    segmentsByRole.get(segment.role).push(segment);
  }

  // Real hardware virtually always supports far more than 2048*6=12288
  // texels wide, but clamping to the GPU's actual limit (WebGL2 guarantees
  // only 2048 at minimum conformance) keeps this from silently failing on
  // very constrained hardware instead of just batching a bit smaller.
  const maxTexWidth = renderer.capabilities.maxTextureSize || 2048;
  const maxSegmentsPerBatch = Math.max(1, Math.min(GHOST_MAX_SEGMENTS_PER_BATCH, Math.floor(maxTexWidth / 6)));

  for (const [role, segments] of segmentsByRole) {
    for (let start = 0; start < segments.length; start += maxSegmentsPerBatch) {
      const chunk = segments.slice(start, start + maxSegmentsPerBatch);
      const data = new Float32Array(chunk.length * GHOST_FLOATS_PER_SEGMENT);
      let count = 0;
      for (const segment of chunk) {
        const branch = job.branchByIndex.get(segment.branchIndex);
        if (branch && packGhostSegment(data, count * GHOST_FLOATS_PER_SEGMENT, branch, segment)) count++;
      }
      if (count === 0) continue;
      processGroup.add(createGhostBatchMesh(role, data, count));
    }
  }

  addMergedTravelLines(travelPairs);
}

// uWorldToClip/uClipToWorld depend on the live (OrbitControls-driven) camera,
// so -- unlike everything else in the viewer, which is static once built --
// Ghost mode's shader uniforms need refreshing every rendered frame, not
// just once at rebuild time. Currently unused (see the comment above
// buildGhostMode -- Ghost mode is temporarily disabled and nothing ever sets
// isGhostBatch), kept ready for when a button calls buildGhostMode again.
function updateGhostUniforms() {
  if (processGroup.children.length === 0) return;
  camera.updateMatrixWorld();
  const worldToClip = camera.projectionMatrix.clone().multiply(camera.matrixWorldInverse);
  const clipToWorld = worldToClip.clone().invert();
  const width = renderer.domElement.width;
  const height = renderer.domElement.height;

  for (const child of processGroup.children) {
    if (!child.userData.isGhostBatch) continue;
    const u = child.material.uniforms;
    u.uWorldToClip.value.copy(worldToClip);
    u.uClipToWorld.value.copy(clipToWorld);
    u.uViewportSize.value.set(width, height);
  }
}

// ---- Mesh mode: print segments as swept superellipse prisms, travel as
// thin lines. Grounded directly in wsp_path's own per-point data (via
// PathBranch, already carried by WASPer.XR.Core since M1) rather than a
// generic computed frame: WasperPrintPath.PtPlanes stores an oriented plane
// per point, and the fixture confirms xAxes is the travel/tangent direction,
// yAxes is the in-layer width direction, zAxes is the layer-stacking height
// direction -- exactly the gW/gH axes WASPer_PrintPathSegmentRenderer.cs's
// GLSL shader takes as input for its own superellipse cross-section, not
// something the shader derives from travel direction either. Using yAxes/
// zAxes here (not a cross-product frame) is what removes the roll ambiguity
// the box-based version had. ----

function getBeadDimensions(job, segment) {
  const branch = job.branchByIndex.get(segment.branchIndex);
  const width = branch?.layerWidthNominal?.[segment.pointIndex];
  const height = branch?.layerHeight?.[segment.pointIndex];
  return {
    width: width && width > 0 ? width : job.defaultBead.nominalWidth || 1,
    height: height && height > 0 ? height : job.defaultBead.nominalHeight || 1,
  };
}

// Per WasperMotion/the .wasperxr "motion" schema, a print segment's
// pointIndex is the branch point it arrives AT; "from" is the previous
// branch point. Confirmed against the sample fixture: motion pointIndex 1
// runs branch.positions[0] -> branch.positions[1].
//
// Computed once per branch point at job-load time (precomputeBranchFrames),
// not per segment -- with 77k+ motions on a real study, re-normalizing two
// Vector3s and re-reading four arrays per segment (each interior point is
// shared by two segments) was pure repeated work.
//
// `center` fixes a real bug: wsp_path's raw per-point position is the
// nozzle/TOP of the bead, not its vertical center -- confirmed directly in
// the native GPU preview's own segment builder (WASPer_PrintPathPreview.cs:222,
// `frame.Center = points[i] + frame.HeightDirection * frame.HalfHeight`, where
// HeightDirection is `-plane.ZAxis`, i.e. down into the material -- see
// wsp_Pp18/wsp_Pp04's ResolveHeightDirections). Earlier bead rendering here
// centered the cross-section directly ON the raw point instead, which sits
// the bead half a layer height too high. `intoMaterial` (= -zAxis) is the
// same HeightDirection, kept alongside `heightAxis` (= +zAxis, "up") since
// Mesh mode's own ring sweep is sign-agnostic (a superellipse is symmetric
// under axis negation) but Ghost mode's GPU shader needs the exact native
// sign to match Pp18's cap/joint geometry precisely.
function computePointFrame(job, branch, index) {
  const yAxis = branch.yAxes?.[index];
  const zAxis = branch.zAxes?.[index];
  if (!yAxis || !zAxis) return null;
  const width = branch.layerWidthNominal?.[index];
  const height = branch.layerHeight?.[index];
  const halfWidth = (width && width > 0 ? width : job.defaultBead.nominalWidth || 1) / 2;
  const halfHeight = (height && height > 0 ? height : job.defaultBead.nominalHeight || 1) / 2;
  const widthAxis = toVec3(yAxis).normalize();
  const heightAxis = toVec3(zAxis).normalize();
  const intoMaterial = heightAxis.clone().negate();
  const center = toVec3(branch.positions[index]).addScaledVector(intoMaterial, halfHeight);
  return { center, widthAxis, heightAxis, intoMaterial, halfWidth, halfHeight };
}

function pointFrame(branch, index) {
  index = Math.max(0, Math.min(index, branch.positions.length - 1));
  return branch.frames ? branch.frames[index] : null;
}

function precomputeBranchFrames(job) {
  for (const branch of job.branches) {
    branch.frames = branch.positions.map((_, i) => computePointFrame(job, branch, i));
  }
}

// Parametric form of the shader's implicit superellipse boundary
// (|x/a|^n + |y/b|^n = 1), using the standard signed-power parametrization
// so points stay evenly spread around the curve at any exponent. n=2 is a
// plain ellipse; larger n rounds toward a rectangle, matching
// WasperPrintPathSegmentRenderer.SetProfileExponent's 2/3/4/6 steps.
//
// Writes straight into a shared, preallocated Float32Array rather than
// building an array of Vector3 objects -- at 77k+ segments x 2 rings x 16
// points, the Vector3-per-point version was allocating millions of small
// objects just to immediately flatten them again in buildTubeGeometry.
function fillSuperellipseRing(out, offset, center, widthAxis, heightAxis, halfWidth, halfHeight, exponent, segments) {
  const twoOverN = 2 / exponent;
  for (let i = 0; i < segments; i++) {
    const theta = (i / segments) * Math.PI * 2;
    const c = Math.cos(theta);
    const s = Math.sin(theta);
    const px = Math.sign(c) * Math.pow(Math.abs(c), twoOverN) * halfWidth;
    const py = Math.sign(s) * Math.pow(Math.abs(s), twoOverN) * halfHeight;
    const idx = offset + i * 3;
    out[idx] = center.x + widthAxis.x * px + heightAxis.x * py;
    out[idx + 1] = center.y + widthAxis.y * px + heightAxis.y * py;
    out[idx + 2] = center.z + widthAxis.z * px + heightAxis.z * py;
  }
}

// Eight profile stations are enough for an interactive phone preview and halve the bead vertex
// load. Desktop retains the smoother 16-station section used for close inspection.
const BEAD_RING_SEGMENTS = IS_MOBILE_DEVICE ? 8 : 16;

// Index pattern (side quads + both cap fans) is identical for every tube --
// only the vertex positions differ per segment -- so it is built exactly
// once and the same typed array is reused (read-only) as every tube
// geometry's index buffer, instead of rebuilding an equivalent array 77k+
// times.
function buildTubeIndexTemplate(n) {
  const capAIndex = 2 * n;
  const capBIndex = 2 * n + 1;
  const indices = [];
  for (let i = 0; i < n; i++) {
    const a0 = i, a1 = (i + 1) % n;
    const b0 = n + i, b1 = n + ((i + 1) % n);
    indices.push(a0, b0, b1, a0, b1, a1);   // side quad
    indices.push(capAIndex, a1, a0);        // start cap fan
    indices.push(capBIndex, b0, b1);        // end cap fan
  }
  return new Uint32Array(indices);
}
const TUBE_INDICES = buildTubeIndexTemplate(BEAD_RING_SEGMENTS);

// Connects two same-size rings into a tube with end caps. Each individual
// PathSegment gets its own closed capsule (caps at both ends) rather than
// stitching a whole branch into one seamless tube the way the shader's
// shared-boundary blending does -- a known simplification: adjacent print
// segments on the same branch will show a faint double wall at their joint
// instead of a perfectly smooth transition.
//
// Returns a bare position-only BufferGeometry (no normals, no material, not
// wrapped in a Mesh) -- the caller batches many of these together via
// BufferGeometryUtils.mergeGeometries and computes normals ONCE on the
// merged result, since normals here never depend on cross-tube vertex
// sharing (each tube is a closed, disjoint capsule) so computing them before
// or after merging gives an identical answer at a fraction of the overhead.
function buildTubeGeometry(fromPos, toPos, startFrame, endFrame, exponent) {
  const n = BEAD_RING_SEGMENTS;
  const positions = new Float32Array((n * 2 + 2) * 3);
  fillSuperellipseRing(positions, 0, fromPos, startFrame.widthAxis, startFrame.heightAxis,
    startFrame.halfWidth, startFrame.halfHeight, exponent, n);
  fillSuperellipseRing(positions, n * 3, toPos, endFrame.widthAxis, endFrame.heightAxis,
    endFrame.halfWidth, endFrame.halfHeight, exponent, n);

  let cax = 0, cay = 0, caz = 0, cbx = 0, cby = 0, cbz = 0;
  for (let i = 0; i < n; i++) {
    cax += positions[i * 3]; cay += positions[i * 3 + 1]; caz += positions[i * 3 + 2];
    const j = (n + i) * 3;
    cbx += positions[j]; cby += positions[j + 1]; cbz += positions[j + 2];
  }
  const capAOffset = n * 2 * 3;
  positions[capAOffset] = cax / n; positions[capAOffset + 1] = cay / n; positions[capAOffset + 2] = caz / n;
  positions[capAOffset + 3] = cbx / n; positions[capAOffset + 4] = cby / n; positions[capAOffset + 5] = cbz / n;

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
  geometry.setIndex(new THREE.BufferAttribute(TUBE_INDICES, 1));
  return geometry;
}

// Returns a bare geometry (see buildTubeGeometry) or null if the branch/
// frame data is missing, in which case the caller falls back to
// buildBeadMeshFallback for that one segment instead of batching it. Uses
// each endpoint's precomputed `center` (see computePointFrame), not the raw
// segment.from/to -- those are the nozzle/top position, not the bead's
// vertical center.
function buildSuperellipseBeadGeometry(branch, segment, exponent) {
  if (!branch) return null;
  const startFrame = pointFrame(branch, segment.pointIndex - 1);
  const endFrame = pointFrame(branch, segment.pointIndex);
  if (!startFrame || !endFrame) return null;

  return buildTubeGeometry(startFrame.center, endFrame.center, startFrame, endFrame, exponent);
}

// Fallback for the rare case a branch is missing or has no per-point axes
// (shouldn't happen for anything Gc07 exports -- WasperPrintPath always
// canonicalizes to at least WorldXY-derived planes -- but a malformed or
// hand-built job shouldn't hard-crash the viewer). Same computed-frame box
// approach M3 shipped with initially, roll ambiguity and all. Expected to be
// rare-to-never, so unlike the main path it is added to the scene as its
// own individual Mesh rather than batched.
function buildBeadMeshFallback(job, segment, material) {
  const from = toVec3(segment.from);
  const to = toVec3(segment.to);
  const dir = new THREE.Vector3().subVectors(to, from);
  const length = dir.length();
  if (length < 1e-6) return null;
  dir.normalize();

  const worldUp = new THREE.Vector3(0, 0, 1);
  let right = new THREE.Vector3().crossVectors(dir, worldUp);
  if (right.lengthSq() < 1e-8) right = new THREE.Vector3().crossVectors(dir, new THREE.Vector3(1, 0, 0));
  right.normalize();
  const up = new THREE.Vector3().crossVectors(right, dir).normalize();

  const { width, height } = getBeadDimensions(job, segment);
  const basis = new THREE.Matrix4().makeBasis(dir, right, up);
  const geometry = new THREE.BoxGeometry(Math.max(length, 0.01), Math.max(width, 0.01), Math.max(height, 0.01));
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.addVectors(from, to).multiplyScalar(0.5);
  mesh.quaternion.setFromRotationMatrix(basis);
  return mesh;
}

// ---- Shared, persistent materials -- created once, reused across every
// mode switch, not recreated per segment or per rebuild. Only disposed (and
// the caches cleared) when a genuinely new job is loaded, since
// sceneScale-dependent values like dash size are baked in at creation time. ----

const roleBeadMaterials = new Map();
let travelMaterial = null;

function getBeadMaterial(role) {
  let material = roleBeadMaterials.get(role);
  if (!material) {
    const color = ROLE_COLOR[role] ?? ROLE_COLOR.undefined;
    material = new THREE.MeshStandardMaterial({ color, roughness: 0.7, side: THREE.DoubleSide });
    roleBeadMaterials.set(role, material);
  }
  return material;
}

function getTravelMaterial() {
  if (!travelMaterial) {
    travelMaterial = new THREE.LineDashedMaterial({
      color: TRAVEL_COLOR, dashSize: sceneScale * 0.03, gapSize: sceneScale * 0.02,
    });
  }
  return travelMaterial;
}

function resetMaterialCaches() {
  for (const material of roleBeadMaterials.values()) material.dispose();
  roleBeadMaterials.clear();
  travelMaterial?.dispose();
  travelMaterial = null;
}

// ---- Batched mesh/line builders -- collect raw geometries per material
// bucket while walking segments, then merge each bucket into a single
// draw call at the end. This is the difference between ~77k individual
// THREE.Mesh/THREE.Line objects (one per PathSegment, the original M3
// shape) and a small constant number of draw calls (one per role, plus
// one for travel) regardless of segment count. ----

function addMergedBeadMeshes(geometriesByRole) {
  for (const [role, geometries] of geometriesByRole) {
    if (geometries.length === 0) continue;
    const merged = geometries.length === 1
      ? geometries[0]
      : BufferGeometryUtils.mergeGeometries(geometries, false);
    if (geometries.length > 1) {
      for (const g of geometries) g.dispose();
    }
    merged.computeVertexNormals();
    processGroup.add(new THREE.Mesh(merged, getBeadMaterial(role)));
  }
}

function pairsToPositions(pairs) {
  const positions = new Float32Array(pairs.length * 6);
  for (let i = 0; i < pairs.length; i++) {
    const [from, to] = pairs[i];
    const o = i * 6;
    positions[o] = from.x; positions[o + 1] = from.y; positions[o + 2] = from.z;
    positions[o + 3] = to.x; positions[o + 4] = to.y; positions[o + 5] = to.z;
  }
  return positions;
}

function addMergedTravelLines(pairs) {
  if (pairs.length === 0) return;
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(pairsToPositions(pairs), 3));
  const lines = new THREE.LineSegments(geometry, getTravelMaterial());
  lines.computeLineDistances();
  processGroup.add(lines);
}

// Renders only what's been printed by `time` -- a segment counts as printed
// once it has STARTED (segment.startTimeSeconds <= time), matching the
// "isPending" convention the earlier Hybrid mode used. Both print and travel
// segments are filtered by time (a print-process replay includes the travel
// moves that have actually happened by then, not just deposited material);
// role visibility only applies to print segments, since travel isn't
// role-specific.
// Fast Mesh: one low-poly unit bead is reused for every print segment through InstancedMesh.
// The old implementation expanded every segment into a separate capped tube, merged all tubes,
// recomputed normals, and repeated that entire process on every playback frame. For large jobs
// that meant tens of megabytes of temporary geometry and substantial garbage collection. Here
// transforms are generated once per job; playback only changes InstancedMesh.count.
const FAST_BEAD_RADIAL_SEGMENTS = IS_MOBILE_DEVICE ? 6 : 8;
let instancedMeshCache = null;
let continuousMeshCache = null;

function disposeInstancedMeshCache() {
  if (!instancedMeshCache) return;
  for (const entry of instancedMeshCache.roles.values()) {
    processGroup.remove(entry.mesh);
  }
  if (instancedMeshCache.travel) {
    processGroup.remove(instancedMeshCache.travel.lines);
    instancedMeshCache.travel.lines.geometry.dispose();
  }
  instancedMeshCache.geometry.dispose();
  instancedMeshCache = null;
}

function disposeContinuousMeshCache() {
  if (!continuousMeshCache) return;
  for (const entry of continuousMeshCache.roles.values()) {
    processGroup.remove(entry.mesh);
    entry.mesh.geometry.dispose();
  }
  if (continuousMeshCache.travel) {
    processGroup.remove(continuousMeshCache.travel.lines);
    continuousMeshCache.travel.lines.geometry.dispose();
  }
  continuousMeshCache = null;
}

function disposeMeshCaches() {
  disposeInstancedMeshCache();
  disposeContinuousMeshCache();
}

function upperBound(sortedValues, value) {
  let low = 0;
  let high = sortedValues.length;
  while (low < high) {
    const middle = (low + high) >>> 1;
    if (sortedValues[middle] <= value) low = middle + 1;
    else high = middle;
  }
  return low;
}

function segmentPointKey(segment, pointIndex, branch) {
  const canonicalIndex = branch?.closed && pointIndex === branch.positions.length - 1
    ? 0
    : pointIndex;
  return `${segment.branchIndex}:${canonicalIndex}`;
}

function continuousSegmentKey(segment) {
  return `${segment.branchIndex}:${segment.pointIndex}`;
}

// Builds one indexed sweep per role. Adjacent print segments reuse the same ring vertices,
// eliminating the cap and duplicated wall that Fast Mesh intentionally creates at every motion.
// Indices remain ordered by motion time, so playback changes only BufferGeometry.drawRange.
function buildContinuousRoleEntry(job, role, sourceSegments) {
  const n = IS_MOBILE_DEVICE ? 6 : 8;
  const segments = sourceSegments
    .filter(segment => {
      const branch = job.branchByIndex.get(segment.branchIndex);
      return pointFrame(branch, segment.pointIndex - 1) && pointFrame(branch, segment.pointIndex);
    })
    .sort((a, b) => a.startTimeSeconds - b.startTimeSeconds);
  if (segments.length === 0) return null;

  const segmentKeys = new Set(segments.map(continuousSegmentKey));
  const ringFrames = new Map();
  let capCount = 0;
  for (const segment of segments) {
    const branch = job.branchByIndex.get(segment.branchIndex);
    const startKey = segmentPointKey(segment, segment.pointIndex - 1, branch);
    const endKey = segmentPointKey(segment, segment.pointIndex, branch);
    if (!ringFrames.has(startKey)) ringFrames.set(startKey, pointFrame(branch, segment.pointIndex - 1));
    if (!ringFrames.has(endKey)) ringFrames.set(endKey, pointFrame(branch, segment.pointIndex));
    if (!branch.closed && !segmentKeys.has(`${segment.branchIndex}:${segment.pointIndex - 1}`)) capCount++;
    if (!branch.closed && !segmentKeys.has(`${segment.branchIndex}:${segment.pointIndex + 1}`)) capCount++;
  }

  const ringVertexCount = ringFrames.size * n;
  const vertexCount = ringVertexCount + capCount * (n + 1);
  const indexCount = segments.length * n * 6 + capCount * n * 3;
  const positions = new Float32Array(vertexCount * 3);
  const indices = new Uint32Array(indexCount);
  const times = new Float64Array(segments.length);
  const indexEnds = new Uint32Array(segments.length);
  const ringBases = new Map();

  let vertexOffset = 0;
  for (const [key, frame] of ringFrames) {
    const base = vertexOffset;
    ringBases.set(key, base);
    fillSuperellipseRing(
      positions, base * 3, frame.center, frame.widthAxis, frame.heightAxis,
      frame.halfWidth, frame.halfHeight, PROFILE_EXPONENT, n);
    vertexOffset += n;
  }

  let indexOffset = 0;
  function appendCap(sourceBase, center, reverse) {
    const capBase = vertexOffset;
    positions.set(positions.subarray(sourceBase * 3, (sourceBase + n) * 3), capBase * 3);
    const centerIndex = capBase + n;
    positions[centerIndex * 3] = center.x;
    positions[centerIndex * 3 + 1] = center.y;
    positions[centerIndex * 3 + 2] = center.z;
    for (let i = 0; i < n; i++) {
      const current = capBase + i;
      const next = capBase + ((i + 1) % n);
      indices[indexOffset++] = centerIndex;
      indices[indexOffset++] = reverse ? next : current;
      indices[indexOffset++] = reverse ? current : next;
    }
    vertexOffset += n + 1;
  }

  for (let segmentIndex = 0; segmentIndex < segments.length; segmentIndex++) {
    const segment = segments[segmentIndex];
    const branch = job.branchByIndex.get(segment.branchIndex);
    const startFrame = pointFrame(branch, segment.pointIndex - 1);
    const endFrame = pointFrame(branch, segment.pointIndex);
    const startBase = ringBases.get(segmentPointKey(segment, segment.pointIndex - 1, branch));
    const endBase = ringBases.get(segmentPointKey(segment, segment.pointIndex, branch));

    for (let i = 0; i < n; i++) {
      const a0 = startBase + i;
      const a1 = startBase + ((i + 1) % n);
      const b0 = endBase + i;
      const b1 = endBase + ((i + 1) % n);
      indices[indexOffset++] = a0;
      indices[indexOffset++] = b0;
      indices[indexOffset++] = b1;
      indices[indexOffset++] = a0;
      indices[indexOffset++] = b1;
      indices[indexOffset++] = a1;
    }

    if (!branch.closed && !segmentKeys.has(`${segment.branchIndex}:${segment.pointIndex - 1}`)) {
      appendCap(startBase, startFrame.center, true);
    }
    if (!branch.closed && !segmentKeys.has(`${segment.branchIndex}:${segment.pointIndex + 1}`)) {
      appendCap(endBase, endFrame.center, false);
    }
    times[segmentIndex] = segment.startTimeSeconds;
    indexEnds[segmentIndex] = indexOffset;
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
  geometry.setIndex(new THREE.BufferAttribute(indices, 1));
  geometry.computeVertexNormals();
  geometry.computeBoundingBox();
  geometry.computeBoundingSphere();
  geometry.setDrawRange(0, 0);
  const mesh = new THREE.Mesh(geometry, getBeadMaterial(role));
  mesh.name = `WASPer continuous beads - ${role}`;
  mesh.frustumCulled = true;
  mesh.userData.cachedProcessObject = true;
  return { mesh, times, indexEnds };
}

function buildTravelCache(segments) {
  const travelSegments = segments
    .filter(segment => segment.type !== "print")
    .sort((a, b) => a.startTimeSeconds - b.startTimeSeconds);
  if (travelSegments.length === 0) return null;
  const positions = new Float32Array(travelSegments.length * 6);
  const times = new Float64Array(travelSegments.length);
  for (let i = 0; i < travelSegments.length; i++) {
    const segment = travelSegments[i];
    const offset = i * 6;
    positions[offset] = segment.from.x;
    positions[offset + 1] = segment.from.y;
    positions[offset + 2] = segment.from.z;
    positions[offset + 3] = segment.to.x;
    positions[offset + 4] = segment.to.y;
    positions[offset + 5] = segment.to.z;
    times[i] = segment.startTimeSeconds;
  }
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
  geometry.setDrawRange(0, 0);
  const lines = new THREE.LineSegments(geometry, getTravelMaterial());
  lines.computeLineDistances();
  lines.userData.cachedProcessObject = true;
  return { lines, times };
}

function ensureContinuousMeshCache(job) {
  if (continuousMeshCache?.job === job) return continuousMeshCache;
  disposeContinuousMeshCache();
  const segmentsByRole = new Map();
  for (const segment of job.segments) {
    if (segment.type !== "print") continue;
    if (!segmentsByRole.has(segment.role)) segmentsByRole.set(segment.role, []);
    segmentsByRole.get(segment.role).push(segment);
  }
  const roles = new Map();
  for (const [role, segments] of segmentsByRole) {
    const entry = buildContinuousRoleEntry(job, role, segments);
    if (entry) roles.set(role, entry);
  }
  continuousMeshCache = { job, roles, travel: buildTravelCache(job.segments) };
  return continuousMeshCache;
}

function updateContinuousMeshPlayback(time) {
  if (!continuousMeshCache) return;
  for (const entry of continuousMeshCache.roles.values()) {
    const printed = upperBound(entry.times, time);
    entry.mesh.geometry.setDrawRange(0, printed > 0 ? entry.indexEnds[printed - 1] : 0);
  }
  if (continuousMeshCache.travel) {
    const count = upperBound(continuousMeshCache.travel.times, time);
    continuousMeshCache.travel.lines.geometry.setDrawRange(0, count * 2);
  }
}

const beadMatrixScratch = {
  a: new THREE.Vector3(), b: new THREE.Vector3(), center: new THREE.Vector3(),
  length: new THREE.Vector3(), width: new THREE.Vector3(), height: new THREE.Vector3(),
  basisX: new THREE.Vector3(), basisY: new THREE.Vector3(), basisZ: new THREE.Vector3(),
  matrix: new THREE.Matrix4(),
};

function composeFastBeadMatrix(job, segment, target) {
  const branch = job.branchByIndex.get(segment.branchIndex);
  const startFrame = pointFrame(branch, segment.pointIndex - 1);
  const endFrame = pointFrame(branch, segment.pointIndex);
  const s = beadMatrixScratch;
  let halfWidth;
  let halfHeight;

  if (startFrame && endFrame) {
    s.a.copy(startFrame.center);
    s.b.copy(endFrame.center);
    s.width.copy(startFrame.widthAxis).add(endFrame.widthAxis);
    halfWidth = (startFrame.halfWidth + endFrame.halfWidth) * 0.5;
    halfHeight = (startFrame.halfHeight + endFrame.halfHeight) * 0.5;
  } else {
    s.a.copy(toVec3(segment.from));
    s.b.copy(toVec3(segment.to));
    const dimensions = getBeadDimensions(job, segment);
    halfWidth = dimensions.width * 0.5;
    halfHeight = dimensions.height * 0.5;
    s.width.set(0, 0, 1);
  }

  s.length.subVectors(s.b, s.a);
  const length = s.length.length();
  if (length < 1e-6) return false;
  s.length.multiplyScalar(1 / length);

  // Keep the width axis perpendicular to travel. Degenerate/missing frames get a stable fallback.
  s.width.addScaledVector(s.length, -s.width.dot(s.length));
  if (s.width.lengthSq() < 1e-10) {
    s.width.set(0, 0, 1).cross(s.length);
    if (s.width.lengthSq() < 1e-10) s.width.set(1, 0, 0).cross(s.length);
  }
  s.width.normalize();
  s.height.crossVectors(s.width, s.length).normalize();
  s.center.addVectors(s.a, s.b).multiplyScalar(0.5);

  s.basisX.copy(s.width).multiplyScalar(Math.max(halfWidth, 0.005));
  s.basisY.copy(s.length).multiplyScalar(Math.max(length, 0.01));
  s.basisZ.copy(s.height).multiplyScalar(Math.max(halfHeight, 0.005));
  target.makeBasis(s.basisX, s.basisY, s.basisZ);
  target.setPosition(s.center);
  return true;
}

function ensureInstancedMeshCache(job) {
  if (instancedMeshCache?.job === job) return instancedMeshCache;
  disposeInstancedMeshCache();

  const counts = new Map();
  let travelCount = 0;
  for (const segment of job.segments) {
    if (segment.type === "print") counts.set(segment.role, (counts.get(segment.role) || 0) + 1);
    else travelCount++;
  }

  const geometry = new THREE.CylinderGeometry(
    1, 1, 1, FAST_BEAD_RADIAL_SEGMENTS, 1, false);
  const roles = new Map();
  for (const [role, count] of counts) {
    const mesh = new THREE.InstancedMesh(geometry, getBeadMaterial(role), count);
    mesh.name = `WASPer fast beads - ${role}`;
    mesh.count = 0;
    mesh.frustumCulled = false;
    mesh.instanceMatrix.setUsage(THREE.StaticDrawUsage);
    mesh.userData.cachedProcessObject = true;
    roles.set(role, { mesh, times: new Float64Array(count), next: 0 });
  }

  const travelPositions = new Float32Array(travelCount * 6);
  const travelTimes = new Float64Array(travelCount);
  let travelIndex = 0;
  for (const segment of job.segments) {
    if (segment.type === "print") {
      const entry = roles.get(segment.role);
      if (!composeFastBeadMatrix(job, segment, beadMatrixScratch.matrix)) continue;
      entry.mesh.setMatrixAt(entry.next, beadMatrixScratch.matrix);
      entry.times[entry.next] = segment.startTimeSeconds;
      entry.next++;
      continue;
    }

    const offset = travelIndex * 6;
    travelPositions[offset] = segment.from.x;
    travelPositions[offset + 1] = segment.from.y;
    travelPositions[offset + 2] = segment.from.z;
    travelPositions[offset + 3] = segment.to.x;
    travelPositions[offset + 4] = segment.to.y;
    travelPositions[offset + 5] = segment.to.z;
    travelTimes[travelIndex] = segment.startTimeSeconds;
    travelIndex++;
  }

  for (const entry of roles.values()) {
    entry.mesh.instanceMatrix.needsUpdate = true;
    entry.mesh.count = entry.next;
    entry.mesh.computeBoundingBox();
    entry.mesh.computeBoundingSphere();
    entry.mesh.count = 0;
    // A malformed zero-length segment may have been skipped; expose only initialized instances.
    if (entry.next < entry.times.length) entry.times = entry.times.slice(0, entry.next);
  }

  let travel = null;
  if (travelIndex > 0) {
    const travelGeometry = new THREE.BufferGeometry();
    travelGeometry.setAttribute("position", new THREE.BufferAttribute(
      travelIndex === travelCount ? travelPositions : travelPositions.slice(0, travelIndex * 6), 3));
    travelGeometry.setDrawRange(0, 0);
    const lines = new THREE.LineSegments(travelGeometry, getTravelMaterial());
    lines.computeLineDistances();
    lines.userData.cachedProcessObject = true;
    travel = {
      lines,
      times: travelIndex === travelCount ? travelTimes : travelTimes.slice(0, travelIndex),
    };
  }

  instancedMeshCache = { job, geometry, roles, travel };
  return instancedMeshCache;
}

function updateInstancedMeshPlayback(time) {
  if (!instancedMeshCache) return;
  for (const entry of instancedMeshCache.roles.values()) {
    entry.mesh.count = upperBound(entry.times, time);
  }
  if (instancedMeshCache.travel) {
    const count = upperBound(instancedMeshCache.travel.times, time);
    instancedMeshCache.travel.lines.geometry.setDrawRange(0, count * 2);
  }
}

function updateMeshPlayback(time) {
  if (meshRenderMode === "continuous") updateContinuousMeshPlayback(time);
  else updateInstancedMeshPlayback(time);
}

function buildMeshMode(job, time) {
  const cache = meshRenderMode === "continuous"
    ? ensureContinuousMeshCache(job)
    : ensureInstancedMeshCache(job);
  updateMeshPlayback(time);
  for (const [role, entry] of cache.roles) {
    if (roleVisibility.get(role) !== false) processGroup.add(entry.mesh);
  }
  if (cache.travel) processGroup.add(cache.travel.lines);
}

function disposeProcessGroup() {
  // Geometry is rebuilt fresh every call and disposed here. Mesh mode's
  // materials are shared/cached (getBeadMaterial/getTravelMaterial) and
  // reused across every mode switch within the same job, so they are
  // deliberately NOT disposed here -- only resetMaterialCaches (on loading a
  // genuinely new job) tears those down. Ghost mode's materials and their
  // segment-data textures are the opposite: unique to this specific rebuild
  // (each batch's texture is real per-job/per-frame data), so those ARE
  // disposed every time, right here.
  //
  // Context and helpers are sibling groups with their own lifecycles, so this
  // loop owns only playback geometry. Iterate a copy because removing children
  // while traversing Three.js's live children array would skip entries.
  for (const child of processGroup.children.slice()) {
    if (child.userData.cachedProcessObject) {
      processGroup.remove(child);
      continue;
    }
    child.geometry?.dispose();
    if (child.userData.isGhostBatch) {
      child.material.uniforms.uSegments.value?.dispose();
      child.material.dispose();
    } else if (child.userData.ownsMaterial) {
      if (Array.isArray(child.material)) {
        for (const material of child.material) material.dispose();
      } else {
        child.material.dispose();
      }
    }
    processGroup.remove(child);
  }
  pathPlaybackEntries = [];
}

function statusText() {
  const parts = [];
  if (showPath) parts.push("Path: printed solid, pending translucent.");
  if (showMesh) parts.push(`Mesh: ${meshRenderMode} bead up to the time slider.`);
  return parts.length ? parts.join(" ") : "Nothing shown -- enable Path or Mesh above.";
}

function rebuildContent() {
  if (!currentJob) return;
  disposeProcessGroup();

  if (showPath) buildPathMode(currentJob, currentTime);
  if (showMesh) buildMeshMode(currentJob, currentTime);

  document.getElementById("status").textContent = statusText();
}

const modeButtons = {
  path: document.getElementById("modePath"),
  mesh: document.getElementById("modeMesh"),
  context: document.getElementById("modeContext"),
  bbox: document.getElementById("modeBBox"),
};
let renderModeChosenByUser = false;
modeButtons.path.classList.add("active");
modeButtons.context.classList.add("active");
modeButtons.bbox.classList.add("active");

function updateTimelineVisibility() {
  document.getElementById("timeline").classList.toggle(
    "visible", (showPath || showMesh) && !playbackDisabledByJob);
}

modeButtons.path.addEventListener("click", () => {
  renderModeChosenByUser = true;
  showPath = !showPath;
  modeButtons.path.classList.toggle("active", showPath);
  updateTimelineVisibility();
  rebuildContent();
  refreshVisibleBounds();
});
modeButtons.mesh.addEventListener("click", () => {
  renderModeChosenByUser = true;
  showMesh = !showMesh;
  modeButtons.mesh.classList.toggle("active", showMesh);
  updateTimelineVisibility();
  rebuildContent();
  refreshVisibleBounds();
});
meshQualitySelect.addEventListener("change", () => {
  meshQualityChosenByUser = true;
  meshRenderMode = meshQualitySelect.value === "fast" ? "fast" : "continuous";
  meshQualitySelect.title = meshRenderMode === "continuous"
    ? "Connected bead sweep; smoother but heavier for large jobs"
    : "Instanced bead preview; fastest for large and live jobs";
  disposeMeshCaches();
  if (showMesh) rebuildContent();
  else document.getElementById("status").textContent = statusText();
});
modeButtons.context.addEventListener("click", () => {
  showContext = !showContext;
  modeButtons.context.classList.toggle("active", showContext);
  contextGroup.visible = showContext;
  refreshVisibleBounds();
});
// Doesn't go through rebuildContent() like Path/Mesh above -- bboxHelper isn't torn down and
// rebuilt on toggle (see disposeProcessGroup's comment), so flipping its own .visible directly
// is all that's needed here.
modeButtons.bbox.addEventListener("click", () => {
  showBoundingBox = !showBoundingBox;
  modeButtons.bbox.classList.toggle("active", showBoundingBox);
  if (bboxHelper) bboxHelper.visible = showBoundingBox;
});

// Simple UI as the phone default (added 2026-08-19), rather than requiring a manual tap every
// time: matches the same 640px breakpoint the rest of the mobile layout already uses (see the
// @media query above) via matchMedia instead of duplicating a width number, so this and the CSS
// breakpoint can never quietly drift apart. Full UI/desktop is untouched, and the toggle still
// works normally afterward either way -- this only decides the starting interface state. The
// rendering mode itself stays on the lightweight Path preview until the user explicitly enables
// the bead Mesh.
if (window.matchMedia("(max-width: 640px)").matches) {
  document.body.classList.add("simple-ui");
  toggleUiButton.textContent = "Full UI";
}

function applyMobileRenderPolicy(job) {
  if (!IS_MOBILE_DEVICE || renderModeChosenByUser) return;
  showPath = true;
  showMesh = false;
  modeButtons.path.classList.toggle("active", showPath);
  modeButtons.mesh.classList.toggle("active", showMesh);
}

function applyAdaptiveMeshPolicy(job) {
  if (meshQualityChosenByUser) return;
  let printSegmentCount = 0;
  for (const segment of job.segments) {
    if (segment.type === "print") printSegmentCount++;
  }
  meshRenderMode = IS_MOBILE_DEVICE || printSegmentCount > CONTINUOUS_MESH_AUTO_LIMIT
    ? "fast"
    : "continuous";
  meshQualitySelect.value = meshRenderMode;
  meshQualitySelect.title = meshRenderMode === "fast"
    ? `Fast selected automatically for ${printSegmentCount.toLocaleString()} print segments`
    : `Continuous selected automatically for ${printSegmentCount.toLocaleString()} print segments`;
}

function buildLegend(job) {
  const rolesUsed = [...new Set(job.branches.map(b => b.role))].sort();
  roleVisibility.clear();
  for (const role of rolesUsed) roleVisibility.set(role, true);

  const legend = document.getElementById("legend");
  legend.innerHTML = "";
  for (const role of rolesUsed) {
    const row = document.createElement("div");
    row.className = "row";
    row.dataset.role = role;
    const color = "#" + (ROLE_COLOR[role] ?? ROLE_COLOR.undefined).toString(16).padStart(6, "0");
    row.innerHTML = `<span><span class="eyeToggle">\u{1F441}</span><span class="swatch" style="background:${color}"></span>${ROLE_NAME[role] ?? role}</span><span></span>`;
    legend.appendChild(row);
  }
}

// One delegated listener on the legend container handles every role row --
// simpler than attaching/detaching a listener per row on every buildLegend
// call (which runs once per job load).
document.getElementById("legend").addEventListener("click", (event) => {
  const row = event.target.closest(".row[data-role]");
  if (!row) return;
  const role = row.dataset.role;
  const visible = !(roleVisibility.get(role) === false);
  roleVisibility.set(role, !visible);
  row.classList.toggle("hidden", visible);
  rebuildContent();
  refreshVisibleBounds();
});

// Renders whatever KPI list came through on the job -- grouped by kpi.group,
// in the order groups/items first appear -- rather than hardcoding specific
// fields, so any KPI Gc07/WasperPathKpiExtractor produces today or in the
// future shows up here without an index.html change. Collapsed by default
// (there can be a dozen-plus entries once Infill/Support groups exist
// alongside Fabrication) so it doesn't push the load-file box off screen.
// Groups kpis and renders them into `container` -- shared by buildKpiPanel below for both the
// inline #kpiBody (Full UI/desktop) and the full-screen #kpiFullBody (Simple UI's #openKpisButton
// -- see its CSS comment for why phones get a separate full-screen view instead), so the two never
// drift out of sync with each other.
function buildKpiRows(container, kpis) {
  container.innerHTML = "";
  const groups = new Map();
  for (const kpi of kpis) {
    const groupName = kpi.group || "Other";
    if (!groups.has(groupName)) groups.set(groupName, []);
    groups.get(groupName).push(kpi);
  }

  for (const [groupName, items] of groups) {
    const heading = document.createElement("div");
    heading.className = "kpiGroup";
    heading.textContent = groupName;
    container.appendChild(heading);

    for (const kpi of items) {
      const row = document.createElement("div");
      row.className = "row";
      const display = kpi.value != null
        ? formatKpiValue(kpi.value) + (kpi.unit ? ` ${kpi.unit}` : "")
        : (kpi.textValue || "—");
      row.innerHTML = `<span>${kpi.label || kpi.key}</span><span>${display}</span>`;
      container.appendChild(row);
    }
  }
}

function buildKpiPanel(job) {
  // Gross dims (added 2026-08-19) is synthetic -- not one of Gc07's real KPIs -- but unshifted in
  // as its own "General" group ahead of everything else so it reads as the first KPI regardless
  // of what real groups (Thermal, etc.) the job actually has. Reuses computeBounds/
  // formatBoundingBoxDims (the same numbers already shown in the #hud "Bounding box" stat row and
  // the BBox toggle's dashed wireframe) rather than computing anything separately.
  const grossDimsKpi = {
    group: "General", key: "grossDims", label: "Gross dims",
    textValue: formatBoundingBoxDims(computeBounds(job), job),
  };
  const kpis = [grossDimsKpi, ...(job.kpis || [])];

  const empty = document.getElementById("kpiEmpty");
  const count = document.getElementById("kpiCount");
  const fullEmpty = document.getElementById("kpiFullEmpty");
  const fullCount = document.getElementById("kpiFullCount");

  count.textContent = String(kpis.length);
  empty.style.display = "none"; // Gross dims means kpis.length is now never 0 -- see the comment above
  fullCount.textContent = count.textContent;
  fullEmpty.style.display = "none";

  document.getElementById("kpiBody").style.display = "";
  buildKpiRows(document.getElementById("kpiBody"), kpis);
  buildKpiRows(document.getElementById("kpiFullBody"), kpis);
}

function formatKpiValue(value) {
  if (!Number.isFinite(value)) return "—";
  const abs = Math.abs(value);
  if (abs !== 0 && (abs < 0.01 || abs >= 100000)) return value.toExponential(2);
  const decimals = abs >= 100 ? 1 : abs >= 1 ? 2 : 3;
  let text = value.toFixed(decimals);
  if (text.includes(".")) text = text.replace(/0+$/, "").replace(/\.$/, "");
  return text;
}

document.getElementById("kpiHeader").addEventListener("click", () => {
  document.getElementById("kpiPanel").classList.toggle("expanded");
});

function computeBounds(job, includeContext = showContext) {
  const box = new THREE.Box3();
  for (const branch of job.branches) {
    for (const p of branch.positions) box.expandByPoint(toVec3(p));
  }
  if (includeContext) {
    for (const mesh of job.contextMeshes || []) {
      for (const vertex of mesh.vertices || []) box.expandByPoint(toVec3(vertex));
    }
  }
  if (box.isEmpty()) box.set(new THREE.Vector3(-50, -50, 0), new THREE.Vector3(50, 50, 50));
  return box;
}

// Camera fitting and the visible BBox must follow what the user can currently see, rather than
// the complete job payload. Path and Mesh represent the same print branches, so either enabled
// layer contributes each currently enabled role once; Context contributes only while its own
// scene toggle is active. Returns null when every scene layer is off -- there is then no honest
// object to frame, so Reset Camera leaves the current view untouched.
function computeVisibleBounds(job) {
  if (!job) return null;
  const box = new THREE.Box3();
  if (showPath || showMesh) {
    for (const branch of job.branches || []) {
      if (roleVisibility.get(branch.role) === false) continue;
      for (const point of branch.positions || []) box.expandByPoint(toVec3(point));
    }
  }
  if (showContext) {
    for (const mesh of job.contextMeshes || []) {
      for (const vertex of mesh.vertices || []) box.expandByPoint(toVec3(vertex));
    }
  }
  return box.isEmpty() ? null : box;
}

function refreshVisibleBounds() {
  if (!currentJob) return;
  const bounds = computeVisibleBounds(currentJob);
  if (!bounds) {
    currentCameraBounds = null;
    disposeBoundingBox();
    document.getElementById("statBBox").textContent = "—";
    return;
  }
  currentCameraBounds = bounds.clone();
  buildBoundingBox(bounds);
  document.getElementById("statBBox").textContent = formatBoundingBoxDims(bounds, currentJob);
}

function formatDuration(seconds) {
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}

const timeSlider = document.getElementById("timeSlider");
const timeReadout = document.getElementById("timeReadout");

function updateTimeReadout(duration) {
  timeReadout.textContent = `${currentTime.toFixed(1)}s / ${duration.toFixed(1)}s`;
}

// Path and both Mesh representations are cached, so scrubbing only updates group/draw ranges or
// visible instance counts. Coalesce input events because a fast drag can outpace display refresh.
let playbackUpdatePending = false;
timeSlider.addEventListener("input", () => {
  currentTime = Number(timeSlider.value);
  updateTimeReadout(currentJob.statistics.estimatedDurationSeconds);
  if ((!showPath && !showMesh) || playbackUpdatePending) return;
  playbackUpdatePending = true;
  requestAnimationFrame(() => {
    playbackUpdatePending = false;
    if (showPath) updatePathPlayback(currentTime);
    updateMeshPlayback(currentTime);
  });
});

// ---- Playback -- same interaction as Gc05's WasperPlaybackForm (Components/
// 5.0_Gcode/wsp_Gc05_WASPer Simulation.cs): Play/Pause, Stop (back to the
// start), and a continuous speed control from -200% to 1000% (negative plays
// in reverse), all driven by a wall-clock delta each frame rather than fixed
// steps -- ported line-for-line from Gc05's own UpdatePlayback loop, just
// against requestAnimationFrame's timestamp instead of a component solve. ----

const playToggleButton = document.getElementById("playToggle");
const stopButton = document.getElementById("stopPlayback");
const speedSlider = document.getElementById("speedSlider");
const speedReadout = document.getElementById("speedReadout");

let isPlaying = false;
let playbackSpeed = 1.0; // speedSlider.value / 100, matches Gc05's PlaybackSpeed
let playbackLastTick = null; // ms, from requestAnimationFrame's timestamp; null while paused
// Set from the current job's metadata.disablePlayback (Sm05 XR Scene Params, 2026-08-19): true
// when an external source (typically Gc05, via Sm05's sim_par) already owns the simulated
// print position, so the local Play/Stop/time-slider controls are hidden and disabled rather
// than run a second, conflicting clock. See applyJob, below, for where this is set.
let playbackDisabledByJob = false;
// Set when playback runs off either end of the timeline (matching Gc05's
// _restartFromStartOnPlay): the next Play press restarts from 0 instead of
// immediately re-hitting the same boundary and stopping again.
let restartFromStartOnPlay = false;

function updatePlayButton() {
  playToggleButton.innerHTML = isPlaying ? "&#9208;&#65038;" : "&#9654;&#65038;";
  playToggleButton.title = isPlaying ? "Pause" : "Play";
}

function syncTimelineUi() {
  timeSlider.value = String(currentTime);
  updateTimeReadout(currentJob?.statistics?.estimatedDurationSeconds || 0);
}

function pausePlayback() {
  isPlaying = false;
  playbackLastTick = null;
  updatePlayButton();
}

function togglePlay() {
  // Defensive, in addition to playToggleButton.disabled below (which already blocks the click
  // that reaches here) -- guards any other future caller of togglePlay().
  if (playbackDisabledByJob) return;
  if (isPlaying) {
    pausePlayback();
    return;
  }
  if (!currentJob) return;
  if (restartFromStartOnPlay) {
    currentTime = 0;
    restartFromStartOnPlay = false;
    syncTimelineUi();
  }
  // If both visual layers were disabled, prefer the lightweight Path playback. Mesh remains an
  // explicit opt-in rather than being silently enabled when Play is pressed.
  let contentChanged = false;
  if (!showPath && !showMesh) {
    showPath = true;
    modeButtons.path.classList.add("active");
    updateTimelineVisibility();
    contentChanged = true;
  }
  isPlaying = true;
  playbackLastTick = null;
  updatePlayButton();
  if (contentChanged) rebuildContent();
}

function stopPlayback() {
  pausePlayback();
  restartFromStartOnPlay = false;
  currentTime = 0;
  syncTimelineUi();
  if (showPath) updatePathPlayback(currentTime);
  if (showMesh) updateMeshPlayback(currentTime);
}

function advancePlayback(nowMs) {
  if (!isPlaying) return;
  if (playbackLastTick === null) {
    // First frame after Play was pressed -- no prior timestamp to diff
    // against yet, so just record this one and start accumulating next frame.
    playbackLastTick = nowMs;
    return;
  }
  const deltaSeconds = (nowMs - playbackLastTick) / 1000;
  playbackLastTick = nowMs;
  if (!currentJob) return;

  const duration = currentJob.statistics.estimatedDurationSeconds || 0;
  currentTime += deltaSeconds * playbackSpeed;
  if (duration > 1e-6 && (currentTime <= 0 || currentTime >= duration)) {
    currentTime = Math.min(Math.max(currentTime, 0), duration);
    pausePlayback();
    restartFromStartOnPlay = true;
  }
  syncTimelineUi();
  if (showPath) updatePathPlayback(currentTime);
  if (showMesh) updateMeshPlayback(currentTime);
}

playToggleButton.addEventListener("click", togglePlay);
stopButton.addEventListener("click", stopPlayback);
speedSlider.addEventListener("input", () => {
  playbackSpeed = Number(speedSlider.value) / 100;
  speedReadout.textContent = `${speedSlider.value}%`;
});

// Shared by both load paths: a static /api/job fetch (loadJob, below) and a live push over
// /live/view (connectLiveSocket, below) -- both end up with a WASPerPrintJob and need to do the
// exact same scene/HUD rebuild with it. autoFrame is false for live updates after the first one
// so the camera doesn't get yanked back to a default framing every time Grasshopper pushes a new
// frame while the user is orbiting around looking at something.
function applyJob(job, { filePath = null, autoFrame = true } = {}) {
  const errorEl = document.getElementById("loadError");
  errorEl.style.display = "none";

  currentJob = job;
  applyJobViewerStyle(job.viewerStyle);
  applyMobileRenderPolicy(job);
  applyAdaptiveMeshPolicy(job);

  // Best-effort guess at this job's study.json, for the Dashboard panel's
  // path box: real exports land at <StudyFolder>\XR\<job>.wasperxr, so
  // walking up out of the XR folder lands on the study folder itself.
  // Just a prefill -- wrong or missing guesses are harmless, the user can
  // always type/paste the real path. Live updates carry no file path, so
  // this is simply skipped for them (guessStudyPath(null) is a no-op).
  guessedStudyPath = guessStudyPath(filePath);
  const studyPathBox = document.getElementById("studyPathInput");
  if (studyPathBox && !studyPathBox.value) studyPathBox.value = guessedStudyPath;

  // branchByIndex + precomputed per-point frames turn buildSuperellipseBeadGeometry's
  // per-segment lookup from an O(branches) linear scan into an O(1) map read, and
  // avoid re-normalizing axis vectors for every segment that shares a branch point.
  // resetMaterialCaches() drops any materials baked with the previous job's
  // sceneScale (dash sizing) before a fresh set gets lazily created for this one.
  job.branchByIndex = new Map(job.branches.map(b => [b.branchIndex, b]));
  precomputeBranchFrames(job);
  disposeMeshCaches();
  resetMaterialCaches();
  buildContextMeshes(job);

  document.getElementById("jobTitleText").textContent = job.metadata.name || job.metadata.jobId;
  document.getElementById("statBranches").textContent = job.branches.length;
  document.getElementById("statSegments").textContent = job.segments.length;
  document.getElementById("statLayers").textContent = job.layers.length;
  document.getElementById("statDuration").textContent = formatDuration(job.statistics.estimatedDurationSeconds);
  buildLegend(job);
  buildKpiPanel(job);

  // Default to the FULL duration so the completed Path (and optional Mesh) is shown on load.
  // The first Play press restarts from zero, revealing pending Path segments translucently.
  const duration = job.statistics.estimatedDurationSeconds || 1;
  timeSlider.max = String(duration);
  timeSlider.step = String(Math.max(duration / 200, 0.01));
  timeSlider.value = String(duration);
  currentTime = duration;
  updateTimeReadout(duration);

  // Sm05 XR Scene Params (2026-08-19): when the exporting Sm01 had an external sim_par
  // connected, an outside source already owns the simulated print position (each live push
  // already carries whatever partial/complete path that source produced) -- so the browser's
  // own Play/Stop/time-slider would just be a second, conflicting clock. Hide and disable it
  // instead, and surface a small note explaining why in its place.
  playbackDisabledByJob = !!(job.metadata && job.metadata.disablePlayback);
  if (playbackDisabledByJob) pausePlayback();
  playToggleButton.disabled = playbackDisabledByJob;
  stopButton.disabled = playbackDisabledByJob;
  timeSlider.disabled = playbackDisabledByJob;
  speedSlider.disabled = playbackDisabledByJob;
  document.getElementById("externalPlaybackNote").style.display =
    playbackDisabledByJob ? "block" : "none";
  updateTimelineVisibility();

  // Follow Gc05's own playback position (2026-08-19): when disabled by an external source,
  // job.metadata.simulationParameter carries that source's current 0-1 progress (Sm05's
  // sim_par). Map it onto this job's own duration and reuse the exact same "printed so far"
  // Mesh-mode rendering the local time slider already used -- no separate code path, just a
  // different origin for currentTime. Each live push (Sm01 solves on every recompute,
  // debounced ~400ms) re-applies this, so scrubbing/playing Gc05 in Rhino updates the browser
  // at roughly that cadence. Left at full duration (set above) when nothing external is
  // driving it, unchanged from before this field existed.
  if (playbackDisabledByJob) {
    const rawSimPar = Number(job.metadata && job.metadata.simulationParameter);
    const simPar = Number.isFinite(rawSimPar) ? Math.max(0, Math.min(1, rawSimPar)) : 1;
    currentTime = simPar * duration;
    timeSlider.value = String(currentTime);
    updateTimeReadout(duration);
  }
  restartFromStartOnPlay = !playbackDisabledByJob && currentTime >= duration - 1e-6;

  const bounds = computeVisibleBounds(job) || computeBounds(job);
  disposePlatform();
  buildPlatform(bounds);
  if (autoFrame) frameCamera(bounds);
  buildBoundingBox(bounds);
  document.getElementById("statBBox").textContent = formatBoundingBoxDims(bounds, job);

  rebuildContent();
  if (pendingExternalSimulationParameter !== null) {
    applyExternalSimulationParameter(pendingExternalSimulationParameter);
  }
}

// Lightweight external playback update. Sm01 sends this tiny message when only Sm05's sim_par
// changed, so the existing path/context/mesh buffers remain untouched and only their visible
// playback ranges move. This is also the intended extension point for future robot TCP frames.
function applyExternalSimulationParameter(simulationParameter) {
  const value = Number(simulationParameter);
  if (!Number.isFinite(value)) return;
  pendingExternalSimulationParameter = Math.max(0, Math.min(1, value));
  if (!currentJob) return;
  const duration = currentJob.statistics?.estimatedDurationSeconds || 0;
  currentTime = pendingExternalSimulationParameter * duration;
  timeSlider.value = String(currentTime);
  updateTimeReadout(duration);
  restartFromStartOnPlay = false;
  if (showPath) updatePathPlayback(currentTime);
  if (showMesh) updateMeshPlayback(currentTime);
}

function applyExternalSimulationState(state) {
  const externalControl = !!state?.externalControl;
  playbackDisabledByJob = externalControl;
  if (playbackDisabledByJob) pausePlayback();
  playToggleButton.disabled = playbackDisabledByJob;
  stopButton.disabled = playbackDisabledByJob;
  timeSlider.disabled = playbackDisabledByJob;
  speedSlider.disabled = playbackDisabledByJob;
  document.getElementById("externalPlaybackNote").style.display =
    playbackDisabledByJob ? "block" : "none";
  updateTimelineVisibility();

  if (externalControl) {
    applyExternalSimulationParameter(state.simulationParameter);
  } else {
    pendingExternalSimulationParameter = null;
    const duration = currentJob?.statistics?.estimatedDurationSeconds || 0;
    restartFromStartOnPlay = currentTime >= duration - 1e-6;
    syncTimelineUi();
  }
}

async function loadJob(filePath) {
  const url = filePath ? `/api/job?path=${encodeURIComponent(filePath)}` : "/api/job";
  const response = await fetch(url);
  if (!response.ok) {
    let detail = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      detail = body.error || body.detail || detail;
    } catch { /* response wasn't JSON -- keep the status text */ }
    throw new Error(detail);
  }
  const job = await response.json();
  applyJob(job, { filePath, autoFrame: true });
}

const filePathInput = document.getElementById("filePathInput");
const loadErrorEl = document.getElementById("loadError");

async function loadFromInput() {
  const path = filePathInput.value.trim();
  if (!path) {
    loadErrorEl.textContent = "Enter the full path to a .wasperxr file, or send a live job from Sm01.";
    loadErrorEl.style.display = "block";
    return;
  }
  try {
    loadErrorEl.style.display = "none";
    await loadJob(path);
  } catch (err) {
    loadErrorEl.textContent = String(err.message || err);
    loadErrorEl.style.display = "block";
    console.error(err);
  }
}

document.getElementById("loadFileButton").addEventListener("click", loadFromInput);
filePathInput.addEventListener("keydown", e => {
  if (e.key === "Enter") loadFromInput();
});

// A page opened as .../?path=C:\... loads that file directly, so a link can
// point straight at a specific export without anyone touching the input box.
const initialPath = new URLSearchParams(window.location.search).get("path");
if (initialPath) filePathInput.value = initialPath;

// ============================================================================
// Study Dashboard -- mirrors Sm01's native Dashboard tab (Components/1.2_
// Studies/Sm01/WASPer_Sm01DashboardTab.cs): KPI history, X-vs-Y scatter,
// distribution/histogram, correlation heatmap, and parallel coordinates over
// a study.json's iterations. History/Scatter/Histogram use Chart.js (loaded
// from CDN, see the <script> tag near the top of the page); the heatmap and
// parallel-coordinates views are hand-rolled 2D canvas renderers since no
// Chart.js chart type fits either well. Deliberately narrower than the
// native tab in a few ways, called out here rather than silently: no
// draggable scatter point-label repositioning, no per-chart "Labels..."
// title/axis-min-max editing dialog, and no Groups show/hide checklist for
// the heatmap/parallel variable set (it always uses whatever varies, capped
// at 14). Everything else -- the 5 chart types, their KPI/parameter
// dropdowns, scatter style/colour-by, histogram style/bins/bandwidth -- is
// there. Not yet tested in a real browser, same standing limitation as the
// rest of this viewer's JS/WebGL work this session.
// ============================================================================

let currentStudy = null;
let guessedStudyPath = "";
let dashCatalog = { paramVars: [], kpiVars: [], all: [] };
const dashCharts = {}; // Chart.js instances, kept so we can .destroy() before rebuilding

function guessStudyPath(jobPath) {
  if (!jobPath) return "";
  const parts = jobPath.split(/[\\/]/).filter(p => p.length > 0);
  if (!parts.length) return "";
  parts.pop(); // drop the file name
  if (parts.length && parts[parts.length - 1].toLowerCase() === "xr") parts.pop();
  if (!parts.length) return "";
  return parts.join("\\") + "\\study.json";
}

function varId(ref) {
  return ref ? (ref.isInput ? "in:" : "kpi:") + ref.key : "";
}

function findVar(id) {
  return dashCatalog.all.find(v => varId(v) === id) || null;
}

// Builds the flat catalog of everything a chart can plot: every swept study
// parameter (always numeric) plus every distinct KPI key seen across all
// iterations (numeric if any iteration gave it a Value, categorical if any
// gave it a TextValue -- matching native's separate numeric-only vs
// colour-by-eligible option lists).
function buildDashboardCatalog(study) {
  const paramVars = (study.parameters || []).map(p => ({
    key: p.name, isInput: true, label: p.name, group: "Parameters", numeric: true, categorical: false
  }));

  const kpiMap = new Map();
  for (const it of study.iterations || []) {
    for (const kpi of it.kpis || []) {
      if (!kpiMap.has(kpi.key)) {
        kpiMap.set(kpi.key, {
          key: kpi.key, isInput: false, label: kpi.label || kpi.key,
          group: kpi.group || "Other", numeric: false, categorical: false
        });
      }
      const entry = kpiMap.get(kpi.key);
      if (typeof kpi.value === "number" && Number.isFinite(kpi.value)) entry.numeric = true;
      if (kpi.textValue) entry.categorical = true;
    }
  }
  const kpiVars = [...kpiMap.values()];
  return { paramVars, kpiVars, all: [...paramVars, ...kpiVars] };
}

function getVarValue(iteration, ref) {
  if (!ref) return null;
  if (ref.isInput) {
    const v = iteration.parameters ? iteration.parameters[ref.key] : undefined;
    return typeof v === "number" && Number.isFinite(v) ? v : null;
  }
  const kpi = (iteration.kpis || []).find(k => k.key === ref.key);
  if (!kpi || typeof kpi.value !== "number" || !Number.isFinite(kpi.value)) return null;
  return kpi.value;
}

function getVarCategory(iteration, ref) {
  if (!ref) return null;
  if (ref.isInput) {
    const v = getVarValue(iteration, ref);
    return v == null ? null : String(v);
  }
  const kpi = (iteration.kpis || []).find(k => k.key === ref.key);
  if (!kpi) return null;
  if (kpi.textValue) return kpi.textValue;
  if (typeof kpi.value === "number" && Number.isFinite(kpi.value)) return String(kpi.value);
  return null;
}

// Populates a <select> with <optgroup>s -- "Parameters" first, then each KPI
// group in first-seen order -- approximating native's DisplayGroup grouping
// (Group [+ " - " + Method] [+ " (" + SubsetId + ")"]; Method/SubsetId are
// carried by the native WasperKpi but not by this viewer's trimmed
// PrintJobKpi DTO, so grouping here is by Group alone -- the same in
// practice for every KPI seen so far, where Method/SubsetId are empty).
function populateVarSelect(select, includeNone, filterFn) {
  select.innerHTML = "";
  if (includeNone) {
    const opt = document.createElement("option");
    opt.value = "";
    opt.textContent = "No colour";
    select.appendChild(opt);
  }
  const groups = new Map();
  for (const v of dashCatalog.all) {
    if (!filterFn(v)) continue;
    if (!groups.has(v.group)) groups.set(v.group, []);
    groups.get(v.group).push(v);
  }
  for (const [groupName, vars] of groups) {
    const group = document.createElement("optgroup");
    group.label = groupName;
    for (const v of vars) {
      const opt = document.createElement("option");
      opt.value = varId(v);
      opt.textContent = v.label;
      group.appendChild(opt);
    }
    select.appendChild(group);
  }
}

function selectValueOrFirst(select, preferredId) {
  if (preferredId && [...select.options].some(o => o.value === preferredId)) {
    select.value = preferredId;
  } else if (select.options.length) {
    select.selectedIndex = (select.options[0].value === "" && select.options.length > 1) ? 1 : 0;
  }
}

const DASH_PALETTE = ["#4fc3f7", "#ff8a65", "#81c784", "#ba68c8", "#ffd54f",
  "#4dd0e1", "#f06292", "#a1887f", "#90a4ae", "#dce775"];
const DASH_OTHER_COLOR = "#6f6f7e";

function destroyChart(key) {
  if (dashCharts[key]) { dashCharts[key].destroy(); delete dashCharts[key]; }
}

function dashLineOptions(xTitle, yTitle) {
  return {
    responsive: true,
    animation: false,
    scales: {
      x: { type: "linear", title: { display: true, text: xTitle, color: "#9a9aa8" },
        ticks: { color: "#9a9aa8" }, grid: { color: "rgba(255,255,255,0.06)" } },
      y: { title: { display: true, text: yTitle, color: "#9a9aa8" },
        ticks: { color: "#9a9aa8" }, grid: { color: "rgba(255,255,255,0.06)" } }
    },
    plugins: { legend: { labels: { color: "#e8e8ee" } } }
  };
}

// ---- History: one line, X = iteration order, Y = the chosen KPI. ----

function rebuildHistoryChart() {
  const select = document.getElementById("dashHistoryKpi");
  const ref = findVar(select.value);
  const canvas = document.getElementById("dashHistoryCanvas");
  destroyChart("history");
  if (!ref || !currentStudy) return;

  const points = currentStudy.iterations
    .map(it => ({ x: it.index, y: getVarValue(it, ref) }))
    .filter(p => p.y != null)
    .sort((a, b) => a.x - b.x);

  dashCharts.history = new Chart(canvas, {
    type: "line",
    data: { datasets: [{ label: ref.label, data: points, borderColor: DASH_PALETTE[0],
      backgroundColor: DASH_PALETTE[0], pointRadius: 3, tension: 0.15 }] },
    options: dashLineOptions("Iteration", ref.label)
  });
}

// ---- Scatter: X/Y any variable, optional categorical colour grouping. ----

function rebuildScatterChart() {
  const xRef = findVar(document.getElementById("dashScatterX").value);
  const yRef = findVar(document.getElementById("dashScatterY").value);
  const colorRef = findVar(document.getElementById("dashScatterColor").value);
  const style = document.getElementById("dashScatterStyle").value;
  const canvas = document.getElementById("dashScatterCanvas");
  destroyChart("scatter");
  if (!xRef || !yRef || !currentStudy) return;

  const showLine = style === "Line" || style === "LineMarkers";
  const pointRadius = style === "Line" ? 0 : 4;

  const rows = currentStudy.iterations
    .map(it => ({
      x: getVarValue(it, xRef), y: getVarValue(it, yRef),
      category: colorRef ? getVarCategory(it, colorRef) : null
    }))
    .filter(r => r.x != null && r.y != null);

  let datasets;
  if (colorRef) {
    const categories = [...new Set(rows.map(r => r.category ?? "(none)"))];
    datasets = categories.map((cat, i) => ({
      label: cat,
      data: rows.filter(r => (r.category ?? "(none)") === cat),
      borderColor: i < DASH_PALETTE.length ? DASH_PALETTE[i] : DASH_OTHER_COLOR,
      backgroundColor: i < DASH_PALETTE.length ? DASH_PALETTE[i] : DASH_OTHER_COLOR,
      showLine, pointRadius, fill: false
    }));
  } else {
    datasets = [{ label: `${yRef.label} vs ${xRef.label}`, data: rows,
      borderColor: DASH_PALETTE[0], backgroundColor: DASH_PALETTE[0], showLine, pointRadius, fill: false }];
  }

  dashCharts.scatter = new Chart(canvas, {
    type: "scatter",
    data: { datasets },
    options: dashLineOptions(xRef.label, yRef.label)
  });
}

// ---- Histogram: Bars / Region (frequency polygon) / Density (Gaussian KDE,
// Silverman's-rule bandwidth scaled by the user's Smoothing % control). ----

function silvermanBandwidth(values) {
  const n = values.length;
  if (n < 2) return 1;
  const mean = values.reduce((a, b) => a + b, 0) / n;
  const variance = values.reduce((a, b) => a + (b - mean) * (b - mean), 0) / (n - 1);
  const sd = Math.sqrt(variance);
  const sorted = [...values].sort((a, b) => a - b);
  const q1 = sorted[Math.floor((n - 1) * 0.25)];
  const q3 = sorted[Math.floor((n - 1) * 0.75)];
  const iqr = q3 - q1;
  const spread = iqr > 0 ? Math.min(sd, iqr / 1.34) : sd;
  const base = spread > 0 ? spread : (sd > 0 ? sd : 1);
  return 0.9 * base * Math.pow(n, -0.2);
}

function rebuildHistogramChart() {
  const ref = findVar(document.getElementById("dashHistogramVar").value);
  const mode = document.getElementById("dashHistogramStyle").value;
  const bins = Math.max(2, Math.min(60, Number(document.getElementById("dashHistogramBins").value) || 11));
  const bandwidthPercent = Math.max(10, Number(document.getElementById("dashHistogramBandwidth").value) || 100);
  const canvas = document.getElementById("dashHistogramCanvas");
  destroyChart("histogram");
  document.getElementById("dashHistogramBinsRow").style.display = mode === "Density" ? "none" : "";
  document.getElementById("dashHistogramBandwidthRow").style.display = mode === "Density" ? "" : "none";
  if (!ref || !currentStudy) return;

  const values = currentStudy.iterations.map(it => getVarValue(it, ref)).filter(v => v != null);
  if (values.length === 0) return;
  const min = Math.min(...values), max = Math.max(...values);
  const span = max - min || 1;

  if (mode === "Density") {
    const bandwidth = silvermanBandwidth(values) * (bandwidthPercent / 100) || 1;
    const steps = 100;
    const pad = span * 0.1;
    const points = [];
    for (let i = 0; i <= steps; i++) {
      const x = (min - pad) + (i / steps) * (span + 2 * pad);
      let density = 0;
      for (const v of values) {
        const z = (x - v) / bandwidth;
        density += Math.exp(-0.5 * z * z);
      }
      density /= (values.length * bandwidth * Math.sqrt(2 * Math.PI));
      points.push({ x, y: density });
    }
    const rug = values.map(v => ({ x: v, y: 0 })); // ticks marking actual sample positions
    dashCharts.histogram = new Chart(canvas, {
      type: "line",
      data: { datasets: [
        { label: ref.label + " density", data: points, borderColor: DASH_PALETTE[0],
          backgroundColor: "rgba(79,195,247,0.15)", fill: true, pointRadius: 0, tension: 0.25 },
        { label: "samples", data: rug, borderColor: DASH_PALETTE[1], backgroundColor: DASH_PALETTE[1],
          showLine: false, pointRadius: 3, pointStyle: "line" }
      ] },
      options: dashLineOptions(ref.label, "Density")
    });
    return;
  }

  const width = span / bins;
  const counts = new Array(bins).fill(0);
  for (const v of values) {
    let idx = Math.floor((v - min) / width);
    if (idx >= bins) idx = bins - 1;
    if (idx < 0) idx = 0;
    counts[idx]++;
  }

  if (mode === "Region") {
    const points = counts.map((c, i) => ({ x: min + (i + 0.5) * width, y: c }));
    dashCharts.histogram = new Chart(canvas, {
      type: "line",
      data: { datasets: [{ label: ref.label, data: points, borderColor: DASH_PALETTE[0],
        backgroundColor: "rgba(79,195,247,0.15)", fill: true, pointRadius: 0, tension: 0.2 }] },
      options: dashLineOptions(ref.label, "Count")
    });
    return;
  }

  const labels = counts.map((_, i) => (min + i * width).toFixed(2));
  dashCharts.histogram = new Chart(canvas, {
    type: "bar",
    data: { labels, datasets: [{ label: ref.label, data: counts, backgroundColor: DASH_PALETTE[0] }] },
    options: {
      responsive: true, animation: false,
      scales: {
        x: { title: { display: true, text: ref.label, color: "#9a9aa8" }, ticks: { color: "#9a9aa8" }, grid: { display: false } },
        y: { title: { display: true, text: "Count", color: "#9a9aa8" }, ticks: { color: "#9a9aa8" }, grid: { color: "rgba(255,255,255,0.06)" } }
      },
      plugins: { legend: { display: false } }
    }
  });
}

// ---- Correlation heatmap + Parallel coordinates share one variable set:
// numeric parameters/KPIs that actually vary across iterations, capped at
// 14 with parameters given priority (roughly half/half with KPIs), matching
// native's DashboardMultivariateLimit behaviour. Both are hand-rolled 2D
// canvas renders -- no Chart.js chart type fits either. ----

function dashboardMultivariateVars() {
  if (!currentStudy) return [];
  const varies = (v) => {
    const values = currentStudy.iterations.map(it => getVarValue(it, v)).filter(x => x != null);
    if (values.length < 2) return false;
    const first = values[0];
    return values.some(x => Math.abs(x - first) > 1e-9);
  };
  const params = dashCatalog.paramVars.filter(varies);
  const kpis = dashCatalog.kpiVars.filter(v => v.numeric && varies(v));
  const limit = 14;
  const half = Math.floor(limit / 2);
  const takenParams = params.slice(0, Math.max(half, limit - kpis.length));
  const takenKpis = kpis.slice(0, Math.max(0, limit - takenParams.length));
  return [...takenParams, ...takenKpis];
}

function truncateLabel(text, max) {
  return text.length > max ? text.slice(0, max - 1) + "\u2026" : text;
}

function sizeCanvasForCard(canvas, cssHeight) {
  const dpr = window.devicePixelRatio || 1;
  const cssWidth = canvas.parentElement.clientWidth || 600;
  canvas.width = cssWidth * dpr;
  canvas.height = cssHeight * dpr;
  canvas.style.width = cssWidth + "px";
  canvas.style.height = cssHeight + "px";
  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssWidth, cssHeight);
  return { ctx, cssWidth, cssHeight };
}

function drawDashEmptyMessage(ctx, cssWidth, cssHeight, message) {
  ctx.fillStyle = "#6f6f7e";
  ctx.font = "12px sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText(message, cssWidth / 2, cssHeight / 2);
}

function correlationColor(r) {
  if (r == null) return "rgba(255,255,255,0.05)";
  const t = Math.max(-1, Math.min(1, r));
  if (t >= 0) {
    const g = Math.round(255 - t * 165), b = Math.round(255 - t * 205);
    return `rgb(255,${g},${b})`;
  }
  const at = -t;
  const rr = Math.round(255 - at * 175), g = Math.round(255 - at * 95);
  return `rgb(${rr},${g},255)`;
}

function rebuildHeatmapChart() {
  const canvas = document.getElementById("dashHeatmapCanvas");
  const vars = dashboardMultivariateVars();
  const { ctx, cssWidth, cssHeight } = sizeCanvasForCard(canvas, 420);

  if (!currentStudy || vars.length < 2) {
    drawDashEmptyMessage(ctx, cssWidth, cssHeight, "Need at least 2 varying numeric variables.");
    return;
  }

  const n = vars.length;
  const labelSpace = 96;
  const gridSize = Math.max(60, Math.min(cssWidth - labelSpace, cssHeight - labelSpace));
  const cell = gridSize / n;
  const originX = labelSpace, originY = labelSpace;
  const series = vars.map(v => currentStudy.iterations.map(it => getVarValue(it, v)));

  function pearson(ai, bi) {
    const pairs = [];
    for (let i = 0; i < series[ai].length; i++) {
      const a = series[ai][i], b = series[bi][i];
      if (a != null && b != null) pairs.push([a, b]);
    }
    if (pairs.length < 2) return null;
    const mx = pairs.reduce((s, p) => s + p[0], 0) / pairs.length;
    const my = pairs.reduce((s, p) => s + p[1], 0) / pairs.length;
    let cov = 0, vx = 0, vy = 0;
    for (const [a, b] of pairs) { const dx = a - mx, dy = b - my; cov += dx * dy; vx += dx * dx; vy += dy * dy; }
    if (vx === 0 || vy === 0) return null;
    return cov / Math.sqrt(vx * vy);
  }

  ctx.font = "10px sans-serif"; ctx.fillStyle = "#9a9aa8";
  for (let row = 0; row < n; row++) {
    ctx.save();
    ctx.textAlign = "right"; ctx.textBaseline = "middle";
    ctx.fillText(truncateLabel(vars[row].label, 14), originX - 6, originY + row * cell + cell / 2);
    ctx.restore();
  }
  for (let col = 0; col < n; col++) {
    ctx.save();
    ctx.translate(originX + col * cell + cell / 2, originY - 6);
    ctx.rotate(-Math.PI / 4);
    ctx.textAlign = "left"; ctx.textBaseline = "middle";
    ctx.fillText(truncateLabel(vars[col].label, 14), 0, 0);
    ctx.restore();
  }

  for (let row = 0; row < n; row++) {
    for (let col = 0; col < n; col++) {
      const r = row === col ? 1 : pearson(row, col);
      const x = originX + col * cell, y = originY + row * cell;
      ctx.fillStyle = correlationColor(r);
      ctx.fillRect(x, y, cell - 1, cell - 1);
      if (r != null && cell >= 28) {
        ctx.fillStyle = Math.abs(r) > 0.55 ? "#14151a" : "#e8e8ee";
        ctx.font = "10px sans-serif"; ctx.textAlign = "center"; ctx.textBaseline = "middle";
        ctx.fillText(r.toFixed(2), x + cell / 2, y + cell / 2);
      }
    }
  }
}

function rebuildParallelChart() {
  const canvas = document.getElementById("dashParallelCanvas");
  const vars = dashboardMultivariateVars();
  const { ctx, cssWidth, cssHeight } = sizeCanvasForCard(canvas, 320);

  if (!currentStudy || vars.length < 2) {
    drawDashEmptyMessage(ctx, cssWidth, cssHeight, "Need at least 2 varying numeric variables.");
    return;
  }

  const topPad = 24, bottomPad = 28, sidePad = 40;
  const plotWidth = cssWidth - sidePad * 2;
  const plotHeight = cssHeight - topPad - bottomPad;
  const n = vars.length;
  const axisX = (i) => sidePad + (n === 1 ? plotWidth / 2 : (i / (n - 1)) * plotWidth);

  const ranges = vars.map(v => {
    const values = currentStudy.iterations.map(it => getVarValue(it, v)).filter(x => x != null);
    return { min: Math.min(...values), max: Math.max(...values) };
  });

  ctx.strokeStyle = "rgba(255,255,255,0.18)"; ctx.lineWidth = 1;
  ctx.fillStyle = "#9a9aa8"; ctx.font = "10px sans-serif"; ctx.textAlign = "center";
  for (let i = 0; i < n; i++) {
    const x = axisX(i);
    ctx.beginPath(); ctx.moveTo(x, topPad); ctx.lineTo(x, topPad + plotHeight); ctx.stroke();
    ctx.fillText(truncateLabel(vars[i].label, 12), x, topPad - 8);
  }

  ctx.lineWidth = 1;
  for (const it of currentStudy.iterations) {
    const ys = [];
    let complete = true;
    for (let i = 0; i < n; i++) {
      const v = getVarValue(it, vars[i]);
      if (v == null) { complete = false; break; }
      const { min, max } = ranges[i];
      const t = max > min ? (v - min) / (max - min) : 0.5;
      ys.push(topPad + (1 - t) * plotHeight);
    }
    if (!complete) continue;
    ctx.strokeStyle = "rgba(79,195,247,0.35)";
    ctx.beginPath();
    for (let i = 0; i < n; i++) {
      const x = axisX(i);
      if (i === 0) ctx.moveTo(x, ys[i]); else ctx.lineTo(x, ys[i]);
    }
    ctx.stroke();
  }
}

// ---- Orchestration ----

function rebuildAllDashCharts() {
  rebuildHistoryChart();
  rebuildScatterChart();
  rebuildHistogramChart();
  rebuildHeatmapChart();
  rebuildParallelChart();
}

function applyDashboardDefaults(defaults) {
  const historySelect = document.getElementById("dashHistoryKpi");
  populateVarSelect(historySelect, false, v => v.numeric);
  selectValueOrFirst(historySelect, varId(defaults?.historyKpi));

  const xSelect = document.getElementById("dashScatterX");
  const ySelect = document.getElementById("dashScatterY");
  populateVarSelect(xSelect, false, v => v.numeric);
  populateVarSelect(ySelect, false, v => v.numeric);
  selectValueOrFirst(xSelect, varId(defaults?.scatterX));
  selectValueOrFirst(ySelect, varId(defaults?.scatterY));

  const colorSelect = document.getElementById("dashScatterColor");
  populateVarSelect(colorSelect, true, v => v.numeric || v.categorical);
  colorSelect.value = defaults?.scatterColor ? varId(defaults.scatterColor) : "";

  document.getElementById("dashScatterStyle").value =
    (defaults?.scatterStyle === "Line" || defaults?.scatterStyle === "LineMarkers") ? defaults.scatterStyle : "Markers";

  const histVarSelect = document.getElementById("dashHistogramVar");
  populateVarSelect(histVarSelect, false, v => v.numeric);
  selectValueOrFirst(histVarSelect, varId(defaults?.histogramVariable));

  const histogramMode = ["Bars", "Region", "Density"].includes(defaults?.histogramMode) ? defaults.histogramMode : "Bars";
  document.getElementById("dashHistogramStyle").value = histogramMode;
  document.getElementById("dashHistogramBins").value = defaults?.histogramBins || 11;
  document.getElementById("dashHistogramBandwidth").value = defaults?.histogramBandwidthPercent || 100;
  document.getElementById("dashHistogramBinsRow").style.display = histogramMode === "Density" ? "none" : "";
  document.getElementById("dashHistogramBandwidthRow").style.display = histogramMode === "Density" ? "" : "none";
}

async function loadStudy(studyPath) {
  const statusEl = document.getElementById("dashboardStatus");
  statusEl.textContent = "Loading...";
  try {
    const response = await fetch(`/api/study?path=${encodeURIComponent(studyPath)}`);
    if (!response.ok) {
      let detail = `${response.status} ${response.statusText}`;
      try { const body = await response.json(); detail = body.error || body.detail || detail; } catch { /* not JSON */ }
      throw new Error(detail);
    }
    currentStudy = await response.json();
    dashCatalog = buildDashboardCatalog(currentStudy);
    applyDashboardDefaults(currentStudy.dashboard);
    rebuildAllDashCharts();
    statusEl.textContent = `${currentStudy.runName || currentStudy.studyId || "Study"}: ${currentStudy.iterations.length} iteration(s).`;
  } catch (err) {
    currentStudy = null;
    statusEl.textContent = "Failed to load: " + String(err.message || err);
    console.error(err);
  }
}

const dashboardOverlay = document.getElementById("dashboardOverlay");
document.getElementById("openDashboard").addEventListener("click", () => {
  dashboardOverlay.classList.add("visible");
  // Always attempt a load on first open, even with an empty path box --
  // /api/study falls back to a bundled SampleData\study.json if present
  // (same convention as /api/job), so a standalone package's Dashboard
  // works with zero typing. A genuinely empty result just surfaces as a
  // quiet "no study" status message rather than a real failure.
  if (!currentStudy) {
    const box = document.getElementById("studyPathInput");
    loadStudy(box.value.trim());
  }
});
document.getElementById("dashboardClose").addEventListener("click", () => {
  dashboardOverlay.classList.remove("visible");
});

// Full-screen KPIs panel (added 2026-08-19) -- same open/close pattern as the Study Dashboard
// above; data is already kept in sync with the inline panel by buildKpiPanel/buildKpiRows, so
// opening this never needs to trigger a load the way the Dashboard's first open does.
const kpiFullOverlay = document.getElementById("kpiFullOverlay");
document.getElementById("openKpisButton").addEventListener("click", () => {
  kpiFullOverlay.classList.add("visible");
});
document.getElementById("kpiFullClose").addEventListener("click", () => {
  kpiFullOverlay.classList.remove("visible");
});
document.getElementById("studyLoadButton").addEventListener("click", () => {
  const path = document.getElementById("studyPathInput").value.trim();
  if (path) loadStudy(path);
});
document.getElementById("studyPathInput").addEventListener("keydown", e => {
  if (e.key === "Enter") document.getElementById("studyLoadButton").click();
});

document.getElementById("dashHistoryKpi").addEventListener("change", rebuildHistoryChart);
for (const id of ["dashScatterX", "dashScatterY", "dashScatterColor", "dashScatterStyle"]) {
  document.getElementById(id).addEventListener("change", rebuildScatterChart);
}
for (const id of ["dashHistogramVar", "dashHistogramStyle", "dashHistogramBins", "dashHistogramBandwidth"]) {
  document.getElementById(id).addEventListener("change", rebuildHistogramChart);
}

function animate(nowMs, frame) {
  // Driven by renderer.setAnimationLoop (below), not a raw requestAnimationFrame self-call as
  // before M9 (2026-08-19): WebXR requires that -- it's what lets three.js swap this same
  // callback onto the XR device's own frame timing during an AR session (frame, the second
  // argument, is only ever populated then) and back to plain rAF timing once the session ends,
  // with no separate code path needed for either.
  if (!renderer.xr.isPresenting) controls.update(); // OrbitControls fights the AR camera pose otherwise
  advancePlayback(nowMs); // undefined on the very first (non-rAF) call, harmless: isPlaying starts false
  // updateGhostUniforms() call removed while Ghost mode is disabled (see the
  // comment above buildGhostMode) -- nothing in contentGroup will ever have
  // userData.isGhostBatch set while it's dormant, so this would be a no-op
  // anyway, but skipping the call avoids paying for it every frame.
  // updateArCalibration(frame) removed 2026-08-19 alongside the rest of the drag-to-adjust-
  // rectangle machinery -- calibration no longer needs per-frame plane-detection processing, just
  // the same reticle.matrix a normal tap already reads directly in onArSelect.
  if (frame) {
    updateArHitTest(frame);
  }
  renderer.render(scene, camera);
}

window.addEventListener("resize", () => {
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);

  // Chart.js canvases (History/Scatter/Histogram) resize themselves via
  // their own ResizeObserver (responsive: true); the heatmap/parallel
  // canvases are hand-rolled and don't, so redraw them explicitly -- only
  // while the Dashboard is actually open, since currentStudy may be unset.
  if (dashboardOverlay.classList.contains("visible") && currentStudy) {
    rebuildHeatmapChart();
    rebuildParallelChart();
  }
});

// Independent of loadJob below: the live socket connects (and keeps retrying) regardless of
// whether the initial static/sample job load succeeds, so a page opened with nothing to show yet
// still picks up the first live push the moment Grasshopper sends one.
const liveJobConnection = createLiveJobConnection({
  applyJob,
  applySimulationState: applyExternalSimulationState,
});
liveJobConnection.connect();
window.addEventListener("pagehide", () => liveJobConnection.dispose(), { once: true });

renderer.setAnimationLoop(animate);

// A bare viewer is the normal Live Link entry point. Keep it empty until Sm01 pushes the
// active job; only perform a file load when the URL explicitly supplies ?path=.
if (initialPath) {
  loadJob(initialPath).catch(err => {
    document.getElementById("jobTitleText").textContent = "Failed to load job";
    loadErrorEl.textContent = String(err.message || err);
    loadErrorEl.style.display = "block";
    console.error(err);
  });
}

# WASPer Process Viewer Guide

Last updated: 2026-08-21

## 1. Required .NET Runtime

The WASPer browser Process Viewer is shipped as a lightweight framework-dependent viewer. This keeps the WASPer_3DP package smaller, but the computer running Rhino/Grasshopper must have the **.NET 8 ASP.NET Core Runtime** installed before the **Process Viewer (XR)** tab can start the local WebViewer server.

Install it from the official Microsoft .NET 8 download page:

```text
https://dotnet.microsoft.com/download/dotnet/8.0
```

On Windows, choose **ASP.NET Core Runtime 8.x** for your machine architecture, usually **Windows x64**. The .NET SDK also works, but it is larger and intended for development. After installation, close and reopen Rhino/Grasshopper so the `dotnet` host is available to WASPer.

To verify the installation manually, open PowerShell and run:

```powershell
dotnet --list-runtimes
```

The list should include a line similar to:

```text
Microsoft.AspNetCore.App 8.x.x [...]
```

If Sm01 does not detect the runtime, the **Process Viewer (XR)** tab highlights the **Open in Browser** and **Download Guide** buttons and reports the missing dependency in the **Application** row. The rest of Sm01 remains usable; only the browser Process Viewer server is blocked until the runtime is installed.

## 2. Overview

The WASPer Process Viewer is a browser-based viewer for inspecting and simulating a WASPer_3DP fabrication job. It is opened and controlled from the **Process Viewer (XR)** tab of `wsp_Sm01_WASPer Study Manager`.

The viewer can run on the Rhino/Grasshopper computer or on another device connected to the same local network. On a compatible Android phone, it can also place the print job in the physical environment using WebXR augmented reality (AR).

The current browser viewer is independent of vvvv Gamma and VL.Rhino.3dm. Those tools remain useful for future native or headset-oriented viewers, but they are not required for the present desktop, mobile-browser, or Android AR workflow.

## 3. Main Workflow

The normal Grasshopper connection is:

```text
complete, stable wsp_path --------------------> Sm01 wsp_path

optional context geometry --+
optional materials ---------> Sm05 XR Scene Params ---> Sm01 xr_pack
optional sim_par ------------+
```

Important: connect the complete and stable `wsp_path` to Sm01. When Gc05 controls the simulation, connect only its normalized `sim_par` value to Sm05. Do not feed Sm01 a repeatedly changing partial path; doing so forces expensive structural package rebuilds and can make live playback lag.

### Quick Start

1. Connect a complete `wsp_path` to Sm01.
2. Open **Study Manager** and select **Process Viewer (XR)**.
3. Leave **Live: On** enabled for automatic updates.
4. Click **Open in Browser**.
5. Use **Path**, **Mesh**, **Context**, and **BBox** to choose what is visible.
6. Use the timeline to play or scrub the fabrication sequence.
7. Click **Reset Camera** to frame the objects that are currently turned on.

The local server starts automatically once Sm01 has a valid path or an existing XR export. **Open in Browser** is only a shortcut for opening the page; it is not the server start command.

## 4. Sm01 Process Viewer Controls

- **Open in Browser** opens the current session at `http://localhost:5252` on the computer.
- **Refresh** refreshes the XR scene, verifies the local server, pushes the current state, polls viewer status, and regenerates the available mobile QR links.
- **Live: On/Off** controls automatic Grasshopper-to-browser updates.
- **Push Change** sends one manual update while Live is off.
- **Export** writes a reusable `.wasperxr` package.
- **Mobile Access (QR code)** lists one QR code for each usable LAN address because the computer may have Ethernet, Wi-Fi, VPN, or Mobile Hotspot addresses simultaneously.
- **Viewer status** reports whether a browser is connected to this Sm01 session.

Each Sm01 instance uses a session identifier derived from the Grasshopper document and the component instance. The browser URL contains `?session=...`, preventing two open definitions or two Sm01 components from unintentionally sharing a live scene.

By project convention, Process Viewer packages are stored in the active study's `XR` folder. Without an active study, the fallback is the project's `Simulations/XR` location.

## 5. Browser Viewer Features

### Fabrication Visualization

- Displays the complete WASPer path as lightweight lines.
- Distinguishes printed and pending path sections during playback. Pending paths use the same role palette with lower opacity.
- Reconstructs fabrication timing from the motion plan, including print, travel, and Z-hop motions.
- Supports browser-controlled playback, reverse playback, scrubbing, and speeds from -200% to 1000%.
- Accepts external playback from Sm05 `sim_par`. When connected, the browser timeline is disabled to avoid two competing clocks.
- Displays path roles such as Shell, Infill, Partition, and Support, with per-role visibility controls.
- Provides WASPer color palettes and preserves the viewer style included in the package.

### Mesh Preview

- **Fast** uses instanced bead segments and is recommended for phones and large jobs.
- **Continuous** builds connected bead geometry for a smoother surface, but costs more CPU time and memory.
- Mesh display is off by default so large files open quickly.
- The viewer automatically prefers Fast mode on mobile devices and for high segment counts unless the user explicitly selects another mode.

### Context and Scene Inspection

- Sm05 accepts meshes, Breps, surfaces, and extrusions as context geometry.
- Breps, surfaces, and extrusions are converted to display meshes before transmission.
- Materials can be supplied per object or broadcast from one item to all objects.
- Grasshopper colors and Rhino material diffuse color/transparency are supported.
- If no material is supplied, mesh vertex colors are detected and averaged for display.
- **Context** independently hides or shows this non-print geometry.
- **BBox** shows a dashed bounding box and dimensions. It encloses only the currently enabled scene categories.
- **Reset Camera** frames the geometry that is currently visible, rather than the complete hidden scene.
- The HUD reports branches, segments, layers, duration, dimensions, roles, and package KPIs.
- **Simple UI** reduces screen clutter while preserving fabrication controls.

### Mobile and AR

- Responsive phone layout and touch-friendly controls.
- One-finger orbit and two-finger zoom/pan in the normal 3D viewer.
- WebXR `immersive-ar` mode on compatible Android devices.
- Surface detection and hit testing with a placement reticle.
- Tap once to place and lock the model. The blue surface reticle disappears after placement.
- Use **Reposition** to show the reticle again and permit one deliberate replacement tap.
- Z-up WASPer geometry is converted to WebXR's gravity-aligned Y-up space.
- Auto-fit placement, true-scale calibration, manual scale, rotation, and pinch resizing.
- Optional AR overlay for playback, scene modes, and role visibility.
- The same Path, Mesh, Context, BBox, palette, and playback data are reused in AR; a separate AR model is not generated.

## 6. Technical Architecture

```text
Rhino / Grasshopper
  Sm01 + optional Sm05
          |
          | complete binary job or small sim_par message
          v
ASP.NET Core local server (Kestrel, port 5252)
  /live/push   Grasshopper -> server
  /live/view   server -> browser
  /live/status connection count for Sm01
  /api/job     file-based .wasperxr import
          |
          v
Browser viewer (three.js + WebGL + WebXR)
  desktop Chrome/Edge, Android Chrome, or another WebGL browser
```

### Data Transport

The principal transport is the binary `.wasperxr` container:

- Magic identifier: `WSPXRBN1`.
- Binary payload schema: `0.2.0`.
- GZip compression.
- A shared `Float64` origin and origin-relative `Float32` coordinates reduce package size while preserving large-world-coordinate accuracy.
- Contains job metadata, path branches, point orientations, layer/width/flow series, motion timing, roles, bounds, KPIs, optional context meshes, and viewer style.
- Legacy schema `0.1.0` JSON packages remain readable.

Sm01 sends the complete binary package when structural data changes. Once the browser has that package, `sim_par` changes are sent as small JSON text messages so the heavy paths and context meshes do not need to be rebuilt or retransmitted for every simulation step.

Browser-to-server WebSockets negotiate per-message compression. The server binds to `0.0.0.0:5252`, while the desktop shortcut uses `localhost:5252`.

### Rendering Strategy

- Path rendering uses batched GPU line geometry.
- Fast mesh mode uses `THREE.InstancedMesh` to reuse one bead geometry across many path segments.
- Continuous mesh mode joins connected bead geometry and changes draw ranges during playback.
- Context meshes are cached and only rebuilt when their geometry or material signature changes.
- Camera framing is based on the bounds of the currently visible categories.
- WebXR rendering uses `renderer.setAnimationLoop`, a `local-floor` reference space, the Hit Test API, and optional DOM Overlay controls.

## 7. Libraries and Dependencies

| Layer | Dependency | Purpose |
| --- | --- | --- |
| Grasshopper plugin | Rhino 8, Grasshopper, RhinoCommon, Grasshopper SDK | Generates and supplies WASPer paths, context geometry, materials, and parameters. |
| Grasshopper plugin | .NET 8 Windows | Current WASPer_3DP component target. |
| QR generation | QRCoder 1.8.0 | Creates the mobile access QR codes in Sm01. |
| XR core | `WASPer.XR.Core`, .NET 8 | Platform-independent job model and `.wasperxr` import. It intentionally has no Rhino, Grasshopper, browser, Unity, Godot, Stride, or vvvv dependency. |
| Web host | ASP.NET Core/Kestrel, .NET 8 | Serves the viewer and manages HTTP and WebSocket endpoints. |
| 3D rendering | three.js 0.160.0 | WebGL scene, cameras, materials, meshes, instancing, and WebXR integration. |
| Navigation | three.js `OrbitControls` | Desktop and touch orbit, pan, and zoom. |
| Geometry | three.js `BufferGeometryUtils` | Merges browser-side geometries where needed. |
| Charts | Chart.js 4.4.4 | Study dashboard charts. The dashboard is currently hidden in the UI while its implementation remains available. |
| Android AR | Chrome, WebXR Device API, ARCore / Google Play Services for AR | Camera tracking, surface hit testing, and immersive AR presentation. |

The browser currently loads three.js and Chart.js from public CDNs. The first page load therefore requires internet access unless those libraries are later bundled locally. Once cached, behavior depends on the browser cache and should not be assumed for an offline event.

## 8. Android AR Setup

### Requirements

- An ARCore-supported Android device.
- Google Chrome for Android.
- Google Play Services for AR installed and enabled.
- The laptop and phone must be able to communicate directly.
- The viewer server must be running on TCP port `5252`.
- WebXR must see the page as a secure context.

Google's current WebXR requirements are documented at:

- <https://developers.google.com/ar/develop/webxr/requirements>
- <https://developers.google.com/ar/devices>

### A. Create a Reachable Local Network

The tested institutional Wi-Fi used client isolation, so two devices connected to the same Wi-Fi could not reach each other. Windows Mobile Hotspot was the successful workaround.

1. On Windows, open **Settings > Network & internet > Mobile hotspot**.
2. Select the internet connection to share.
3. Set **Share over** to **Wi-Fi** and turn **Mobile hotspot** on.
4. On the Android phone, join the hotspot using the displayed network name and password.
5. In Grasshopper, open Sm01's **Process Viewer (XR)** tab.
6. Click **Refresh** to regenerate the network candidates and QR codes.
7. Use the QR code whose adapter/address belongs to the Mobile Hotspot. Windows commonly uses `192.168.137.1`, but use the address shown by Sm01 rather than assuming this value.

If the phone cannot open the page, verify that Windows treats the hotspot connection as a **Private network** and that Windows Defender Firewall allows inbound TCP traffic on port `5252` for private networks. Do not expose this development server on a public or untrusted network.

### B. Trust the Local HTTP Origin for WebXR

This is probably the remembered "trusted host" step.

WebXR normally requires HTTPS. `localhost` is also trusted, but the phone opens the laptop through a LAN address such as `http://192.168.137.1:5252`, which is plain HTTP and is not considered secure. For local testing, Chrome can treat that exact origin as secure:

1. On the Android phone, open Chrome.
2. Navigate to:

   ```text
   chrome://flags/#unsafely-treat-insecure-origin-as-secure
   ```

3. Enable **Insecure origins treated as secure**.
4. Enter the exact Process Viewer origin shown by Sm01, for example:

   ```text
   http://192.168.137.1:5252
   ```

   Use only the origin: protocol, IP address, and port. Do not add the session query string or another page path.
5. Tap **Relaunch**. If Chrome does not fully restart, close it from Android's recent-apps screen and reopen it.
6. Scan the Sm01 QR code again or reopen the mobile URL.

This flag weakens normal browser security checks for the listed origin. Use it only for a known local development machine and remove the origin or restore the flag to **Default** after testing.

For a more secure development setup, Google's documented alternative is USB Chrome DevTools port forwarding. It maps the laptop viewer to `localhost:5252` on the phone, allowing WebXR without the insecure-origin flag. A future HTTPS deployment would also remove the need for this workaround.

### C. Enter and Use AR

1. Confirm that the normal 3D viewer loads on the phone.
2. Confirm that **Enter AR** appears. If it does not, see Troubleshooting below.
3. Tap **Enter AR** and allow camera access.
4. Move the phone slowly so ARCore can detect a floor, desk, or other suitable plane.
5. When the blue reticle appears, tap the surface to place and lock the model. The reticle then disappears.
6. Use **Calibrate** for true-scale placement, or **Fit to Screen** for a convenient preview size.
7. Use the scale and rotation controls, or pinch with two fingers to resize.
8. To move it, tap **Reposition**, aim at another detected surface, and tap once. The model locks again automatically.
9. Use **Show UI** to expose the Path/Mesh/Context/BBox, playback, and role controls during AR.
10. Tap **Exit AR** to return to the normal browser viewer.

## 9. Troubleshooting

### The Phone Cannot Open the QR Link

- Confirm that the viewer server is running and Sm01 shows a valid scene.
- Click **Refresh** and try the QR code associated with the network the phone actually joined.
- Use Windows Mobile Hotspot if institutional or guest Wi-Fi isolates clients.
- Confirm that TCP port `5252` is allowed through Windows Defender Firewall on private networks.
- Test the URL manually in Chrome, including `http://` and `:5252`.

### The Viewer Loads but Enter AR Is Missing

- Ensure the phone appears in Google's ARCore-supported device list.
- Install or update **Google Play Services for AR**.
- Update Chrome.
- Recheck the exact origin in `chrome://flags/#unsafely-treat-insecure-origin-as-secure` and relaunch Chrome.
- The flag entry must match the QR origin exactly, including the port.
- Confirm camera permission is allowed for Chrome.

### The Scene Does Not Update

- Confirm **Live: On** in Sm01, or click **Push Change** when Live is off.
- Check that the browser URL contains the same Sm01 `session` shown by the Process Viewer.
- Click **Refresh** to repush the current package and playback state.
- Keep the complete `wsp_path` connected directly to Sm01.
- If using Gc05, send only its `sim_par` through Sm05.
- Restart the local viewer process if an older server build is still holding port `5252`.

### The Phone Is Slow

- Keep Mesh off and use Path mode for the lightest visualization.
- If a bead preview is needed, select **Fast** rather than **Continuous**.
- Hide Context when it is not needed.
- Simplify context meshes before Sm05, especially printer assemblies and imported CAD models.
- Avoid retransmitting changing geometry; use the lightweight `sim_par` channel.

### The Object Is Missing or Badly Framed

- Turn on the categories you want to inspect and click **Reset Camera**.
- Reset Camera intentionally ignores categories that are switched off.
- Turn on **BBox** to inspect dimensions and scene extents.
- Check Rhino document units and unexpectedly distant context geometry.

## 10. Security and Current Scope

- The server is intended for a trusted local network and does not currently provide authentication or TLS.
- Anyone who can reach port `5252` may be able to open the viewer while it is running.
- Do not forward port `5252` to the public internet.
- The Android Chrome insecure-origin flag is a development workaround, not a deployment strategy.
- AR support depends on the Android device, Chrome version, ARCore support, and browser implementation.
- Current work focuses on Android browser AR. VR/headset mode and live machine telemetry are future extensions.
- vvvv Gamma and VL.Rhino.3dm are not dependencies of this browser viewer.

## 11. Developer File Map

- Sm01 Process Viewer UI: `01_WASPer_3DP/Components/1.2_Studies/Sm01/WASPer_Sm01ProcessViewerTab.cs`
- Sm01 live/export controller: `01_WASPer_3DP/Components/1.2_Studies/Sm01/WASPer_Sm01ProcessViewerController.cs`
- Mobile QR discovery: `01_WASPer_3DP/Components/1.2_Studies/Sm01/WASPer_Sm01MobileAccess.cs`
- Sm05 scene bundle: `01_WASPer_3DP/Components/1.2_Studies/wsp_Sm05_XR Scene Params.cs`
- XR platform-neutral model/import: `04_WASPer_3DP.XR/05_Core/WASPer.XR.Core`
- Web server: `04_WASPer_3DP.XR/07_WebViewer/WASPer.XR.WebViewer/Program.cs`
- Live WebSocket hub: `04_WASPer_3DP.XR/07_WebViewer/WASPer.XR.WebViewer/LiveJobHub.cs`
- Browser interface: `04_WASPer_3DP.XR/07_WebViewer/WASPer.XR.WebViewer/wwwroot/index.html`
- Rendering and AR logic: `04_WASPer_3DP.XR/07_WebViewer/WASPer.XR.WebViewer/wwwroot/js/viewer.js`
- Browser live-link client: `04_WASPer_3DP.XR/07_WebViewer/WASPer.XR.WebViewer/wwwroot/js/live-link.js`
- Launcher: `04_WASPer_3DP.XR/07_WebViewer/scripts/Start-WASPerWebViewer.ps1`
- Standalone packager: `04_WASPer_3DP.XR/07_WebViewer/scripts/Package-StandaloneViewer.ps1`

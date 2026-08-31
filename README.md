# WASPer_3DP

**WASPer_3DP** is a Rhino 8 / Grasshopper plugin for Design for Additive Manufacturing (DfAM). It brings computational design, geometry generation, fabrication-path preparation, process visualization, and early-stage performance feedback into one parametric workflow for 3D-printed building components.

The project began around large-scale Liquid Deposition Modeling (LDM), clay printing, and WASP 40100-style workflows. It has since grown into a broader research toolkit for implicit modelling, infill generation, planar and non-planar slicing, `wsp_path` toolpaths, Marlin G-code, robotic fabrication, material workflows, and design studies.

WASPer_3DP is an independent research project developed at **Politecnico di Torino** and currently enhanced in collaboration with **ACTech Hub at the University of Minho**. It is not an official WASP product.

Current public version: **v1.0.5.8**

- [Download on food4Rhino](https://www.food4rhino.com/en/app/wasper3dp)
- [YouTube](https://www.youtube.com/@WASPer_3DP)
- [LinkedIn](https://www.linkedin.com/in/juan-diego-vargas-vel/)
- [Instagram](https://www.instagram.com/wasper_3dp/)

## Requirements

- Rhino 8 for Windows
- Grasshopper
- [.NET 8 ASP.NET Core Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) only for Sm01's browser-based Process Viewer (normally Windows x64)
- The [Robots](https://github.com/visose/Robots) Grasshopper plugin only when using the optional `5.1_Robot Gcode` components

The rest of Sm01 and WASPer continue to work when the ASP.NET Core Runtime is missing; only the local browser viewer is unavailable. Sm01 detects this condition and highlights its Process Viewer installation guide.

WASPer_3DP is under active development. Component interfaces may evolve, and some tools may contain bugs or edge cases. Feedback, issue reports, and examples of real use are very welcome.

## Installation

### Rhino Package Manager

1. Open Rhino 8.
2. Run the `PackageManager` command.
3. Search for `WASPer_3DP`.
4. Install the latest version and restart Rhino.

The Package Manager distribution includes the main `.gha`, supporting libraries, active user objects, material data, examples, and the local web Process Viewer.

### Manual installation

1. Download the ZIP package from food4Rhino.
2. Extract it before installation.
3. If Windows has blocked the downloaded assemblies, open each file's Properties and select **Unblock**.
4. Copy the contents of `WASPer_3DP_lib` into a dedicated folder inside the Grasshopper Libraries folder.
5. Copy the contents of `WASPer_3DP_ghuser` into the Grasshopper UserObjects folder.
6. Restart Rhino and Grasshopper.

The relevant Grasshopper folders can be opened from Grasshopper through `File > Special Folders > Components Folder` and `File > Special Folders > User Object Folder`.

## Grasshopper organization

WASPer is organized into two Grasshopper tabs. The split keeps design and fabrication tools together while giving performance, study, and characterization workflows their own space.

| Tab | Category | Purpose | Availability |
| --- | --- | --- | --- |
| `WASPer_3DP` | `0.0_WASPer_3DP` | WASPet companion, examples, plugin information, display controls, and workflow map | Public (this repo) |
| `WASPer_3DP` | `1.0_Utils` | Data exchange, viewport capture, document helpers, and general utilities | Public (this repo) |
| `WASPer_3DP` | `2.0_Geometry` | Geometry preparation, projection, mesh tools, bitmap conversion, and mesh painting | Public (this repo) |
| `WASPer_3DP` | `2.1_Facades` | Facade panelization, joints, weighted layouts, and facade-oriented geometry | Public (this repo) |
| `WASPer_3DP` | `2.2_Fields` | Two-dimensional scalar and distance-field creation and editing | Public (this repo) |
| `WASPer_3DP` | `2.3_Fields_3D` | Volumetric fields, booleans, offsets, shells, meshing, and field conversion | Public (this repo) |
| `WASPer_3DP` | `3.0_Slicing` | Planar, non-planar, surface-aware, trimming, orientation, and visualization workflows | Public (this repo) |
| `WASPer_3DP` | `3.1_Infills` | 2D/3D infills, conformal paths, TPMS, cellular systems, and SDF-based infills | Public (this repo) |
| `WASPer_3DP` | `4.0_Print Paths` | `wsp_path` construction, roles, process values, optimization, and visualization | Public (this repo) |
| `WASPer_3DP` | `4.1_Printability` | Geometric printability checks and early-stage fabrication-risk proxies | Public (this repo) |
| `WASPer_3DP` | `5.0_Gcode` | Marlin G-code generation, parsing, saving, and process simulation | Public (this repo) |
| `WASPer_3DP.Robots` | `5.1_Robot Gcode` | Optional Robots-compatible targets, KUKA utilities, and post-processing | Public (this repo), optional assembly |
| `WASPerformance` | `1.1_Data Vis` | Native charts and multivariate visualization in Rhino/Grasshopper | Public (this repo) |
| `WASPerformance` | `1.2_Studies` | Study Manager, KPI wrappers, design iteration, export, dashboards, and reports | Public (this repo) |
| `WASPerformance` | `6_Grids of Points` | Structured point grids used by analysis and geometry workflows | Public (this repo) |
| `WASPerformance` | `7_Material Library` | Material, layer, gas, and 3D-printing property records | Public (this repo) |
| `WASPerformance` | `8_Morphology` | Porosity, tortuosity, surface area, printability proxies, shrinkage, and pore-flow metrics | Not in this repo yet (Phase 2) |
| `WASPerformance` | `9.0_BCs` | Boundary conditions and temperature/irradiance/weather series generators | Not in this repo yet (Phase 2) |
| `WASPerformance` | `9.1_Heat Transfer` | Analytical and numerical thermal solvers, U-value and comfort calculations | Not in this repo yet (Phase 2) |
| `WASPerformance` | `9.2_Moisture_Buffering` | Moisture buffering and room-scale diffusion solvers | Not in this repo yet (Phase 2) |
| `WASPerformance` | `9.3_Structural` | Compression, buckling, and structural proxy checks | Not in this repo yet (Phase 2) |

This is the Phase 1 public source release. Research categories `8_Morphology` through `9.3_Structural`, together with their private-only shared implementation and resources, are intentionally withheld in a separate local `WASPer_3DP.Performance` project while their methods and interfaces are developed and validated. They are planned for a later open-source phase and are not available functionality in this repository.

## Main functionality

### Geometry, images, and interactive fields

WASPer includes tools for preparing solids and meshes, projecting geometry, creating facade systems, translating images and colored meshes into scalar data, and building two- and three-dimensional fields. Field operations include selected booleans, offsets, shells, transformations, contouring, meshing, and exchange between WASPer and Isopod field workflows.

Interactive painting tools make it possible to draw values into a 2D field or directly onto mesh vertices. Painted values can drive local mesh displacement, texture intensity, openings, field operations, and other customization workflows without rebuilding every local variation as a long parametric definition.

### Infill generation and slicing

The plugin supports a range of infill strategies for architectural additive manufacturing, including:

- S-pattern and spiral infills
- Conformal and surface-aware paths
- TPMS families
- Polyhedral and cellular systems
- Brick-like cavities and partition strategies
- User-defined and signed-distance-field-based infills
- Planar, non-planar, and surface-aware slicing
- Re-trimming, orientation, and visualization of existing paths

These tools are intended to keep geometric generation connected to the constraints of layer-by-layer fabrication.

### WASPer path (`wsp_path`)

`wsp_path` is the central fabrication-path data model used by newer WASPer workflows. Instead of passing only curves, it keeps path geometry and process context together as one reusable Grasshopper object.

A `wsp_path` can store:

- Printing and travel curves organized by layer and fabrication sequence
- Point-level layer height, bead width, flow, velocity, and extrusion values
- Local planes or fabrication targets, including information useful for non-planar paths
- Path roles: `0 Undefined`, `1 Shell`, `2 Infill`, `3 Partition`, and `4 Support`
- Motion and playback information
- Metadata, units, summaries, and Study Manager KPI data

Keeping these values synchronized makes it safer to optimize, trim, simplify, simulate, compare, visualize, and export a fabrication path without losing its intended meaning.

```text
geometry / fields / infill
           |
           v
 slicing or path generation
           |
           v
   construct / enrich wsp_path
           |
           +--> path optimization
           +--> printability checks
           +--> Study Manager
           +--> G-code generation
           +--> robotic fabrication
           +--> Process Viewer (XR/AR)
```

### Path preparation, optimization, and G-code

Printing-path components support path construction from curves, role assignment, transformations, ordering, point reduction, local flow estimation, velocity utilities, fuzzy-skin and fuzzy-pocket effects, proximity-based process adjustment, and role-aware visualization.

The `5.0_Gcode` category provides Marlin-oriented G-code generation, parsing, saving, inspection, and fabrication playback for LDM/FDM workflows. Current machine-code workflows are primarily focused on 3-axis Delta and Cartesian printers.

Generated code must always be reviewed, simulated, and validated for the target machine, controller, material, tool, and safety setup before fabrication.

### Robotic fabrication

Generated `wsp_path` toolpaths can also support robotic fabrication because they retain planes or targets, layer heights, roles, process values, and metadata. This information can be used alongside robotic tools such as Robots or KUKA|prc.

WASPer ships a separate optional `WASPer_3DP.Robots.gha` assembly for the `5.1_Robot Gcode` category. Its components use the open-source Robots plugin to:

- Convert `wsp_path` data into native Robots `CartesianTarget` objects
- Add Cartesian offsets and home moves
- Rotate the TCP at selected targets
- Define KUKA tools and bases
- Merge KRL output
- Create a WASPer-oriented KUKA post-processor

Robotic components require the Robots plugin and still require machine-specific calibration, reachability and collision checks, tool definition, controller validation, and physical safety procedures.

### Study Manager and KPI workflow

`wsp_Sm01_WASPer Study Manager` provides a dedicated interface for comparing design alternatives without distributing the study logic across many Grasshopper components.

The Study Manager can:

- Link Grasshopper number sliders and run Cartesian parameter studies
- Receive structured KPI sets from WASPer components or custom KPI wrappers
- Capture `wsp_path`, KPI, G-code, image, and XR data per iteration
- Compare alternatives through tables, plots, Pareto views, heatmaps, and parallel coordinates
- Export CSV, XLSX, JSON, images, G-code, and PDF reports
- Restore and review previous studies through a study library
- Pass selected iterations to the Process Viewer

The KPI exchange layer keeps names, units, values, source identity, and grouping together so the manager can compare values consistently across iterations.

### Data visualization

WASPerformance includes native Grasshopper/Rhino components for scatter plots, bar charts, box plots, histograms and density views, heatmaps and correlation matrices, Pareto fronts, 3D graphs, and parallel coordinates. These visualizations remain inside the same parametric environment as the generated geometry and study data.

### Materials and performance data

Material utilities provide reusable records for opaque materials, gas layers, 3D-printing properties, equivalent air conductivity, water-content calculations, vapor-transport properties, and layered assemblies. These typed records can travel through fabrication and study workflows without exposing the Phase 2 solvers that may consume them later.

### WASPet companion

The WASPet is a floating Grasshopper companion that provides quick access to built-in examples and starter workflows, plugin information and structure, the `wsp_path` guide, global display settings and path palettes, workflow maps in Rhino, and project links.

### Web Process Viewer and XR/AR

The Study Manager can package a solved fabrication job and open it in a local browser-based Process Viewer. The viewer supports path and deposited-bead display, playback, contextual machine or scene geometry, mobile access through a local QR link, and experimental WebXR/AR inspection.

The Process Viewer is a visualization and communication tool. It is not a metrology, collision-safety, machine-control, or as-built verification system. AR scale and alignment, browser performance, device compatibility, and live synchronization depend on the receiving device and local network. Sharing is currently limited to `localhost` or the local network; WASPer does not provide an internet-hosted public sharing service.

## Typical workflow

1. Prepare or generate the component geometry.
2. Create a field, facade system, or infill strategy.
3. Slice the geometry and organize shell, infill, partition, and support paths.
4. Construct a `wsp_path` and assign fabrication parameters.
5. Optimize and inspect the printing path.
6. Evaluate relevant geometric, fabrication, material, or process KPIs.
7. Compare alternatives with the Study Manager when needed.
8. Simulate the process and review G-code or robot targets.
9. Validate the complete machine workflow before fabrication.

## Examples

The maintained examples distributed with WASPer_3DP live under the main Visual Studio project:

```text
01_WASPer_3DP/Resources/Examples/
|-- 3DPv2_NON_planar_260807.gh
|-- 3DPv2_Planar1_Slicing_260807.gh
|-- 3DPv2_Planar2_InfillsCrvs_260807.gh
|-- 3DPv2_Planar2_InfillsCrvs_Texture_260807.gh
|-- Implicit_260807.gh
`-- Implicit_Isopod_to_WASPer_260807.gh
```

The project file discovers the `.gh` and `.ghx` definitions in this directory, lists them in the generated package manifest, and copies them into the release `examples` folder. The WASPet can insert these packaged examples directly into the active Grasshopper document.

The sibling workspace directory `../03_Examples/` is a local working area for larger studies, generated simulations, images, G-code, Rhino models, and temporary research definitions. It is outside Git and is not the authoritative source for packaged examples.

## Repository structure

The Git repository contains the current plugin source, maintained package examples, and companion projects. Published packages, historical releases, backups, and larger working studies are kept outside the tracked source tree.

```text
WASPer_3DP repository/
|-- AGENTS.md                    repository and backup policy
|-- 00_OpenSource/               Phase 1 boundary manifest and validator
|-- 00_Release/                  automatic release-packaging utility
|-- 01_WASPer_3DP/               main Rhino/Grasshopper plugin project
|   `-- Resources/Examples/      maintained examples included in packages
|-- 02_WASPer_3DP.AI/            internal Grasshopper inspection/bridge library
|-- 03_WASPer_3DP.Robots/        optional Robots-dependent assembly
|   `-- Components/
|       `-- 5.1_Robot Gcode/     active robot targets, KUKA tools, and post-processing
|-- 04_WASPer_3DP.XR/             tracked LiveLink, XR core, and WebViewer build dependencies
|-- 01_WASPer_3DP_Structure.txt
|-- 02_WASPer_3DP.AI_Structure.txt
|-- 03_WASPer_3DP.Robots_Structure.txt
`-- README.md
```

The surrounding development workspace may additionally contain `00_Backups/`, `01_Icons_development/`, `02_UserObjects_archive/`, `03_Examples/`, `04_Kuka/`, `02_Published/`, implementation-plan folders, and directories beginning with `Archive` inside the projects. These are local working areas excluded from Git and release packages. Their presence does not mean they are part of the public source or installed plugin. Experiments may be retained under the ignored repository folder `00_Trials/`; backups and published packages should remain outside the repository.

### Structure maps

The detailed source inventories are maintained as text maps beside the Visual Studio projects:

- [`01_WASPer_3DP_Structure.txt`](01_WASPer_3DP_Structure.txt): main plugin components, resources, tabs, and packaging rules
- [`02_WASPer_3DP.AI_Structure.txt`](02_WASPer_3DP.AI_Structure.txt): Grasshopper inspection and bridge support library
- [`03_WASPer_3DP.Robots_Structure.txt`](03_WASPer_3DP.Robots_Structure.txt): optional Robots integration assembly
- `04_WASPer_3DP.XR_Structure.txt`: local map for the broader experimental XR workspace; Phase 1 tracks only the LiveLink protocol, XR core models, and bundled WebViewer required by the public build

The detailed `00_WASPer_ChangeLog.txt` is a local development record and is intentionally not published through Git. Public release information should be summarized in the README and release/package notes.

### Main solution architecture

| Project | Role |
| --- | --- |
| `WASPer_3DP` | Main `.gha`, components, palettes, resources, examples, and packaging rules |
| `WASPer_3DP.AI` | Internal canvas inspection and controlled Grasshopper bridge used by selected utilities |
| `WASPer_3DP.Robots` | Optional `.gha` that compiles the Robots-dependent `5.1_Robot Gcode` components |
| `WASPer_3DP.XR` | Tracked Phase 1 LiveLink protocol, platform-neutral XR data, and local web viewer; unrelated experimental viewers remain local |

The main component source lives in `01_WASPer_3DP/Components/`. Shared data models and services live under `Components/Shared`. The Phase 1 boundary is defined in [`00_OpenSource/phase1-boundary.json`](00_OpenSource/phase1-boundary.json) and can be checked with `00_OpenSource/Test-Phase1Boundary.ps1`. Maintainers synchronize this private development tree with the separate public repository through [`00_OpenSource/Sync-PublicSnapshot.ps1`](00_OpenSource/README.md), which also protects merged public pull requests from being overwritten. Local historical snapshots under directories beginning with `Archive` are excluded from compilation, Git, and release packages.

## Building from source

Open:

```text
01_WASPer_3DP/WASPer_3DP.sln
```

The main project targets `net8.0-windows` and uses the Rhino 8 / Grasshopper 8.21 SDK baseline. A Release build produces the main plugin and supporting assemblies in the project's `bin/Release/net8.0-windows` folder. The build also generates the Yak manifest, copies active user objects, copies the maintained definitions from `Resources/Examples`, includes material files, and publishes the bundled local WebViewer.

The optional Robots project references `Robots.Rhino` and builds `WASPer_3DP.Robots.gha` beside the core plugin. Debug and Release configurations write to their corresponding output trees.

Build output, `bin`, `obj`, local backups, generated packages, and experimental viewer payloads are intentionally ignored by Git.

### Automatic release packages

The public four-part version in `01_WASPer_3DP/Components/0.0_WASPer_3DP/manifest.header.yml` is the single source of truth. The shared `00_Release/WASPer.Version.props` imports that value into the core and Robots assembly metadata, and the generated Yak manifest and release folders use the same value.

For an intentional publication build, open `00_Release/Open-WASPer-ReleaseBuilder.cmd`. The Release Builder presents one version field and a publication-folder picker, updates the shared version, and builds the complete release. The selected destination is stored only on that computer in `%LOCALAPPDATA%/WASPer_3DP/release-settings.json`; absolute user paths are never committed.

Building the complete solution directly in `Release` configuration also builds the Robots companion and runs `00_Release/Stage-ReleasePackages.ps1`. It uses the locally saved destination. On a new clone with no local setting, packages default to `01_WASPer_3DP/bin/Release/Published/<major.minor.patch>/`, beside the standard Release output and inside a Git-ignored directory. Both workflows create the established layouts:

- `v<version>_<YYMMDD>_net8.0`: Package Manager layout, including the generated `.yak` archive when Rhino's Yak executable is available.
- `v<version>_<YYMMDD>_f4f`: food4Rhino manual-installation layout with separate `WASPer_3DP_lib` and `WASPer_3DP_ghuser` folders.

Rebuilding the same version on the same date refreshes those two folders. Debug builds never stage publication folders. To perform a Release build without staging packages, set the MSBuild property `StageReleasePackages=false`.

## Research status and limitations

WASPer_3DP is a research and development framework. In particular:

- Printability and deformation components are simplified proxies unless explicitly documented otherwise.
- Material behavior, fresh-state deformation, and rheology require suitable input data and experimental validation.
- Categories `8_Morphology` through `9.3_Structural` are not part of the Phase 1 public source or package.
- G-code, robot targets, and post-processed files are not guaranteed to be safe or correct for a particular machine.
- XR/AR output is intended for visualization, not measurement or machine control.

Always inspect outputs and validate them against the relevant material tests, standards, machine documentation, controller configuration, and safety procedures.

## Feedback and collaboration

Bug reports, workflow examples, validation data, and suggestions are welcome. When reporting a problem, please include the WASPer_3DP version, Rhino/Grasshopper version, a minimal definition or reproducible sequence, the complete warning or error, and relevant machine/material context.

Use the repository's [Issues](https://github.com/jdvargas12/WASPer_3DP/issues) page or contact [Juan Diego Vargas](https://www.linkedin.com/in/juan-diego-vargas-vel/). See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request, and please follow the [Code of Conduct](CODE_OF_CONDUCT.md). Security issues should go through [SECURITY.md](SECURITY.md) instead of a public issue.

## License

WASPer_3DP is released under the [MIT License](LICENSE). Third-party dependencies and the Rhino/Grasshopper SDK are covered separately in [NOTICE.md](NOTICE.md).

Categories `8_Morphology` through `9.3_Structural` are not part of this repository yet and are not covered by this release; see [Grasshopper organization](#grasshopper-organization).

## Acknowledgements

WASPer_3DP has been developed through PhD research at Politecnico di Torino and further developed in collaboration with ACTech Hub at the University of Minho. The project builds on the Rhino/Grasshopper ecosystem and interacts with external tools such as Robots, KUKA|prc, Isopod, and other research and fabrication workflows. Those projects remain the work of their respective authors.

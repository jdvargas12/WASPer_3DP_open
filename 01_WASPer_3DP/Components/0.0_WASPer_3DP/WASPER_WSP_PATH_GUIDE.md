# WASPer Path (`wsp_path`) Guide

Last updated: 2026-08-21

## 1. Overview

`wsp_path` is the main fabrication-path container used by WASPer_3DP. It packages printing curves together with the information needed to understand how those curves should be interpreted, visualized, optimized, simulated, exported, and fabricated.

Instead of moving only raw curves from one component to another, a `wsp_path` keeps the geometric path and its process context together. This makes it easier to build workflows where slicing, path optimization, print simulation, G-code generation, robotic fabrication, study management, and XR/AR visualization all operate on the same fabrication object.

## 2. What It Stores

A `wsp_path` can carry:

- Printing and travel curves organized by branch, layer, and fabrication sequence.
- Point-level values such as layer height, bead width, flow, velocity, extrusion state, and other process attributes.
- Path roles, such as Shell, Infill, Partition, Support, and Undefined.
- Motion information used for previewing, simulation, and playback.
- Metadata and summaries used by downstream components and the Study Manager.
- Visualization settings and role-aware color logic used by WASPer previews and the XR/AR Process Viewer.

The exact contents depend on the upstream components. A path coming directly from slicing may be relatively simple, while a path that has passed through Gc03, Gc05, or related utilities can include richer process and motion data.

## 3. Why It Matters

`wsp_path` is intended to reduce the fragility of long Grasshopper definitions. Curves, flow values, roles, and printing parameters often need to stay synchronized. If each of those values is moved as a separate tree, it becomes easy to lose alignment after trimming, sorting, simplifying, or optimizing a path.

By carrying the path as a structured object, WASPer_3DP can preserve the relationship between:

- Geometry and fabrication order.
- Layers and point-level process values.
- Path roles and visualization colors.
- Optimized paths and their original fabrication meaning.
- Study iterations and the path data used to evaluate them.
- Browser/XR playback and the actual fabrication sequence.

In practice, this makes `wsp_path` the spine of many newer WASPer workflows.

## 4. Typical Workflow

A common path flow is:

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
        +--> printability and process checks
        +--> Study Manager
        +--> Process Viewer (XR/AR)
        +--> G-code export
        +--> robot / printer-oriented workflows
```

The recommended approach is to keep one complete and stable `wsp_path` as the main fabrication object, then derive previews, reports, simulations, or exports from it.

## 5. Roles

Path roles allow different parts of the print to be identified and handled differently. WASPer currently uses:

```text
0 = Undefined
1 = Shell
2 = Infill
3 = Partition
4 = Support
```

Roles are useful for preview colors, visibility toggles, statistics, fabrication checks, and future process-specific behavior. For example, Shell and Infill paths can be visualized separately in the WASPet path palette or in the web Process Viewer.

## 6. Optimization and Simulation

Because `wsp_path` keeps geometry and process data together, downstream components can modify or analyze the path while preserving its meaning.

Examples include:

- Reducing points based on curvature or proximity while keeping path attributes aligned.
- Estimating local flow and velocity-related values.
- Comparing alternatives in Sm01 Study Manager.
- Simulating fabrication playback in Gc05 and in the web Process Viewer.
- Sending only lightweight playback-state updates when the path structure does not change.

For best performance, especially with the Process Viewer, connect Sm01 to the complete stable `wsp_path` and drive live playback through a parameter such as Sm05 `sim_par` instead of repeatedly sending partial paths.

## 7. G-code and Robotic Fabrication

`wsp_path` supports both conventional printer-oriented and robot-oriented workflows.

For 3-axis workflows, it can feed Marlin-oriented G-code generation, parsing, visualization, and playback tools. For robotic fabrication, WASPer_3DP includes a dedicated **5.1_Robot Gcode** category with components compatible with the open-source Robots plugin. These components can translate a `wsp_path` into native Robots `CartesianTarget` objects, add target offsets or home moves, assign tool/frame information, merge KRL output, and support KUKA-oriented post-processing while keeping the WASPer fabrication path as the upstream source.

The goal is not to hide machine-specific setup. Calibration, post-processing, tool definition, external axes, safety validation, and robot-controller requirements still need to be handled carefully in the appropriate machine environment. `wsp_path` provides a consistent fabrication data model that can support those translations.

## 8. Study Manager and XR/AR

Sm01 Study Manager uses `wsp_path` as the main fabrication object for study capture, comparison, KPI extraction, report generation, G-code capture, and Process Viewer export.

The XR/AR Process Viewer also uses the path structure to reconstruct fabrication order, layer timing, roles, printed/pending states, and path/mesh playback. This is why a complete `wsp_path` is important: the viewer needs the whole fabrication sequence to show the process correctly.

## 9. Practical Notes

- Keep the path stable when using live XR/AR preview; update only simulation parameters when possible.
- Use role assignment when different path families need different colors, visibility, or future behavior.
- Prefer `wsp_path` outputs over loose curve/value trees when moving into optimization, studies, or visualization.
- Check summaries and deconstruction tools when debugging path attributes.
- Treat machine-code output as fabrication-critical: preview first, then validate carefully before printing.

## 10. Limitations

`wsp_path` is a structured data model, not a replacement for physical process validation. It can organize fabrication data, support simulation, and improve consistency between components, but it does not by itself guarantee printability, collision safety, machine calibration, material behavior, or successful fabrication.

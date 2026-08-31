---
name: wasper-eto-ui
description: Implement, migrate, debug, or review WASPer-owned Eto.Forms interfaces for Rhino and Grasshopper, especially modeless windows, layouts, drag/drop, bitmap rendering, and Windows/macOS compatibility. Do not use for host-owned Grasshopper canvas drawing or unrelated backend work.
---

# WASPer Eto UI

Use the repository's `AGENTS.md` as the authority. Apply this workflow when touching a WASPer-owned window or dialog.

## Before Editing

1. Identify the owning Rhino document, the strong reference that keeps the form alive, and the code that clears it on `Closed`.
2. Separate view code from workflow, persistence, geometry, and study logic. Preserve existing events and serialization.
3. Inspect the complete control tree and event lifecycle before changing one visible symptom.

## Implementation Rules

- For document-related modeless tools, set `Owner = RhinoEtoApp.MainWindowForDocument(document)`, call `UseRhinoStyle()`, keep one strong reference, and reuse the open instance. Canvas focus must never hide or dispose it.
- Use logical Eto dimensions. Keep buttons, labels, numeric fields, and compact cards natural or constrained; expand only deliberate content and spacer cells. Set `TableCell` scaling and `TableRow.ScaleHeight` explicitly.
- Use `Splitter` for user-adjustable regions. Bound long wrapped text before it can determine a window's preferred width.
- After rebuilding an attached `DynamicLayout`, call `Create()`. Use non-scaling rows for compact repeated cards and recompute wrapping from the form's logical client width.
- Never mount one control under two visual parents. Dispose replaced Eto images and transferred `System.Drawing` sources.
- For drag/drop, use real row or cell hit testing such as `GridView.GetCellAt()`, show destination feedback, and retain an accessible button alternative.
- Render bitmap charts at the host size. Draw exact-size images 1:1; filter only temporary scaling and keep display-to-render coordinate transforms explicit for hit testing.
- Keep UI mutations on the UI thread. Use `UITimer` for polling or debounce work, stop it on close, and guard delayed callbacks against closed forms.
- Isolate and platform-gate Windows-only APIs. Do not use WinForms as the implementation framework for a WASPer-owned interface.

## Verification

Build the affected project graph, then clearly separate build results from runtime acceptance. Ask the maintainer to verify in Rhino:

- click the Grasshopper canvas and return to the modeless window;
- open, close, reopen, minimize, restore, and resize through minimum and large sizes;
- exercise context menus, drag/drop, keyboard/button alternatives, and dynamic content refresh;
- switch Rhino/Grasshopper documents and test high-DPI or Retina layout;
- shut down Rhino and confirm no form, timer, or event handler remains.

Record any untested Windows or macOS behavior. Do not remove a legacy UI until the migrated workflow passes its runtime checklist.

# WASPer_3DP Repository Instructions

These instructions apply to the `00_Visual_Studio` Git repository and all projects below it.

## Backup policy

- Use the workspace folder `../../00_Backups/` as the only location for local safety backups in the maintainer's standard layout.
- Do not create `00_Backup`, `*Backups*`, or conversion-backup folders elsewhere in the repository.
- Directories beginning with `Archive` inside the projects and the sibling `../02_UserObjects_archive/` are local source-history collections, not project backups. Keep them in their existing locations, but keep them ignored by Git and out of release packages.
- Create a backup only when requested or before a substantial, risky, or difficult-to-reverse change. Git remains the normal history mechanism for routine edits.
- Name new snapshots `YYYYMMDD_HHMM_pre_<short-purpose>` using a concise ASCII description.
- Preserve repository-relative paths inside each snapshot so files can be restored unambiguously.
- Add a `BACKUP_INFO.md` to each new snapshot with the creation date, reason, source paths, intended change, and restore steps.
- Never overwrite an existing snapshot. If a name already exists, add a clear origin or sequence suffix.
- Never edit files inside a completed snapshot. Restore selected files into the working tree, then make changes there.
- `../../00_Backups/` is outside the repository. On other clones, use an equivalent external backup location; do not move or copy backups into tracked source.

## Documentation policy

- Keep `README.md` focused on the public project, installation, capabilities, repository layout, and contributor-facing information.
- Keep the project structure maps in `*_Structure.txt` synchronized when projects, components, resources, or packaging rules change.
- Keep the detailed development changelog at `00_WASPer_ChangeLog.txt` as a local working record. It is intentionally ignored by Git.
- Published packages and historical release folders belong in the locally configured publication directory, not in tracked source. On an unconfigured clone, generated packages default to `01_WASPer_3DP/bin/Release/Published/`, which is ignored by Git.
- The sibling `../03_Examples/` folder is a local working area outside Git. Maintained examples intended for packaging belong in `01_WASPer_3DP/Resources/Examples/`.
- Keep local experiments, validation mirrors, session captures, and disposable prototypes under `00_Trials/`. This folder is local-only: do not force-add it to Git or include it in release packages.

## Code Structure

- Begin component files with the component filename, WASPer subcategory, and a one-line purpose. Keep namespaces aligned with the owning project and component category.
- Name Grasshopper components `wsp_<category code><two-digit index>_<descriptive title>`, following the existing category prefixes such as `Pp`, `Gc`, and `Sm`. Use the next available index; never renumber released components merely to close a gap.
- Preserve every released `ComponentGuid`. Changes to an existing input/output contract require an obsolete compatibility component and an `IGH_UpgradeObject`, as described in `CONTRIBUTING.md`.
- Treat the four-part version in `manifest.header.yml` as the only release-version source. Do not hard-code independent versions in components; derive displayed component versions from the executing assembly.
- Register inputs before outputs and keep their index order stable. Use `lower_snake_case` for parameter names and established short nicknames such as `wsp_path`, `sim_par`, and `geo`.
- Choose `GH_ParamAccess.item`, `list`, or `tree` to match the actual data shape. Mark optional inputs explicitly, provide defaults only when they are behaviorally meaningful, and document units, ranges, matching/broadcast behavior, and disconnected-input behavior.
- Prefer typed Grasshopper parameters where a suitable type exists. Use generic parameters for packed WASPer objects or genuinely polymorphic data, and validate/cast them at the component boundary.
- Preserve Grasshopper tree paths and branch ordering unless the component explicitly documents flattening, matching, or reindexing. Keep computational and data-model logic outside parameter-registration and UI code.
- Organize component classes consistently: constants and fields, constructor, GUID/exposure/icon, parameter registration, `SolveInstance`, persistence/menu hooks, then focused helper methods.
- In generated or substantially edited code, introduce classes, methods, and meaningful logical sections with a short `// title or purpose` phrase. Use these as concise reading aids, not comments on every line or self-explanatory statement.
- Keep methods focused, use descriptive names, and return structured results instead of relying on hidden shared state. Surface recoverable user problems through clear Grasshopper runtime messages; do not silently swallow failures that affect outputs.

## Temporary build outputs

- Isolated build-output folders such as `bin/CodexCheck`, `bin/CodexFinal`, or other `bin/Codex*` directories may be created when they are useful for validating a change without disturbing the normal build outputs.
- Delete these temporary verification folders as soon as the relevant build, test, or inspection has been completed and validated. Do not leave accumulated `Codex*` folders in project `bin` directories.
- Keep temporary build outputs excluded from Git. Never package or publish them as part of WASPer_3DP.
- `bin/Debug` and `bin/Release` are the standard Visual Studio outputs. They may be cleaned and regenerated through the normal build workflow.

## Release packaging

- Keep Visual Studio release automation under `00_Release/` and keep project-specific helper scripts inside the project they support.
- Treat `01_WASPer_3DP/Components/0.0_WASPer_3DP/manifest.header.yml` as the single source of truth for the public four-part WASPer version. `00_Release/WASPer.Version.props` reads it for the core and Robots assembly metadata; do not add independent version numbers to those project files.
- Use `00_Release/Open-WASPer-ReleaseBuilder.cmd` for an intentional publication build. Its dialog updates the shared version, remembers the selected publication root in `%LOCALAPPDATA%/WASPer_3DP/release-settings.json`, and starts the complete Release build.
- A successful `Release` build of `WASPer_3DP.Robots` runs `00_Release/Stage-ReleasePackages.ps1` after the core and Robots assemblies are available in the shared output folder.
- The staging script resolves the publication root in this order: explicit `-PublishedRoot`, `WASPER_PUBLISHED_ROOT`, the local settings file, then the portable default `01_WASPer_3DP/bin/Release/Published`. It refreshes both the Package Manager and food4Rhino folder layouts there without hard-coded user paths.
- Debug builds must not create publication folders. For a binaries-only Release build, pass `StageReleasePackages=false`.
- Keep the script's default target framework and expected package layouts synchronized with the project and publication structure.

## Open-source publication boundary

- Phase 1 publishes component categories 0 through 7. Categories `8_Morphology`, `9.0_BCs`, `9.1_Heat Transfer`, `9.2_Moisture_Buffering`, and `9.3_Structural` remain private until Phase 2.
- Treat `00_OpenSource/phase1-boundary.json` as the machine-readable boundary. Update it deliberately when public ownership changes; do not weaken it merely to make validation pass.
- Run `00_OpenSource/Test-Phase1Boundary.ps1` before a public commit, package, or repository export. The check must pass together with a clean build.
- Use `00_OpenSource/Sync-PublicSnapshot.ps1` to synchronize the private source with the separate `../00_WASPer_3DP_open` checkout. It must never commit, pull, or push automatically.
- Treat merged public pull requests as upstream changes: pull them in the public checkout, import them with `-Mode ImportPublicChanges`, review and commit them privately, then record the reconciled public HEAD before the next export.
- Withhold private-only source, icons, examples, scripts, tests, plans, and documentation. A normal Git deletion does not sanitize prior history; create the eventual public repository from a validated clean snapshot.
- Public shared exceptions are dependency-driven. Keep `WASPer_GridTools.cs`, `WASPer_MaterialTypes.cs`, and `WASPer_MoistureTransportTypes.cs` public while their category 0-7 consumers exist.
- Do not recombine public grid helpers with the private sparse solver. Mixed public/private source files make the publication boundary fragile.
- Preserve all existing component GUIDs across both phases so definitions created with either release remain resolvable when Phase 2 is published.

## Cross-platform UI policy

- Treat Windows and macOS as supported UI targets. New or substantially changed WASPer-owned windows, dialogs, menus, pickers, and settings panels must use Eto.Forms.
- Use Grasshopper canvas APIs for canvas overlays, drawing, hit testing, dragging, and interactions. Do not add transparent WASPer-owned WinForms controls to `GH_Canvas.Controls`.
- Grasshopper host APIs may expose WinForms types such as `ToolStripButton`, `MouseEventArgs`, or `Keys`. Keep those references at the host boundary and do not use them as the implementation framework for WASPer-owned UI.
- Parent Eto windows with `RhinoEtoApp.MainWindowForDocument(document)` when a Rhino document is available, call `UseRhinoStyle()` on top-level forms and dialogs, and avoid relying on `RhinoDoc.ActiveDoc` when the associated document is already known.
- Use logical Eto dimensions. Do not read `Control.DeviceDpi`, assume 96 DPI, or position Eto controls using physical screen pixels. Check Windows scaling and macOS Retina behavior.
- Standard command buttons should be 30 logical pixels high, with a minimum width of 88 pixels or enough width for their text. Keep icon-only toolbar buttons on the host's standard size. Do not allow command buttons to absorb unused vertical space.
- Put command buttons in dedicated horizontal command rows or dialog footers. Stretch the content area, tables, text views, and sliders; keep buttons and labels at their natural or explicitly constrained size.
- Build unrelated rows with separate `TableLayout` or `StackLayout` containers. Do not share one expanding column grid between long command labels and compact slider/value rows.
- Keep primary dialog actions in a fixed footer outside a scrollable body. Use scrolling for content that may grow, and provide a sensible minimum size when a dialog is resizable.
- Reserve fixed widths for stable label/value columns where useful, but let the central input control expand. Verify that long labels, translated text, and the longest option do not clip or force horizontal scrolling at the dialog's minimum size.
- Use Eto context menus, message boxes, color dialogs, file dialogs, sliders, and text controls for WASPer-owned UI. Prefer explicit state changes over platform-dependent `CheckOnClick` timing.
- Any Windows-only native call, registry access, OpenGL binding, or system integration must be platform-gated before invocation and must fail softly when the feature is optional.
- Subscribe and unsubscribe canvas, document, timer, and window events symmetrically. Opening a new Grasshopper document or recreating a canvas must not leave duplicate controls, paint handlers, timers, or menus behind.
- Before considering a UI change complete, verify normal and high-DPI Windows layouts and obtain a Rhino for Mac check when the change affects rendering, context menus, mouse interaction, ownership, or native integration. Record any untested platform behavior explicitly.
- When touching an existing WinForms UI, classify it as host-bound, portable as-is, requiring a compatibility guard, or requiring an Eto migration. Do not broaden a narrow fix into a full migration unless the affected workflow can be validated end to end.
- Keep UI state and workflow logic outside Eto forms and controls. Forms should present state and raise events; controllers, geometry processing, study execution, serialization, and fabrication logic must remain UI-toolkit independent.
- Use `Eto.Forms.UITimer` for UI animation and polling. Marshal background callbacks through the Eto or Rhino UI thread before changing controls, and stop timers when their window closes.
- Avoid mixing `System.Drawing` and `Eto.Drawing` types inside UI code. Convert colors, points, rectangles, bitmaps, and sizes explicitly at host or rendering boundaries.
- Do not use `Screen`, `Cursor.Position`, `Control.PointToScreen`, `TopMost`, or native window handles to position WASPer-owned UI unless a documented, platform-gated fallback is required. Prefer document ownership and Eto screen/mouse APIs.
- Platform-specific managed members can fail while a method is being JIT-compiled, before an internal `try/catch` runs. Isolate such access behind reflection or a non-inlined platform adapter, and keep the caller guarded.
- Modeless windows must have a single documented lifecycle: prevent duplicate instances, restore focus appropriately, and verify hide, close, reopen, document switching, and Rhino shutdown behavior.
- An Eto migration must preserve existing component behavior, saved settings, event contracts, and serialization. Do not combine a UI migration with unrelated workflow or data-model changes.

### Eto implementation checklist

- Keep one strong reference to each modeless form, give it the matching Rhino document owner, and clear the reference only from `Closed`. Canvas focus must never hide or dispose the window.
- Make expansion intentional. Keep commands and compact controls constrained, use `Splitter` for adjustable regions, bound long wrapped text, and call `DynamicLayout.Create()` after rebuilding an attached dynamic layout.
- Do not assume `Eto.Forms.GridView` is safe in every Rhino backend. Exercise complex editable grids in the actual Rhino targets; if native grid rendering is unstable, use a `Scrollable` with ordinary Eto controls arranged in `TableLayout` or `StackLayout`.
- Check custom header and status colours in both light and dark Rhino themes. Do not assume `SystemColors.Control` and default text colours provide sufficient contrast.
- When reclassification, renaming, or refresh rebuilds a row, preserve explicit user selections and edits instead of silently restoring inferred defaults.
- Use real control hit testing for drag/drop, render bitmap UI at its host size, preserve display-to-render coordinates, and dispose replaced native image resources.
- Keep control changes on the UI thread; stop timers and unsubscribe external handlers when the form closes.
- A build is not UI acceptance. Test canvas focus, reopen, resize, dynamic content, document switching, high DPI/Retina, and Rhino shutdown on the affected platforms.
- Use the repository skill at `00_Skills/wasper-eto-ui`; install or synchronize it to `$CODEX_HOME/skills/wasper-eto-ui` for Codex discovery.

## Safety

- Do not delete or overwrite user work, local releases, archives, or backups.
- Before a recursive move or deletion, resolve and verify that every source and destination remains inside the WASPer workspace.
- When consolidating similarly named folders, preserve both versions unless their equivalence has been explicitly verified.

# Contributing to WASPer_3DP

Thanks for your interest in WASPer_3DP. This is a research plugin developed as part of
PhD work at Politecnico di Torino, maintained part-time, so please be patient with
review times.

## Getting started

```text
01_WASPer_3DP/WASPer_3DP.sln
```

Open it in Visual Studio (Rhino 8, .NET 8 SDK). See the [README](./README.md#building-from-source)
for the full build, packaging, and installation walkthrough, and
[`WASPer_3DP_Grasshopper_Folder_Junction_Setup.txt`](01_WASPer_3DP/WASPer_3DP_Grasshopper_Folder_Junction_Setup.txt)
for wiring a local build straight into Grasshopper without copying files by hand.

## Project structure

The `*_Structure.txt` files beside each Visual Studio project (`01_WASPer_3DP_Structure.txt`,
`02_WASPer_3DP.AI_Structure.txt`, `03_WASPer_3DP.Robots_Structure.txt`) are the source of
truth for where components, resources, and packaging rules live. Read the relevant one
before adding or moving files.

## What's not in this repo yet

This is the Phase 1 public source release. Component categories `8_Morphology` through
`9.3_Structural` (morphology, boundary conditions, heat transfer, moisture buffering,
structural) are still under development and validation, and are intentionally not part
of this repository — see the [README](./README.md#grasshopper-organization) and
`00_OpenSource/phase1-boundary.json`. Contributions to those areas aren't possible yet;
everything else is open.

## The one rule that matters most

**Never change a released Grasshopper component's parameter list without an
OBSOLETE + upgrader.** Grasshopper wires saved definitions by parameter index; changing
inputs/outputs on a component that's already shipped silently breaks every saved file
that uses it. If a component needs new or removed parameters, snapshot the old version
into its `Archive` folder, give the live component a new GUID, and add an
`IGH_UpgradeObject` remapping indices. This applies across both the public and future
private phases: existing GUIDs must keep resolving.

## Code style

- Clear code over clever code; comment only non-obvious decisions, workarounds, and
  invariants, not what the code already says.
- New or substantially changed WASPer-owned UI (windows, dialogs, menus, pickers) must
  use `Eto.Forms`, not `System.Windows.Forms` - WASPer_3DP targets Rhino on both Windows
  and macOS.
- Keep UI state/workflow logic outside forms and controls; forms present state and raise
  events, everything else (geometry processing, study execution, fabrication logic)
  stays UI-toolkit independent.

## Before you submit

A GitHub Actions build check runs automatically on every PR (`dotnet build` of
`01_WASPer_3DP/WASPer_3DP.sln` in Release, compile-only). You can run the same command
locally before pushing:

```text
dotnet build 01_WASPer_3DP/WASPer_3DP.sln -c Release -p:SkipYakManifest=true
```

If your change touches UI, also smoke-test it in Rhino/Grasshopper: open, resize, and
reopen a saved definition using the changed component.

## Sending a change

Fork the repo, branch from `master`, open a PR back to `master`. Keep the description
concise: what needs solving and why, referencing related issues. Small, focused PRs are
much easier to review than large ones.

Maintainers reconcile merged public pull requests into the private development workspace
before the next public export. Contributors work only through this public repository and
do not need access to, or take any synchronization action against, the private workspace.

## Reporting bugs

Use the [Issues page](https://github.com/jdvargas12/WASPer_3DP/issues). Include the
WASPer_3DP version, Rhino/Grasshopper version, a minimal definition or reproducible
sequence, the complete warning or error, and relevant machine/material context. See
[SUPPORT.md](./SUPPORT.md) for other ways to get help, and
[SECURITY.md](./SECURITY.md) if the issue is a security vulnerability.

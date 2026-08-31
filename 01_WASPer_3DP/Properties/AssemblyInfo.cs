using System.Runtime.CompilerServices;

// Lets the private WASPer_3DP.Performance assembly (categories 8-9.3: morphology,
// boundary conditions, heat transfer, moisture buffering, structural) use this
// project's internal shared helpers (e.g. WasperGridTools) without making them
// part of the public API surface for external plugin developers.
[assembly: InternalsVisibleTo("WASPer_3DP.Performance")]

# Third-Party Notices

WASPer_3DP is MIT-licensed (see [LICENSE](LICENSE)). It references or depends on the
following third-party components. This is provided for transparency; consult each
package's own license for authoritative terms.

## Rhino / Grasshopper SDK

- **What:** `RhinoCommon` and `Grasshopper` (NuGet, reference assemblies only,
  `ExcludeAssets="runtime"`), plus local copies under `01_WASPer_3DP/ThirdParty/GH817/`
  used for SDK-version compatibility checks during development.
- **License:** Proprietary, © Robert McNeel & Associates. Used under the terms of the
  Rhino/Grasshopper developer SDK; not redistributed as part of the WASPer_3DP package.
  Grasshopper.exe and RhinoCommon.dll always come from the end user's own Rhino
  installation at runtime.

## NuGet dependencies

To the best of our knowledge, each is licensed as follows; see the package's own
license file on NuGet.org for authoritative terms.

- **Newtonsoft.Json** - MIT, © James Newton-King
- **ClosedXML** - MIT, © ClosedXML contributors
- **DocumentFormat.OpenXml** - MIT, © Microsoft Corporation
- **QRCoder** - MIT, © Raffael Herrmann
- **System.Drawing.Common** - MIT, © .NET Foundation and contributors
- **Robots.Rhino** (optional `WASPer_3DP.Robots` project only) - MIT, © Vicente Soler
  and contributors ([visose/Robots](https://github.com/visose/Robots))

## Attribution

WASPer_3DP itself does not currently bundle or embed third-party binaries, fonts, or
source files that require attribution beyond the dependencies listed above. This file
will be updated if that changes.

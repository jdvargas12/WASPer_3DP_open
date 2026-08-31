# Security Policy

## Supported versions

WASPer_3DP is an actively evolving research plugin. Security fixes target the latest
published release only; older versions are not backported.

## Reporting a vulnerability

Please report security issues privately using
[GitHub Security Advisories](https://github.com/jdvargas12/WASPer_3DP/security/advisories/new)
("Report a vulnerability" under the Security tab of this repo), or contact
[Juan Diego Vargas](https://www.linkedin.com/in/juan-diego-vargas-vel/) directly.
Please do not open a public issue for security reports.

Include:

- A description of the vulnerability and its potential impact
- Steps to reproduce, ideally a minimal Grasshopper definition
- The WASPer_3DP version and Rhino/Grasshopper version affected

This is a small, part-time-maintained research project rather than a funded security
team, so response times will vary, but reports will be acknowledged and addressed as
soon as possible.

## Scope

WASPer_3DP is a local Rhino/Grasshopper plugin. It does not transmit data externally
or manage user accounts or credentials. The one networked component is the optional
local Process Viewer (`WASPer.XR.WebViewer`), which serves a browser-based visualization
over `localhost` or the local network for mobile/AR access; it is not exposed to the
public internet and provides no built-in hosted sharing service. Reports involving that
local web server, or any Grasshopper definition that could execute unexpected code or
file-system access, are especially welcome.

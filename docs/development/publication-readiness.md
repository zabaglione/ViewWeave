# Publication preparation and remaining checks

> Technical release preparation. [Technical Guide](../technical-guide.md) · [Compatibility](../compatibility.md) · [Third-Party Notices](../THIRD_PARTY_NOTICES.md)

This record describes preparation, not a visibility change or a public release. Original ViewWeave code remains Apache-2.0. Native Unity PluginAPI notices and full Unity Companion License / MIT texts are included in the source Bridge package and generated URP/HDRP distributions. Future macOS CI artifact uploads include the binary, original-code license, notice, and both external license texts.

## Remaining publication and validation checks

- The packaged Windows DLL was built with the installed Unity 6000.6.4f1 PluginAPI. Its exact build-header hashes and file-specific notices were not retained. The added notices use the public reference and do not claim to verify those installed files. Confirm the original build records or rebuild with verified public headers before treating exact Windows header provenance as resolved. Do not inspect Unity installation directories as part of this repository's automated checks.
- CI artifacts uploaded before this workflow change contain only the shim and lack license notices. They were not deleted or rewritten. Before changing repository visibility or redistributing those artifacts, recheck active artifacts and either wait for their seven-day retention to expire or perform separately authorized, scoped cleanup. New artifact packaging does not retroactively modify old ZIPs.
- Anonymous Git/UPM installation can be verified only after public access is enabled. Until then, access-restricted copies require authorized Git authentication. No credentials belong in installation URLs.
- The Quick Start Samples' Windows import/output and all Sample Player builds remain unverified. Current Windows native tests use fake callbacks and WARP/test adapters; older fixtures do not validate the packaged DLL's physical output. The [Compatibility matrix](../compatibility.md#current-development-version-validation) records those limits and macOS signing, device, and shutdown boundaries.
- Looking Glass Bridge is not bundled. Its commercial-use and redistribution conditions remain those of the user's vendor agreement; ViewWeave's license does not extend them.

Run `python3 tools/build_upm_distribution.py all --check`, `python3 tools/validate_repository.py`, and the publication safety audit before a later release. Keep commit identity, target refs, secrets/media review, artifact contents, and repository visibility checks scoped to the actual requested operation. Do not rewrite Git history or delete artifacts as an incidental documentation cleanup.

## Current-tree preparation — 2026-10-14

Machine-specific path roots in publication copies are documented in the [public evidence policy](../validation/PUBLIC_EVIDENCE.md) and [hash manifest](../validation/PUBLIC_REDACTIONS.json). Existing as-run hashes and byte counts remain historical values. Git history was not rewritten; a later visibility change must review the original paths retained there.

The publication scanner's two token-like matches in the archived `FrustumSceneMetadataProbe.cs` and `FrustumSceneMetadataProbe-clone-asrun.cs` are fixed, hyphenated diagnostic identifiers passed to `errors.Add`, not authentication material. Its two email warnings in the sample-refresh report and JSON are the public GitHub SSH remote syntax, not personal author addresses. The missing npm lockfile warning is inapplicable to this Unity/Python/CMake repository. These are reviewed scanner dispositions, not a claim that every historical artifact has been audited or that release gates have passed.

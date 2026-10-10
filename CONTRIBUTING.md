# Contributing

> Audience: Unity and package contributors. This document covers repository contribution procedures. Browse the [Technical Guide](docs/technical-guide.md) for engineering references or the [User Guide](docs/user-guide.md) for product setup and operation.

This prototype is intended for community review. Keep runtime and Editor assemblies/directories separate where practical and preserve Unity `.meta` files. Do not copy proprietary source code, headers, or binaries into this repository. Record the provenance and license of third-party source or assets in `docs/THIRD_PARTY_NOTICES.md`.

English is the default language for repository documentation. When adding or changing user-facing documentation, update the corresponding Japanese page under `docs/ja/` or the adjacent `*.ja.md` file where one exists; link both language versions at the top of each page. See [README.ja.md](README.ja.md) for Japanese project information.

Repository and package sources are distributed under Apache-2.0. The root `LICENSE` and each package `LICENSE.md` contain the complete license text; third-party notices remain subject to provenance review.

Logs must contain ASCII characters only. Remove serial numbers, account data, absolute local paths, and credentials before attaching diagnostics. Use the repository validator before opening a pull request and include its output in the PR description.

Unity projects under `Tests/` are a separate test matrix. CI runs the Python repository validator and native transport tests; Unity license access, hardware, and render verification need a separate runner and report. Optional package samples must live below `Samples~`; they are not imported automatically.

Before running the sample lifecycle tests from a clean clone, prepare the ignored sample copies with `python tools/prepare_sample_tests.py`. Use `--dry-run` to inspect the copy list without changing files.

Formal integration support is planned for URP and HDRP; Built-in pipeline support is outside the formal support target.

## Package versions and releases

During development, a Git URL such as `#main` can move to a new commit while the package continues to report the same version (currently `0.1.0`). Unity locks the resolved Git commit separately, so a version display alone does not identify the source revision. Keep sample project Git refs aligned with the SDK revision they are intended to demonstrate.

When preparing a formal release, update the package SemVer versions, internal package dependency versions, and `VERSION` in `tools/build_upm_distribution.py` together. Record the release and user-visible changes in the changelog. Generate the UPM distributions and run the repository validator to check the generated packages. Create an immutable `vX.Y.Z` tag only for the matching released contents; never move or reuse a published version tag. Create tags and publish a release only when explicitly requested.

Do not make performance claims without a fair, reproducible benchmark that states Unity version, render pipeline, graphics API, GPU, resolution, view count, and test scene. Bridge runtime libraries are an independent distribution concern: do not add official proprietary Bridge source, headers, or binaries. Native CMake build instructions and Unity Plugin API header requirements are documented in [the Bridge package README](Packages/com.zabaglione.lookingglass.viewweave.bridge/README.md); provenance is recorded in `Plugins/native-provenance.json`. Distribution policy remains subject to the external runtime terms.

## Pull requests

Explain the user-visible behavior and scope. Include `python tools/validate_repository.py` output and relevant Unity test-matrix evidence. Keep generated files, `Library/`, `UserSettings/`, logs, captures, and local notes out of commits. A passing static check does not establish Unity rendering or legal rights clearance.

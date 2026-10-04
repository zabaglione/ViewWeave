# Contributing

This prototype is intended for community review. Keep runtime and Editor assemblies/directories separate where practical and preserve Unity `.meta` files. Do not copy proprietary source code, headers, or binaries into this repository. Record the provenance and license of third-party source or assets in `docs/THIRD_PARTY_NOTICES.md`.

Repository and package sources are distributed under Apache-2.0. The root `LICENSE` and each package `LICENSE.md` contain the complete license text; third-party notices remain subject to provenance review.

Logs must contain ASCII characters only. Remove serial numbers, account data, absolute local paths, and credentials before attaching diagnostics. Use the repository validator before opening a pull request and include its output in the PR description.

Unity projects under `Tests/` are a separate test matrix. CI runs the Python repository validator and native transport tests; Unity license access, hardware, and render verification need a separate runner and report. Optional package samples must live below `Samples~`; they are not imported automatically.

Before running the sample lifecycle tests from a clean clone, prepare the ignored sample copies with `python tools/prepare_sample_tests.py`. Use `--dry-run` to inspect the copy list without changing files.

Formal integration support is planned for URP and HDRP; Built-in pipeline support is outside the formal support target.

Do not make performance claims without a fair, reproducible benchmark that states Unity version, render pipeline, graphics API, GPU, resolution, view count, and test scene. Bridge runtime libraries are an independent distribution concern: do not add official proprietary Bridge source, headers, or binaries. Native CMake build instructions and Unity Plugin API header requirements are documented in [the Bridge package README](Packages/com.zabaglione.viewweave.bridge/README.md); provenance is recorded in `Plugins/native-provenance.json`. Distribution policy remains subject to the external runtime terms.

## Pull requests

Explain the user-visible behavior and scope. Include `python tools/validate_repository.py` output and relevant Unity test-matrix evidence. Keep generated files, `Library/`, `UserSettings/`, logs, captures, and local notes out of commits. A passing static check does not establish Unity rendering or legal rights clearance.

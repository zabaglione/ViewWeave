[Japanese](ROADMAP.ja.md)

# Development Roadmap

There are no fixed dates. Move to the next phase when its acceptance criteria are met. This is a plan; unimplemented features are not represented as currently supported.

## M0: Distribution, Rights, and Measurement Foundations

Define package boundaries, rights review, reproducible performance benchmarks, and formats for saving test logs and screenshots. Adoption of Apache-2.0 for original code is complete. Audits of external rights, including Bridge, Unity, trademarks, and third-party patents, are ongoing. Acceptance requires installing Core and the relevant adapter in a URP/HDRP project, rendering a basic Quilt, and documenting dependencies and external components.

Move GPU Resident Drawer applicability testing earlier, around M0/M1. Compare GRD OFF, GRD ON with occlusion OFF, and GRD ON with occlusion ON after validating history. Keep this work separate from the Bridge shutdown fix.

After the GRD MVP, a minimal Lit test of custom multiview using shared culling across views and GPU-resident geometry was run. Under the limited 8-view conditions, 6 checks passed, the feature ran 1 time, and 8 RendererLists were confirmed. Reprojection remains an optional later investigation. Details and open questions are in [docs/multiview-rendering.md](docs/multiview-rendering.md). This is not an adoption decision for the production path or evidence of a performance target being met.

## M1: Bridge Normal-Thread Path (Implemented; Acceptance Gates Remain)

Implemented and API-validated: collection of call stacks, thread IDs, device contexts, and fences; copies on Unity's render thread only; a Bridge-owned D3D11 device; a shared 3-slot GPU ring buffer; a WM message pump; resize; and close/reopen. When the queue is full, new submissions are dropped while already queued submissions are retained. Routine CPU readback was not adopted. Keep the direct guard until the separate path is accepted. The Bridge Worker normal-thread path was verified in a Windows D3D11 Player, including reconnecting within the same process, and CTest for the new WM pump DLL passed. Optical quality, long-duration lifecycle, official distribution signing, shutdown handling, and the macOS path remain separate release gates.

## M2: Profiles, Rig, and Ease of Setup (Partially Passed; Continue Device Validation)

Prepare high-resolution profiles, an integrated Rig, automatic device selection, an installer, and removal of the need to specify a manual DLL path during normal use. Quality profiles (Preview/Balanced/Native) are registered. ViewWeave Rig adds the supported URP/HDRP adapters through the provider registry. The goal is to go from an empty M0 project to UPM installation, Bridge runtime configuration, and Lit output in 5 minutes. The URP Sample Player confirmed Native 48 views, all tiles non-empty, red/green depth, movement, parallax, 124 draws, and pending 0. The HDRP Sample Player confirmed Native 48 views, all tiles non-empty, red/green depth, movement, parallax, 125 draws, and pending 0. Acceptance requires consistent settings derived from supported-device profiles and avoiding false compatibility claims for unsupported devices.

## M3: Per-View Compatibility (Partially Passed; Some Conditions Not Passed)

Validate compatibility of per-view post-processing, UI, particles, and VFX. The consumer path must document the order of per-view processing, assembly, and calibration, and avoid tile seams from applying Bloom to the entire Quilt. Acceptance requires a feature matrix and reproducible procedures.

VFX Graph requires Linear color space. Unity Visual Effect Graph 17.6 does not support URP Gamma; the URP VFX driver stops before resource creation in Gamma mode. Supporting Gamma VFX requires a separate change officially supported by Unity.

Before the current guard was added, the previous full Windows D3D11 PlayMode run passed 108 of 123 tests, with 15 failures and 0 skipped. Session 18/18, GRD 27/27, SRP additional 10/10, URP MSAA 1/1, and VFX driver 5/5 were confirmed. An additional Linear MSAA boundary suite passed 10/10; on this RTX GPU, requested 2x/4x/8x matched supported levels. The 1x path was confirmed with pipeline 1x, allowMSAA disabled, and Deferred fallback; low-MSAA-capability GPUs remain untested. Resize/cleanup, center-color matching, and edge differences at supported MSAA levels were also checked. A dedicated MSAA Player test was not run. The current Core/SRP/VFX Editor targeted tests passed Gamma 8/8 (7 MSAA cases and 1 Gamma VFX rejection) and Linear 17/17 (11 SRP cases and 6 VFX driver cases); these are not results for the full 125-test suite. Gamma MSAA and normal URP Bridge Player execution remain untested. Gamma VFX is officially unsupported. The outstanding failures were 1 HDRP priming case and 14 VFX compatibility cases; a separate VFX matrix run passed 5 of 20 and failed 15. Under the failing conditions, sufficient full-tile effects were not confirmed. D3D12 Player coverage, comprehensive optical validation, and signed official distribution remain separate release gates. Passing an individual fixture does not establish support for every material or effect.

## M4: Performance Investigations (Evaluation Continues; Nothing Adopted for Production)

Measure the prerequisites for shared culling and shadows through public URP extensions, pooling, command reuse, GPU-instanced multiview, and depth reprojection (including transparency, occlusion, and reflection fallback). A shared-culling prototype passed under limited Lit conditions; in an eight-view microbenchmark, CPU submission p50 was 1.8023 ms for the baseline and 0.3198 ms with shared culling. GPU performance, 48 views, shadows, transparency, post-processing, and VFX remain unverified. A 20% GPU frame-time improvement at equivalent quality may be used as an internal development target, but must not be described as an achieved result or guarantee. Acceptance requires a benchmark with fixed conditions and at least three runs, plus image-quality checks. The production path does not use this prototype.

## M5: Alpha / Beta / Stable (Preparing an Experimental Release; No Official Tag)

The current state is reproducible local installation of a Windows experimental build; no official release tag has been issued. Alpha requires clearly identifying experimental Windows paths and documenting unresolved normal-thread or hardware issues. Beta requires a reproducible normal-thread path, Windows hardware validation, a compatibility matrix, performance measurements, and rights review. Stable requires a stabilized normal-thread path, Windows and macOS hardware validation, reproducible distribution, rights review, and resolution of release blockers. If unmet criteria remain as release blockers, do not advance to the next stage.

Register Samples as optional through the UPM `Samples~` directory and the `samples` definition in `package.json`. For users, install a self-contained URP or HDRP distribution package from a Git URL, including Core, Bridge, and the selected render pipeline. Keep the development source package as a local reference in the test project.

Show differences from official features using verified results in the feature matrix; do not assert substitutability without evidence.

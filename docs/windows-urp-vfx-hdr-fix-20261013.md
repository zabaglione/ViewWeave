# Windows URP VFX HDR intermediate fix validation (2026-10-13)

This report records the production-source HDR intermediate implementation and its bounded editor validation. The earlier [root-cause investigation](windows-urp-vfx-hdr-rootcause-20261013.md) and Phase A-C artifacts remain the historical clone-only evidence; they are not rewritten as production-code or device results.

## Change and source basis

The Core render-target format provider is optional. URP selects a linear half-float view target when the source camera allows HDR and the active URP asset supports HDR. The existing LDR view target is retained only when source-camera HDR is off, the active URP asset reports HDR unsupported, or no provider is installed. Once a provider requests HDR half-float, an unsupported or invalid format fails closed with an exception; it does not silently fall back to LDR. The final quilt remains RGBA8 sRGB. The URP implementation is shared by the SDK packages and `Distribution/urp`; HDRP has no provider implementation and retains the existing default format path.

The implementation preserves CoreTrack ownership and detach/retain behavior, resolves an MSAA view once into the LDR quilt target using actual target/device support, and restores graphics state including the active target. It keeps the existing camera identity for TAA histories and leaves the configured diagnostic history budgets unchanged.

The tested checkout is `11312b8c6cf824d9a7ac7c8940e95cee742fa076` with pre-existing local diagnostic changes and this uncommitted HDR implementation. It is not committed or released. The primary source hashes recorded by the selected Editor runs are:

| Source | SHA-256 |
| --- | --- |
| Core `QuiltRenderer.cs` | `8DC2F11769425D22C123ACFE49C59B742C2CF548A2F956D845F4895A2E4A9BBE` |
| Core `IQuiltViewTextureFormatProvider.cs` | `99182903A82CA89E818CA03770AC9EAA18FF5CB861D0624DBC3B43912EC0FC11` |
| URP `UrpQuiltCamera.cs` | `864C024CA951E6EB07C6FDF09EF4BDAB8762ACB2E536AC619AA097F639113CBB` |
| MSAA lifecycle fixture | `DEFDD8FDCAD8637A91A6AC547831FD0EA62562997F0D56FFFC70B86370ABB921` |
| HDRP preview fixture used in final retry | `55A89AD09B0AC46E8D917FD525501915E3CB3384972036F9A8C6C68158DEBFF5` |

## Results

The recorded final selected Editor cases passed 12/12 NUnit cases with D3D12 on Unity `6000.6.4f1`: 11 URP cases and one bounded HDRP Preview case. The URP subset includes the output-format/default-LDR and resource-lifecycle fixture (7 cases), TAA history and effective MSAA permission checks, a URP Editor Preview case, and Preview-to-Play transition cleanup. Exact XML and runner metadata are linked in the [selected Editor run archive](validation/windows-urp-vfx-hdr-fix-20261013/runs/).

The total of 16 adopted NUnit cases is the 12 selected Editor cases plus two offscreen probes and two physical-device probes; setup failures and interruptions are not included. The HDRP case is a limited warmed Preview/lifecycle check on shared Core code, not an HDRP VFX support claim. A preceding cold attempt failed its 15-second readiness limit: the source image became green after 22.40 seconds (868 green-dominant pixels), so Preview never began. The revised, separately recorded retry observed cold initialization in 1.12 seconds and the same green result in 0.08 seconds on the warmed source control, then completed an eight-view Preview and profile-ownership checks. This retry does not establish a cold-start performance guarantee. The earlier failure remains recorded in the selected Editor run archive.

An earlier OS interruption had no XML and is not counted. An initial test-setup compile failure (`CS0246`) is preserved as setup history and is not counted; the setup was corrected before the passing selected runs. Other historical failures remain archived rather than being replaced by the final retry.

| Gate | Result | Boundary |
| --- | --- | --- |
| URP half-linear HDR selection and LDR fallback | PASS | Existing LDR is selected when source-camera HDR is off, active URP HDR support is false, or no provider is installed. An unsupported/invalid provider-requested HDR format throws rather than silently falling back; D3D12 Editor fixtures only. |
| RGBA8 quilt destination, MSAA resolve and lifecycle | PASS | Selected URP Editor cases; no Player or other graphics API. |
| TAA history camera identity and accumulation | PASS | Selected URP Editor fixture; does not prove all TAA configurations or view counts. |
| URP Editor Preview and Preview-to-Play shutdown | PASS | Selected D3D12 Editor cases only. |
| HDRP shared-Core warmed Preview regression check | PASS | One eight-view case; prior cold timing failure remains, no provider is installed for HDRP. |
| Two original-graph offscreen probes | PASS, limited | D3D12 PlayMode; selected graphs, PP/Bloom/ACES on, AA off, one quilt view. |
| Looking Glass physical output | PASS, limited | Two selected graphs on the main SDK, D3D12 Editor, Preview/Quarter, 12 rendered/history views, one active graph at a time; user acknowledged both requested visual cues. |
| macOS / Metal | NOT RUN | The Core implementation is shared source, but the new HDR format path has no Metal execution evidence. Prior Metal results are not reused. |
| Player, all-scene/all-graph compatibility, Balanced output, and other GPUs/APIs | NOT RUN | No general graph, scene, or Player claim follows from these fixtures. |

## Follow-up and independent incidents

After the 12 selected Editor cases, two separate one-view offscreen probes and two main-SDK physical-device probes passed (16 adopted NUnit cases total). The portable follow-up archive is [main-sdk-hdr](validation/windows-urp-vfx-hdr-fix-20261013/main-sdk-hdr/) with a 70-file manifest (32,481,347 bytes; SHA-256 `F2F3C2B8C463F1FAAD43BD53A994F6E57AF59ECACF877A3AA1AAD38C589D5015`). It contains exact runner/XML, descriptor/evidence, source snapshots, and device stage/response records, but no full Editor logs.

The offscreen D3D12 PlayMode probes used the original `MultiStripGPUEvents` and `RotationAngle` graphs, PP/Bloom/ACES on, AA off, and one quilt view. Green ran 20.0078 seconds (720 pairs) and the strict green classifier was positive in 566 normal and 566 quilt rows. Coin ran 6.0147 seconds (233 pairs). Both used an `R16G16B16A16_SFloat` linear normal target and the existing `R8G8B8A8_SRGB` quilt target. These are separate bounded offscreen observations, not device results.

The two physical stages ran on Windows D3D12, Unity `6000.6.4f1`, VFX Graph 17.6.0, NVIDIA RTX 3090 Ti, Bridge 2.6.3, Preview/Quarter. The actual profile layout was 1920x1080, 8x6 / 48-layout views, with 12 rendered/history views. Both kept TAA, post-processing and HDR enabled. The view target was 240x180 half-linear, the resolve target 240x180 RGBA8 sRGB, and the quilt 1920x1080 RGBA8 sRGB. The user acknowledged `MultiStripGPUEvents` as green (`green-visible`) and `RotationAngle` as a visible strong glow/flicker (`coin-flash-visible`). Each stage passed 1/1, exited naturally with code 0, drained output, matched its response nonce, and passed cleanup: Idle session, no pending shutdown, zero history cameras, all three render targets released, and all effects inactive. Graph assets and TAA/post-processing/history budgets were unchanged.

The growth safety limit applies to the selected graph's post-prewarm growth only, not total process allocation. Green selected-effect peak growth was 27,213,568 graphics bytes / 28,651,520 private bytes. Coin startup was 246,065,608 graphics / 1,753,247,744 private bytes; all-VFX-off prewarm was 953,826,708 / 2,995,957,760 bytes; selected-effect growth was 4,295,680 / 7,618,560 bytes. These process counters are not physical VRAM measurements. The result is limited to these two graphs and this Preview/Quarter profile; it does not cover all scene effects, all views simultaneously, Balanced output, Player, or other GPUs/APIs.

One earlier Green setup attempt failed during the all-VFX-disabled prewarm, before a valid profile, rendered view or device visual response. Cleanup returned to Idle with no pending shutdown, history cameras or render targets. The helper guard setup was corrected before the passing physical stages. The attempt is not counted among the 16 adopted cases; its result, runner and XML are preserved in the archive support-failures folder. The implementation does not resolve the separate URP memory-budget exception or Editor shutdown crash; their causality remains unknown.

The original user projects were not changed. Editor tests used the SDK checkout and isolated test fixtures. The HDRP run is only a regression signal for the shared Core code under its recorded warm Preview conditions. The selected physical result is a bounded user-acknowledged output observation, not an independent optical calibration or general support claim.

## Related records

- [HDR range root-cause investigation and Phase A-C evidence](windows-urp-vfx-hdr-rootcause-20261013.md)
- [URP rendering mismatch, budget and shutdown incident](windows-urp-vfx-rendering-incident-20261013.md)
- [Structured fix validation record](validation/windows-urp-vfx-hdr-fix-20261013.json)

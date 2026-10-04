# Changelog

## Unreleased

This repository is a prototype and is not a formal 0.1 release.

- Repository and all four UPM packages use Apache-2.0 for independently authored code. Bridge runtime, Unity, trademark, and other third-party rights remain separate.
- Core renders a quilt through one dedicated Camera and N view requests. URP/HDRP use the Standard RenderRequest path; Built-in is outside the support target.
- Quality changes use a quiescent transfer reset on the same host and window, then recreate transfer resources and resume automatic rendering. `StopSession()` and `StartSession()` remain an explicit restart path.
- The final Windows D3D11 PlayMode run has 123 tests: 108 pass, 15 fail, and 0 skip. Session is 18/18; compatibility 37/37; GRD 27/27; SRP additional 10/10; SRP extension 2/2; VFX driver 5/5; Samples 4/4. The remaining failures are one HDRP priming case and 14 VFX compatibility cases. The separate VFX matrix has 20 cases with 5 pass and 15 fail.
- Fixed URP and HDRP Player quality cycles passed Preview, Balanced, Native, Preview with 122 completions per stage and an explicit restart under documented Windows D3D11 conditions. Bridge-connected URP/HDRP checks also passed all six quality conditions at 48 views: Preview, Balanced, and Native with GRD ON and OFF.
- The fixed GRD benchmark records approximately 13.0% lower 48-view CPU submission p50 with GRD enabled. GPU and render-thread timing are unavailable, and no official SDK comparison has been performed.
- The shared-culling multiview prototype passed six limited Lit checks at 8 views, with one feature execution and eight RendererLists. It is not the production backend; general and GPU performance, 48 views, shadows, transparency, post processing, and VFX remain unconfirmed.
- A fixed 8-view microbenchmark measured CPU submission p50 of 1.8023 ms for eight StandardRequests and 0.3198 ms for one request plus eight RendererLists. This is a two-object Lit test; GPU timing, 48 views, and general performance are unmeasured.
- URP Simple Burst VFX passed six paused conditions at 1, 8, and 48 views with GRD ON/OFF, and four live conditions at 8 and 48 views with GRD ON/OFF. HDRP VFX, general graph compatibility, sorting, soft depth, and same-camera priming remain unconfirmed.
- Optional Core and Bridge Samples~ include owned Lit material resources. URP and HDRP Sample Player validation passed with Native 48-view output, non-empty red/green evidence, movement and parallax, 124/125 completed draws, and no pending cleanup under the documented Windows D3D11 conditions.
- Native and Bridge conditions remain Windows D3D11 specific. The loader 100-cycle test, legacy handle-close cleanup, and independent D3D12-to-D3D11 probe passed. Normal 600-second and 30-second Bridge Player soak-watchdog runs passed. Dedicated init-delay and draw/release-delay Player fault runs also passed with the repository fake runtime and native cleanup evidence; they do not establish physical hotplug or arbitrary hardware hang behavior. Connection, shutdown, and completion watchdogs use a 10-second limit. Unity/Bridge D3D12 Player execution is out of scope because the production backend is unimplemented. macOS Metal, hotplug, multiple devices, complete optical coverage, distribution signing, and general device coverage remain open.

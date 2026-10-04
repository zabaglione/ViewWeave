# Changelog

## Unreleased

This repository is a prototype and is not a formal 0.1 release.

- Repository and all four UPM packages use Apache-2.0 for independently authored code. Bridge runtime, Unity, trademark, and other third-party rights remain separate.
- Core renders a quilt through one dedicated Camera and N view requests. URP/HDRP use the Standard RenderRequest path; Built-in is outside the support target.
- Quality changes use a quiescent transfer reset on the same host and window, then recreate transfer resources and resume automatic rendering. `StopSession()` and `StartSession()` remain an explicit restart path.
- The Windows D3D11 integration matrix has 82 cases: 71 pass, 11 fail, and 0 skip. Session is 14/14; additional compatibility 12/12; Geometry 10/10; HDRP fixture 7/7; URP fixture 8/8; HDRP priming 0/1; SRP additional 4/5; SRP extension 2/2; VFX 5/14; URP driver 5/5; Samples 4/4.
- Fixed URP and HDRP Player quality cycles passed Preview, Balanced, Native, Preview with 122 completions per stage and an explicit restart under documented Windows D3D11 conditions. Bridge-connected URP/HDRP checks also passed the six GRD ON/OFF conditions at 1, 8, and 48 views.
- The fixed GRD benchmark records approximately 13.0% lower 48-view CPU submission p50 with GRD enabled. GPU and render-thread timing are unavailable, and no official SDK comparison has been performed.
- The shared-culling multiview prototype passed six limited Lit checks at 8 views, with one feature execution and eight RendererLists. It is not the production backend; 48 views, performance, shadows, transparency, post processing, and VFX remain unconfirmed.
- URP Simple Burst VFX passed six paused and four live conditions across 1, 8, and 48 views with GRD ON/OFF. HDRP VFX, general graph compatibility, sorting, soft depth, and same-camera priming remain unconfirmed.
- Optional Core and Bridge Samples~ include owned Lit material resources. URP and HDRP Sample Player validation passed with Native 48-view output, non-empty red/green evidence, movement and parallax, 124/125 completed draws, and no pending cleanup under the documented Windows D3D11 conditions.
- Native and Bridge conditions remain Windows D3D11 specific. macOS Metal, hotplug, multiple devices, optical human review, distribution signing, and general device coverage remain open.

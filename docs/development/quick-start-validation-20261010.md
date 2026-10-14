# Quick Start Sample validation — 2026-10-10

> Technical record. [Technical Guide](../technical-guide.md) · [User Guide](../user-guide.md) · [Illustrated Quick Start](../getting-started.md)

## Scope

Unity **6000.6.4f1**, macOS, Metal, URP/HDRP **17.6.0**, and a connected Looking Glass Go were used. Dedicated disposable projects were used for authoring and screenshots. The saved user projects were closed before final Sample validation.

Each pipeline has one `QuickStart.unity`, shared by Metal and Windows D3D11/D3D12. The Sample helper chooses its enabled transport from the active graphics API; it does not start output or overwrite focus, quality, or the user's auto-start preferences. This selection helper belongs to the Sample. The normal Rig creation menu retains its existing platform-specific component selection.

## Confirmed results

| Check | Result |
| --- | --- |
| URP UPM import | Imported **URP Quick Start** through Package Manager from the local self-contained distribution; opened the imported scene alone. |
| HDRP UPM import | Imported **HDRP Quick Start** through Package Manager from the local self-contained distribution; opened the imported scene alone. |
| Scene assets | One enabled Audio Listener per scene; valid Rig and Metal binding. HDRP's five Volume components persist as subassets after save/reload and UPM import. |
| Edit Mode device preview | Both imported Samples rendered to Go. The user confirmed natural orientation and depth for URP, and natural orientation, depth, and brightness for HDRP. HDRP preview presented 137 frames at the recorded checkpoint. |
| Animation and manual start/stop | URP imported Sample presented 1,463 frames in Play; HDRP authoring scene presented 318. Cube rotation and Sphere movement were observed through Animator values. Auto-start is disabled in the saved Samples. |
| Reload-disabled transition | URP was checked with both Domain Reload and Scene Reload disabled. The selected auto-start setting survived Preview → Play; output presented 1,250 frames. Manual Stop remained stopped. Editor options were restored afterward. |
| Resource retirement | Preview/Play stop checkpoints reported zero retained preview resources and zero pending Metal retirement. |
| Disabled Windows Session | First enable and subsequent disable/enable reached Idle with a Closed worker and the expected binding, without native output startup. |
| Editor regressions | Rig validation suite: **36 passed, 0 failed, 0 skipped**, including destroyed Inspector targets and black-background framing copy. |
| UI captures | Actual Unity UI screenshots illustrate UPM import, scene creation, background, animation, focus, preview, and quality in the user guide. |

HDRP uses a black background and fixed exposure. The simple lighting leaves the unlit side of the Sphere very dark; the user accepted that appearance for this introductory Sample.

## Editor observations and limits

The URP session history contains one `Mismatched state in DefaultScenario, was Stopping but expected Idle` assertion during the reload-disabled Play-stop checks. The stack is inside Unity's Play Mode editor state management. The separately documented [Metal validation record](macos-metal-validation-20261010.md) describes an earlier occurrence and the related Unity issue. This run does not establish that the trigger is identical or fix the assertion.

The HDRP import session also recorded `NullReferenceException` in `UnityEditor.ExposablePopupMenu.OnGUI` / `ProjectBrowser.SearchAreaBar`, with GUI clip-balance errors during Editor UI initialization. Those stacks contain no SDK frames. Their trigger was not independently isolated. The current Console after scene load reported zero errors/warnings and no compilation failure, but that does not erase the historical entries.

**Windows native rendering, Windows UPM import, and Player builds were not checked in this run.** Windows support in the shared scene is based on the existing SDK path, asset inspection, and the managed Session checks above. This is not a new D3D11/D3D12 device validation.

Test XML, Editor logs, authoring projects, and command responses are retained under the repository's ignored `.local/documentation-refresh/` directory. They are verification artifacts, not UPM content or user setup prerequisites.

# Reference Camera Inspector validation (2026-10-11)

This record covers the shared Editor Inspector assignment path for the ViewWeave Rig's Reference Camera. It does not validate URP or HDRP rendering behavior.

## Basis and environment

- Source baseline: `16d9b1875d49375e3b5f8ce0f1a0cb268a5bbcca`, with the Editor UI and test-fixture changes that were uncommitted at the time of validation, as described below.
- Unity 6000.6.4f1, Unity CLI 1.0.0-beta.12, Windows, Direct3D 11, NVIDIA GeForce RTX 3090 Ti.
- EditMode filter: `ViewWeave.RigCameraCreationValidation.RigAuthoringInspectorTests;ViewWeave.RigCameraCreationValidation.RigCameraCreationEditorTests`.
- The Unity project had Built-in as its global pipeline; one existing test fixture temporarily creates URP assets. This validates shared Editor UI and authoring checks, not separate URP/HDRP rendering.
- `ViewWeaveRigInspectorUI.cs` SHA-256: `76254D1382FE5C19BD466B0079F0BBB742B59ACB7FE9F441214677C1DC841AEF`.
- Test inputs: `RigAuthoringInspectorTests.cs` SHA-256 `E02255C9B497FE3F6B2F0F5D99043D4AB25D5D74B10F2ACE11A9CF0F0DA49A64`; `RigCameraCreationEditorTests.cs` SHA-256 `2F46B6DFD5E02D00E04CEA49048E89FD9EC7DC18B8546CEBD349CECDE18C722E`.

## Results

The initial run executed 31 tests: 27 passed, 4 failed, 0 skipped, and the Unity CLI exited 8. The four failures were fixture issues: the synthetic `ChangeEvent<Object>` did not target the ObjectField, so its callback did not run; after Undo, the test read a detached field instance following Inspector rebuild. The failures were not product rendering failures. The original XML and logs remain at `.local/rig-reference-camera-validation-20261011/rig-reference-camera-editmode-d3d11/`.

The corrected fixture set the event target to the actual field and reacquired the field after Undo/Redo. The rerun passed 31/31 tests with 0 failures and 0 skips: `RigAuthoringInspectorTests` 25/25 and `RigCameraCreationEditorTests` 6/6. The CLI and Editor exited naturally with code 0. The raw XML is `.local/rig-reference-camera-validation-20261011/rig-reference-camera-editmode-d3d11-fixture-fix/rig-reference-camera-editmode-d3d11-fixture-fix/test-results.xml`; the matching `summary.json`, `command.txt`, and `unity-editor.log` are beside it.

The fixtures verify that a Camera can be assigned directly or resolved from a tagless GameObject containing a Camera; a GameObject without a Camera or with a Camera only on a child preserves the previous assignment and reports why. Explicit null clears the assignment. Undo/Redo restores the reference. Selection from another Preview Stage, the Rig source Camera, its descendant, and a renderer retained by an Editor owner remain rejected. The warning remains after refresh and clears on a valid assignment or explicit clear.

This is shared Editor UI evidence on the listed Windows machine. It does not establish separate URP/HDRP rendering, macOS execution, Player behavior, Native Bridge/device output, optical quality, or manual drag-and-drop behavior. Device opt-in was false; no Native trace or loaded-module identity was collected. No Application Error 1000/1001 events or crash reports were observed. Project settings were clean at start. Unity-generated GraphicsSettings and QualitySettings changes were restored after the run; the final settings diff was clean and cleanup reported no remaining status.

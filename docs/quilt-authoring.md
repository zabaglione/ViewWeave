[日本語](ja/quilt-authoring.md)

# Adjust Quilt camera framing and focus

1. Select the GameObject that has `QuiltRenderer`.
2. In the Scene View, the purple rectangle is the zero-parallax plane, teal is Near Clip, and orange is Far Clip.
3. In the Inspector's `Camera Framing`, adjust `Focus Distance`, `Vertical Field Of View`, `Near Clip`, and `Far Clip`.
4. In the Scene View, drag the handle at the center of the purple plane forward or backward to adjust `Focus Distance`.

The zero-parallax plane is the reference plane where a subject at that distance aligns with the display surface. `Focus Distance` (`Quilt Settings > Camera Distance`) is measured in Unity units forward from the world position of the effective framing Camera. The purple plane is placed at that point, which is indicated by the gizmo line and distance label. Units follow the Unity scene scale; if the project does not use 1 unit per meter, the value does not represent actual meters. `Focus Distance` adjusts stereo parallax. It does not change depth of field, autofocus, or a Volume's focus distance.

Near/Far bounds use the clip distances of the dedicated `sourceCamera`. If `Reference Camera` is set, its runtime position and rotation are applied to the dedicated Camera before Quilt rendering. When `Follow Reference Field Of View` is enabled, `Vertical Field Of View` is also synchronized from the reference Camera before each Quilt render. Therefore, an FOV entered in the Inspector is overwritten on the next render. Disable the option to use the FOV specified in Quilt settings. `viewAspect`, `Camera Distance`, `viewCone`, and Near/Far are not synchronized automatically. The original reference Camera settings and Transform are not changed.

The Bridge Rig creation menu assigns the resolved framing Camera to `Reference Camera`. With Cinemachine, reference the actual Camera driven by Cinemachine Brain rather than a Virtual Camera. Regular automatic Quilt rendering happens after scene rendering, so it normally reads Brain's output for that frame. Recording locks Quilt settings when it starts and stops if FOV or other Quilt settings change during recording. Keep FOV and Quilt geometry fixed while recording, including when the reference Camera moves.

Use `Show Near / Far` in the Inspector to show or hide the clip planes. Toggle it when a large clip range makes the nearby bounds hard to see. Enable `Show Extreme Views` to show Near/Far planes for the leftmost and rightmost views. These mark the configured view range. The gizmo continues to show the configured extremes when Half or Quarter render density is selected.

## Explicit camera authoring controls

`Camera Authoring` shows the target `Effective Camera` and `Effective Camera Transform`. If `Reference Camera` is set, it is the target; otherwise, `Source Camera` is used. The reference Camera's Transform is copied to the Source Camera before Quilt rendering, so manual controls also operate on the effective framing Camera.

To use `Set Focus From Selected Object Center`, first lock the QuiltRenderer Inspector, then select one GameObject in the scene. Click the button in the locked Inspector to project the center of the Renderer bounds onto the Camera's forward axis and set that distance as the zero-parallax `Focus Distance`. If there is no Renderer, the Collider bounds center is used; if neither bounds exists, the Transform position is used. Objects behind the Camera and invalid values are rejected. This only changes the setting and does not move or rotate the Camera. This is not depth-of-field focus.

`Frame Focus Plane Center in Scene View` moves the Scene View to the center of the zero-parallax plane. Enter an angle for `Horizontal Orbit` or `Vertical Orbit` and press the corresponding Apply button to rotate the Effective Camera's position and orientation together around the zero-parallax point. The pivot stays fixed; horizontal rotation is limited to ±180 degrees and vertical rotation to ±89 degrees. These manual operations are recorded in Undo, including changes on Prefab instances.

`Show 3D Focus Cursor` is disabled by default. When enabled, it displays a Position Handle at the center of the focus plane instead of the existing forward/back slider. The dragged position is projected onto the Camera's forward axis to update distance only, so lateral movement is discarded and the handle returns to the center of the focus plane. The Camera Transform is not changed.

Camera authoring controls are disabled in Play Mode and during the transition into Play. If Cinemachine or Animation preview drives the reference Camera, a later update may overwrite a manual Transform change; edit while stopped. These controls are hidden and disabled when multiple `QuiltRenderer` objects are selected.

Gizmos are Scene View adjustment aids. When a reference Camera is assigned, planes use its current world pose and followed FOV. Edit Mode display updates do not write to the Camera or settings. Gizmos do not preview display lens correction, view assignment, Volume effects, or the composite result. They are shown for a regular perspective Camera with valid finite settings. `QuiltRenderer` does not support Orthographic cameras.

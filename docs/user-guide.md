# ViewWeave User Guide

[日本語](ja/user-guide.md)

> For Unity creators using ViewWeave in a project. This guide covers installation, preview, camera framing, quality, capture, and playback. For implementation details, test procedures, and dated evidence, use the [Technical Guide](technical-guide.md). The [documentation index](index.md) has both entrances and separate license links.

## Install and open a sample

Use Unity 6000.6.4f1 with a URP or HDRP project. Install one self-contained package from **Package Manager > + > Install package from git URL**:

- URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`
- HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`

Import **URP Quick Start** or **HDRP Quick Start** from the package's **Samples** tab, then open its `QuickStart.unity` scene. The scene contains a configured Rig and animated sample objects.

For a step-by-step walkthrough with screenshots, see [Getting Started](getting-started.md). To preview the sample, select **ViewWeave Bridge Rig** and choose **Preview > Start Local Edit Mode Preview** for an Editor view, or **Start Looking Glass Device Preview** for a connected display. Device output needs the official runtime and a supported display. Review [Compatibility](compatibility.md) for current conditions; the Windows Quick Start scene has not been validated on Windows.

## Frame a scene

Select a ViewWeave Rig and use its Inspector or open `Window > ViewWeave > Rig Authoring`. The window keeps its assigned Rig when you change the Unity selection. Use **Camera & Focus** to choose the Reference Camera, set framing, and adjust focus.

The **Focus Target** field is a temporary Editor selection. Assign a scene object, then click **Set Focus From Target Center** to apply its center as the zero-parallax distance. The selected target itself is not saved; the applied focus distance is saved with the scene. For a fuller camera workflow, see [Quilt camera authoring](quilt-authoring.md).

The scene sample is static during Edit Mode Preview. Press **Play** to run its Animator clips and see the Cube rotate and Sphere move. To show that animation on a connected display, explicitly choose **Bridge > Start Output** in the Rig; output does not start automatically. The sample helper selects the platform's output component but does not start it. Stop Play before changing scene content or using edit-time tools that require a stopped Editor.

In the Rig's **Bridge** section, use **Start Output** and **Stop Output** to control the regular Play Mode output and check its status. Set a runtime path only when automatic Bridge discovery does not find the installed runtime. This is separate from the Edit Mode device Preview controls. The **Advanced** section contains lower-level options; begin with the other Rig sections for normal use.

## Choose rendering cost and resolution

Under **Rendering**, **Quilt View Render Mode** controls how many viewpoints are rendered. **Full** renders all views. **Half** and **Quarter** render fewer views, which can lower GPU work; as you move sideways, the image can change in larger steps. Choose Full when view-to-view continuity matters more than render cost. Actual performance depends on the scene and GPU.

**Output Quality** controls per-view render resolution separately from view count. The available Preview, Balanced, and Native choices use progressively larger fractions of the connected display profile's resolution. A local preview uses the Rig's local Quilt profile. Start at a lower setting if rendering is too slow, then check the image on the target display. See [Compatibility](compatibility.md) for tested boundaries and [Performance](performance.md) for dated measurements.

## Preview on the Editor or display

In **Preview**, choose **Start Local Edit Mode Preview** to inspect the Quilt in the Editor or **Start Looking Glass Device Preview** to send it to a connected display. These are explicit actions. A local preview does not require a display; device preview requires a supported output path and its prerequisites. Use Pause/Resume to hold or continue an active preview, and Close Preview when finished. Preview does not change the saved scene's preview state.

For macOS Looking Glass Go setup and its current boundaries, follow the [macOS Metal guide](macos-metal.md). For other platform combinations, see [Compatibility](compatibility.md).

## Save, record, and play back

The Rig's **Capture & Playback** section opens the tools for the assigned renderer. Use [Quilt Capture](quilt-capture.md) to save a still Quilt or record a PNG sequence. If you also need sound, use [Quilt PNG and audio recording](quilt-recording-audio.md); the WAV is saved separately. To review saved output, use [Quilt Playback](quilt-playback.md). To create a silent MP4 from a completed sequence, follow [Quilt Video Export](quilt-video-export.md).

Playback uses an existing active output and does not set up a new display session by itself. Follow the requirements in the relevant page before starting capture or playback.

## Optional features

- [VFX Graph](vfx-graph.md) lists the current pipeline and color-space conditions. It does not promise support for every graph or effect.
- Quick Start details are also available for [URP](../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/QuickStartURP/README.md) and [HDRP](../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/QuickStartHDRP/README.md).
- The optional **Windows URP Lit** and **Windows HDRP Lit** Samples remain available for generated Lit scenes. They are separate from Quick Start; see [Getting Started](getting-started.md#import-optional-samples) before importing them. Their separate guides cover [URP Lit](../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/WindowsUrpLit/README.md) and [HDRP Lit](../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/WindowsHdrpLit/README.md).

For setup errors or a platform decision, start with [Compatibility](compatibility.md). For design, diagnostics, and test results, continue to the [Technical Guide](technical-guide.md).

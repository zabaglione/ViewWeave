[日本語](README.ja.md)

# HDRP Quick Start

[User Guide](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/user-guide.md) · [Technical Guide](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/technical-guide.md)

This sample includes a configured ViewWeave Rig, colored primitives, and looping Animator clips. No sample generator or object script is needed. HDRP projects require Linear color space.

1. Import **HDRP Quick Start** from Package Manager's **Samples** tab.
2. Open `QuickStart.unity` from the imported sample.
3. Select **ViewWeave Bridge Rig** and start **Preview > Start Looking Glass Device Preview**. Without a display, choose **Start Local Edit Mode Preview**.

Device output requires the official Looking Glass Bridge runtime and a connected display. Local-package import, Editor rendering, device output, and Player checks have separate scopes. See [Compatibility](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/compatibility.md) for current OS/API conditions; Editor success alone does not establish device output or Player support.

The scene's **Focus Target** is an Editor-only selection. The applied focus distance is stored in the scene. **Full** renders more viewpoints; **Quarter** can reduce GPU work but show larger changes as you move sideways. **Output Quality** changes resolution independently. Edit Mode Preview shows a static scene; press **Play** to run its Animator clips. Output to a connected display starts automatically by default, matching a menu-created Rig. The helper selects the platform output and preserves your startup preference. Disable **Start Output On Enable** (Metal) or **Start Session On Enable** (Windows) for manual startup via **Bridge > Start Output**. Earlier imported copies need reimporting or the startup option enabled; back up edited scenes first.

[Quick Start and illustrated setup](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/getting-started.md)

[日本語](README.ja.md)

# URP Quick Start

[User Guide](https://github.com/zabaglione/ViewWeave/blob/main/docs/user-guide.md) · [Technical Guide](https://github.com/zabaglione/ViewWeave/blob/main/docs/technical-guide.md)

This sample includes a configured ViewWeave Rig, colored primitives, and looping Animator clips. No sample generator or object script is needed.

1. Import **URP Quick Start** from Package Manager's **Samples** tab.
2. Open `QuickStart.unity` from the imported sample.
3. Select **ViewWeave Bridge Rig** and start **Preview > Start Looking Glass Device Preview**. Without a display, choose **Start Local Edit Mode Preview**.

Device output requires the official Looking Glass Bridge runtime and a connected display. Local-package import, Editor rendering, device output, and Player checks have separate scopes. See [Compatibility](https://github.com/zabaglione/ViewWeave/blob/main/docs/compatibility.md) for current OS/API conditions; Editor success alone does not establish device output or Player support.

The scene's **Focus Target** is an Editor-only selection. The applied focus distance is stored in the scene. **Full** renders more viewpoints; **Quarter** can reduce GPU work but show larger changes as you move sideways. **Output Quality** changes resolution independently. Edit Mode Preview shows a static scene; press **Play** to run its Animator clips. To send the animation to a connected display, explicitly choose **Bridge > Start Output**. Output does not start automatically; the sample helper only selects the platform output component.

[Quick Start and illustrated setup](https://github.com/zabaglione/ViewWeave/blob/main/docs/getting-started.md)

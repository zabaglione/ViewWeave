using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave.Samples.QuickStartURP
{
    // Selects only the transport. Rendering, quality, focus and start/stop remain on the Rig.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("")]
    public sealed class QuickStartPlatformOutput : MonoBehaviour
    {
        bool selectionPending;

        void OnEnable() { selectionPending = true; SelectOutput(); }
        void Start() { SelectOutput(); }
        void Update() { if (selectionPending) SelectOutput(); }
        void OnDisable() { selectionPending = false; }

        void SelectOutput()
        {
            var rig = GetComponent<ViewWeaveRig>();
            var metal = GetComponent<BridgeMetalQuiltOutput>();
            var worker = GetComponent<BridgeWorkerQuiltOutput>();
            var session = GetComponent<ViewWeaveBridgeSession>();
            if (rig == null || metal == null || worker == null || session == null) { selectionPending = false; return; }
            var api = SystemInfo.graphicsDeviceType;
            bool useMetal = api == GraphicsDeviceType.Metal;
            bool useWindows = api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12;
            MonoBehaviour selected = useMetal ? (MonoBehaviour)metal : worker;
            if ((useMetal || useWindows) && rig.Renderer != null && rig.Renderer.OutputBehaviour == selected && metal.enabled == useMetal && worker.enabled == useWindows && session.enabled == useWindows)
            {
                selectionPending = false;
                return;
            }
            // Preview, recording and playback can retain the renderer while GPU work retires.
            if (rig.Renderer != null && !rig.Renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out _)) return;
            worker.initializeOnEnable = false;
            metal.enabled = useMetal;
            worker.enabled = useWindows;
            session.enabled = useWindows;
            selectionPending = false;
            if (!useMetal && !useWindows)
            {
                Debug.LogWarning("ViewWeave Quick Start requires Metal, Direct3D11 or Direct3D12.");
                return;
            }
            if (rig.Renderer == null || rig.Renderer.OutputBehaviour != selected) rig.SetOutput(selected);
            rig.ConfigureComponents();
        }
    }
}

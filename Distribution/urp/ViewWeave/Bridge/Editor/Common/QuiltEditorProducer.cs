using System;
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    internal static class QuiltEditorProducer
    {
        internal static ViewWeaveRig Create(ViewWeaveRig source, out QuiltEditorResourceRecord resources)
        {
            var host = new GameObject("ViewWeave Editor Quilt Producer")
            { hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset };
            host.SetActive(false);
            Camera camera = host.AddComponent<Camera>();
            camera.CopyFrom(source.Renderer.sourceCamera);
            camera.enabled = false;
            camera.targetTexture = null;
            QuiltRenderer renderer = host.AddComponent<QuiltRenderer>();
            resources = QuiltEditorResourceRecord.Create(host, renderer);
            if (!renderer.BeginEditorPreviewRetention(resources)) throw new InvalidOperationException("Editor producer retention owner could not be attached.");
            var rig = host.AddComponent<ViewWeaveRig>();
            rig.ApplyProfile(source.Profile);
            if (!ViewWeaveRigProviderRegistry.TryAddAdapter(host, source.Renderer.EffectiveCamera, out MonoBehaviour adapter))
                throw new InvalidOperationException("The active pipeline has no ViewWeave adapter provider.");
            if (source.Renderer.GuardBehaviour != null && source.Renderer.GuardBehaviour.GetType() == adapter.GetType())
                EditorUtility.CopySerialized(source.Renderer.GuardBehaviour, adapter);
            foreach (MonoBehaviour component in source.Renderer.sourceCamera.GetComponents<MonoBehaviour>())
            {
                if (component == null || component is QuiltRenderer || component is ViewWeaveRig || component is IQuiltOutput || component is IQuiltRenderGuard) continue;
                Component destination = camera.GetComponent(component.GetType());
                if (destination != null) EditorUtility.CopySerialized(component, destination);
            }
            rig.SetGuard(adapter);
            rig.SetOutput(null);
            rig.AutoRender = false;
            if (!rig.ConfigureComponents()) throw new InvalidOperationException("The Editor producer could not configure its adapter.");
            Sync(source, rig, false);
            host.SetActive(true);
            return rig;
        }

        internal static void Sync(ViewWeaveRig source, ViewWeaveRig producer, bool deviceProfile)
        {
            QuiltRenderer input = source.Renderer, output = producer.Renderer;
            Camera camera = output.sourceCamera;
            camera.CopyFrom(input.sourceCamera);
            camera.enabled = false;
            camera.targetTexture = null;
            camera.transform.SetPositionAndRotation(input.sourceCamera.transform.position, input.sourceCamera.transform.rotation);
            output.referenceCamera = input.referenceCamera;
            output.followReferenceFieldOfView = input.followReferenceFieldOfView;
            output.useRenderRequests = input.useRenderRequests;
            output.settings = input.settings.Clone();
            if (deviceProfile) producer.Profile.ApplyTo(output.settings);
            output.physicalFraming = input.physicalFraming == null ? new QuiltPhysicalFraming() : input.physicalFraming.Clone();
            output.viewRenderMode = input.viewRenderMode;
            output.autoRender = false;
        }
    }
}

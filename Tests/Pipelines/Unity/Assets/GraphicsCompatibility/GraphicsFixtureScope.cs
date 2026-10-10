#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

namespace ViewWeave.GraphicsCompatibility
{
    internal sealed class GraphicsFixtureScope : System.IDisposable
    {
        public const int Layer = 31;

        readonly Scene previousScene;
        readonly Scene isolatedScene;
        readonly GameObject root;
        readonly List<Behaviour> disabledBehaviours = new List<Behaviour>();
        readonly Light previousSun;
        public AsyncOperation UnloadOperation { get; private set; }

        GraphicsFixtureScope(GameObject fixtureRoot)
        {
            root = fixtureRoot;
            previousScene = SceneManager.GetActiveScene();
            previousSun = RenderSettings.sun;
            isolatedScene = SceneManager.CreateScene("GraphicsCompatibility_Isolated");
            SceneManager.MoveGameObjectToScene(root, isolatedScene);
            SceneManager.SetActiveScene(isolatedScene);
        }

        public static GraphicsFixtureScope Create(GameObject fixtureRoot)
        {
            if (fixtureRoot == null)
                throw new System.ArgumentNullException(nameof(fixtureRoot));
            return new GraphicsFixtureScope(fixtureRoot);
        }

        public void Apply(Camera camera)
        {
            SetLayerRecursively(root, Layer);
            if (camera != null)
                camera.cullingMask = 1 << Layer;

            foreach (var light in root.GetComponentsInChildren<Light>(true))
                light.cullingMask = 1 << Layer;

            foreach (var light in FindLoadedComponents<Light>())
            {
                if (IsOwned(light.gameObject))
                    continue;
                if (light.enabled)
                {
                    light.enabled = false;
                    disabledBehaviours.Add(light);
                }
            }

            foreach (var otherCamera in FindLoadedComponents<Camera>())
            {
                if (IsOwned(otherCamera.gameObject))
                    continue;
                if (otherCamera.enabled)
                {
                    otherCamera.enabled = false;
                    disabledBehaviours.Add(otherCamera);
                }
            }

            var fixtureLight = root.GetComponentInChildren<Light>(true);
            if (fixtureLight != null)
                RenderSettings.sun = fixtureLight;
        }

        IEnumerable<T> FindLoadedComponents<T>() where T : Component
        {
            for (int index = 0; index < SceneManager.sceneCount; ++index)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded)
                    continue;
                foreach (var sceneRoot in scene.GetRootGameObjects())
                {
                    foreach (var component in sceneRoot.GetComponentsInChildren<T>(true))
                        yield return component;
                }
            }
        }

        bool IsOwned(GameObject item)
        {
            return item == root || item.transform.IsChildOf(root.transform);
        }

        static void SetLayerRecursively(GameObject item, int layer)
        {
            item.layer = layer;
            foreach (Transform child in item.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        public void Dispose()
        {
            foreach (var behaviour in disabledBehaviours)
            {
                if (behaviour != null)
                    behaviour.enabled = true;
            }
            if (previousScene.IsValid() && previousScene.isLoaded)
                SceneManager.SetActiveScene(previousScene);
            RenderSettings.sun = previousSun;
            if (isolatedScene.IsValid() && isolatedScene.isLoaded)
                UnloadOperation = SceneManager.UnloadSceneAsync(isolatedScene);
        }
    }
}
#endif

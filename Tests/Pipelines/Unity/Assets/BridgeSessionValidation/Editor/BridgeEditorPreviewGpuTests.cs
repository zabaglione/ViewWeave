using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.Editor;
using ViewWeave.HDRP;
using ViewWeave.URP;
using Object = UnityEngine.Object;

namespace ViewWeave.BridgeEditorPreviewValidation
{
    public sealed class BridgeEditorPreviewGpuTests
    {
        const string DevicePlayTransitionRigName = "ViewWeave_DevicePreviewPlayTransition_Rig";
        const string DevicePlayTransitionTargetName = "ViewWeave_DevicePreviewPlayTransition_Target";
        const string DevicePlayTransitionPipelineName = "ViewWeave_DevicePreviewPlayTransition_Pipeline";
        const string DevicePlayTransitionRendererDataName = "ViewWeave_DevicePreviewPlayTransition_RendererData";
        const string DevicePlayTransitionStatePrefix = "ViewWeave.EditorPreview.DevicePlayTransition.";
        const string DevicePlayTransitionAssetFolderPrefix = "Assets/__ViewWeaveDevicePlayTransition_";

        [UnityTest]
        public IEnumerator DisplaySnapshotCacheIdentitySurvivesOptions3AndDiffersForDuplicates()
        {
            if (Environment.GetEnvironmentVariable("VIEWWEAVE_DISPLAY_SNAPSHOT_CACHE_IDENTITY_TEST") != "1")
                Assert.Ignore("Set VIEWWEAVE_DISPLAY_SNAPSHOT_CACHE_IDENTITY_TEST=1 to run the SDK-free identity proof.");

            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            bool previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions previousOptions = EditorSettings.enterPlayModeOptions;
            string folder = "Assets/__ViewWeaveSnapshotIdentity_" + Guid.NewGuid().ToString("N");
            string savedScenePath = folder + "/Identity.unity";
            Scene savedScene = default;
            Scene unsavedScene = default;
            bool enteredPlay = false;
            string savedRootName = "ViewWeave_SnapshotIdentity_Saved_" + Guid.NewGuid().ToString("N");
            string savedDuplicateName = savedRootName + "_Duplicate";
            string unsavedRootName = "ViewWeave_SnapshotIdentity_Unsaved_" + Guid.NewGuid().ToString("N");
            string unsavedDuplicateName = unsavedRootName + "_Duplicate";
            try
            {
                if (string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder))))
                    throw new InvalidOperationException("Could not create the uniquely owned identity-test folder.");

                savedScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject savedRoot = CreateIdentityOutput(savedRootName, savedScene);
                GameObject savedDuplicate = Object.Instantiate(savedRoot);
                savedDuplicate.name = savedDuplicateName;
                SceneManager.MoveGameObjectToScene(savedDuplicate, savedScene);
                if (!EditorSceneManager.SaveScene(savedScene, savedScenePath))
                    throw new InvalidOperationException("Could not save the uniquely owned identity-test scene.");

                unsavedScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                GameObject unsavedRoot = CreateIdentityOutput(unsavedRootName, unsavedScene);
                GameObject unsavedDuplicate = Object.Instantiate(unsavedRoot);
                unsavedDuplicate.name = unsavedDuplicateName;
                SceneManager.MoveGameObjectToScene(unsavedDuplicate, unsavedScene);
                EditorSceneManager.SetActiveScene(unsavedScene);

                BridgeWorkerQuiltOutput savedOutput = savedRoot.GetComponent<BridgeWorkerQuiltOutput>();
                BridgeWorkerQuiltOutput savedDuplicateOutput = savedDuplicate.GetComponent<BridgeWorkerQuiltOutput>();
                BridgeWorkerQuiltOutput unsavedOutput = unsavedRoot.GetComponent<BridgeWorkerQuiltOutput>();
                BridgeWorkerQuiltOutput unsavedDuplicateOutput = unsavedDuplicate.GetComponent<BridgeWorkerQuiltOutput>();
                int savedHashBefore = savedOutput.GetHashCode();
                int savedDuplicateHashBefore = savedDuplicateOutput.GetHashCode();
                int unsavedHashBefore = unsavedOutput.GetHashCode();
                int unsavedDuplicateHashBefore = unsavedDuplicateOutput.GetHashCode();
                string savedGlobalIdBefore = GlobalObjectId.GetGlobalObjectIdSlow(savedOutput).ToString();
                string savedDuplicateGlobalIdBefore = GlobalObjectId.GetGlobalObjectIdSlow(savedDuplicateOutput).ToString();
                string unsavedGlobalIdBefore = GlobalObjectId.GetGlobalObjectIdSlow(unsavedOutput).ToString();
                string unsavedDuplicateGlobalIdBefore = GlobalObjectId.GetGlobalObjectIdSlow(unsavedDuplicateOutput).ToString();

                Assert.IsFalse(savedOutput.initializeOnEnable || savedDuplicateOutput.initializeOnEnable ||
                    unsavedOutput.initializeOnEnable || unsavedDuplicateOutput.initializeOnEnable,
                    "All test outputs must remain SDK-disabled.");
                Assert.IsFalse(savedRoot.GetComponent<ViewWeaveBridgeSession>().startOnEnable ||
                    savedDuplicate.GetComponent<ViewWeaveBridgeSession>().startOnEnable ||
                    unsavedRoot.GetComponent<ViewWeaveBridgeSession>().startOnEnable ||
                    unsavedDuplicate.GetComponent<ViewWeaveBridgeSession>().startOnEnable,
                    "All test Sessions must remain stopped.");

                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
                Debug.Log("DISPLAY_SNAPSHOT_IDENTITY phase=before_play options=3 savedHash=" + savedHashBefore +
                    " savedDuplicateHash=" + savedDuplicateHashBefore + " unsavedHash=" + unsavedHashBefore +
                    " unsavedDuplicateHash=" + unsavedDuplicateHashBefore + " savedGlobalId=" + savedGlobalIdBefore +
                    " savedDuplicateGlobalId=" + savedDuplicateGlobalIdBefore + " unsavedGlobalId=" + unsavedGlobalIdBefore +
                    " unsavedDuplicateGlobalId=" + unsavedDuplicateGlobalIdBefore);
                yield return new EnterPlayMode();
                enteredPlay = true;

                BridgeWorkerQuiltOutput savedOutputAfter = FindIdentityOutput(savedRootName);
                BridgeWorkerQuiltOutput savedDuplicateOutputAfter = FindIdentityOutput(savedDuplicateName);
                BridgeWorkerQuiltOutput unsavedOutputAfter = FindIdentityOutput(unsavedRootName);
                BridgeWorkerQuiltOutput unsavedDuplicateOutputAfter = FindIdentityOutput(unsavedDuplicateName);
                Assert.NotNull(savedOutputAfter, "The saved-scene output must be reacquired after Play entry.");
                Assert.NotNull(savedDuplicateOutputAfter, "The saved-scene duplicate must be reacquired after Play entry.");
                Assert.NotNull(unsavedOutputAfter, "The unsaved-scene output must be reacquired after Play entry.");
                Assert.NotNull(unsavedDuplicateOutputAfter, "The unsaved-scene duplicate must be reacquired after Play entry.");
                string savedGlobalIdAfter = GlobalObjectId.GetGlobalObjectIdSlow(savedOutputAfter).ToString();
                string savedDuplicateGlobalIdAfter = GlobalObjectId.GetGlobalObjectIdSlow(savedDuplicateOutputAfter).ToString();
                Assert.AreEqual(savedHashBefore, savedOutputAfter.GetHashCode(), "Saved-scene wrapper identity must survive options3 Play entry.");
                Assert.AreEqual(savedDuplicateHashBefore, savedDuplicateOutputAfter.GetHashCode(),
                    "Saved duplicate wrapper identity must survive options3 Play entry.");
                Assert.AreEqual(unsavedHashBefore, unsavedOutputAfter.GetHashCode(), "Unsaved-scene wrapper identity must survive options3 Play entry.");
                Assert.AreEqual(unsavedDuplicateHashBefore, unsavedDuplicateOutputAfter.GetHashCode(),
                    "Unsaved duplicate wrapper identity must survive options3 Play entry.");
                Assert.AreEqual(savedGlobalIdBefore, savedGlobalIdAfter, "Saved-scene GlobalObjectId must survive options3 Play entry.");
                Assert.AreEqual(savedDuplicateGlobalIdBefore, savedDuplicateGlobalIdAfter,
                    "Saved duplicate GlobalObjectId must survive options3 Play entry.");
                string unsavedGlobalIdAfter = GlobalObjectId.GetGlobalObjectIdSlow(unsavedOutputAfter).ToString();
                string unsavedDuplicateGlobalIdAfter = GlobalObjectId.GetGlobalObjectIdSlow(unsavedDuplicateOutputAfter).ToString();
                Debug.Log("DISPLAY_SNAPSHOT_IDENTITY phase=in_play savedHash=" + savedOutputAfter.GetHashCode() +
                    " savedDuplicateHash=" + savedDuplicateOutputAfter.GetHashCode() +
                    " unsavedHash=" + unsavedOutputAfter.GetHashCode() +
                    " unsavedDuplicateHash=" + unsavedDuplicateOutputAfter.GetHashCode() +
                    " savedGlobalId=" + savedGlobalIdAfter + " savedDuplicateGlobalId=" + savedDuplicateGlobalIdAfter +
                    " unsavedGlobalId=" + unsavedGlobalIdAfter + " unsavedDuplicateGlobalId=" + unsavedDuplicateGlobalIdAfter +
                    " savedState=" + savedOutputAfter.State + " unsavedState=" + unsavedOutputAfter.State);
                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, savedOutputAfter.State);
                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, unsavedOutputAfter.State);

                yield return new ExitPlayMode();
                enteredPlay = false;
                savedOutputAfter = FindIdentityOutput(savedRootName);
                savedDuplicateOutputAfter = FindIdentityOutput(savedDuplicateName);
                unsavedOutputAfter = FindIdentityOutput(unsavedRootName);
                unsavedDuplicateOutputAfter = FindIdentityOutput(unsavedDuplicateName);
                Assert.NotNull(savedOutputAfter);
                Assert.NotNull(savedDuplicateOutputAfter);
                Assert.NotNull(unsavedOutputAfter);
                Assert.NotNull(unsavedDuplicateOutputAfter);
                string savedGlobalIdAfterExit = GlobalObjectId.GetGlobalObjectIdSlow(savedOutputAfter).ToString();
                string savedDuplicateGlobalIdAfterExit = GlobalObjectId.GetGlobalObjectIdSlow(savedDuplicateOutputAfter).ToString();
                string unsavedGlobalIdAfterExit = GlobalObjectId.GetGlobalObjectIdSlow(unsavedOutputAfter).ToString();
                string unsavedDuplicateGlobalIdAfterExit = GlobalObjectId.GetGlobalObjectIdSlow(unsavedDuplicateOutputAfter).ToString();

                Assert.AreEqual(savedHashBefore, savedOutputAfter.GetHashCode(), "Saved-scene wrapper identity must survive options3.");
                Assert.AreEqual(savedDuplicateHashBefore, savedDuplicateOutputAfter.GetHashCode(), "Saved duplicate identity must survive options3.");
                Assert.AreEqual(unsavedHashBefore, unsavedOutputAfter.GetHashCode(), "Unsaved-scene wrapper identity must survive options3.");
                Assert.AreEqual(unsavedDuplicateHashBefore, unsavedDuplicateOutputAfter.GetHashCode(), "Unsaved duplicate identity must survive options3.");
                Assert.AreNotEqual(savedHashBefore, savedDuplicateHashBefore, "Saved-scene duplicates must have distinct wrapper identity.");
                Assert.AreNotEqual(unsavedHashBefore, unsavedDuplicateHashBefore, "Unsaved-scene duplicates must have distinct wrapper identity.");
                Assert.AreEqual(savedGlobalIdBefore, savedGlobalIdAfterExit, "Saved-scene GlobalObjectId must survive options3.");
                Assert.AreEqual(savedDuplicateGlobalIdBefore, savedDuplicateGlobalIdAfterExit,
                    "Saved duplicate GlobalObjectId must survive options3.");
                Assert.AreNotEqual(savedGlobalIdBefore, savedDuplicateGlobalIdBefore,
                    "Saved-scene duplicate GlobalObjectId must be distinct.");
                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, savedOutputAfter.State);
                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, unsavedOutputAfter.State);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle,
                    savedOutputAfter.GetComponent<ViewWeaveBridgeSession>().State);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle,
                    unsavedOutputAfter.GetComponent<ViewWeaveBridgeSession>().State);
                Debug.Log("DISPLAY_SNAPSHOT_IDENTITY phase=after_exit savedHash=" + savedOutputAfter.GetHashCode() +
                    " savedDuplicateHash=" + savedDuplicateOutputAfter.GetHashCode() +
                    " unsavedHash=" + unsavedOutputAfter.GetHashCode() +
                    " unsavedDuplicateHash=" + unsavedDuplicateOutputAfter.GetHashCode() +
                    " savedGlobalId=" + savedGlobalIdAfterExit + " savedDuplicateGlobalId=" + savedDuplicateGlobalIdAfterExit +
                    " unsavedGlobalId=" + unsavedGlobalIdAfterExit + " unsavedDuplicateGlobalId=" + unsavedDuplicateGlobalIdAfterExit +
                    " outputsClosed=true sessionsIdle=true");
            }
            finally
            {
                if (Application.isPlaying && enteredPlay)
                {
                    EditorApplication.isPlaying = false;
                    EditorApplication.delayCall += () => RestoreIdentityTestState(
                        previousSceneSetup, previousOptionsEnabled, previousOptions, folder);
                }
                else RestoreIdentityTestState(previousSceneSetup, previousOptionsEnabled, previousOptions, folder);
            }
        }

        static void RestoreIdentityTestState(
            SceneSetup[] previousSceneSetup,
            bool previousOptionsEnabled,
            EnterPlayModeOptions previousOptions,
            string folder)
        {
            if (Application.isPlaying)
            {
                EditorApplication.delayCall += () => RestoreIdentityTestState(
                    previousSceneSetup, previousOptionsEnabled, previousOptions, folder);
                return;
            }
            EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
            EditorSettings.enterPlayModeOptions = previousOptions;
            if (previousSceneSetup != null && previousSceneSetup.Length > 0)
                EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            AssetDatabase.Refresh();
        }

        static GameObject CreateIdentityOutput(string name, Scene scene)
        {
            var gameObject = new GameObject(name);
            gameObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            ViewWeaveBridgeSession session = gameObject.AddComponent<ViewWeaveBridgeSession>();
            BridgeWorkerQuiltOutput output = gameObject.GetComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            session.startOnEnable = false;
            gameObject.SetActive(true);
            return gameObject;
        }

        static BridgeWorkerQuiltOutput FindIdentityOutput(string gameObjectName)
        {
            BridgeWorkerQuiltOutput found = null;
            foreach (BridgeWorkerQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeWorkerQuiltOutput>())
            {
                if (output == null || output.gameObject.name != gameObjectName || EditorUtility.IsPersistent(output)) continue;
                if (!output.gameObject.scene.IsValid() || !output.gameObject.scene.isLoaded) continue;
                if (found != null) return null;
                found = output;
            }
            return found;
        }

        [Test]
        public void EditorSnapshotCacheLazilyRestoresExactSnapshotWithoutReplayingRefreshEvent()
        {
            GameObject gameObject;
            BridgeWorkerQuiltOutput output = CreateSnapshotCacheOutput("Snapshot cache restore", out gameObject);
            int eventCount = 0;
            output.ConnectedDisplaySnapshotChanged += _ => eventCount++;
            try
            {
                BridgeDisplaySnapshot original = CreateSnapshotForCacheTests(0x1020304050607080UL);
                BridgeDisplaySnapshotEditorCache cache = BridgeDisplaySnapshotEditorCache.TryCreate(
                    output, original, false, output.runtimePath, output.applicationName, output.displayOrdinal);
                Assert.NotNull(cache, "A valid successful snapshot should be cacheable for this loaded scene output.");
                SetSnapshotCache(output, cache);
                SetOutputSnapshotField(output, null);

                BridgeDisplaySnapshot restored = output.ConnectedDisplaySnapshot;
                Assert.NotNull(restored, "The public getter should lazily restore the serialized Editor snapshot.");
                Assert.AreEqual(original.Generation, restored.Generation);
                Assert.AreEqual(original.Displays.Count, restored.Displays.Count);
                Assert.IsTrue(original.TryGetDisplay(0, out BridgeDisplayProfile originalPhysical));
                Assert.IsTrue(restored.TryGetDisplay(0, out BridgeDisplayProfile restoredPhysical));
                Assert.AreEqual(originalPhysical.DisplayIndex, restoredPhysical.DisplayIndex);
                Assert.AreEqual(originalPhysical.Width, restoredPhysical.Width);
                Assert.AreEqual(originalPhysical.Height, restoredPhysical.Height);
                Assert.AreEqual(originalPhysical.Aspect, restoredPhysical.Aspect);
                Assert.AreEqual(originalPhysical.Viewcone, restoredPhysical.Viewcone);
                Assert.AreEqual(originalPhysical.QuiltWidth, restoredPhysical.QuiltWidth);
                Assert.AreEqual(originalPhysical.Columns, restoredPhysical.Columns);
                Assert.AreEqual(originalPhysical.PhysicalWidthMillimeters, restoredPhysical.PhysicalWidthMillimeters);
                Assert.AreEqual(originalPhysical.PhysicalHeightMillimeters, restoredPhysical.PhysicalHeightMillimeters);
                Assert.AreEqual(originalPhysical.CalibrationDpi, restoredPhysical.CalibrationDpi);
                Assert.AreEqual(0, eventCount, "Lazy cache restoration must not replay the successful-refresh event.");
                Assert.AreSame(restored, output.CurrentConfigurationDisplaySnapshot);
            }
            finally { Object.DestroyImmediate(gameObject); }
        }

        [Test]
        public void EditorSnapshotCacheRejectsChangedScopeEpochSchemaAndDuplicateIdentity()
        {
            GameObject scopeObject;
            BridgeWorkerQuiltOutput scopeOutput = CreateSnapshotCacheOutput("Snapshot cache scope", out scopeObject);
            GameObject cloneObject = null;
            GameObject duplicateObject = null;
            GameObject epochObject = null;
            GameObject schemaObject = null;
            try
            {
                BridgeDisplaySnapshot snapshot = CreateSnapshotForCacheTests(7);
                SetSnapshotCache(scopeOutput, BridgeDisplaySnapshotEditorCache.TryCreate(scopeOutput, snapshot,
                    false, scopeOutput.runtimePath, scopeOutput.applicationName, scopeOutput.displayOrdinal));
                SetOutputSnapshotField(scopeOutput, null);
                scopeOutput.applicationName = "ChangedApplication";
                Assert.IsNull(scopeOutput.ConnectedDisplaySnapshot, "A raw Session/output scope change must invalidate the cache.");
                Assert.IsNull(GetSnapshotCache(scopeOutput), "Rejected scope data must not be resurrected later.");

                BridgeWorkerQuiltOutput cloneSource = CreateSnapshotCacheOutput("Snapshot cache clone source", out cloneObject);
                SetSnapshotCache(cloneSource, BridgeDisplaySnapshotEditorCache.TryCreate(cloneSource, snapshot,
                    false, cloneSource.runtimePath, cloneSource.applicationName, cloneSource.displayOrdinal));
                duplicateObject = Object.Instantiate(cloneObject);
                duplicateObject.name = "Snapshot cache duplicate";
                BridgeWorkerQuiltOutput duplicate = duplicateObject.GetComponent<BridgeWorkerQuiltOutput>();
                SetOutputSnapshotField(duplicate, null);
                Assert.IsNull(duplicate.ConnectedDisplaySnapshot, "A duplicated output must not inherit another output's cache.");
                Assert.IsNull(GetSnapshotCache(duplicate), "Rejected duplicate cache data must be cleared.");

                epochObject = new GameObject("Snapshot cache stale epoch");
                epochObject.SetActive(false);
                BridgeWorkerQuiltOutput epochOutput = epochObject.AddComponent<BridgeWorkerQuiltOutput>();
                epochOutput.initializeOnEnable = false;
                epochObject.SetActive(true);
                BridgeDisplaySnapshotEditorCache staleEpoch = BridgeDisplaySnapshotEditorCache.TryCreate(epochOutput,
                    snapshot, false, epochOutput.runtimePath, epochOutput.applicationName, epochOutput.displayOrdinal);
                SetField(staleEpoch, "editorEpoch", "previous-editor-session");
                SetSnapshotCache(epochOutput, staleEpoch);
                Assert.IsNull(epochOutput.ConnectedDisplaySnapshot, "A cache from another Editor session must be rejected.");
                Assert.IsNull(GetSnapshotCache(epochOutput));

                schemaObject = new GameObject("Snapshot cache unsupported schema");
                schemaObject.SetActive(false);
                BridgeWorkerQuiltOutput schemaOutput = schemaObject.AddComponent<BridgeWorkerQuiltOutput>();
                schemaOutput.initializeOnEnable = false;
                schemaObject.SetActive(true);
                BridgeDisplaySnapshotEditorCache unsupportedSchema = BridgeDisplaySnapshotEditorCache.TryCreate(schemaOutput,
                    snapshot, false, schemaOutput.runtimePath, schemaOutput.applicationName, schemaOutput.displayOrdinal);
                SetField(unsupportedSchema, "schemaVersion", 0);
                SetSnapshotCache(schemaOutput, unsupportedSchema);
                Assert.IsNull(schemaOutput.ConnectedDisplaySnapshot, "An unsupported cache schema must be rejected.");
                Assert.IsNull(GetSnapshotCache(schemaOutput));
            }
            finally
            {
                if (duplicateObject != null) Object.DestroyImmediate(duplicateObject);
                if (cloneObject != null) Object.DestroyImmediate(cloneObject);
                if (epochObject != null) Object.DestroyImmediate(epochObject);
                if (schemaObject != null) Object.DestroyImmediate(schemaObject);
                Object.DestroyImmediate(scopeObject);
            }
        }

        [Test]
        public void EditorSnapshotCacheRejectsReconnectAndMalformedProfiles()
        {
            GameObject gameObject;
            BridgeWorkerQuiltOutput output = CreateSnapshotCacheOutput("Snapshot cache invalid profile", out gameObject);
            try
            {
                BridgeDisplaySnapshot snapshot = CreateSnapshotForCacheTests(9);
                BridgeDisplaySnapshotEditorCache cache = BridgeDisplaySnapshotEditorCache.TryCreate(output,
                    snapshot, false, output.runtimePath, output.applicationName, output.displayOrdinal);
                Assert.NotNull(cache);
                Array displays = (Array)GetField(cache, "displays");
                object first = displays.GetValue(0);
                SetField(first, "width", 0u);
                displays.SetValue(first, 0);
                SetSnapshotCache(output, cache);
                Assert.IsNull(output.ConnectedDisplaySnapshot, "A cached profile with invalid dimensions must be rejected.");
                Assert.IsNull(GetSnapshotCache(output));

                cache = BridgeDisplaySnapshotEditorCache.TryCreate(output,
                    snapshot, false, output.runtimePath, output.applicationName, output.displayOrdinal);
                SetSnapshotCache(output, cache);
                SetOutputRequiresReconnect(output, true);
                Assert.IsNull(output.ConnectedDisplaySnapshot, "A cache must not restore while reconnect is required.");
                Assert.IsNull(GetSnapshotCache(output));
            }
            finally { Object.DestroyImmediate(gameObject); }
        }

        [Test]
        public void EditorSnapshotCacheCapturedInUntitledSceneSurvivesSavingThatScene()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            bool previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions previousOptions = EditorSettings.enterPlayModeOptions;
            string folder = "Assets/__ViewWeaveSnapshotSaveIdentity_" + Guid.NewGuid().ToString("N");
            string scenePath = folder + "/SavedAfterRefresh.unity";
            GameObject gameObject = null;
            try
            {
                Assert.IsNotEmpty(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                BridgeWorkerQuiltOutput output = CreateSnapshotCacheOutput("Snapshot cache saved after refresh", out gameObject);
                BridgeDisplaySnapshotEditorCache cache = BridgeDisplaySnapshotEditorCache.TryCreate(output,
                    CreateSnapshotForCacheTests(11), false, output.runtimePath, output.applicationName, output.displayOrdinal);
                Assert.NotNull(cache);
                Assert.IsFalse((bool)GetField(cache, "usesGlobalObjectId"),
                    "An untitled-scene refresh must bind to the proven component identity hash.");
                SetSnapshotCache(output, cache);
                SetOutputSnapshotField(output, null);

                Scene scene = EditorSceneManager.GetActiveScene();
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, scenePath));
                Assert.AreNotEqual(0, GlobalObjectId.GetGlobalObjectIdSlow(output).identifierType,
                    "Saving the scene should give its component a persistent GlobalObjectId.");
                BridgeDisplaySnapshot restored = output.ConnectedDisplaySnapshot;
                Assert.NotNull(restored,
                    "A hash-bound cache should survive saving the scene without changing its output identity.");
                Assert.AreEqual(11UL, restored.Generation);
            }
            finally
            {
                if (gameObject != null) Object.DestroyImmediate(gameObject);
                EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
                EditorSettings.enterPlayModeOptions = previousOptions;
                if (previousSceneSetup != null && previousSceneSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                AssetDatabase.Refresh();
            }
        }

        static BridgeWorkerQuiltOutput CreateSnapshotCacheOutput(string name, out GameObject gameObject)
        {
            gameObject = new GameObject(name);
            gameObject.SetActive(false);
            BridgeWorkerQuiltOutput output = gameObject.AddComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            output.runtimePath = string.Empty;
            output.applicationName = "SnapshotCacheTest";
            output.displayOrdinal = 0;
            gameObject.SetActive(true);
            return output;
        }

        static BridgeDisplaySnapshot CreateSnapshotForCacheTests(ulong generation)
        {
            var physicalProfile = new BridgeNative.DisplayInfo
            {
                index = 0,
                width = 3840,
                height = 2160,
                aspect = 16f / 9f,
                viewcone = 40f,
                quiltWidth = 7680,
                quiltHeight = 4320,
                quiltColumns = 8,
                quiltRows = 6
            };
            int physicalSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.PhysicalDisplayInfoV1));
            var physicalCalibration = new BridgeWorkerNative.PhysicalDisplayInfoV1
            {
                structSize = (uint)physicalSize,
                version = 1,
                pixelWidth = 3840,
                pixelHeight = 2160,
                dpi = 283f,
                valid = 1
            };
            var noCalibrationProfile = new BridgeNative.DisplayInfo
            {
                index = 1,
                width = 1920,
                height = 1080,
                aspect = 16f / 9f,
                viewcone = 40f,
                quiltWidth = 4096,
                quiltHeight = 4096,
                quiltColumns = 8,
                quiltRows = 6
            };
            return new BridgeDisplaySnapshot(generation, new uint[] { 0, 3 }, new[]
            {
                new BridgeDisplayProfile(physicalProfile, physicalCalibration),
                new BridgeDisplayProfile(noCalibrationProfile)
            });
        }

        static void SetSnapshotCache(BridgeWorkerQuiltOutput output, BridgeDisplaySnapshotEditorCache cache) =>
            SetField(output, "editorConnectedDisplaySnapshotCache", cache);

        static BridgeDisplaySnapshotEditorCache GetSnapshotCache(BridgeWorkerQuiltOutput output) =>
            (BridgeDisplaySnapshotEditorCache)GetField(output, "editorConnectedDisplaySnapshotCache");

        static void SetOutputSnapshotField(BridgeWorkerQuiltOutput output, BridgeDisplaySnapshot snapshot) =>
            SetField(output, "connectedDisplaySnapshot", snapshot);

        static void SetOutputRequiresReconnect(BridgeWorkerQuiltOutput output, bool value) =>
            SetField(output, "<RequiresReconnect>k__BackingField", value);

        static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "Expected private test field " + name + " to exist.");
            return field.GetValue(target);
        }

        static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "Expected private test field " + name + " to exist.");
            field.SetValue(target, value);
        }

        [Test]
        public void DisplayProfileSelectionRejectsUnavailableAndStalePreviewAndSessionSources()
        {
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(false, false, true, true, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, true, true, true, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, false, true, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, false, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, true, false, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, true, true, true));
            Assert.IsTrue(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, true, true, false));

            BridgeDisplayProfile profile = MakeDisplayProfile(16f / 9f);
            var gameObject = new GameObject("Display profile selection test");
            QuiltRenderer renderer = gameObject.AddComponent<QuiltRenderer>();
            try
            {
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, false, profile, false, 1f));
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, default, false, 1f));
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, true, 1f));
                Assert.IsTrue(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1f));

                renderer.physicalFraming.enabled = true;
                renderer.physicalFraming.visibleApertureWidthMillimeters = 160f;
                renderer.physicalFraming.visibleApertureHeightMillimeters = 90f;
                Assert.IsTrue(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1f));
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1.01f));
                renderer.physicalFraming.visibleApertureWidthMillimeters = 150f;
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1f));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }

            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(false, true, true, true, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, false, true, true, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, false, true, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, false, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, default, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, profile, false, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, profile, true, false));
            Assert.IsTrue(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, profile, true, true));
        }

        static BridgeDisplayProfile MakeDisplayProfile(float aspect)
        {
            int width = aspect > 1f ? 1920 : 1080;
            int height = aspect > 1f ? 1080 : 1920;
            return new BridgeDisplayProfile(new BridgeNative.DisplayInfo
            {
                index = 0,
                width = (uint)width,
                height = (uint)height,
                aspect = aspect,
                viewcone = 40f,
                quiltWidth = 4096,
                quiltHeight = 4096,
                quiltColumns = 8,
                quiltRows = 6
            });
        }

        [Test]
        public void AuthoringRefreshPreservesFocusFieldWhilePhysicalModeChanges()
        {
            var fixture = new Fixture(false);
            var root = new VisualElement();
            try
            {
                fixture.Setup();
                var ui = new ViewWeaveRigInspectorUI(root, () => fixture.Rig, () => null, _ => { });
                ui.Rebuild();
                FieldInfo field = typeof(ViewWeaveRigInspectorUI).GetField("focusDistanceInput", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                FloatField focusField = (FloatField)field.GetValue(ui);
                Assert.NotNull(focusField);
                for (int i = 0; i < 3; ++i) ui.Refresh();
                Assert.AreSame(focusField, field.GetValue(ui), "Periodic refresh must retain the focus input instance.");

                fixture.Rig.Renderer.physicalFraming.enabled = true;
                fixture.Rig.Renderer.physicalFraming.visibleApertureWidthMillimeters = 160f;
                fixture.Rig.Renderer.physicalFraming.visibleApertureHeightMillimeters = 90f;
                ui.Refresh();
                Assert.AreSame(focusField, field.GetValue(ui), "Switching physical framing must not rebuild the focus field.");
                Assert.AreEqual(DisplayStyle.None, focusField.style.display.value, "Stored base focus distance should be hidden while physical framing is enabled.");
            }
            finally
            {
                root.Clear();
                fixture.Dispose();
            }
        }

        [Test]
        public void EditorPreviewRenderBoundaryRequiresExactOwnerAndRejectsActiveSrpCallbacksAndCompletionReentry()
        {
            var producerObject = new GameObject("Editor preview callback boundary test");
            QuiltRenderer renderer = producerObject.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producerObject, renderer);
            BridgeEditorPreviewRetentionRecord wrongOwner = ScriptableObject.CreateInstance<BridgeEditorPreviewRetentionRecord>();
            FieldInfo retentionRequired = typeof(QuiltRenderer).GetField("editorPreviewRetentionRequired", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo completionCallback = typeof(QuiltRenderer).GetField("notifyingRenderComplete", BindingFlags.Instance | BindingFlags.NonPublic);

            try
            {
                Assert.IsTrue(QuiltRenderer.EditorPreviewRenderCallbackTrackingReady);
                Assert.IsTrue(renderer.BeginEditorPreviewRetention(owner));

                InvalidOperationException ownerError = Assert.Throws<InvalidOperationException>(
                    () => renderer.RenderEditorPreviewAtBoundary(wrongOwner));
                StringAssert.Contains("serialized retention owner", ownerError.Message);

                ScriptableRenderContext context = default;
                var cameras = new System.Collections.Generic.List<Camera>();
                QuiltRendererEditorCallbackTracker.OnBeginContextRendering(context, cameras);
                QuiltRendererEditorCallbackTracker.OnBeginCameraRendering(context, null);
                try
                {
                    Assert.IsTrue(QuiltRenderer.EditorPreviewRenderCallbackActive);
                    InvalidOperationException callbackError = Assert.Throws<InvalidOperationException>(
                        () => renderer.RenderEditorPreviewAtBoundary(owner));
                    StringAssert.Contains("outside SRP render callbacks", callbackError.Message);
                }
                finally
                {
                    QuiltRendererEditorCallbackTracker.OnEndCameraRendering(context, null);
                    QuiltRendererEditorCallbackTracker.OnEndContextRendering(context, cameras);
                }

                Assert.IsFalse(QuiltRenderer.EditorPreviewRenderCallbackActive);
                completionCallback.SetValue(renderer, true);
                InvalidOperationException reentryError = Assert.Throws<InvalidOperationException>(
                    () => renderer.RenderEditorPreviewAtBoundary(owner));
                StringAssert.Contains("quilt completion callback", reentryError.Message);
            }
            finally
            {
                completionCallback.SetValue(renderer, false);
                retentionRequired.SetValue(renderer, false);
                if (wrongOwner != null) Object.DestroyImmediate(wrongOwner);
                if (owner != null) Object.DestroyImmediate(owner);
                if (producerObject != null) Object.DestroyImmediate(producerObject);
            }
        }

        [UnityTest]
        public IEnumerator UrpEditorPreviewRendersAndRejectsProfileMutationWhileOwned()
        {
            return RunLocalPreview(false);
        }

        [UnityTest]
        public IEnumerator HdrpEditorPreviewRendersAndRejectsProfileMutationWhileOwned()
        {
            return RunLocalPreview(true);
        }

        [UnityTest]
        public IEnumerator UrpViewRenderDensityChangesAtFrameBoundaryAndPreservesLogicalQuiltResources()
        {
            var fixture = new Fixture(false);
            bool previewAttempted = false;
            bool callbackMutationAttempted = false;
            bool callbackMutationAccepted = true;
            string callbackMutationError = string.Empty;
            QuiltRenderer producerRenderer = null;
            Action<QuiltRenderer> completionCallback = renderer =>
            {
                if (callbackMutationAttempted) return;
                callbackMutationAttempted = true;
                callbackMutationAccepted = renderer.TrySetViewRenderMode(QuiltViewRenderMode.Half, out callbackMutationError);
            };
            try
            {
                fixture.Setup();
                QuiltRenderer sourceRenderer = fixture.Rig.Renderer;
                previewAttempted = true;
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("The URP local preview did not start: " + startError);
                producerRenderer = PreviewApi.ProducerRenderer;
                Assert.IsNotNull(producerRenderer, "The preview producer should be constructed before its first frame.");
                producerRenderer.QuiltRendered += completionCallback;
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture) &&
                    callbackMutationAttempted && producerRenderer.HasRenderedQuilt,
                    "The initial Full preview frame and its completion callback did not run.");

                Assert.IsFalse(callbackMutationAccepted, "Sampling changes must be rejected during completion notification.");
                StringAssert.Contains("completion notification", callbackMutationError);
                QuiltRenderSnapshot initialSnapshot = producerRenderer.LatestSnapshot;
                Assert.AreEqual(QuiltViewRenderMode.Full, initialSnapshot.ViewRenderMode);
                Assert.AreEqual(8, initialSnapshot.LastRenderedViewCount);
                Assert.AreEqual(8, initialSnapshot.Layout.ViewCount);
                Assert.AreEqual(4, initialSnapshot.Layout.Columns);
                Assert.AreEqual(2, initialSnapshot.Layout.Rows);
                RenderTexture quiltTexture = PreviewApi.Texture;
                RenderTexture viewTexture = PreviewApi.ViewTexture;
                Assert.IsNotNull(viewTexture);

                QuiltViewRenderMode[] modes = { QuiltViewRenderMode.Half, QuiltViewRenderMode.Quarter };
                int[] expectedCounts = { 4, 2 };
                for (int i = 0; i < modes.Length; ++i)
                {
                    Assert.IsTrue(sourceRenderer.TrySetViewRenderMode(modes[i], out string modeError), modeError);
                    long previousRevision = producerRenderer.LatestSnapshot.Revision;
                    PreviewApi.RequestRefresh();
                    QuiltViewRenderMode expectedMode = modes[i];
                    yield return WaitFor(() => producerRenderer != null && producerRenderer.LatestSnapshot.IsValid &&
                        producerRenderer.LatestSnapshot.Revision > previousRevision &&
                        producerRenderer.LatestSnapshot.ViewRenderMode == expectedMode,
                        "The preview did not apply " + expectedMode + " at the next requested frame boundary.");

                    QuiltRenderSnapshot snapshot = producerRenderer.LatestSnapshot;
                    Assert.AreEqual(expectedCounts[i], snapshot.LastRenderedViewCount, expectedMode + " should render its expected sparse view count.");
                    Assert.AreEqual(initialSnapshot.Layout.QuiltWidth, snapshot.Layout.QuiltWidth);
                    Assert.AreEqual(initialSnapshot.Layout.QuiltHeight, snapshot.Layout.QuiltHeight);
                    Assert.AreEqual(initialSnapshot.Layout.Columns, snapshot.Layout.Columns);
                    Assert.AreEqual(initialSnapshot.Layout.Rows, snapshot.Layout.Rows);
                    Assert.AreEqual(initialSnapshot.Layout.ViewCount, snapshot.Layout.ViewCount,
                        "Sampling density must preserve the logical quilt view count.");
                    Assert.AreSame(quiltTexture, PreviewApi.Texture, "Changing sampling density should not rebuild the quilt texture.");
                    Assert.AreSame(viewTexture, PreviewApi.ViewTexture, "Changing sampling density should not rebuild the view texture.");
                }
            }
            finally
            {
                if (producerRenderer != null) producerRenderer.QuiltRendered -= completionCallback;
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                fixture.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RigAuthoringWindowRetargetAndCloseStopLocalPreview()
        {
            var fixture = new Fixture(false);
            ViewWeaveRig secondaryRig = null;
            ViewWeaveRigAuthoringWindow window = null;
            bool previewAttempted = false;
            try
            {
                fixture.Setup();
                secondaryRig = CreateSecondaryRig("Editor Preview Retarget Rig");
                previewAttempted = true;
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("The first local preview did not start: " + startError);
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "The first local preview did not produce a texture before window retarget.");

                window = ViewWeaveRigAuthoringWindow.OpenForRig(fixture.Rig);
                window = ViewWeaveRigAuthoringWindow.OpenForRig(secondaryRig);
                yield return WaitFor(() => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                    BridgeEditorPreviewRetention.PendingCount == 0,
                    "Changing the authoring window Rig did not stop and prove the previous preview.");
                Debug.Log("EDITOR_PREVIEW_WINDOW retarget active=false pending=0");

                if (!PreviewApi.Start(secondaryRig, false, out startError))
                    throw new AssertionException("The retargeted local preview did not start: " + startError);
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "The retargeted local preview did not produce a texture before window close.");
                window.Close();
                window = null;
                yield return WaitFor(() => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                    BridgeEditorPreviewRetention.PendingCount == 0,
                    "Closing the authoring window did not stop and prove its preview.");
                Debug.Log("EDITOR_PREVIEW_WINDOW close active=false pending=0");
            }
            finally
            {
                if (window != null) window.Close();
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                if (secondaryRig != null) Object.DestroyImmediate(secondaryRig.gameObject);
                fixture.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator LocalPreviewStopsBeforePlayModeTransition()
        {
            var fixture = new Fixture(false);
            bool previewAttempted = false;
            bool crossedPlayMode = false;
            try
            {
                fixture.Setup();
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("The local preview did not start before Play transition: " + startError);
                previewAttempted = true;
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "The local preview did not produce a texture before Play transition.");

                yield return new EnterPlayMode();
                crossedPlayMode = true;
                Assert.IsTrue(Application.isPlaying, "Unity must enter Play mode for the transition check.");
                Assert.IsFalse(PreviewApi.IsActive, "The Edit Mode preview must stop before Play mode begins.");
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount,
                    "The preview must finish retention proof before the Play mode transition completes.");
                Debug.Log("EDITOR_PREVIEW_PLAY_TRANSITION play=true active=false pending=0");

                yield return new ExitPlayMode();
                yield return null;
                Assert.IsFalse(Application.isPlaying, "The test must return Unity to Edit mode.");
                Assert.IsFalse(PreviewApi.IsActive, "Returning to Edit mode must not silently restart the preview.");
                Debug.Log("EDITOR_PREVIEW_PLAY_TRANSITION play=false active=false");
            }
            finally
            {
                if (previewAttempted && !Application.isPlaying &&
                    (PreviewApi.IsActive || PreviewApi.IsStopping || BridgeEditorPreviewRetention.PendingCount != 0))
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                if (previewAttempted && !Application.isPlaying)
                    Debug.Log("EDITOR_PREVIEW_DEVICE_CLEANUP active=" + PreviewApi.IsActive +
                        ", stopping=" + PreviewApi.IsStopping +
                        ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount +
                        ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount);
                if (!crossedPlayMode)
                {
                    fixture.Dispose();
                    SessionState.SetBool(DevicePlayTransitionStatePrefix + "captured", false);
                    SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", true);
                }
                else Debug.Log("EDITOR_PREVIEW_PLAY_TRANSITION fixture_disposal_skipped_after_domain_reload=true");
            }
        }

        [UnityTest]
        public IEnumerator DeviceEditorPreviewStartsStopsAndRestartsWhenExplicitlyEnabled()
        {
            if (Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST") != "1")
                Assert.Ignore("Set VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST=1 only when a Looking Glass Bridge and device are available.");
            Assert.IsTrue(BridgeGraphicsBackendPolicy.TrySelect(
                BridgeGraphicsBackendRoute.EditorPreview,
                Application.platform,
                Application.isEditor,
                Application.isPlaying,
                SystemInfo.graphicsDeviceType,
                SystemInfo.renderingThreadingMode,
                out BridgeGraphicsBackendDecision backend,
                out string backendError), backendError);
            string initialPipelineName = GraphicsSettings.currentRenderPipeline == null
                ? "BuiltIn"
                : GraphicsSettings.currentRenderPipeline.GetType().FullName;
            Debug.Log("EDITOR_PREVIEW_DEVICE environment unity=" + Application.unityVersion +
                ", graphics=" + SystemInfo.graphicsDeviceType +
                ", initialPipeline=" + initialPipelineName +
                ", route=" + backend.Route +
                ", unityProducer=" + backend.UnityProducer +
                ", bridgeConsumer=" + backend.BridgeConsumer);
            bool useHdrp = string.Equals(Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_PIPELINE"), "HDRP", StringComparison.OrdinalIgnoreCase);
            var fixture = new Fixture(useHdrp);
            bool previewAttempted = false;
            try
            {
                fixture.Setup();
                Debug.Log("EDITOR_PREVIEW_DEVICE fixturePipeline=" +
                    (GraphicsSettings.currentRenderPipeline == null ? "BuiltIn" : GraphicsSettings.currentRenderPipeline.GetType().FullName));
                fixture.Rig.gameObject.SetActive(false);
                var sourceOutput = fixture.Rig.gameObject.AddComponent<BridgeWorkerQuiltOutput>();
                sourceOutput.initializeOnEnable = false;
                fixture.Rig.SetOutput(sourceOutput);
                if (!fixture.Rig.ConfigureComponents()) throw new AssertionException("The device test source Rig could not configure its Bridge output.");
                fixture.Rig.gameObject.SetActive(true);
                previewAttempted = true;
                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("first");
                StopDevicePreview("first");
                yield return WaitForDeviceStop("first");

                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("restart");
                StopDevicePreview("restart");
                yield return WaitForDeviceStop("restart");
            }
            finally
            {
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                fixture.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator DevicePreviewRestartKeepsProducerQuiltVisibleAcrossTwoCycles()
        {
            if (Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_RESTART_CONTENT_TEST") != "1")
                Assert.Ignore("Set VIEWWEAVE_EDITOR_PREVIEW_RESTART_CONTENT_TEST=1 only for the owned isolated project with Bridge and a connected display.");
            string nativeTracePath = Environment.GetEnvironmentVariable("LKG_NATIVE_TRACE");
            string bridgeSnapshotBasePath = Environment.GetEnvironmentVariable("LKG_BRIDGE_SNAPSHOT");
            string sessionSuffixFlag = Environment.GetEnvironmentVariable("LKG_BRIDGE_SNAPSHOT_SESSION_SUFFIX");
            string frameSequenceFlag = Environment.GetEnvironmentVariable("LKG_BRIDGE_SNAPSHOT_FRAME_SEQUENCE");
            bool snapshotConfigurationRequested = !string.IsNullOrWhiteSpace(bridgeSnapshotBasePath) ||
                !string.IsNullOrWhiteSpace(sessionSuffixFlag) || !string.IsNullOrWhiteSpace(frameSequenceFlag);
            bool validateBridgeSnapshots = snapshotConfigurationRequested &&
                !string.IsNullOrWhiteSpace(bridgeSnapshotBasePath) && sessionSuffixFlag == "1" && frameSequenceFlag == "1";
            if (snapshotConfigurationRequested && !validateBridgeSnapshots)
                Assert.Ignore("Bridge PNG validation requires a unique snapshot path, session suffix, and frame sequence flags together.");
            if (validateBridgeSnapshots && string.IsNullOrWhiteSpace(nativeTracePath))
                Assert.Ignore("Set LKG_NATIVE_TRACE for the owned diagnostic snapshot run.");
            double observerHoldSeconds = GetRestartObserverHoldSeconds();
            Assert.IsTrue(BridgeGraphicsBackendPolicy.TrySelect(
                BridgeGraphicsBackendRoute.EditorPreview,
                Application.platform,
                Application.isEditor,
                Application.isPlaying,
                SystemInfo.graphicsDeviceType,
                SystemInfo.renderingThreadingMode,
                out BridgeGraphicsBackendDecision backend,
                out string backendError), backendError);
            Assert.AreEqual(UnityEngine.Rendering.GraphicsDeviceType.Direct3D12, SystemInfo.graphicsDeviceType,
                "The restart-content regression must run on the D3D12 path used by the report.");

            var fixture = new Fixture(false);
            bool previewAttempted = false;
            long firstGeneration = 0;
            GameObject referenceCameraObject = null;
            try
            {
                fixture.Setup();
                fixture.Rig.gameObject.SetActive(false);
                fixture.Rig.Renderer.sourceCamera.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
                fixture.TargetObject.transform.localScale = new Vector3(1.25f, 1.25f, 1f);
                Assert.IsTrue(fixture.Rig.Renderer.TrySetViewRenderMode(QuiltViewRenderMode.Half, out string modeError), modeError);
                Assert.IsFalse(fixture.Rig.Renderer.physicalFraming.enabled,
                    "The two-cycle fixture must match the user's non-physical framing configuration.");
                referenceCameraObject = new GameObject("Editor Preview Reference Camera");
                Camera referenceCamera = referenceCameraObject.AddComponent<Camera>();
                referenceCamera.transform.position = fixture.Rig.Renderer.sourceCamera.transform.position;
                referenceCamera.transform.rotation = fixture.Rig.Renderer.sourceCamera.transform.rotation;
                referenceCamera.fieldOfView = fixture.Rig.Renderer.sourceCamera.fieldOfView;
                fixture.Rig.Renderer.referenceCamera = referenceCamera;
                fixture.Rig.Renderer.followReferenceFieldOfView = true;
                SetPreviewTargetColor(fixture.TargetObject, Color.green);
                BridgeWorkerQuiltOutput output = fixture.Rig.gameObject.AddComponent<BridgeWorkerQuiltOutput>();
                output.initializeOnEnable = false;
                output.runtimePath = string.Empty;
                output.applicationName = "ViewWeave";
                output.displayOrdinal = 0;
                fixture.Rig.SetOutput(output);
                ViewWeaveBridgeSession session = fixture.Rig.gameObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = true;
                session.runtimePath = string.Empty;
                session.applicationName = "ViewWeave";
                session.displayOrdinal = 0;
                Assert.IsTrue(session.SetQuality(QuiltQualityPreset.Preview),
                    "The Session must keep the user's explicit Preview quality preset.");
                Assert.AreEqual(QuiltQualityPreset.Preview, session.Quality);
                if (!fixture.Rig.ConfigureComponents())
                    throw new AssertionException("The restart-content fixture Rig could not configure its Bridge output.");
                fixture.Rig.gameObject.SetActive(true);

                Debug.Log("EDITOR_PREVIEW_RESTART environment unity=" + Application.unityVersion +
                    ", graphics=" + SystemInfo.graphicsDeviceType +
                    ", route=" + backend.Route +
                    ", pipeline=" + (GraphicsSettings.currentRenderPipeline == null ? "BuiltIn" : GraphicsSettings.currentRenderPipeline.GetType().FullName) +
                    ", trace=" + (string.IsNullOrWhiteSpace(nativeTracePath) ? "disabled" : ToAscii(Path.GetFullPath(nativeTracePath))) +
                    ", target=green-then-magenta, background=0.2/0.2/0.2, sessionQuality=" + session.Quality +
                    ", sessionStartOnEnable=" + session.startOnEnable + ", viewMode=" + fixture.Rig.Renderer.viewRenderMode +
                    ", physicalFraming=" + fixture.Rig.Renderer.physicalFraming.enabled +
                    ", followReferenceFov=" + fixture.Rig.Renderer.followReferenceFieldOfView +
                    ", application=ViewWeave, ordinal=0");

                int priorSnapshotSessions = CountBridgeFrameFiveSnapshots(bridgeSnapshotBasePath);
                previewAttempted = true;
                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("content-first");
                yield return WaitForDevicePreviewContent("content-first", false);
                if (validateBridgeSnapshots)
                    yield return WaitForBridgeSnapshotContent("content-first", bridgeSnapshotBasePath, priorSnapshotSessions, false);
                BridgeWorkerQuiltOutput firstOutput = PreviewApi.ProducerOutput;
                Assert.IsNotNull(firstOutput);
                firstGeneration = firstOutput.SessionGeneration;
                LogDevicePreviewCycle("content-first", nativeTracePath, firstOutput);
                StopDevicePreview("content-first");
                yield return WaitForDeviceStop("content-first");

                priorSnapshotSessions = CountBridgeFrameFiveSnapshots(bridgeSnapshotBasePath);
                SetPreviewTargetColor(fixture.TargetObject, Color.magenta);
                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("content-restart");
                yield return WaitForDevicePreviewContent("content-restart", true);
                if (validateBridgeSnapshots)
                    yield return WaitForBridgeSnapshotContent("content-restart", bridgeSnapshotBasePath, priorSnapshotSessions, true);
                BridgeWorkerQuiltOutput secondOutput = PreviewApi.ProducerOutput;
                Assert.IsNotNull(secondOutput);
                Assert.AreNotEqual(firstGeneration, secondOutput.SessionGeneration,
                    "Restart must create a new Worker session generation.");
                LogDevicePreviewCycle("content-restart", nativeTracePath, secondOutput);
                if (observerHoldSeconds > 0.0)
                    yield return HoldMagentaPreviewForObservation(observerHoldSeconds);
                StopDevicePreview("content-restart");
                yield return WaitForDeviceStop("content-restart");
                Assert.IsFalse(PreviewApi.IsActive);
                Assert.IsFalse(PreviewApi.IsStopping);
                Assert.AreEqual(0, BridgeWorkerQuiltOutput.ShutdownPendingCount);
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount);
            }
            finally
            {
                if (previewAttempted)
                {
                    if (PreviewApi.IsActive && !PreviewApi.IsStopping) PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                    Debug.Log("EDITOR_PREVIEW_RESTART cleanup active=" + PreviewApi.IsActive +
                        ", stopping=" + PreviewApi.IsStopping +
                        ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                        ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount +
                        ", status=" + ToAscii(PreviewApi.Status) +
                        ", error=" + ToAscii(PreviewApi.LastError));
                }
                fixture.Dispose();
                if (referenceCameraObject != null) Object.DestroyImmediate(referenceCameraObject);
            }
        }

        [UnityTest]
        public IEnumerator ConnectedDisplayRefreshSurvivesToolbarPlayStopAndCanRefreshAgain()
        {
            if (Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_SESSION_REFRESH_TEST") != "1")
                Assert.Ignore("Set VIEWWEAVE_EDITOR_PREVIEW_SESSION_REFRESH_TEST=1 only for the owned isolated URP project with Bridge and a connected display.");
            string nativeTracePath = Environment.GetEnvironmentVariable("LKG_NATIVE_TRACE");
            if (string.IsNullOrWhiteSpace(nativeTracePath))
                Assert.Ignore("Set LKG_NATIVE_TRACE to a unique .local trace file for the owned isolated Editor process.");
            if (SessionState.GetBool(DevicePlayTransitionStatePrefix + "captured", false) ||
                SessionState.GetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", false))
                throw new InvalidOperationException("A previous device-transition fixture still owns render-pipeline state or assets; refusing to overwrite its recovery record.");

            bool previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions previousOptions = EditorSettings.enterPlayModeOptions;
            CaptureDeviceTransitionPlayOptions(previousOptionsEnabled, previousOptions);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            var fixture = new Fixture(false);
            BridgeWorkerQuiltOutput output = null;
            ViewWeaveBridgeSession session = null;
            bool crossedPlayMode = false;
            bool fixtureSetup = false;
            try
            {
                Debug.Log("EDITOR_PREVIEW_REFRESH nativeTrace=" + ToAscii(Path.GetFullPath(nativeTracePath)));
                string pipelineAssetDirectory = CreateOwnedDeviceTransitionAssetFolder();
                fixture.Setup(pipelineAssetDirectory);
                CaptureDeviceTransitionPipelineSettings(
                    fixture.PreviousDefaultPipeline,
                    fixture.PreviousQualityPipeline,
                    previousOptionsEnabled,
                    previousOptions);
                fixtureSetup = true;

                fixture.Rig.gameObject.SetActive(false);
                fixture.Rig.gameObject.name = DevicePlayTransitionRigName;
                fixture.TargetObject.name = DevicePlayTransitionTargetName;
                fixture.SetOwnedPipelineNames(DevicePlayTransitionPipelineName, DevicePlayTransitionRendererDataName);
                Assert.IsTrue(fixture.Rig.Renderer.TrySetViewRenderMode(QuiltViewRenderMode.Half, out string modeError), modeError);
                Assert.IsFalse(fixture.Rig.Renderer.physicalFraming.enabled, "The regression fixture must use the user's non-physical framing configuration.");

                output = fixture.Rig.gameObject.AddComponent<BridgeWorkerQuiltOutput>();
                output.initializeOnEnable = false;
                output.runtimePath = string.Empty;
                output.applicationName = "ViewWeave";
                output.displayOrdinal = 0;
                fixture.Rig.SetOutput(output);
                session = fixture.Rig.gameObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = true;
                session.runtimePath = string.Empty;
                session.applicationName = "ViewWeave";
                session.displayOrdinal = 0;
                Assert.IsTrue(session.SetQuality(QuiltQualityPreset.Preview), "The Session must use the user's explicit Preview quality preset.");
                Assert.AreEqual(QuiltQualityPreset.Preview, session.Quality);
                if (!fixture.Rig.ConfigureComponents())
                    throw new AssertionException("The refresh-to-Play source Rig could not configure its Bridge output.");
                fixture.Rig.gameObject.SetActive(true);

                BridgeDisplaySnapshot initialSnapshot = null;
                yield return RequestAndWaitForDisplayRefresh(output, "before-play", value => initialSnapshot = value);
                Assert.IsNotNull(initialSnapshot);
                Assert.IsTrue(initialSnapshot.TryGetDisplay(0, out BridgeDisplayProfile initialDisplay),
                    "The initial snapshot must contain display ordinal zero.");
                Assert.IsTrue(initialDisplay.IsValid, "The initial display profile must contain valid geometry.");
                Assert.IsTrue(initialDisplay.HasPhysicalCalibration, "The connected display should report physical calibration.");

                Assert.IsTrue(QuiltDisplaySettingsResolver.TryResolve(fixture.Rig.Renderer, out QuiltSettings beforeSettings,
                    out QuiltEditorDisplayProfile beforeDisplay, out string beforeError), beforeError);
                Assert.AreEqual(QuiltDisplaySettingsSource.ConnectedDisplaySnapshot, beforeDisplay.source,
                    "The Edit Mode gizmo should resolve the just-refreshed connected display profile before Play.");
                Assert.AreEqual(QuiltViewRenderMode.Half, fixture.Rig.Renderer.viewRenderMode);
                Assert.IsFalse(fixture.Rig.Renderer.physicalFraming.enabled);
                ViewWeaveBridgeSession sourceSessionReference = session;
                BridgeWorkerQuiltOutput sourceOutputReference = output;
                LogRefreshTransitionState("before-play", session, output, initialSnapshot, beforeDisplay, beforeSettings.viewAspect);

                Debug.Log("EDITOR_PREVIEW_REFRESH_PLAY start options=" + EditorSettings.enterPlayModeOptions +
                    ", runtimePathEmpty=true, application=ViewWeave, ordinal=0, viewMode=Half, physical=false, quality=" +
                    session.Quality + "(" + (int)session.Quality + "), outputRuntimePathEmpty=" + string.IsNullOrEmpty(output.runtimePath) +
                    ", outputApplication=" + output.applicationName + ", outputOrdinal=" + output.displayOrdinal);
                yield return new EnterPlayMode();
                crossedPlayMode = true;
                Assert.IsTrue(Application.isPlaying, "Unity must enter Play mode for the toolbar-transition regression.");
                ViewWeaveRig playRig = FindSceneRig(DevicePlayTransitionRigName);
                Assert.IsNotNull(playRig, "The uniquely named refresh-transition Rig must survive options3.");
                session = playRig.GetComponent<ViewWeaveBridgeSession>();
                output = playRig.GetComponent<BridgeWorkerQuiltOutput>();
                Assert.IsNotNull(session);
                Assert.IsNotNull(output);
                bool sameSessionInstanceAfterPlayEntry = ReferenceEquals(sourceSessionReference, session);
                bool sameOutputInstanceAfterPlayEntry = ReferenceEquals(sourceOutputReference, output);
                Debug.Log("EDITOR_PREVIEW_REFRESH_PLAY entered sessionInstanceSame=" + sameSessionInstanceAfterPlayEntry +
                    ", outputInstanceSame=" + sameOutputInstanceAfterPlayEntry +
                    ", sessionRefHash=" + RuntimeHelpers.GetHashCode(session) +
                    ", outputRefHash=" + RuntimeHelpers.GetHashCode(output) + ", sessionGeneration=" + output.SessionGeneration +
                    ", handle=0x" + output.NativeHandle.ToInt64().ToString("X"));
                Assert.AreEqual(QuiltQualityPreset.Preview, session.Quality);

                double renderStartedAt = EditorApplication.timeSinceStartup;
                double renderDeadline = renderStartedAt + 60.0;
                double nextRenderLog = renderStartedAt + 5.0;
                bool runtimeCompleted = false;
                while (EditorApplication.timeSinceStartup < renderDeadline)
                {
                    if (session.State == ViewWeaveBridgeSession.SessionState.Rendering && output.Ready && output.Completed > 0)
                    {
                        runtimeCompleted = true;
                        break;
                    }
                    if (session.State == ViewWeaveBridgeSession.SessionState.Fault) break;
                    if (EditorApplication.timeSinceStartup >= nextRenderLog)
                    {
                        Debug.Log("EDITOR_PREVIEW_REFRESH_PLAY_WAIT elapsed=" +
                            (EditorApplication.timeSinceStartup - renderStartedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                            ", " + DescribeOutputState(output) + ", session=" + session.State +
                            ", sessionError=" + ToAscii(session.LastError) + ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                            ", discoveryBusy=" + BridgeDisplayDiscovery.IsBusy + ", discoveryStatus=" + ToAscii(BridgeDisplayDiscovery.Status));
                        nextRenderLog += 5.0;
                    }
                    yield return null;
                }
                Debug.Log("EDITOR_PREVIEW_REFRESH_PLAY runtimeReady=" + runtimeCompleted + ", state=" + session.State +
                    ", completed=" + output.Completed + ", " + DescribeOutputState(output));

                Debug.Log("EDITOR_PREVIEW_REFRESH_PLAY direct-exit state=" + session.State + ", completed=" + output.Completed +
                    ", sessionGeneration=" + output.SessionGeneration + ", handle=0x" + output.NativeHandle.ToInt64().ToString("X") +
                    ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount);
                yield return new ExitPlayMode();
                Assert.IsFalse(Application.isPlaying, "Toolbar Stop must return Unity to Edit Mode.");

                ViewWeaveRig editRig = FindSceneRig(DevicePlayTransitionRigName);
                Assert.IsNotNull(editRig, "The uniquely named Rig must remain available after options3 toolbar Stop.");
                session = editRig.GetComponent<ViewWeaveBridgeSession>();
                output = editRig.GetComponent<BridgeWorkerQuiltOutput>();
                Assert.IsNotNull(session);
                Assert.IsNotNull(output);
                bool sameSessionInstanceAfterPlayStop = ReferenceEquals(sourceSessionReference, session);
                bool sameOutputInstanceAfterPlayStop = ReferenceEquals(sourceOutputReference, output);
                double stopStartedAt = EditorApplication.timeSinceStartup;
                double stopDeadline = stopStartedAt + 30.0;
                double nextStopLog = stopStartedAt + 5.0;
                while (EditorApplication.timeSinceStartup < stopDeadline &&
                       (session.State != ViewWeaveBridgeSession.SessionState.Idle ||
                        output.State != BridgeWorkerQuiltOutput.WorkerState.Closed ||
                        BridgeWorkerQuiltOutput.ShutdownPendingCount != 0 || BridgeEditorPreviewRetention.PendingCount != 0))
                {
                    if (EditorApplication.timeSinceStartup >= nextStopLog)
                    {
                        Debug.Log("EDITOR_PREVIEW_REFRESH_STOP_WAIT elapsed=" +
                            (EditorApplication.timeSinceStartup - stopStartedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                            ", session=" + session.State + ", sessionError=" + ToAscii(session.LastError) +
                            ", output=" + DescribeOutputState(output) + ", generation=" + output.SessionGeneration +
                            ", shutdownPending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                            ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount +
                            ", discoveryBusy=" + BridgeDisplayDiscovery.IsBusy);
                        nextStopLog += 5.0;
                    }
                    yield return null;
                }
                bool stoppedCleanly = session.State == ViewWeaveBridgeSession.SessionState.Idle &&
                    output.State == BridgeWorkerQuiltOutput.WorkerState.Closed &&
                    BridgeWorkerQuiltOutput.ShutdownPendingCount == 0 && BridgeEditorPreviewRetention.PendingCount == 0;
                Debug.Log("EDITOR_PREVIEW_REFRESH_STOP settled=" + stoppedCleanly + ", elapsed=" +
                    (EditorApplication.timeSinceStartup - stopStartedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    ", session=" + session.State + ", output=" + output.State + ", generation=" + output.SessionGeneration +
                    ", handle=0x" + output.NativeHandle.ToInt64().ToString("X") +
                    ", sessionRefHash=" + RuntimeHelpers.GetHashCode(session) + ", outputRefHash=" + RuntimeHelpers.GetHashCode(output) +
                    ", sessionInstanceSame=" + sameSessionInstanceAfterPlayStop + ", outputInstanceSame=" + sameOutputInstanceAfterPlayStop +
                    ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                    ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount +
                    ", requiresReconnect=" + output.RequiresReconnect +
                    ", rawSnapshotGeneration=" + (output.ConnectedDisplaySnapshot == null ? 0UL : output.ConnectedDisplaySnapshot.Generation) +
                    ", currentSnapshotGeneration=" + (output.CurrentConfigurationDisplaySnapshot == null ? 0UL : output.CurrentConfigurationDisplaySnapshot.Generation));
                bool postStopConfigMatches = editRig.Renderer.viewRenderMode == QuiltViewRenderMode.Half &&
                    !editRig.Renderer.physicalFraming.enabled && session.Quality == QuiltQualityPreset.Preview &&
                    string.IsNullOrEmpty(session.runtimePath) && session.applicationName == "ViewWeave" && session.displayOrdinal == 0 &&
                    string.IsNullOrEmpty(output.runtimePath) && output.applicationName == "ViewWeave" && output.displayOrdinal == 0;
                bool resolvedAfterStop = QuiltDisplaySettingsResolver.TryResolve(editRig.Renderer, out _,
                    out QuiltEditorDisplayProfile afterDisplay, out string afterError);
                bool connectedProfileRetained = resolvedAfterStop &&
                    afterDisplay.source == QuiltDisplaySettingsSource.ConnectedDisplaySnapshot &&
                    Mathf.Abs(beforeDisplay.aspect - afterDisplay.aspect) <= 0.0001f;
                Debug.Log("EDITOR_PREVIEW_REFRESH_PROFILE beforeAspect=" + beforeDisplay.aspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    ", beforeSource=" + beforeDisplay.source + ", afterResolved=" + resolvedAfterStop +
                    ", afterAspect=" + (resolvedAfterStop ? afterDisplay.aspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "unavailable") +
                    ", afterSource=" + (resolvedAfterStop ? afterDisplay.source.ToString() : "unavailable") +
                    ", afterError=" + ToAscii(afterError) + ", rendererAspect=" + editRig.Renderer.settings.viewAspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    ", currentSnapshot=" + (output.CurrentConfigurationDisplaySnapshot != null) +
                    ", requiresReconnect=" + output.RequiresReconnect + ", app=" + session.applicationName + ", runtimePathEmpty=" + string.IsNullOrEmpty(session.runtimePath) +
                    ", ordinal=" + session.displayOrdinal + ", outputRuntimePathEmpty=" + string.IsNullOrEmpty(output.runtimePath) +
                    ", outputApp=" + output.applicationName + ", outputOrdinal=" + output.displayOrdinal +
                    ", quality=" + session.Quality + "(" + (int)session.Quality + "), mode=" + editRig.Renderer.viewRenderMode +
                    ", physical=" + editRig.Renderer.physicalFraming.enabled + ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount);

                BridgeDisplaySnapshot secondSnapshot = null;
                yield return RequestAndWaitForDisplayRefresh(output, "after-play", value => secondSnapshot = value);
                bool secondHasDisplay = secondSnapshot != null && secondSnapshot.TryGetDisplay(0, out BridgeDisplayProfile _);
                BridgeDisplayProfile secondDisplay = default;
                if (secondSnapshot != null) secondSnapshot.TryGetDisplay(0, out secondDisplay);
                bool refreshedProfileResolved = QuiltDisplaySettingsResolver.TryResolve(editRig.Renderer, out _,
                    out QuiltEditorDisplayProfile refreshedDisplay, out string refreshedError);
                Debug.Log("EDITOR_PREVIEW_REFRESH_PLAY secondRefresh=" + (secondSnapshot != null) +
                    ", generation=" + (secondSnapshot == null ? 0UL : secondSnapshot.Generation) +
                    ", hasDisplay=" + secondHasDisplay + ", valid=" + (secondHasDisplay && secondDisplay.IsValid) +
                    ", calibration=" + (secondHasDisplay && secondDisplay.HasPhysicalCalibration) +
                    ", currentMatches=" + (secondSnapshot != null && output.CurrentConfigurationDisplaySnapshot == secondSnapshot) +
                    ", requiresReconnect=" + output.RequiresReconnect +
                    ", resolved=" + refreshedProfileResolved + ", source=" + (refreshedProfileResolved ? refreshedDisplay.source.ToString() : "unavailable") +
                    ", aspect=" + (refreshedProfileResolved ? refreshedDisplay.aspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "unavailable") +
                    ", error=" + ToAscii(refreshedError) + ", discoveryBusy=" + BridgeDisplayDiscovery.IsBusy +
                    ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                    ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount + ", output=" + DescribeOutputState(output));
                Assert.IsTrue(stoppedCleanly,
                    "Toolbar Stop did not complete the runtime Session and worker cleanup proof within 30 seconds.");
                Assert.IsTrue(runtimeCompleted,
                    "The ordinary Bridge Session must render before toolbar Stop. state=" + session.State +
                    ", error=" + ToAscii(session.LastError) + ", output=" + DescribeOutputState(output));
                Assert.IsTrue(postStopConfigMatches, "Toolbar Stop changed the expected session/output quality or transport configuration.");
                Assert.IsFalse(PreviewApi.IsActive, "The Edit Mode device-preview owner must not start in this regression.");
                Assert.IsFalse(PreviewApi.IsStopping);
                Assert.IsNotNull(secondSnapshot, "The second explicit display refresh must complete after toolbar Stop.");
                Assert.IsTrue(secondHasDisplay, "The second display snapshot must include ordinal zero.");
                Assert.IsTrue(secondDisplay.IsValid);
                Assert.IsTrue(secondDisplay.HasPhysicalCalibration);
                Assert.IsFalse(output.RequiresReconnect);
                Assert.AreSame(secondSnapshot, output.CurrentConfigurationDisplaySnapshot);
                Assert.AreEqual(0, BridgeWorkerQuiltOutput.ShutdownPendingCount);
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount);
                Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
                Assert.IsTrue(refreshedProfileResolved, ToAscii(refreshedError));
                Assert.AreEqual(QuiltDisplaySettingsSource.ConnectedDisplaySnapshot, refreshedDisplay.source);
                Assert.AreEqual(secondDisplay.Aspect, refreshedDisplay.aspect, 0.0001f);
                Assert.IsTrue(resolvedAfterStop, "The Scene View display profile should resolve after toolbar Stop. " + ToAscii(afterError));
                Assert.IsTrue(connectedProfileRetained,
                    "Toolbar Stop changed the effective connected-display gizmo profile before the second refresh. " +
                    "beforeAspect=" + beforeDisplay.aspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    ", afterAspect=" + (resolvedAfterStop ? afterDisplay.aspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "unavailable") +
                    ", afterSource=" + (resolvedAfterStop ? afterDisplay.source.ToString() : "unavailable"));
                CleanupDeviceTransitionFixture();
            }
            finally
            {
                if (Application.isPlaying && session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle)
                    session.StopSession();
                if (!crossedPlayMode)
                {
                    if (!BridgeDisplayDiscovery.IsBusy && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0 &&
                        BridgeEditorPreviewRetention.PendingCount == 0)
                    {
                        fixture.Dispose();
                        CleanupDeviceTransitionFixture();
                    }
                    else Debug.Log("EDITOR_PREVIEW_REFRESH fixtureCleanupDeferredUntilMetadataOrWorkerRelease=true");
                }
                else if (!Application.isPlaying && fixtureSetup &&
                    !SessionState.GetBool(DevicePlayTransitionStatePrefix + "cleanupDone", false))
                {
                    if (session != null && output != null && session.State == ViewWeaveBridgeSession.SessionState.Idle &&
                        output.State == BridgeWorkerQuiltOutput.WorkerState.Closed && !BridgeDisplayDiscovery.IsBusy &&
                        BridgeWorkerQuiltOutput.ShutdownPendingCount == 0 && BridgeEditorPreviewRetention.PendingCount == 0 &&
                        !PreviewApi.IsActive && !PreviewApi.IsStopping)
                        CleanupDeviceTransitionFixture();
                    else Debug.Log("EDITOR_PREVIEW_REFRESH fixtureCleanupDeferredUntilMetadataOrWorkerRelease=true, " +
                        "session=" + (session == null ? "null" : session.State.ToString()) +
                        ", output=" + (output == null ? "null" : output.State.ToString()) +
                        ", discoveryBusy=" + BridgeDisplayDiscovery.IsBusy +
                        ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                        ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount);
                }
                else if (Application.isPlaying)
                {
                    Debug.Log("EDITOR_PREVIEW_REFRESH fixtureCleanupDeferredUntilEditorExit=true");
                }
                RestoreDeviceTransitionPlayOptions();
            }
        }

        static IEnumerator RequestAndWaitForDisplayRefresh(BridgeWorkerQuiltOutput output, string phase,
            Action<BridgeDisplaySnapshot> completed)
        {
            BridgeDisplaySnapshot published = null;
            Action<BridgeDisplaySnapshot> onSnapshot = value => published = value;
            output.ConnectedDisplaySnapshotChanged += onSnapshot;
            if (!output.TryRequestDisplayRefresh(out string requestError))
            {
                output.ConnectedDisplaySnapshotChanged -= onSnapshot;
                Debug.Log("EDITOR_PREVIEW_REFRESH rejected phase=" + phase + ", error=" + ToAscii(requestError) +
                    ", busy=" + BridgeDisplayDiscovery.IsBusy + ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                    ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount);
                completed?.Invoke(null);
                yield break;
            }

            double startedAt = EditorApplication.timeSinceStartup;
            double deadline = startedAt + 30.0;
            double nextLogAt = startedAt + 5.0;
            while (EditorApplication.timeSinceStartup < deadline &&
                   !(published != null && output.CurrentConfigurationDisplaySnapshot == published && !BridgeDisplayDiscovery.IsBusy))
            {
                if (EditorApplication.timeSinceStartup >= nextLogAt)
                {
                    Debug.Log("EDITOR_PREVIEW_REFRESH_WAIT phase=" + phase + ", elapsed=" +
                        (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                        ", busy=" + BridgeDisplayDiscovery.IsBusy + ", status=" + ToAscii(BridgeDisplayDiscovery.Status) +
                        ", error=" + ToAscii(BridgeDisplayDiscovery.LastError) + ", outputStatus=" + ToAscii(output.DisplayRefreshStatus) +
                        ", outputError=" + ToAscii(output.LastError) + ", generation=" + output.SessionGeneration +
                        ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                        ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount);
                    nextLogAt += 5.0;
                }
                yield return null;
            }
            output.ConnectedDisplaySnapshotChanged -= onSnapshot;
            if (published == null || output.CurrentConfigurationDisplaySnapshot != published || BridgeDisplayDiscovery.IsBusy)
            {
                Debug.Log("EDITOR_PREVIEW_REFRESH timeout phase=" + phase + ", status=" +
                    ToAscii(BridgeDisplayDiscovery.Status) + ", error=" + ToAscii(BridgeDisplayDiscovery.LastError) +
                    ", outputStatus=" + ToAscii(output.DisplayRefreshStatus) + ", outputError=" + ToAscii(output.LastError) +
                    ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                    ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount);
                completed?.Invoke(null);
                yield break;
            }
            completed?.Invoke(published);
            Debug.Log("EDITOR_PREVIEW_REFRESH complete phase=" + phase + ", generation=" + published.Generation +
                ", displays=" + published.Displays.Count + ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount);
        }

        static void LogRefreshTransitionState(string phase, ViewWeaveBridgeSession session,
            BridgeWorkerQuiltOutput output, BridgeDisplaySnapshot snapshot, QuiltEditorDisplayProfile display, float effectiveAspect)
        {
            Assert.IsNotNull(session);
            Assert.IsNotNull(output);
            Debug.Log("EDITOR_PREVIEW_REFRESH_STATE phase=" + phase + ", generation=" + snapshot.Generation +
                ", sessionState=" + session.State + ", outputState=" + output.State +
                ", outputGeneration=" + output.SessionGeneration + ", runtimePathEmpty=" + string.IsNullOrEmpty(session.runtimePath) +
                ", sessionRefHash=" + RuntimeHelpers.GetHashCode(session) + ", outputRefHash=" + RuntimeHelpers.GetHashCode(output) +
                ", app=" + session.applicationName + ", sessionOrdinal=" + session.displayOrdinal +
                ", outputOrdinal=" + output.displayOrdinal + ", viewMode=" + output.GetComponent<QuiltRenderer>().viewRenderMode +
                ", handle=0x" + output.NativeHandle.ToInt64().ToString("X") +
                ", physical=" + output.GetComponent<QuiltRenderer>().physicalFraming.enabled +
                ", profileSource=" + display.source + ", displayAspect=" + display.aspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                ", effectiveAspect=" + effectiveAspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                ", rawSnapshotGeneration=" + (output.ConnectedDisplaySnapshot == null ? 0UL : output.ConnectedDisplaySnapshot.Generation) +
                ", currentSnapshotGeneration=" + (output.CurrentConfigurationDisplaySnapshot == null ? 0UL : output.CurrentConfigurationDisplaySnapshot.Generation) +
                ", currentMatchesSnapshot=" + (output.CurrentConfigurationDisplaySnapshot == snapshot) +
                ", requiresReconnect=" + output.RequiresReconnect + ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount);
        }

        [UnityTest]
        public IEnumerator DevicePreviewStopsBeforePlayAndRuntimeSessionRendersWhenExplicitlyEnabled()
        {
            if (Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST") != "1")
                Assert.Ignore("Set VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST=1 only for the owned isolated project with Bridge and a connected display.");
            if (SessionState.GetBool(DevicePlayTransitionStatePrefix + "captured", false) ||
                SessionState.GetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", false))
                throw new InvalidOperationException("A previous device-transition fixture still owns render-pipeline state or assets; refusing to overwrite its recovery record.");

            bool previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions previousOptions = EditorSettings.enterPlayModeOptions;
            bool disableDomainAndSceneReload = Environment.GetEnvironmentVariable(
                "VIEWWEAVE_EDITOR_PREVIEW_DISABLE_DOMAIN_AND_SCENE_RELOAD_TEST") == "1";
            CaptureDeviceTransitionPlayOptions(previousOptionsEnabled, previousOptions);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = disableDomainAndSceneReload
                ? EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload
                : EnterPlayModeOptions.None;

            bool previewAttempted = false;
            bool crossedPlayMode = false;
            bool exitingEditModeObserved = false;
            var fixture = new Fixture(false);
            BridgeWorkerQuiltOutput sourceOutput = null;
            ViewWeaveBridgeSession session = null;
            Action<BridgeDisplaySnapshot> onSnapshot = null;
            Action<PlayModeStateChange> playModeObserver = null;
            try
            {
                string pipelineAssetDirectory = CreateOwnedDeviceTransitionAssetFolder();
                fixture.Setup(pipelineAssetDirectory);
                CaptureDeviceTransitionPipelineSettings(
                    fixture.PreviousDefaultPipeline,
                    fixture.PreviousQualityPipeline,
                    previousOptionsEnabled,
                    previousOptions);
                fixture.Rig.gameObject.SetActive(false);
                fixture.Rig.gameObject.name = DevicePlayTransitionRigName;
                fixture.TargetObject.name = DevicePlayTransitionTargetName;
                fixture.SetOwnedPipelineNames(DevicePlayTransitionPipelineName, DevicePlayTransitionRendererDataName);
                sourceOutput = fixture.Rig.gameObject.AddComponent<BridgeWorkerQuiltOutput>();
                sourceOutput.initializeOnEnable = false;
                fixture.Rig.SetOutput(sourceOutput);
                session = fixture.Rig.gameObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = false;
                if (!fixture.Rig.ConfigureComponents())
                    throw new AssertionException("The DevicePreview-to-Play source Rig could not configure its Bridge output.");
                fixture.Rig.gameObject.SetActive(true);

                BridgeDisplaySnapshot refreshedSnapshot = null;
                onSnapshot = value => refreshedSnapshot = value;
                sourceOutput.ConnectedDisplaySnapshotChanged += onSnapshot;
                if (!sourceOutput.TryRequestDisplayRefresh(out string refreshError))
                    throw new AssertionException("The explicit pre-preview display refresh was rejected: " + refreshError);
                double metadataDeadline = EditorApplication.timeSinceStartup + 30.0;
                double nextMetadataLog = EditorApplication.timeSinceStartup + 5.0;
                while (EditorApplication.timeSinceStartup < metadataDeadline &&
                       !(refreshedSnapshot != null &&
                         sourceOutput.CurrentConfigurationDisplaySnapshot == refreshedSnapshot &&
                         !BridgeDisplayDiscovery.IsBusy))
                {
                    if (EditorApplication.timeSinceStartup >= nextMetadataLog)
                    {
                        Debug.Log("EDITOR_PREVIEW_DEVICE_METADATA_WAIT busy=" + BridgeDisplayDiscovery.IsBusy +
                            ", status=" + (BridgeDisplayDiscovery.Status ?? string.Empty) +
                            ", discoveryError=" + (BridgeDisplayDiscovery.LastError ?? string.Empty) +
                            ", outputEnabled=" + sourceOutput.enabled +
                            ", outputActive=" + sourceOutput.gameObject.activeInHierarchy +
                            ", outputActiveAndEnabled=" + sourceOutput.isActiveAndEnabled +
                            ", outputStatus=" + (sourceOutput.DisplayRefreshStatus ?? string.Empty) +
                            ", outputError=" + (sourceOutput.LastError ?? string.Empty) +
                            ", snapshot=" + (refreshedSnapshot != null) +
                            ", rawSnapshot=" + (sourceOutput.ConnectedDisplaySnapshot != null) +
                            ", currentSnapshot=" + (sourceOutput.CurrentConfigurationDisplaySnapshot != null) +
                            ", shutdownPending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                            ", native=" + ReadDiscoveryDiagnostic());
                        nextMetadataLog += 5.0;
                    }
                    yield return null;
                }
                if (refreshedSnapshot == null || sourceOutput.CurrentConfigurationDisplaySnapshot != refreshedSnapshot ||
                    BridgeDisplayDiscovery.IsBusy)
                    throw new AssertionException("The explicit pre-preview display refresh did not publish a current configuration snapshot. " +
                        "busy=" + BridgeDisplayDiscovery.IsBusy +
                        ", status=" + (BridgeDisplayDiscovery.Status ?? string.Empty) +
                        ", discoveryError=" + (BridgeDisplayDiscovery.LastError ?? string.Empty) +
                        ", outputEnabled=" + sourceOutput.enabled +
                        ", outputActive=" + sourceOutput.gameObject.activeInHierarchy +
                        ", outputActiveAndEnabled=" + sourceOutput.isActiveAndEnabled +
                        ", outputStatus=" + (sourceOutput.DisplayRefreshStatus ?? string.Empty) +
                        ", outputError=" + (sourceOutput.LastError ?? string.Empty) +
                        ", snapshot=" + (refreshedSnapshot != null) +
                        ", rawSnapshot=" + (sourceOutput.ConnectedDisplaySnapshot != null) +
                        ", currentSnapshot=" + (sourceOutput.CurrentConfigurationDisplaySnapshot != null) +
                        ", shutdownPending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                        ", native=" + ReadDiscoveryDiagnostic());
                sourceOutput.ConnectedDisplaySnapshotChanged -= onSnapshot;
                onSnapshot = null;
                Assert.Greater(refreshedSnapshot.Displays.Count, 0, "Bridge should report the connected display.");
                Assert.IsTrue(refreshedSnapshot.TryGetDisplay(sourceOutput.displayOrdinal, out BridgeDisplayProfile profile),
                    "The selected display ordinal must be present in the refreshed snapshot.");
                Assert.IsTrue(profile.IsValid, "The selected display profile must have valid geometry.");
                Assert.IsTrue(profile.HasPhysicalCalibration, "The selected display must include physical calibration data.");
                Debug.Log("EDITOR_PREVIEW_DEVICE_METADATA generation=" + refreshedSnapshot.Generation +
                    ", displays=" + refreshedSnapshot.Displays.Count + ", calibration=true");

                session.startOnEnable = true;
                previewAttempted = true;
                Debug.Log("EDITOR_PREVIEW_DEVICE playOptions=" + EditorSettings.enterPlayModeOptions);
                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("before-play");
                BridgeWorkerQuiltOutput previewOutput = PreviewApi.ProducerOutput;
                Assert.IsNotNull(previewOutput);
                Assert.Greater(previewOutput.Submissions, 0);
                Assert.Greater(previewOutput.Completed, 0);

                playModeObserver = state =>
                {
                    if (state == PlayModeStateChange.ExitingEditMode) exitingEditModeObserved = true;
                };
                EditorApplication.playModeStateChanged += playModeObserver;
                yield return new EnterPlayMode();
                crossedPlayMode = true;
                Assert.IsTrue(Application.isPlaying, "Unity must enter Play mode for the device transition check.");
                ViewWeaveRig reloadedRig = FindSceneRig(DevicePlayTransitionRigName);
                Assert.IsNotNull(reloadedRig,
                    "Exactly one uniquely named device-transition Rig must survive the Editor scene reload. " + DescribeMatchingSceneObjects(DevicePlayTransitionRigName));
                GameObject reloadedTarget = FindSceneObject(DevicePlayTransitionTargetName);
                Assert.IsNotNull(reloadedTarget, "The uniquely named device-transition target must survive the Editor scene reload.");
                session = reloadedRig.GetComponent<ViewWeaveBridgeSession>();
                sourceOutput = reloadedRig.GetComponent<BridgeWorkerQuiltOutput>();
                Assert.IsNotNull(session, "The reloaded device-transition Rig must retain its Bridge Session component.");
                Assert.IsNotNull(sourceOutput, "The reloaded device-transition Rig must retain its Bridge output component.");
                yield return WaitForDevicePreviewStop(session, sourceOutput, () => exitingEditModeObserved);
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY editorProducerStopped=true retentionPending=0 nativePending=0");

                double runtimeStartedAt = EditorApplication.timeSinceStartup;
                double runtimeDeadline = runtimeStartedAt + 45.0;
                while (EditorApplication.timeSinceStartup < runtimeDeadline)
                {
                    if (session != null && session.State == ViewWeaveBridgeSession.SessionState.Rendering &&
                        sourceOutput != null && sourceOutput.Ready && sourceOutput.Completed > 0) break;
                    if (session != null && session.State == ViewWeaveBridgeSession.SessionState.Fault)
                        Assert.Fail("The runtime Bridge Session faulted after the Editor preview stopped: " + session.LastError);
                    yield return null;
                }
                Assert.IsNotNull(session);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Rendering, session.State,
                    "The runtime Bridge Session did not reach Rendering after the Editor preview stopped.");
                Assert.IsTrue(sourceOutput.Ready, "The runtime Bridge worker must be Ready.");
                Assert.Greater(sourceOutput.Completed, 0, "The runtime Bridge worker must complete a submission.");
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY runtimeState=" + session.State + ", submissions=" + sourceOutput.Submissions +
                    ", completed=" + sourceOutput.Completed);

                session.StopSession();
                double stopDeadline = EditorApplication.timeSinceStartup + 30.0;
                while (EditorApplication.timeSinceStartup < stopDeadline &&
                       (session.State != ViewWeaveBridgeSession.SessionState.Idle ||
                        sourceOutput.State != BridgeWorkerQuiltOutput.WorkerState.Closed ||
                        BridgeWorkerQuiltOutput.ShutdownPendingCount != 0 || BridgeEditorPreviewRetention.PendingCount != 0))
                    yield return null;
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle, session.State,
                    "The runtime Session did not return to Idle after Stop.");
                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, sourceOutput.State,
                    "The runtime Bridge worker must close after Stop.");
                Assert.AreEqual(0, BridgeWorkerQuiltOutput.ShutdownPendingCount,
                    "Native cleanup must be complete after Stop.");
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount,
                    "Preview retention proof must be complete after Stop.");
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY runtimeStopped=true nativePending=0 retentionPending=0");

                yield return new ExitPlayMode();
                Assert.IsFalse(Application.isPlaying, "The hardware test must return Unity to Edit Mode.");
                Assert.IsFalse(PreviewApi.IsActive, "Returning to Edit Mode must not restart the Editor preview.");
                ViewWeaveRig editModeRig = FindSceneRig(DevicePlayTransitionRigName);
                Assert.IsNotNull(editModeRig, "The owned Rig fixture must be available for cleanup after returning to Edit Mode.");
                GameObject editModeTarget = FindSceneObject(DevicePlayTransitionTargetName);
                Assert.IsNotNull(editModeTarget, "The owned target fixture must be available for cleanup after returning to Edit Mode.");
                CleanupDeviceTransitionFixture();
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY editModeReturned=true editorPreviewRestarted=false");
            }
            finally
            {
                if (playModeObserver != null) EditorApplication.playModeStateChanged -= playModeObserver;
                if (onSnapshot != null && sourceOutput != null) sourceOutput.ConnectedDisplaySnapshotChanged -= onSnapshot;
                if (Application.isPlaying && session == null)
                {
                    ViewWeaveRig currentRig = FindSceneRig(DevicePlayTransitionRigName);
                    if (currentRig != null)
                    {
                        session = currentRig.GetComponent<ViewWeaveBridgeSession>();
                        sourceOutput = currentRig.GetComponent<BridgeWorkerQuiltOutput>();
                    }
                }
                if (session != null && Application.isPlaying && session.State != ViewWeaveBridgeSession.SessionState.Idle)
                    session.StopSession();
                if (previewAttempted && !Application.isPlaying &&
                    (PreviewApi.IsActive || PreviewApi.IsStopping || BridgeEditorPreviewRetention.PendingCount != 0))
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                if (!crossedPlayMode)
                {
                    fixture.Dispose();
                    CleanupDeviceTransitionFixture();
                }
                else if (!Application.isPlaying)
                {
                    CleanupDeviceTransitionFixture();
                }
                else Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY fixtureCleanupDeferredUntilEditorExit=true");
                RestoreDeviceTransitionPlayOptions();
            }
        }

        static void CaptureDeviceTransitionPipelineSettings(
            RenderPipelineAsset previousDefault,
            RenderPipelineAsset previousQuality,
            bool previousOptionsEnabled,
            EnterPlayModeOptions previousOptions)
        {
            if ((previousDefault != null && !EditorUtility.IsPersistent(previousDefault)) ||
                (previousQuality != null && !EditorUtility.IsPersistent(previousQuality)))
                throw new InvalidOperationException("The device transition test requires persistent or null original render pipeline assets so it can restore them after domain reload.");
            string defaultPath = previousDefault == null ? string.Empty : AssetDatabase.GetAssetPath(previousDefault);
            string qualityPath = previousQuality == null ? string.Empty : AssetDatabase.GetAssetPath(previousQuality);
            if ((previousDefault != null && string.IsNullOrEmpty(defaultPath)) ||
                (previousQuality != null && string.IsNullOrEmpty(qualityPath)))
                throw new InvalidOperationException("The device transition test cannot safely restore an original render pipeline asset without an AssetDatabase path.");
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "captured", true);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", false);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "defaultNull", previousDefault == null);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "qualityNull", previousQuality == null);
            SessionState.SetString(DevicePlayTransitionStatePrefix + "defaultPath", defaultPath);
            SessionState.SetString(DevicePlayTransitionStatePrefix + "qualityPath", qualityPath);
            CaptureDeviceTransitionPlayOptions(previousOptionsEnabled, previousOptions);
        }

        static string CreateOwnedDeviceTransitionAssetFolder()
        {
            string folderName = "__ViewWeaveDevicePlayTransition_" + Guid.NewGuid().ToString("N");
            string path = "Assets/" + folderName;
            if (AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException("A unique device-transition asset folder unexpectedly already exists.");
            string guid = AssetDatabase.CreateFolder("Assets", folderName);
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException("The isolated device-transition asset folder could not be created.");
            SessionState.SetString(DevicePlayTransitionStatePrefix + "assetFolder", path);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", true);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", false);
            return path;
        }

        static void CleanupDeviceTransitionFixture()
        {
            if (SessionState.GetBool(DevicePlayTransitionStatePrefix + "cleanupDone", false)) return;
            RestoreDeviceTransitionPipelineSettings();

            GameObject target = FindSceneObject(DevicePlayTransitionTargetName);
            if (target != null)
            {
                MeshRenderer renderer = target.GetComponent<MeshRenderer>();
                Material material = renderer == null ? null : renderer.sharedMaterial;
                if (material != null && !EditorUtility.IsPersistent(material)) Object.DestroyImmediate(material);
                Object.DestroyImmediate(target);
            }

            ViewWeaveRig rig = FindSceneRig(DevicePlayTransitionRigName);
            if (rig != null) Object.DestroyImmediate(rig.gameObject);

            DestroyTransientAsset<UniversalRenderPipelineAsset>(DevicePlayTransitionPipelineName);
            DestroyTransientAsset<UniversalRendererData>(DevicePlayTransitionRendererDataName);
            DeleteOwnedDeviceTransitionAssetFolder();
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", true);
            Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY fixtureCleanup=true");
        }

        static void DeleteOwnedDeviceTransitionAssetFolder()
        {
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", false)) return;
            string path = SessionState.GetString(DevicePlayTransitionStatePrefix + "assetFolder", string.Empty);
            if (string.IsNullOrEmpty(path) || !path.StartsWith(DevicePlayTransitionAssetFolderPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete a device-transition asset folder outside the exact owned path prefix.");
            if (AssetDatabase.IsValidFolder(path) && !AssetDatabase.DeleteAsset(path))
                throw new InvalidOperationException("The owned device-transition pipeline asset folder could not be deleted.");
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", false);
            SessionState.SetString(DevicePlayTransitionStatePrefix + "assetFolder", string.Empty);
        }

        static void RestoreDeviceTransitionPipelineSettings()
        {
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "captured", false)) return;
            RenderPipelineAsset defaultAsset = SessionState.GetBool(DevicePlayTransitionStatePrefix + "defaultNull", true)
                ? null
                : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(SessionState.GetString(DevicePlayTransitionStatePrefix + "defaultPath", string.Empty));
            RenderPipelineAsset qualityAsset = SessionState.GetBool(DevicePlayTransitionStatePrefix + "qualityNull", true)
                ? null
                : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(SessionState.GetString(DevicePlayTransitionStatePrefix + "qualityPath", string.Empty));
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "defaultNull", true) && defaultAsset == null)
                throw new InvalidOperationException("The original default render pipeline asset could not be restored from its saved AssetDatabase path.");
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "qualityNull", true) && qualityAsset == null)
                throw new InvalidOperationException("The original quality render pipeline asset could not be restored from its saved AssetDatabase path.");
            GraphicsSettings.defaultRenderPipeline = defaultAsset;
            QualitySettings.renderPipeline = qualityAsset;
            Assert.AreSame(defaultAsset, GraphicsSettings.defaultRenderPipeline, "The device transition test must restore the original default render pipeline.");
            Assert.AreSame(qualityAsset, QualitySettings.renderPipeline, "The device transition test must restore the original quality render pipeline.");
            RestoreDeviceTransitionPlayOptions();
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "captured", false);
        }

        static void CaptureDeviceTransitionPlayOptions(bool optionsEnabled, EnterPlayModeOptions options)
        {
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "optionsStored", true);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "optionsEnabled", optionsEnabled);
            SessionState.SetInt(DevicePlayTransitionStatePrefix + "options", (int)options);
        }

        static IEnumerator WaitForDevicePreviewStop(
            ViewWeaveBridgeSession session,
            BridgeWorkerQuiltOutput sourceOutput,
            Func<bool> exitingEditModeObserved)
        {
            const double timeoutSeconds = 30.0;
            double startedAt = EditorApplication.timeSinceStartup;
            double nextLogAt = startedAt + 5.0;
            Func<bool> stopped = () => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                BridgeEditorPreviewRetention.PendingCount == 0 && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0;

            while (!stopped() && EditorApplication.timeSinceStartup - startedAt < timeoutSeconds)
            {
                double elapsed = EditorApplication.timeSinceStartup - startedAt;
                if (elapsed >= nextLogAt - startedAt)
                {
                    Debug.Log("EDITOR_PREVIEW_DEVICE_STOP_WAIT elapsed=" + elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                        ", " + DescribeDevicePreviewStopState(session, sourceOutput, exitingEditModeObserved()));
                    nextLogAt += 5.0;
                }
                yield return null;
            }

            if (!stopped())
            {
                string diagnostic = "elapsed=" + (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    ", " + DescribeDevicePreviewStopState(session, sourceOutput, exitingEditModeObserved());
                Debug.Log("EDITOR_PREVIEW_DEVICE_STOP_TIMEOUT " + diagnostic);
                Assert.Fail("The Edit Mode device producer did not stop and prove its resources before runtime startup. " + diagnostic);
            }
        }

        static string DescribeDevicePreviewStopState(
            ViewWeaveBridgeSession session,
            BridgeWorkerQuiltOutput sourceOutput,
            bool exitingEditModeObserved)
        {
            Type tracker = typeof(QuiltRenderer).Assembly.GetType("ViewWeave.QuiltRendererEditorCallbackTracker");
            object ReadTracker(string propertyName)
            {
                PropertyInfo property = tracker?.GetProperty(propertyName, BindingFlags.Static | BindingFlags.NonPublic);
                return property == null ? "missing" : property.GetValue(null);
            }

            BridgeWorkerQuiltOutput producer = PreviewApi.ProducerOutput;
            string summary = "exitingEditMode=" + exitingEditModeObserved +
                ", previewActive=" + PreviewApi.IsActive +
                ", previewStopping=" + PreviewApi.IsStopping +
                ", previewStatus=" + PreviewApi.Status +
                ", previewError=" + PreviewApi.LastError +
                ", callbackReady=" + ReadTracker("IsReady") +
                ", callbackInside=" + ReadTracker("IsInsideRenderCallback") +
                ", contextDepth=" + ReadTracker("ContextDepth") +
                ", cameraDepth=" + ReadTracker("CameraDepth") +
                ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount +
                ", retentionRecords={" + PreviewApi.DescribeRetentionStateWithoutNativeCalls() + "}" +
                ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                ", imageValidationPending=" + PreviewApi.NativeImageValidationPending +
                ", sourceSession=" + (session == null ? "null" : session.State.ToString()) +
                ", sourceSessionError=" + (session == null ? string.Empty : session.LastError) +
                ", sourceOutput=" + DescribeOutputState(sourceOutput) +
                ", producerOutput=" + DescribeOutputState(producer);
            return ToAscii(summary);
        }

        static string DescribeOutputState(BridgeWorkerQuiltOutput output)
        {
            if (output == null) return "null";
            return "ready=" + output.Ready + ", state=" + output.State +
                ", submissions=" + output.Submissions + ", completed=" + output.Completed +
                ", error=" + (output.LastError ?? string.Empty);
        }

        static string ToAscii(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] characters = new char[value.Length];
            int length = 0;
            foreach (char character in value)
                if (character >= 0x20 && character <= 0x7e) characters[length++] = character;
            return new string(characters, 0, length);
        }

        static void RestoreDeviceTransitionPlayOptions()
        {
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "optionsStored", false)) return;
            EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(
                DevicePlayTransitionStatePrefix + "optionsEnabled", false);
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(
                DevicePlayTransitionStatePrefix + "options", (int)EnterPlayModeOptions.None);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "optionsStored", false);
        }

        static void DestroyTransientAsset<T>(string exactName) where T : Object
        {
            T found = null;
            foreach (T candidate in Resources.FindObjectsOfTypeAll<T>())
            {
                if (candidate == null || candidate.name != exactName || EditorUtility.IsPersistent(candidate)) continue;
                if (found != null) return;
                found = candidate;
            }
            if (found != null) Object.DestroyImmediate(found);
        }

        static ViewWeaveRig FindSceneRig(string exactName)
        {
            ViewWeaveRig found = null;
            foreach (ViewWeaveRig candidate in Resources.FindObjectsOfTypeAll<ViewWeaveRig>())
            {
                if (candidate == null || candidate.gameObject.name != exactName || EditorUtility.IsPersistent(candidate)) continue;
                if (!candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        static GameObject FindSceneObject(string exactName)
        {
            GameObject found = null;
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || candidate.name != exactName || EditorUtility.IsPersistent(candidate)) continue;
                if (!candidate.scene.IsValid() || !candidate.scene.isLoaded) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        static string DescribeMatchingSceneObjects(string exactName)
        {
            var matches = Resources.FindObjectsOfTypeAll<ViewWeaveRig>();
            var summaries = new System.Collections.Generic.List<string>();
            foreach (ViewWeaveRig candidate in matches)
            {
                if (candidate == null) continue;
                summaries.Add(candidate.gameObject.name + "@" + candidate.gameObject.scene.name +
                    ", loaded=" + candidate.gameObject.scene.isLoaded + ", persistent=" + EditorUtility.IsPersistent(candidate));
            }
            return "Expected=" + exactName + ", scene Rigs=" + string.Join("; ", summaries);
        }

        static void StartDevicePreview(ViewWeaveRig rig)
        {
            if (!PreviewApi.Start(rig, true, out string error))
                throw new AssertionException("Looking Glass Edit Mode preview failed to start: " + error);
            Debug.Log("EDITOR_PREVIEW_DEVICE start accepted");
        }

        static IEnumerator WaitForDeviceWorker(string label)
        {
            BridgeWorkerQuiltOutput output = null;
            double startedAt = EditorApplication.timeSinceStartup;
            double deadline = startedAt + 45.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                output = PreviewApi.ProducerOutput;
                if (output != null && output.Ready && output.Profile.IsValid && output.Submissions > 0 && output.Completed > 0)
                {
                    Debug.Log("EDITOR_PREVIEW_DEVICE ready phase=" + label + ", profile=" + output.Profile +
                        ", submissions=" + output.Submissions + ", completed=" + output.Completed);
                    yield break;
                }
                if (output != null && (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || !string.IsNullOrEmpty(output.LastError)))
                    Assert.Fail("Looking Glass Worker failed during " + label + " preview: " + output.LastError);
                yield return null;
            }
            Assert.Fail("Looking Glass Worker did not report Ready and completed submission during " + label +
                " preview. elapsedSeconds=" + (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }

        static IEnumerator WaitForDevicePreviewContent(string label, bool expectMagenta)
        {
            const uint minimumCompletedFrames = 5;
            double startedAt = EditorApplication.timeSinceStartup;
            double deadline = startedAt + 30.0;
            uint lastSampledCompleted = uint.MaxValue;
            string lastDiagnostic = string.Empty;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                BridgeWorkerQuiltOutput output = PreviewApi.ProducerOutput;
                RenderTexture texture = PreviewApi.Texture;
                if (output != null && output.Ready && output.Completed >= minimumCompletedFrames && IsCreated(texture) &&
                    output.Completed != lastSampledCompleted)
                {
                    lastSampledCompleted = output.Completed;
                    bool hasExpectedPixels = HasExpectedPreviewTargetPixels(texture, expectMagenta, out lastDiagnostic);
                    Debug.Log("EDITOR_PREVIEW_RESTART_PIXELS phase=" + label +
                        ", minimumCompletedFrames=" + minimumCompletedFrames +
                        ", expectedColor=" + (expectMagenta ? "magenta" : "green") +
                        ", completed=" + output.Completed +
                        ", submissions=" + output.Submissions +
                        ", sessionGeneration=" + output.SessionGeneration +
                        ", ready=" + output.Ready +
                        ", workerState=" + output.State +
                        ", previewActive=" + PreviewApi.IsActive +
                        ", previewStopping=" + PreviewApi.IsStopping +
                        ", status=" + ToAscii(PreviewApi.Status) +
                        ", error=" + ToAscii(PreviewApi.LastError) +
                        ", textureCreated=" + texture.IsCreated() +
                        ", textureSize=" + texture.width + "x" + texture.height +
                        ", textureNativePtr=0x" + texture.GetNativeTexturePtr().ToInt64().ToString("X") +
                        ", hasExpectedPixels=" + hasExpectedPixels +
                        ", pixelStats=" + lastDiagnostic);
                    if (hasExpectedPixels) yield break;
                }
                if (output != null && (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || !string.IsNullOrEmpty(output.LastError)))
                    Assert.Fail("The device Worker failed while validating " + label + " preview content: " + output.LastError);
                yield return null;
            }
            Assert.Fail("The device preview did not produce the expected " + (expectMagenta ? "magenta" : "green") + " target pixels during " + label +
                ". elapsedSeconds=" + (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ", minimumCompletedFrames=5, completedSamples=" + lastSampledCompleted + ", pixelStats=" + lastDiagnostic +
                ", status=" + ToAscii(PreviewApi.Status) + ", error=" + ToAscii(PreviewApi.LastError));
        }

        static IEnumerator WaitForBridgeSnapshotContent(
            string label,
            string snapshotBasePath,
            int priorSnapshotSessions,
            bool expectMagenta)
        {
            double deadline = EditorApplication.timeSinceStartup + 30.0;
            string frameThreePath = string.Empty;
            string frameFivePath = string.Empty;
            string frameThreeStats = string.Empty;
            string frameFiveStats = string.Empty;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                string[] frameFivePaths = GetBridgeFrameFiveSnapshotPaths(snapshotBasePath);
                if (frameFivePaths.Length > priorSnapshotSessions)
                {
                    string newestFrameFive = frameFivePaths[0];
                    for (int i = 1; i < frameFivePaths.Length; i++)
                    {
                        if (File.GetLastWriteTimeUtc(frameFivePaths[i]) > File.GetLastWriteTimeUtc(newestFrameFive))
                            newestFrameFive = frameFivePaths[i];
                    }

                    frameFivePath = newestFrameFive;
                    frameThreePath = frameFivePath.Replace(".frame-005.bridge.png", ".frame-003.bridge.png");
                    bool frameThreeRead = TryReadBridgeSnapshotPixels(frameThreePath, expectMagenta, out frameThreeStats, out bool frameThreeMatches);
                    bool frameFiveRead = TryReadBridgeSnapshotPixels(frameFivePath, expectMagenta, out frameFiveStats, out bool frameFiveMatches);
                    if (frameThreeRead && frameFiveRead)
                    {
                        Debug.Log("EDITOR_PREVIEW_RESTART_BRIDGE_PIXELS phase=" + label +
                            ", expectedColor=" + (expectMagenta ? "magenta" : "green") +
                            ", frame3=" + ToAscii(Path.GetFileName(frameThreePath)) + ", frame3Matches=" + frameThreeMatches +
                            ", frame3Stats=" + frameThreeStats +
                            ", frame5=" + ToAscii(Path.GetFileName(frameFivePath)) + ", frame5Matches=" + frameFiveMatches +
                            ", frame5Stats=" + frameFiveStats);
                        Assert.IsTrue(frameThreeMatches,
                            "The Bridge interop texture frame 3 did not contain the current " + (expectMagenta ? "magenta" : "green") + " target. " + frameThreeStats);
                        Assert.IsTrue(frameFiveMatches,
                            "The Bridge interop texture frame 5 did not contain the current " + (expectMagenta ? "magenta" : "green") + " target. " + frameFiveStats);
                        yield break;
                    }
                }
                yield return null;
            }

            Assert.Fail("Bridge interop snapshots did not become readable for " + label +
                ". expectedColor=" + (expectMagenta ? "magenta" : "green") +
                ", frame3=" + ToAscii(frameThreePath) + ", frame3Stats=" + frameThreeStats +
                ", frame5=" + ToAscii(frameFivePath) + ", frame5Stats=" + frameFiveStats);
        }

        static int CountBridgeFrameFiveSnapshots(string snapshotBasePath) => GetBridgeFrameFiveSnapshotPaths(snapshotBasePath).Length;

        static double GetRestartObserverHoldSeconds()
        {
            string value = Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_RESTART_OBSERVER_HOLD_SECONDS");
            if (string.IsNullOrWhiteSpace(value)) return 0.0;
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds))
                throw new AssertionException("The optional observer hold must be a finite number of seconds.");
            return Math.Max(0.0, Math.Min(seconds, 120.0));
        }

        static IEnumerator HoldMagentaPreviewForObservation(double seconds)
        {
            double startedAt = EditorApplication.timeSinceStartup;
            double deadline = startedAt + seconds;
            Debug.Log("MAGENTA_HOLD_BEGIN expectedColor=magenta, seconds=" +
                seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ", active=" + PreviewApi.IsActive + ", status=" + ToAscii(PreviewApi.Status));
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (!PreviewApi.IsActive || PreviewApi.IsStopping)
                    Assert.Fail("The magenta preview stopped before the observer hold completed. status=" + ToAscii(PreviewApi.Status) +
                        ", error=" + ToAscii(PreviewApi.LastError));
                yield return null;
            }
            Debug.Log("MAGENTA_HOLD_END expectedColor=magenta, elapsed=" +
                (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ", active=" + PreviewApi.IsActive + ", status=" + ToAscii(PreviewApi.Status));
        }

        static string[] GetBridgeFrameFiveSnapshotPaths(string snapshotBasePath)
        {
            if (string.IsNullOrWhiteSpace(snapshotBasePath)) return Array.Empty<string>();
            string directory = Path.GetDirectoryName(Path.GetFullPath(snapshotBasePath));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return Array.Empty<string>();
            string pattern = Path.GetFileNameWithoutExtension(snapshotBasePath) + ".session-*.frame-005.bridge.png";
            return Directory.GetFiles(directory, pattern);
        }

        static bool TryReadBridgeSnapshotPixels(string path, bool expectMagenta, out string diagnostic, out bool matches)
        {
            diagnostic = string.Empty;
            matches = false;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!image.LoadImage(File.ReadAllBytes(path), false)) return false;
                Color32[] pixels = image.GetPixels32();
                int matchingPixels = 0;
                int nonBlackPixels = 0;
                byte maxRed = 0;
                byte maxGreen = 0;
                byte maxBlue = 0;
                foreach (Color32 pixel in pixels)
                {
                    if (pixel.r > maxRed) maxRed = pixel.r;
                    if (pixel.g > maxGreen) maxGreen = pixel.g;
                    if (pixel.b > maxBlue) maxBlue = pixel.b;
                    if (pixel.r > 2 || pixel.g > 2 || pixel.b > 2) nonBlackPixels++;
                    if (expectMagenta)
                    {
                        if (pixel.r > 32 && pixel.b > 32 && pixel.r > pixel.g * 2 && pixel.b > pixel.g * 2)
                            matchingPixels++;
                    }
                    else if (pixel.g > 32 && pixel.g > pixel.r * 2 && pixel.g > pixel.b * 2)
                    {
                        matchingPixels++;
                    }
                }
                diagnostic = "matchingPixels=" + matchingPixels + ", nonBlack=" + nonBlackPixels +
                    ", maxRGB=" + maxRed + "/" + maxGreen + "/" + maxBlue;
                matches = matchingPixels > 0;
                return true;
            }
            catch (IOException exception)
            {
                diagnostic = "readError=" + ToAscii(exception.Message);
                return false;
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        static bool HasExpectedPreviewTargetPixels(RenderTexture source, bool expectMagenta, out string diagnostic)
        {
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = source;
                image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                image.Apply(false, false);
                Color32[] pixels = image.GetPixels32();
                int matchingPixels = 0;
                int nonBlackPixels = 0;
                byte maxRed = 0;
                byte maxGreen = 0;
                byte maxBlue = 0;
                foreach (Color32 pixel in pixels)
                {
                    if (pixel.r > maxRed) maxRed = pixel.r;
                    if (pixel.g > maxGreen) maxGreen = pixel.g;
                    if (pixel.b > maxBlue) maxBlue = pixel.b;
                    if (pixel.r > 32 || pixel.g > 32 || pixel.b > 32) nonBlackPixels++;
                    if (expectMagenta)
                    {
                        if (pixel.r > 32 && pixel.b > 32 && pixel.r > pixel.g * 2 && pixel.b > pixel.g * 2)
                            matchingPixels++;
                    }
                    else if (pixel.g > 32 && pixel.g > pixel.r * 2 && pixel.g > pixel.b * 2)
                    {
                        matchingPixels++;
                    }
                }
                diagnostic = "matchingPixels=" + matchingPixels + ", nonBlack=" + nonBlackPixels +
                    ", maxRGB=" + maxRed + "/" + maxGreen + "/" + maxBlue;
                return matchingPixels > 0;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(image);
            }
        }

        static void SetPreviewTargetColor(GameObject targetObject, Color color)
        {
            Renderer targetRenderer = targetObject == null ? null : targetObject.GetComponent<Renderer>();
            Material material = targetRenderer == null ? null : targetRenderer.sharedMaterial;
            Assert.IsNotNull(material, "The preview target material must be available for distinct cycle colors.");
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            else Assert.Fail("The preview target shader has no supported color property.");
        }

        static void LogDevicePreviewCycle(string label, string nativeTracePath, BridgeWorkerQuiltOutput output)
        {
            RenderTexture texture = PreviewApi.Texture;
            Assert.IsNotNull(texture, "The active device preview must expose its producer quilt texture.");
            Assert.IsTrue(texture.IsCreated(), "The active device preview quilt texture must be created.");
            QuiltRenderer renderer = PreviewApi.ProducerRenderer;
            Assert.IsNotNull(renderer);
            Debug.Log("EDITOR_PREVIEW_RESTART_CYCLE phase=" + label +
                ", sessionGeneration=" + output.SessionGeneration +
                ", nativeHandle=0x" + output.NativeHandle.ToInt64().ToString("X") +
                ", ready=" + output.Ready +
                ", state=" + output.State +
                ", submissions=" + output.Submissions +
                ", completed=" + output.Completed +
                ", profile=" + output.Profile +
                ", latestViews=" + renderer.LastRenderedViewCount +
                ", layout=" + PreviewApi.Layout +
                ", previewActive=" + PreviewApi.IsActive +
                ", previewStopping=" + PreviewApi.IsStopping +
                ", status=" + ToAscii(PreviewApi.Status) +
                ", error=" + ToAscii(PreviewApi.LastError) +
                ", texture=" + texture.width + "x" + texture.height +
                ", textureNativePtr=0x" + texture.GetNativeTexturePtr().ToInt64().ToString("X") +
                ", windowTrace={" + ReadNativePreviewWindowEvents(nativeTracePath) + "}");
        }

        static string ReadNativePreviewWindowEvents(string tracePath)
        {
            if (string.IsNullOrWhiteSpace(tracePath) || !File.Exists(tracePath)) return "traceUnavailable=true";
            string[] lines = File.ReadAllLines(tracePath);
            int displayCount = 0;
            int shownCount = 0;
            int hiddenCount = 0;
            var tail = new System.Collections.Generic.List<string>();
            foreach (string line in lines)
            {
                if (line.IndexOf("instance_window_display=", StringComparison.Ordinal) >= 0) displayCount++;
                if (line.IndexOf("show_window=true", StringComparison.Ordinal) >= 0) shownCount++;
                if (line.IndexOf("show_window=false", StringComparison.Ordinal) >= 0) hiddenCount++;
                if (line.IndexOf("instance_window_display=", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("show_window=", StringComparison.Ordinal) >= 0)
                {
                    tail.Add(ToAscii(line.Trim()));
                    if (tail.Count > 6) tail.RemoveAt(0);
                }
            }
            return "displayCalls=" + displayCount + ", shownCalls=" + shownCount + ", hiddenCalls=" + hiddenCount +
                ", events=" + string.Join("|", tail);
        }

        static void StopDevicePreview(string label)
        {
            Assert.IsTrue(PreviewApi.Stop(), "The device preview Stop request should be accepted.");
            Debug.Log("EDITOR_PREVIEW_DEVICE stop_requested phase=" + label);
        }

        static IEnumerator WaitForDeviceStop(string label)
        {
            double startedAt = EditorApplication.timeSinceStartup;
            double deadline = startedAt + 30.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (!PreviewApi.IsActive && !PreviewApi.IsStopping && BridgeEditorPreviewRetention.PendingCount == 0 &&
                    BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
                {
                    Debug.Log("EDITOR_PREVIEW_DEVICE stopped phase=" + label + ", active=false, pending=0, elapsedSeconds=" +
                        (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                    yield break;
                }
                yield return null;
            }
            Assert.Fail("Device preview did not complete Worker handoff and GPU proof during " + label + " Stop. active=" +
                PreviewApi.IsActive + ", stopping=" + PreviewApi.IsStopping + ", retentionPending=" +
                BridgeEditorPreviewRetention.PendingCount + ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                ", elapsedSeconds=" + (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ", records=" + PreviewApi.DescribeRetentionRecords());
        }

        static IEnumerator RunLocalPreview(bool hdrp)
        {
            var fixture = new Fixture(hdrp);
            bool previewAttempted = false;
            try
            {
                fixture.Setup();
                QuiltRenderer sourceRenderer = fixture.Rig.Renderer;
                float storedAspect = sourceRenderer.settings.viewAspect;
                float storedDistance = sourceRenderer.settings.cameraDistance;
                sourceRenderer.physicalFraming.enabled = true;
                sourceRenderer.physicalFraming.visibleApertureWidthMillimeters = 800f;
                sourceRenderer.physicalFraming.visibleApertureHeightMillimeters = 450f;
                sourceRenderer.physicalFraming.unitsPerMeter = 1f;
                sourceRenderer.settings.fieldOfView = 60f;
                fixture.RenderSourceControl();
                AssertPhysicalSnapshot(sourceRenderer.LatestSnapshot, 800f / 450f, 60f, 0.45f,
                    "The source renderer should use the configured physical aperture without mutating its base settings.");
                Assert.AreEqual(storedAspect, sourceRenderer.settings.viewAspect, 0.0001f);
                Assert.AreEqual(storedDistance, sourceRenderer.settings.cameraDistance, 0.0001f);
                previewAttempted = true;
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("Local Editor preview did not start: " + startError);

                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "Local Editor preview did not produce a created quilt texture.");
                RenderTexture first = PreviewApi.Texture;
                QuiltRenderer producerRenderer = PreviewApi.ProducerRenderer;
                Assert.IsNotNull(producerRenderer);
                Assert.IsTrue(QuiltDisplaySettingsResolver.TryResolve(sourceRenderer, out QuiltSettings gizmoSettings,
                    out QuiltEditorDisplayProfile displayProfile, out string framingError), framingError);
                Assert.AreEqual(QuiltDisplaySettingsSource.LocalPreview, displayProfile.source);
                AssertPhysicalSnapshot(producerRenderer.LatestSnapshot, 800f / 450f, 60f, 0.45f,
                    "The hidden preview producer should copy and render the physical framing configuration.");
                Assert.AreEqual(gizmoSettings.viewAspect, producerRenderer.LatestSnapshot.Geometry.viewAspect, 0.0001f,
                    "The gizmo resolver and preview producer should use the same effective physical aspect.");
                Assert.AreEqual(gizmoSettings.cameraDistance, producerRenderer.LatestSnapshot.Geometry.cameraDistance, 0.0001f,
                    "The gizmo resolver and preview producer should use the same effective physical distance.");
                Assert.AreEqual(storedAspect, sourceRenderer.settings.viewAspect, 0.0001f,
                    "Physical rendering must not write the derived aspect into the source profile.");
                Assert.AreEqual(storedDistance, sourceRenderer.settings.cameraDistance, 0.0001f,
                    "Physical rendering must not write the derived distance into the source profile.");
                long physicalRevision = producerRenderer.LatestSnapshot.Revision;
                Assert.IsTrue(sourceRenderer.TryGetFraming(out QuiltCameraFraming liveFraming));
                Assert.IsTrue(sourceRenderer.TrySetFraming(new QuiltCameraFraming(45f, liveFraming.FocusDistance,
                    liveFraming.PhysicalFramingEnabled, liveFraming.ApertureWidthMillimeters,
                    liveFraming.ApertureHeightMillimeters, liveFraming.UnitsPerMeter), out string framingUpdateError), framingUpdateError);
                BridgeEditorPreview.RequestRefresh();
                yield return WaitFor(() => producerRenderer != null && producerRenderer.LatestSnapshot.IsValid &&
                    producerRenderer.LatestSnapshot.Revision > physicalRevision &&
                    Mathf.Abs(producerRenderer.LatestSnapshot.Geometry.fieldOfView - 45f) < 0.001f,
                    "The preview did not apply a changed field of view to physical framing.");
                AssertPhysicalSnapshot(producerRenderer.LatestSnapshot, 800f / 450f, 45f, 0.45f,
                    "Changing field of view should move the camera while keeping the physical plane height fixed.");
                Assert.AreEqual(storedAspect, sourceRenderer.settings.viewAspect, 0.0001f);
                Assert.AreEqual(storedDistance, sourceRenderer.settings.cameraDistance, 0.0001f);
                Debug.Log("EDITOR_PREVIEW_GPU cameras=" + PreviewApi.DescribeCameras(fixture.Rig.Renderer.sourceCamera, fixture.TargetObject));
                Assert.IsTrue(producerRenderer.HasRenderedQuilt);
                Assert.AreEqual(8, producerRenderer.LastRenderedViewCount);
                Assert.AreEqual(128, first.width);
                Assert.AreEqual(64, first.height);
                Assert.AreEqual(fixture.ExpectedViewSamples, producerRenderer.ViewAntiAliasingSamples,
                    "The preview view texture should follow the supported URP MSAA sample count.");
                Assert.AreEqual(fixture.ExpectedViewSamples > 1 ? 1 : 0, producerRenderer.ResolveAntiAliasingSamples,
                    "The preview resolve texture should be single-sampled when MSAA is active.");
                Debug.Log("EDITOR_PREVIEW_GPU render pipeline=" + (hdrp ? "HDRP" : "URP") + ", views=" + producerRenderer.LastRenderedViewCount +
                    ", samplesView=" + producerRenderer.ViewAntiAliasingSamples + ", samplesResolve=" + producerRenderer.ResolveAntiAliasingSamples);
                bool hasGreenTargetPixels = HasGreenTargetPixels(first, out string pixelSummary);
                Debug.Log("EDITOR_PREVIEW_GPU pixels=" + pixelSummary);
                Assert.IsTrue(hasGreenTargetPixels, "The quilt should contain green-dominant pixels from the unlit preview target. " + pixelSummary);

                QuiltQualityProfile originalProfile = fixture.Rig.Profile;
                QuiltLayout originalLayout = producerRenderer.LatestSnapshot.Layout;
                InvalidOperationException profileError = Assert.Throws<InvalidOperationException>(
                    () => fixture.Rig.ApplyProfile(new QuiltQualityProfile(256, 128, 4, 2, 8, 1f, 20f)));
                StringAssert.Contains("Editor preview owns this Rig", profileError.Message);
                Assert.AreEqual(originalProfile.QuiltWidth, fixture.Rig.Profile.QuiltWidth);
                Assert.AreEqual(originalProfile.QuiltHeight, fixture.Rig.Profile.QuiltHeight);
                Assert.AreEqual(originalLayout.QuiltWidth, producerRenderer.LatestSnapshot.Layout.QuiltWidth);
                Assert.AreEqual(originalLayout.QuiltHeight, producerRenderer.LatestSnapshot.Layout.QuiltHeight);
                Assert.AreSame(first, PreviewApi.Texture, "A rejected structural change must not rebuild or replace preview textures.");
                Debug.Log("EDITOR_PREVIEW_GPU profile_mutation pipeline=" + (hdrp ? "HDRP" : "URP") + ", rejected=true, profileUnchanged=true");

                PreviewApi.Stop();
                yield return WaitFor(() => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                    BridgeEditorPreviewRetention.PendingCount == 0 && (!first || !first.IsCreated()),
                    "Editor preview resources did not complete stop and GPU proof.");
                Assert.IsTrue(BridgeEditorPreviewRetention.RequestStop(true));
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount);
                Debug.Log("EDITOR_PREVIEW_GPU stop pipeline=" + (hdrp ? "HDRP" : "URP") + ", active=false, pending=0");
            }
            finally
            {
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                fixture.Dispose();
            }
        }

        static void AssertPhysicalSnapshot(QuiltRenderSnapshot snapshot, float expectedAspect, float fieldOfView,
            double expectedPlaneHeight, string message)
        {
            Assert.IsTrue(snapshot.IsValid, message);
            Assert.IsTrue(snapshot.Geometry.physicalFramingEnabled, message);
            Assert.AreEqual(expectedAspect, snapshot.Geometry.viewAspect, 0.0001f, message);
            Assert.AreEqual(fieldOfView, snapshot.Geometry.fieldOfView, 0.001f, message);
            Assert.AreEqual(800f, snapshot.PhysicalFraming.visibleApertureWidthMillimeters, 0.001f, message);
            Assert.AreEqual(450f, snapshot.PhysicalFraming.visibleApertureHeightMillimeters, 0.001f, message);
            float planeHeight = 2f * snapshot.Geometry.cameraDistance * Mathf.Tan(snapshot.Geometry.fieldOfView * Mathf.Deg2Rad * 0.5f);
            Assert.AreEqual(expectedPlaneHeight, planeHeight, 0.001f, message);
        }

        static IEnumerator WaitFor(Func<bool> predicate, string failureMessage)
        {
            for (int frame = 0; frame < 180; frame++)
            {
                if (predicate()) yield break;
                yield return null;
            }
            Assert.Fail(failureMessage);
        }

        static string ReadDiscoveryDiagnostic()
        {
            try
            {
                Assembly bridgeAssembly = typeof(BridgeWorkerQuiltOutput).Assembly;
                Type discoveryType = bridgeAssembly.GetType("ViewWeave.BridgeDisplayDiscovery");
                Type nativeType = bridgeAssembly.GetType("ViewWeave.BridgeDisplayDiscoveryNative");
                Type guardType = bridgeAssembly.GetType("ViewWeave.BridgeNativeImageGuard");
                if (discoveryType == null || nativeType == null || guardType == null) return "reflectionTypesMissing";

                const BindingFlags staticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
                object GetStatic(string name) => discoveryType.GetField(name, staticPrivate)?.GetValue(null);
                object nativeHandle = GetStatic("handle");
                object moduleIdentity = GetStatic("moduleIdentity");
                double startedAt = GetStatic("requestStartedAt") is double started ? started : 0.0;
                bool validationPending = guardType.GetProperty("IsValidationPending", staticPrivate)?.GetValue(null) is bool pending && pending;
                bool identityValid = false;
                string identityError = string.Empty;
                int nativeResult = int.MinValue;
                uint requiredCount = 0;
                object nativeStatus = null;

                if (nativeHandle is IntPtr handle && handle != IntPtr.Zero && moduleIdentity != null && !validationPending)
                {
                    MethodInfo validate = guardType.GetMethod("TryValidateOwnerIdentity", staticPrivate);
                    if (validate != null)
                    {
                        object[] validateArgs = { moduleIdentity, null };
                        identityValid = validate.Invoke(null, validateArgs) is bool valid && valid;
                        identityError = validateArgs[1] as string ?? string.Empty;
                    }
                    if (identityValid)
                    {
                        Type statusType = nativeType.GetNestedType("SnapshotStatusV1", BindingFlags.NonPublic);
                        object adapter = nativeType.GetProperty("Adapter", staticPrivate).GetValue(null);
                        MethodInfo getSnapshot = adapter.GetType().GetMethod("GetDisplaySnapshot");
                        nativeStatus = Activator.CreateInstance(statusType);
                        statusType.GetField("structSize").SetValue(nativeStatus, (uint)Marshal.SizeOf(statusType));
                        statusType.GetField("version").SetValue(nativeStatus, 1u);
                        object[] queryArgs = { handle, nativeStatus, null, 0u, 0u };
                        nativeResult = (int)getSnapshot.Invoke(adapter, queryArgs);
                        nativeStatus = queryArgs[1];
                        requiredCount = (uint)queryArgs[4];
                    }
                }

                string nativeState = "notQueried";
                if (nativeStatus != null)
                {
                    Type statusType = nativeStatus.GetType();
                    object Read(string name) => statusType.GetField(name).GetValue(nativeStatus);
                    nativeState = "nativeResult=" + nativeResult +
                        ", state=" + Read("state") +
                        ", generation=" + Read("snapshotGeneration") +
                        ", requested=" + Read("requestedGeneration") +
                        ", pending=" + Read("pending") +
                        ", cleanup=" + Read("cleanupFinished") +
                        ", result=" + Read("result") +
                        ", count=" + Read("itemCount") +
                        ", required=" + requiredCount +
                        ", error=" + (Read("error") as string ?? string.Empty);
                }

                double elapsed = startedAt == 0.0 ? 0.0 : Time.realtimeSinceStartupAsDouble - startedAt;
                object candidate = GetStatic("candidateSnapshot");
                return "kind=" + GetStatic("requestKind") +
                    ", candidate=" + (candidate != null) +
                    ", closeRequested=" + GetStatic("closeRequested") +
                    ", elapsed=" + elapsed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    ", epoch=" + GetStatic("lastTickEditorEpoch") +
                    ", validationPending=" + validationPending +
                    ", identityValid=" + identityValid +
                    ", identityError=" + identityError +
                    ", recoveryPending=" + GetStatic("recoveryPending") +
                    ", " + nativeState;
            }
            catch (Exception exception)
            {
                return "diagnosticException=" + exception.GetType().Name;
            }
        }

        static bool IsCreated(RenderTexture texture) => texture != null && texture.IsCreated();

        static ViewWeaveRig CreateSecondaryRig(string name)
        {
            var rigObject = new GameObject(name);
            rigObject.SetActive(false);
            Camera camera = rigObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.allowMSAA = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            var renderer = rigObject.AddComponent<QuiltRenderer>();
            var rig = rigObject.AddComponent<ViewWeaveRig>();
            rig.ApplyProfile(new QuiltQualityProfile(128, 64, 4, 2, 8, 1f, 20f));
            renderer.settings.cameraDistance = 3f;
            renderer.settings.fieldOfView = 60f;
            if (GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset)
            {
                var cameraData = rigObject.AddComponent<HDAdditionalCameraData>();
                cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = Color.black;
                rig.SetGuard(rigObject.AddComponent<HdrpQuiltCamera>());
            }
            else if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
            {
                var cameraData = rigObject.AddComponent<UniversalAdditionalCameraData>();
                if (cameraData.renderType != CameraRenderType.Base) cameraData.renderType = CameraRenderType.Base;
                rig.SetGuard(rigObject.AddComponent<UrpQuiltCamera>());
            }
            if (!rig.ConfigureComponents()) throw new InvalidOperationException("The secondary preview Rig could not configure.");
            rigObject.SetActive(true);
            return rig;
        }

        static bool HasGreenTargetPixels(RenderTexture source, out string diagnostic)
        {
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = source;
                image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                image.Apply(false, false);
                Color32[] pixels = image.GetPixels32();
                int greenPixels = 0;
                int nonBlackPixels = 0;
                byte maxRed = 0;
                byte maxGreen = 0;
                byte maxBlue = 0;
                foreach (Color32 pixel in pixels)
                {
                    if (pixel.r > maxRed) maxRed = pixel.r;
                    if (pixel.g > maxGreen) maxGreen = pixel.g;
                    if (pixel.b > maxBlue) maxBlue = pixel.b;
                    if (pixel.r > 32 || pixel.g > 32 || pixel.b > 32) nonBlackPixels++;
                    if (pixel.g > 32 && pixel.g > pixel.r * 2 && pixel.g > pixel.b * 2) greenPixels++;
                }
                diagnostic = "greenDominant=" + greenPixels + ", nonBlack=" + nonBlackPixels + ", maxRGB=" + maxRed + "/" + maxGreen + "/" + maxBlue;
                return greenPixels > 0;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(image);
            }
        }

        sealed class Fixture
        {
            readonly bool hdrp;
            RenderPipelineAsset previousDefault;
            RenderPipelineAsset previousQuality;
            RenderPipelineAsset pipeline;
            UniversalRendererData urpRendererData;
            UniversalRenderPipelineAsset urpPipeline;
            GameObject rigObject;
            GameObject targetObject;
            Material targetMaterial;
            ViewWeaveRig rig;

            internal ViewWeaveRig Rig => rig;
            internal GameObject TargetObject => targetObject;
            internal RenderPipelineAsset PreviousDefaultPipeline => previousDefault;
            internal RenderPipelineAsset PreviousQualityPipeline => previousQuality;
            internal int ExpectedViewSamples { get; private set; } = 1;

            internal Fixture(bool hdrp) => this.hdrp = hdrp;

            internal void Setup(string persistentUrpAssetDirectory = null)
            {
                previousDefault = GraphicsSettings.defaultRenderPipeline;
                previousQuality = QualitySettings.renderPipeline;
                if (hdrp)
                {
                    pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                }
                else
                {
                    if (string.IsNullOrEmpty(persistentUrpAssetDirectory))
                    {
                        urpRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                        urpPipeline = UniversalRenderPipelineAsset.Create(urpRendererData);
                    }
                    else
                    {
                        if (!AssetDatabase.IsValidFolder(persistentUrpAssetDirectory) ||
                            !persistentUrpAssetDirectory.StartsWith(DevicePlayTransitionAssetFolderPrefix, StringComparison.Ordinal))
                            throw new InvalidOperationException("Persistent URP assets require the test-owned device-transition folder.");
                        urpRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                        AssetDatabase.CreateAsset(urpRendererData, persistentUrpAssetDirectory + "/RendererData.asset");
                        urpPipeline = UniversalRenderPipelineAsset.Create(urpRendererData);
                        AssetDatabase.CreateAsset(urpPipeline, persistentUrpAssetDirectory + "/Pipeline.asset");
                    }
                    urpPipeline.msaaSampleCount = 4;
                    if (!string.IsNullOrEmpty(persistentUrpAssetDirectory)) AssetDatabase.SaveAssets();
                    pipeline = urpPipeline;
                }
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;

                rigObject = new GameObject(hdrp ? "HDRP Editor Preview Rig" : "URP Editor Preview Rig");
                rigObject.SetActive(false);
                Camera sourceCamera = rigObject.AddComponent<Camera>();
                sourceCamera.clearFlags = CameraClearFlags.SolidColor;
                sourceCamera.allowMSAA = !hdrp;
                sourceCamera.backgroundColor = Color.black;
                sourceCamera.transform.position = Vector3.zero;
                sourceCamera.transform.rotation = Quaternion.identity;
                QuiltRenderer renderer = rigObject.AddComponent<QuiltRenderer>();
                rig = rigObject.AddComponent<ViewWeaveRig>();
                rig.ApplyProfile(new QuiltQualityProfile(128, 64, 4, 2, 8, 1f, 20f));
                renderer.settings.cameraDistance = 3f;
                renderer.settings.fieldOfView = 60f;
                if (hdrp)
                {
                    var cameraData = rigObject.AddComponent<HDAdditionalCameraData>();
                    cameraData.volumeLayerMask = 1;
                    cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    cameraData.backgroundColorHDR = Color.black;
                    rig.SetGuard(rigObject.AddComponent<HdrpQuiltCamera>());
                }
                else
                {
                    UniversalAdditionalCameraData cameraData = rigObject.AddComponent<UniversalAdditionalCameraData>();
                    if (cameraData.renderType != CameraRenderType.Base) cameraData.renderType = CameraRenderType.Base;
                    rig.SetGuard(rigObject.AddComponent<UrpQuiltCamera>());
                    var descriptor = new RenderTextureDescriptor(32, 32, RenderTextureFormat.ARGB32, 24) { msaaSamples = 4 };
                    int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
                    ExpectedViewSamples = supported == 2 || supported == 4 || supported == 8 ? supported : 1;
                    if (ExpectedViewSamples != 4)
                        TestContext.Progress.WriteLine("URP MSAA request=4 actual supported samples=" + ExpectedViewSamples);
                }
                if (!rig.ConfigureComponents()) throw new InvalidOperationException("Source Rig configuration failed.");

                targetObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                targetObject.name = "Editor Preview GPU target";
                targetObject.transform.position = new Vector3(0f, 0f, 2f);
                targetObject.transform.rotation = Quaternion.identity;
                Shader shader = Shader.Find(hdrp ? "HDRP/Unlit" : "Universal Render Pipeline/Unlit");
                if (shader == null) throw new InvalidOperationException("The pipeline unlit shader is unavailable.");
                targetMaterial = new Material(shader);
                if (targetMaterial.HasProperty("_BaseColor")) targetMaterial.SetColor("_BaseColor", Color.green);
                else if (targetMaterial.HasProperty("_UnlitColor")) targetMaterial.SetColor("_UnlitColor", Color.green);
                else if (targetMaterial.HasProperty("_Color")) targetMaterial.SetColor("_Color", Color.green);
                else throw new InvalidOperationException("The pipeline unlit shader has no supported color property.");
                targetObject.GetComponent<MeshRenderer>().sharedMaterial = targetMaterial;
                rigObject.SetActive(true);
            }

            internal void RenderSourceControl()
            {
                QuiltRenderer sourceRenderer = rig.Renderer;
                sourceRenderer.useRenderRequests = true;
                sourceRenderer.RenderQuilt();
                string summary;
                bool visible = HasGreenTargetPixels(sourceRenderer.QuiltTexture, out summary);
                Debug.Log("EDITOR_PREVIEW_GPU source_control pipeline=" + (hdrp ? "HDRP" : "URP") + ", " + summary +
                    ", camera=" + PreviewApi.DescribeCamera("source", sourceRenderer.sourceCamera, targetObject));
                Assert.IsTrue(visible, "The source Rig control should render green target pixels. " + summary);
            }

            internal void SetOwnedPipelineNames(string pipelineName, string rendererDataName)
            {
                if (pipeline != null) pipeline.name = pipelineName;
                if (urpRendererData != null) urpRendererData.name = rendererDataName;
            }

            internal void Dispose()
            {
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                if (targetObject != null) Object.DestroyImmediate(targetObject);
                if (targetMaterial != null) Object.DestroyImmediate(targetMaterial);
                if (rigObject != null) Object.DestroyImmediate(rigObject);
                if (pipeline != null && !EditorUtility.IsPersistent(pipeline)) Object.DestroyImmediate(pipeline);
                if (urpRendererData != null && !EditorUtility.IsPersistent(urpRendererData)) Object.DestroyImmediate(urpRendererData);
            }
        }

        static class PreviewApi
        {
            static readonly Type Service = Type.GetType("ViewWeave.Editor.BridgeEditorPreview, ViewWeave.Bridge.Editor", true);
            static readonly PropertyInfo ActiveProperty = Service.GetProperty("IsActive", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo StoppingProperty = Service.GetProperty("IsStopping", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo StatusProperty = Service.GetProperty("Status", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo LastErrorProperty = Service.GetProperty("LastError", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo TextureProperty = Service.GetProperty("Texture", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo LayoutProperty = Service.GetProperty("Layout", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerRendererField = Service.GetField("producerRenderer", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ViewTextureField = typeof(QuiltRenderer).GetField("viewTexture", BindingFlags.Instance | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerOutputField = Service.GetField("producerOutput", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerCameraField = Service.GetField("producerCamera", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo StartMethod = Service.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo StopMethod = Service.GetMethod("Stop", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo RequestRefreshMethod = Service.GetMethod("RequestRefresh", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly Type RetentionRecord = Type.GetType("ViewWeave.BridgeEditorPreviewRetentionRecord, ViewWeave.Bridge", true);
            static readonly PropertyInfo ValidationPendingProperty = typeof(BridgeWorkerQuiltOutput).Assembly
                .GetType("ViewWeave.BridgeNativeImageGuard", true)
                .GetProperty("IsValidationPending", BindingFlags.Static | BindingFlags.NonPublic);

            internal static bool IsActive => (bool)ActiveProperty.GetValue(null);
            internal static bool IsStopping => (bool)StoppingProperty.GetValue(null);
            internal static string Status => (string)StatusProperty.GetValue(null) ?? string.Empty;
            internal static string LastError => (string)LastErrorProperty.GetValue(null) ?? string.Empty;
            internal static bool NativeImageValidationPending => (bool)ValidationPendingProperty.GetValue(null);
            internal static RenderTexture Texture => (RenderTexture)TextureProperty.GetValue(null);
            internal static QuiltLayout Layout => (QuiltLayout)LayoutProperty.GetValue(null);
            internal static QuiltRenderer ProducerRenderer => (QuiltRenderer)ProducerRendererField.GetValue(null);
            internal static RenderTexture ViewTexture => (RenderTexture)ViewTextureField.GetValue(ProducerRenderer);
            internal static BridgeWorkerQuiltOutput ProducerOutput => (BridgeWorkerQuiltOutput)ProducerOutputField.GetValue(null);
            internal static string DescribeCameras(Camera sourceCamera, GameObject targetObject)
            {
                Camera producer = (Camera)ProducerCameraField.GetValue(null);
                ulong targetSceneMask = targetObject == null ? 0 : EditorSceneManager.GetSceneCullingMask(targetObject.scene);
                return DescribeCamera("source", sourceCamera, targetObject) + "; " + DescribeCamera("producer", producer, targetObject) +
                    "; target.scene=" + (targetObject == null ? "null" : targetObject.scene.name) + ", target.sceneMask=" + targetSceneMask +
                    ", target.layer=" + (targetObject == null ? -1 : targetObject.layer);
            }

            internal static string DescribeCamera(string label, Camera camera, GameObject targetObject)
            {
                if (camera == null) return label + "=null";
                ulong sceneMask = EditorSceneManager.GetSceneCullingMask(camera.scene);
                return label + ".scene=" + camera.scene.name + ", sceneMask=" + sceneMask +
                    ", overrideMask=" + camera.overrideSceneCullingMask + ", cameraMask=" + camera.cullingMask +
                    ", targetVisibleByLayer=" + (targetObject != null && (camera.cullingMask & (1 << targetObject.layer)) != 0);
            }

            internal static bool Start(ViewWeaveRig rig, bool outputToDevice, out string error)
            {
                object[] arguments = { rig, outputToDevice, null };
                bool result = (bool)StartMethod.Invoke(null, arguments);
                error = arguments[2] as string;
                return result;
            }

            internal static bool Stop() => (bool)StopMethod.Invoke(null, null);

            internal static void RequestRefresh() => RequestRefreshMethod.Invoke(null, null);

            internal static string DescribeRetentionRecords()
            {
                var records = Resources.FindObjectsOfTypeAll(RetentionRecord);
                var summaries = new string[records.Length];
                for (int i = 0; i < records.Length; i++)
                {
                    object record = records.GetValue(i);
                    Type type = record.GetType();
                    summaries[i] = "stop=" + GetRecordProperty(type, record, "StopRequested") +
                        ",detached=" + GetRecordProperty(type, record, "Detached") +
                        ",canRelease=" + GetRecordProperty(type, record, "CanRelease") +
                        ",handoff=" + GetRecordProperty(type, record, "WorkerHandoffComplete") +
                        ",workerComplete=" + GetRecordProperty(type, record, "WorkerRecoveryComplete") +
                        ",gpu=" + GetRecordProperty(type, record, "GpuProofComplete") +
                        ",diagnostic=" + GetRecordProperty(type, record, "Diagnostic") +
                        ",recovery=" + DescribeRecoveryRecord(type, record);
                }
                return string.Join("|", summaries);
            }

            internal static string DescribeRetentionStateWithoutNativeCalls()
            {
                var records = Resources.FindObjectsOfTypeAll(RetentionRecord);
                var summaries = new string[records.Length];
                for (int i = 0; i < records.Length; i++)
                {
                    object record = records.GetValue(i);
                    Type type = record.GetType();
                    object recovery = GetRecordProperty(type, record, "WorkerRecoveryRecord");
                    string recoveryState = "null";
                    if (recovery != null && !(recovery is string))
                    {
                        Type recoveryType = recovery.GetType();
                        recoveryState = "nativeReleased=" + GetRecordProperty(recoveryType, recovery, "NativeReleased") +
                            ",needsClose=" + GetRecordProperty(recoveryType, recovery, "NeedsClose") +
                            ",gpuProof=" + GetRecordProperty(recoveryType, recovery, "HasGpuReleaseProof") +
                            ",canRelease=" + GetRecordProperty(recoveryType, recovery, "CanRelease") +
                            ",diagnostic=" + GetRecordProperty(recoveryType, recovery, "Diagnostic");
                    }
                    summaries[i] = "stop=" + GetRecordProperty(type, record, "StopRequested") +
                        ",detached=" + GetRecordProperty(type, record, "Detached") +
                        ",gpuProof=" + GetRecordProperty(type, record, "GpuProofComplete") +
                        ",handoff=" + GetRecordProperty(type, record, "WorkerHandoffComplete") +
                        ",workerComplete=" + GetRecordProperty(type, record, "WorkerRecoveryComplete") +
                        ",canRelease=" + GetRecordProperty(type, record, "CanRelease") +
                        ",diagnostic=" + GetRecordProperty(type, record, "Diagnostic") +
                        ",recovery={" + recoveryState + "}";
                }
                return string.Join("|", summaries);
            }

            static string DescribeRecoveryRecord(Type ownerType, object owner)
            {
                object recovery = GetRecordProperty(ownerType, owner, "WorkerRecoveryRecord");
                if (recovery == null) return "null";
                Type type = recovery.GetType();
                object handle = GetRecordProperty(type, recovery, "NativeHandle");
                string nativePoll = "not-polled";
                try
                {
                    long handleValue = Convert.ToInt64(handle);
                    int result = BridgeWorkerNative.lkg_worker_poll(new IntPtr(handleValue), out BridgeWorkerNative.Status status);
                    nativePoll = "result=" + result + ",state=" + status.state + ",errorCode=" + status.lastErrorCode +
                        ",error=" + (status.error ?? string.Empty);
                }
                catch (Exception error) { nativePoll = "exception=" + error.GetType().Name + ":" + error.Message; }
                return "generation=" + GetRecordProperty(type, recovery, "Generation") +
                    ",handle=" + handle +
                    ",nativeReleased=" + GetRecordProperty(type, recovery, "NativeReleased") +
                    ",needsClose=" + GetRecordProperty(type, recovery, "NeedsClose") +
                    ",canRelease=" + GetRecordProperty(type, recovery, "CanRelease") +
                    ",release=" + (Equals(GetRecordProperty(type, recovery, "NativeReleased"), true) ? "called" : "not-called") +
                    ",poll={" + nativePoll + "}" +
                    ",diagnostic=" + GetRecordProperty(type, recovery, "Diagnostic");
            }

            static object GetRecordProperty(Type type, object instance, string propertyName)
            {
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
                return property == null ? "missing" : property.GetValue(instance);
            }
        }
    }
}

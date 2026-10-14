#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;

namespace ViewWeave.SceneComparison.Tests
{
    public sealed class FrustumSceneMetadataProbeTests
    {
        const string ExpectedPipeline = "URP";
        const string ExpectedPreparedScene = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const string ExpectedSourceScene = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity";
        const string KnownEyesPath = "Decal Particles/TurntableWithBackdrop_VFX/VFXG Decal Particles/Hoodie/joint33/joint0/Sphere/VFXG_Decal_Eyes";
        const string KnownEyesGuid = "25b93743b12040c41b7aaa6097c9ca16";

        [Serializable]
        sealed class ProbeSummary
        {
            public string schema = "viewweave-frustum-scene-metadata-v1";
            public string status;
            public string expectedPipeline;
            public string graphicsPipeline;
            public string graphicsDevice;
            public bool noGraphics;
            public bool isPlaying;
            public bool isPlayingOrChanging;
            public string preparedScenePath;
            public string preparedSceneSha256Before;
            public string preparedSceneSha256After;
            public bool preparedSceneDirtyAfter;
            public bool preparedSceneSaveObserved;
            public string sourceScenePath;
            public string sourceSceneSha256;
            public string sourceSceneSha256After;
            public bool sourceSceneUnchanged;
            public string unityVersion;
            public string vfxGraphPackageVersion;
            public string vfxGraphPackageSource;
            public string vfxGraphPackagePath;
            public string descriptorInventoryPath;
            public string descriptorInventorySha256;
            public string preflightSnapshotPath;
            public string preflightSnapshotSha256;
            public string priorPreflightSnapshotStatus;
            public bool priorSnapshotMatchesKnownEyesUnresolvedState;
            public string[] priorSnapshotErrorCategories;
            public string priorWholeSceneReferenceGateStatus;
            public bool wholeSceneReferenceGate;
            public int effectComponentCount;
            public int inactiveEffectCount;
            public int resolvedEffectCount;
            public int uniqueResolvedGraphCount;
            public int unresolvedEffectCount;
            public bool knownEyesEntryVerified;
            public bool knownEyesInactiveUnresolved;
            public int missingScriptComponentCount;
            public int disabledCameraCount;
            public int cameraCount;
            public int camerasDisabledInMemoryByProbe;
            public CameraRecord[] cameras = Array.Empty<CameraRecord>();
            public int rigCount;
            public int autoRenderDisabledRigCount;
            public bool allCopiedSampleScriptsStopped;
            public int rootObjectCount;
            public bool noMutationGate;
            public bool sceneIsolationGate;
            public bool descriptorInventoryGate;
            public bool instanceGuidJoinGate;
            public RootRecord[] roots = Array.Empty<RootRecord>();
            public GraphRecord[] graphs = Array.Empty<GraphRecord>();
            public InstanceRecord[] instances = Array.Empty<InstanceRecord>();
            public string[] errorCategories = Array.Empty<string>();
            public string[] errors = Array.Empty<string>();
        }

        [Serializable]
        sealed class RootRecord
        {
            public string name;
            public Vector3 worldPosition;
            public Quaternion worldRotation;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 lossyScale;
        }

        [Serializable]
        sealed class CameraRecord
        {
            public string objectPath;
            public bool activeInHierarchy;
            public bool enabledAtLoad;
            public bool enabledAfterIsolation;
        }

        [Serializable]
        sealed class InstanceRecord
        {
            public string objectPath;
            public string unresolvedReferenceGuid;
            public string rootObjectName;
            public Vector3 rootWorldPosition;
            public Quaternion rootWorldRotation;
            public Vector3 worldPosition;
            public Quaternion worldRotation;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 lossyScale;
            public bool activeSelf;
            public bool activeInHierarchy;
            public bool componentEnabled;
            public string assetPath;
            public string assetGuid;
            public string assetName;
            public string assetSha256Before;
            public string assetSha256After;
            public bool sourceActiveSelf;
            public bool sourceActiveInHierarchy;
            public bool sourceComponentEnabled;
            public string sourceAssetGuid;
            public bool sourceInstanceMatched;
            public int graphCatalogIndex = -1;
            public bool descriptorGuidJoinValid;
            public string inventoryJoinStatus;
        }

        [Serializable]
        sealed class GraphRecord
        {
            public string assetPath;
            public string assetGuid;
            public string assetName;
            public string sourceSha256Before;
            public string sourceSha256After;
            public string compileStatus;
            public string compileOutputType;
            public int gpuDescriptorCount;
            public int cpuDescriptorCount;
            public int temporaryDescriptorCount;
            public int systemDescriptorCount;
            public bool descriptorFieldsComplete;
            public bool mappingIndicesValid;
            public string[] gpuDescriptors;
            public string[] cpuDescriptors;
            public string[] temporaryDescriptors;
            public string[] systemsAndMappings;
            public string authoringInventoryStatus;
            public OutputRecord[] authoringOutputs;
            public string errorType;
        }

        [Serializable]
        sealed class OutputRecord
        {
            public string type;
            public string kind;
            public string capacity;
            public string stripCapacity;
            public string blendMode;
            public string generateMotionVector;
            public string hasMotionVector;
            public string supportsMotionVector;
        }

        [Serializable]
        sealed class PriorSummary
        {
            public string status;
            public string pipeline;
            public bool noGraphicsRequested;
            public bool sceneSaved;
            public int missingScriptComponentCount;
            public int visualEffectComponentCount;
            public int activeVisualEffectComponentCount;
            public bool wholeSceneReferenceGate;
            public EffectRecord[] effects;
            public string[] errors;
        }

        [Serializable]
        sealed class EffectRecord
        {
            public string objectPath;
            public bool activeSelf;
            public bool activeInHierarchy;
            public bool componentEnabled;
            public string assetGuid;
            public string assetPath;
            public string graphInventoryStatus;
            public OutputRecord[] graphOutputs;
        }

        [Serializable]
        sealed class DescriptorSummary
        {
            public string status;
            public string expectedPipeline;
            public string graphicsPipeline;
            public string graphicsDevice;
            public bool noGraphics;
            public bool sceneSaved;
            public int effectComponentCount;
            public int unresolvedEffectAssetCount;
            public bool knownUnresolvedEyesPrefabGuidVerified;
            public bool knownUnresolvedEyesWasInactive;
            public int uniqueGraphCount;
            public bool isolationGate;
            public bool allSceneCamerasDisabled;
            public bool allSampleScriptsStopped;
            public bool wholeSceneReferenceGate;
            public GraphRecord[] graphs;
            public string[] errors;
        }

        static readonly HashSet<string> ErrorCategories = new HashSet<string>(StringComparer.Ordinal);
        static int ErrorCount;
        static string ProbePreparedScenePath;
        static bool ProbeSceneSaveObserved;

        sealed class ProbeGateException : Exception
        {
            public string Code { get; }

            public ProbeGateException(string code) : base(code)
            {
                Code = code;
            }
        }

        [Test]
        public void CapturesSanitizedSceneInstancesAndJoinsExistingGraphDescriptors()
        {
            string resultPath = Environment.GetEnvironmentVariable("VW_FRUSTUM_RESULT") ?? "";
            string preparedScenePath = Environment.GetEnvironmentVariable("VW_FRUSTUM_PREPARED_SCENE") ?? "";
            string sourceScenePath = Environment.GetEnvironmentVariable("VW_FRUSTUM_SOURCE_SCENE") ?? "";
            string priorPath = Environment.GetEnvironmentVariable("VW_FRUSTUM_PREFLIGHT_JSON") ?? "";
            string descriptorPath = Environment.GetEnvironmentVariable("VW_FRUSTUM_DESCRIPTOR_JSON") ?? "";
            var summary = new ProbeSummary
            {
                status = "started",
                expectedPipeline = ExpectedPipeline,
                preparedScenePath = preparedScenePath,
                sourceScenePath = sourceScenePath,
                unityVersion = Application.unityVersion,
                preflightSnapshotPath = priorPath,
                descriptorInventoryPath = descriptorPath,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                noGraphics = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null,
                isPlaying = Application.isPlaying,
                isPlayingOrChanging = EditorApplication.isPlayingOrWillChangePlaymode,
                errorCategories = Array.Empty<string>(),
                errors = Array.Empty<string>()
            };
            var errors = new List<string>();
            var beforeAssetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var beforeAssetPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var instances = new List<InstanceRecord>();
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string preparedFullPath = "";
            string sourceFullPath = "";
            Scene scene = default;
            ErrorCategories.Clear();
            ErrorCount = 0;
            ProbePreparedScenePath = preparedScenePath;
            ProbeSceneSaveObserved = false;
            Application.logMessageReceived += CaptureEditorErrorCategory;
            EditorSceneManager.sceneSaved += CapturePreparedSceneSave;
            try
            {
                Require(!string.IsNullOrWhiteSpace(resultPath), "result-path-missing");
                Require(IsWithinFrustumRoot(resultPath), "result-path-outside-local-root");
                Require(preparedScenePath == ExpectedPreparedScene, "prepared-scene-path-mismatch");
                Require(sourceScenePath == ExpectedSourceScene, "source-scene-path-mismatch");
                Require(!string.IsNullOrWhiteSpace(priorPath) && File.Exists(priorPath), "preflight-snapshot-missing");
                Require(!string.IsNullOrWhiteSpace(descriptorPath) && File.Exists(descriptorPath), "descriptor-inventory-missing");
                Require(summary.noGraphics, "graphics-device-not-null");
                Require(!summary.isPlaying && !summary.isPlayingOrChanging, "editor-play-mode-active");
                summary.graphicsPipeline = GraphicsSettings.currentRenderPipeline == null ? "builtin" : GraphicsSettings.currentRenderPipeline.GetType().FullName;
                Require(summary.graphicsPipeline.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0, "effective-urp-pipeline-mismatch");

                preparedFullPath = FullProjectPath(projectRoot, preparedScenePath);
                sourceFullPath = FullProjectPath(projectRoot, sourceScenePath);
                summary.preparedSceneSha256Before = Sha256(preparedFullPath);
                summary.sourceSceneSha256 = Sha256(sourceFullPath);
                summary.preflightSnapshotSha256 = Sha256(priorPath);
                summary.descriptorInventorySha256 = Sha256(descriptorPath);

                string priorJson = File.ReadAllText(priorPath);
                PriorSummary prior = JsonUtility.FromJson<PriorSummary>(priorJson);
                Require(prior != null, "preflight-snapshot-json-invalid");
                bool priorWholeSceneGateWasRecorded = priorJson.IndexOf("\"wholeSceneReferenceGate\"", StringComparison.Ordinal) >= 0;
                summary.priorPreflightSnapshotStatus = prior.status;
                summary.priorSnapshotErrorCategories = prior.errors ?? Array.Empty<string>();
                EffectRecord priorEyes = prior.effects == null ? null : prior.effects.SingleOrDefault(record => record.objectPath == KnownEyesPath);
                string[] expectedSnapshotErrors = { "effect-asset-unresolved", "exception:InvalidOperationException" };
                bool expectedErrorSet = summary.priorSnapshotErrorCategories.Length == expectedSnapshotErrors.Length &&
                    expectedSnapshotErrors.All(expectedError => summary.priorSnapshotErrorCategories.Contains(expectedError, StringComparer.Ordinal));
                summary.priorSnapshotMatchesKnownEyesUnresolvedState = prior.status == "failed" && expectedErrorSet &&
                    prior.pipeline.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0 && prior.noGraphicsRequested && !prior.sceneSaved &&
                    prior.missingScriptComponentCount == 0 && prior.visualEffectComponentCount == 33 && prior.activeVisualEffectComponentCount == 32 &&
                    prior.effects != null && prior.effects.Length == 33 && priorEyes != null && !priorEyes.activeInHierarchy &&
                    priorEyes.componentEnabled && string.IsNullOrWhiteSpace(priorEyes.assetGuid) && priorEyes.graphInventoryStatus == "no-asset";
                summary.priorWholeSceneReferenceGateStatus = priorWholeSceneGateWasRecorded ? (prior.wholeSceneReferenceGate ? "passed" : "not-passed") : "not-recorded";
                Require(summary.priorSnapshotMatchesKnownEyesUnresolvedState, "source-preflight-snapshot-known-eyes-state-mismatch");

                DescriptorSummary descriptor = JsonUtility.FromJson<DescriptorSummary>(File.ReadAllText(descriptorPath));
                Require(descriptor != null, "descriptor-inventory-json-invalid");
                summary.descriptorInventoryGate = descriptor.status == "success" && descriptor.expectedPipeline == ExpectedPipeline &&
                    descriptor.graphicsPipeline.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0 && descriptor.graphicsDevice == "Null" && descriptor.noGraphics &&
                    !descriptor.sceneSaved && descriptor.effectComponentCount == 33 && descriptor.unresolvedEffectAssetCount == 1 && descriptor.uniqueGraphCount == 32 &&
                    descriptor.isolationGate && descriptor.allSceneCamerasDisabled && descriptor.allSampleScriptsStopped && !descriptor.wholeSceneReferenceGate &&
                    descriptor.knownUnresolvedEyesPrefabGuidVerified && descriptor.knownUnresolvedEyesWasInactive && descriptor.graphs != null && descriptor.graphs.Length == 32 &&
                    (descriptor.errors == null || descriptor.errors.Length == 0) &&
                    descriptor.graphs.All(graph => graph.compileStatus == "success" && graph.descriptorFieldsComplete && graph.mappingIndicesValid);
                Require(summary.descriptorInventoryGate, "descriptor-inventory-gate-failed");
                summary.graphicsPipeline = GraphicsSettings.currentRenderPipeline.GetType().FullName;
                PackageInfoRecord vfxPackage = ReadVfxPackage();
                summary.vfxGraphPackageVersion = vfxPackage.version;
                summary.vfxGraphPackageSource = vfxPackage.source;
                summary.vfxGraphPackagePath = vfxPackage.resolvedPath;
                Require(!string.IsNullOrWhiteSpace(summary.vfxGraphPackageVersion) && !string.IsNullOrWhiteSpace(summary.vfxGraphPackagePath), "vfx-package-provenance-unavailable");

                scene = EditorSceneManager.OpenScene(preparedScenePath, OpenSceneMode.Single);
                Require(scene.IsValid() && scene.isLoaded && !scene.isDirty, "prepared-scene-load-or-dirty-gate-failed");
                GameObject[] objects = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .Select(transform => transform.gameObject)
                    .Distinct()
                    .ToArray();
                summary.rootObjectCount = scene.rootCount;
                summary.roots = scene.GetRootGameObjects().Select(root => new RootRecord
                {
                    name = root.name,
                    worldPosition = root.transform.position,
                    worldRotation = root.transform.rotation,
                    localPosition = root.transform.localPosition,
                    localRotation = root.transform.localRotation,
                    lossyScale = root.transform.lossyScale
                }).ToArray();

                Camera[] cameras = objects.SelectMany(gameObject => gameObject.GetComponents<Camera>()).Where(camera => camera != null).ToArray();
                summary.cameraCount = cameras.Length;
                summary.cameras = cameras.Select(camera => new CameraRecord
                {
                    objectPath = HierarchyPath(camera.transform),
                    activeInHierarchy = camera.gameObject.activeInHierarchy,
                    enabledAtLoad = camera.enabled,
                    enabledAfterIsolation = false
                }).ToArray();
                summary.camerasDisabledInMemoryByProbe = cameras.Count(camera => camera.enabled);
                foreach (Camera camera in cameras)
                    if (camera.enabled) camera.enabled = false;
                for (int i = 0; i < cameras.Length; i++) summary.cameras[i].enabledAfterIsolation = cameras[i].enabled;
                summary.disabledCameraCount = cameras.Count(camera => !camera.enabled);

                EffectRecord[] priorEffects = prior.effects ?? Array.Empty<EffectRecord>();
                var duplicateSourcePaths = priorEffects.GroupBy(record => record.objectPath, StringComparer.Ordinal).Any(group => group.Count() != 1);
                Require(!duplicateSourcePaths && priorEffects.Length == 33, "source-preflight-instance-paths-ambiguous");
                var sourceByPath = priorEffects.ToDictionary(record => record.objectPath, StringComparer.Ordinal);
                var graphsByGuid = descriptor.graphs.ToDictionary(record => record.assetGuid, StringComparer.OrdinalIgnoreCase);
                var graphList = descriptor.graphs.OrderBy(record => record.assetGuid, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (GraphRecord graph in graphList) graph.assetName = Path.GetFileNameWithoutExtension(graph.assetPath);
                var sourceOutputsByGuid = priorEffects.Where(record => !string.IsNullOrWhiteSpace(record.assetGuid))
                    .GroupBy(record => record.assetGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (GraphRecord graph in graphList)
                {
                    if (sourceOutputsByGuid.TryGetValue(graph.assetGuid, out EffectRecord sourceGraph))
                    {
                        graph.authoringInventoryStatus = sourceGraph.graphInventoryStatus;
                        graph.authoringOutputs = sourceGraph.graphOutputs ?? Array.Empty<OutputRecord>();
                    }
                    else
                    {
                        graph.authoringInventoryStatus = "source-preflight-graph-missing";
                        graph.authoringOutputs = Array.Empty<OutputRecord>();
                    }
                }
                bool authoringCapacityCatalogValid = graphList.Count == 32 && graphList.All(graph => graph.authoringInventoryStatus == "success" &&
                    graph.authoringOutputs != null && graph.authoringOutputs.Any(output => output.kind == "particle-render-output" &&
                        int.TryParse(output.capacity, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)));
                Require(authoringCapacityCatalogValid, "authoring-output-capacity-catalog-incomplete");
                var graphIndex = graphList.Select((record, index) => new { record.assetGuid, index }).ToDictionary(item => item.assetGuid, item => item.index, StringComparer.OrdinalIgnoreCase);
                VisualEffect[] effects = objects.SelectMany(gameObject => gameObject.GetComponents<VisualEffect>()).Where(effect => effect != null).ToArray();
                summary.effectComponentCount = effects.Length;
                summary.inactiveEffectCount = effects.Count(effect => !effect.gameObject.activeInHierarchy);
                summary.resolvedEffectCount = effects.Count(effect => effect.visualEffectAsset != null);
                summary.unresolvedEffectCount = effects.Count(effect => effect.visualEffectAsset == null);
                foreach (VisualEffect effect in effects)
                {
                    string objectPath = HierarchyPath(effect.transform);
                    string assetPath = effect.visualEffectAsset == null ? "" : AssetDatabase.GetAssetPath(effect.visualEffectAsset).Replace('\\', '/');
                    string guid = string.IsNullOrWhiteSpace(assetPath) ? "" : AssetDatabase.AssetPathToGUID(assetPath);
                    string assetFullPath = string.IsNullOrWhiteSpace(assetPath) ? "" : FullProjectPath(projectRoot, assetPath);
                    string assetHash = string.IsNullOrWhiteSpace(assetFullPath) || !File.Exists(assetFullPath) ? "" : Sha256(assetFullPath);
                    if (!string.IsNullOrWhiteSpace(guid) && !beforeAssetHashes.ContainsKey(guid))
                    {
                        beforeAssetHashes.Add(guid, assetHash);
                        beforeAssetPaths.Add(guid, assetFullPath);
                    }
                    EffectRecord source = sourceByPath.TryGetValue(objectPath, out EffectRecord matched) ? matched : null;
                    bool isKnownEyes = objectPath == KnownEyesPath && effect.visualEffectAsset == null;
                    if (isKnownEyes) summary.knownEyesInactiveUnresolved = !effect.gameObject.activeInHierarchy;
                    bool sourceInstanceMatched = source != null && string.Equals(assetPath, source.assetPath ?? "", StringComparison.Ordinal) &&
                        (string.IsNullOrWhiteSpace(guid) ? string.IsNullOrWhiteSpace(source.assetGuid) : string.Equals(guid, source.assetGuid, StringComparison.OrdinalIgnoreCase));
                    bool joined = !string.IsNullOrWhiteSpace(guid) && graphsByGuid.TryGetValue(guid, out GraphRecord graph) &&
                        graph.assetPath.Replace('\\', '/') == assetPath && graph.sourceSha256Before == assetHash && graph.sourceSha256After == assetHash &&
                        graph.compileStatus == "success" && graph.descriptorFieldsComplete && graph.mappingIndicesValid &&
                        graph.authoringInventoryStatus == "success" && graph.authoringOutputs.Any(output => output.kind == "particle-render-output" &&
                            int.TryParse(output.capacity, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
                    instances.Add(new InstanceRecord
                    {
                        objectPath = objectPath,
                        unresolvedReferenceGuid = isKnownEyes ? KnownEyesGuid : "",
                        rootObjectName = effect.transform.root.gameObject.name,
                        rootWorldPosition = effect.transform.root.position,
                        rootWorldRotation = effect.transform.root.rotation,
                        worldPosition = effect.transform.position,
                        worldRotation = effect.transform.rotation,
                        localPosition = effect.transform.localPosition,
                        localRotation = effect.transform.localRotation,
                        lossyScale = effect.transform.lossyScale,
                        activeSelf = effect.gameObject.activeSelf,
                        activeInHierarchy = effect.gameObject.activeInHierarchy,
                        componentEnabled = effect.enabled,
                        assetPath = assetPath,
                        assetGuid = guid,
                        assetName = effect.visualEffectAsset == null ? "" : effect.visualEffectAsset.name,
                        assetSha256Before = assetHash,
                        assetSha256After = assetHash,
                        sourceActiveSelf = source != null && source.activeSelf,
                        sourceActiveInHierarchy = source != null && source.activeInHierarchy,
                        sourceComponentEnabled = source != null && source.componentEnabled,
                        sourceAssetGuid = source == null ? "" : source.assetGuid,
                        sourceInstanceMatched = sourceInstanceMatched,
                        graphCatalogIndex = graphIndex.TryGetValue(guid, out int index) ? index : -1,
                        descriptorGuidJoinValid = joined,
                        inventoryJoinStatus = isKnownEyes ? "known-inactive-unresolved" : joined ? "matched-current-asset-sha" : string.IsNullOrWhiteSpace(guid) ? "unresolved" : "descriptor-or-hash-mismatch"
                    });
                }
                summary.uniqueResolvedGraphCount = instances.Where(instance => !string.IsNullOrWhiteSpace(instance.assetGuid)).Select(instance => instance.assetGuid).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                summary.knownEyesEntryVerified = instances.Count(instance => instance.objectPath == KnownEyesPath && instance.inventoryJoinStatus == "known-inactive-unresolved") == 1 &&
                    File.ReadAllText(FullProjectPath(projectRoot, "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Decal Particles.prefab"))
                        .IndexOf(KnownEyesGuid, StringComparison.OrdinalIgnoreCase) >= 0;
                summary.graphs = graphList.ToArray();
                summary.instances = instances.ToArray();
                summary.wholeSceneReferenceGate = instances.All(instance => !string.IsNullOrWhiteSpace(instance.assetGuid));
                summary.instanceGuidJoinGate = summary.uniqueResolvedGraphCount == 32 && graphList.Count == 32 && instances.Count == 33 && instances.Count(instance => !string.IsNullOrWhiteSpace(instance.assetGuid)) == 32 &&
                    instances.Count(instance => string.IsNullOrWhiteSpace(instance.assetGuid)) == 1 && instances.Where(instance => !string.IsNullOrWhiteSpace(instance.assetGuid)).All(instance => instance.descriptorGuidJoinValid) &&
                    instances.All(instance => instance.sourceInstanceMatched) && instances.Count(instance => instance.objectPath == KnownEyesPath && instance.inventoryJoinStatus == "known-inactive-unresolved") == 1;

                summary.rigCount = objects.SelectMany(gameObject => gameObject.GetComponents<ViewWeave.ViewWeaveRig>()).Count(rig => rig != null);
                summary.autoRenderDisabledRigCount = objects.SelectMany(gameObject => gameObject.GetComponents<ViewWeave.ViewWeaveRig>()).Count(rig => rig != null && !rig.AutoRender && rig.GetComponent<ViewWeave.QuiltRenderer>() != null && !rig.GetComponent<ViewWeave.QuiltRenderer>().autoRender);
                summary.allCopiedSampleScriptsStopped = objects.SelectMany(gameObject => gameObject.GetComponents<MonoBehaviour>()).Where(behaviour => behaviour != null)
                    .All(behaviour => !IsCopiedSampleScript(behaviour) || !behaviour.enabled);
                int missingScripts = objects.Sum(gameObject => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject));
                summary.missingScriptComponentCount = missingScripts;
                summary.sceneIsolationGate = summary.effectComponentCount == 33 && summary.inactiveEffectCount == 33 && summary.resolvedEffectCount == 32 &&
                    summary.unresolvedEffectCount == 1 && summary.knownEyesEntryVerified && summary.knownEyesInactiveUnresolved && missingScripts == 0 &&
                    summary.cameraCount > 0 && summary.disabledCameraCount == summary.cameraCount && summary.rigCount > 0 && summary.autoRenderDisabledRigCount == summary.rigCount &&
                    summary.allCopiedSampleScriptsStopped;
                Require(summary.sceneIsolationGate, "prepared-scene-isolation-gate-failed");
                Require(summary.instanceGuidJoinGate, "instance-descriptor-join-gate-failed");
                Require(!summary.wholeSceneReferenceGate, "whole-scene-reference-gate-unexpectedly-passed");

                Require(ErrorCount == 0, "editor-errors-during-probe");
                summary.status = "success";
            }
            catch (Exception exception)
            {
                summary.status = "failed";
                ProbeGateException gateException = exception as ProbeGateException;
                errors.Add(gateException == null
                    ? "exception:" + exception.GetType().Name
                    : "gate:" + gateException.Code);
            }
            finally
            {
                EditorSceneManager.sceneSaved -= CapturePreparedSceneSave;
                Application.logMessageReceived -= CaptureEditorErrorCategory;
                summary.preparedSceneDirtyAfter = scene.IsValid() && scene.isDirty;
                summary.preparedSceneSaveObserved = ProbeSceneSaveObserved;
                bool assetSourcesUnchanged = beforeAssetHashes.Count > 0;
                foreach (KeyValuePair<string, string> item in beforeAssetHashes)
                {
                    string after = "";
                    try
                    {
                        string path = beforeAssetPaths[item.Key];
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) after = Sha256(path);
                    }
                    catch { errors.Add("graph-source-after-hash-unavailable"); }
                    if (!string.Equals(after, item.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        assetSourcesUnchanged = false;
                        errors.Add("graph-source-changed");
                    }
                    foreach (InstanceRecord instance in instances.Where(value => string.Equals(value.assetGuid, item.Key, StringComparison.OrdinalIgnoreCase)))
                        instance.assetSha256After = after;
                }
                try
                {
                    if (string.IsNullOrWhiteSpace(preparedFullPath) && preparedScenePath == ExpectedPreparedScene)
                        preparedFullPath = FullProjectPath(projectRoot, preparedScenePath);
                    if (string.IsNullOrWhiteSpace(sourceFullPath) && sourceScenePath == ExpectedSourceScene)
                        sourceFullPath = FullProjectPath(projectRoot, sourceScenePath);
                    if (!string.IsNullOrWhiteSpace(preparedFullPath) && File.Exists(preparedFullPath)) summary.preparedSceneSha256After = Sha256(preparedFullPath);
                    if (!string.IsNullOrWhiteSpace(sourceFullPath) && File.Exists(sourceFullPath)) summary.sourceSceneSha256After = Sha256(sourceFullPath);
                }
                catch { errors.Add("scene-after-hash-unavailable"); }
                summary.sourceSceneUnchanged = !string.IsNullOrWhiteSpace(summary.sourceSceneSha256) &&
                    string.Equals(summary.sourceSceneSha256, summary.sourceSceneSha256After, StringComparison.OrdinalIgnoreCase);
                summary.noMutationGate = !summary.preparedSceneSaveObserved && assetSourcesUnchanged &&
                    !string.IsNullOrWhiteSpace(summary.preparedSceneSha256Before) &&
                    string.Equals(summary.preparedSceneSha256Before, summary.preparedSceneSha256After, StringComparison.OrdinalIgnoreCase) && summary.sourceSceneUnchanged;
                if (!summary.noMutationGate)
                {
                    errors.Add("on-disk-no-mutation-gate-failed");
                    summary.status = "failed";
                }
                summary.errorCategories = ErrorCategories.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (ErrorCount > 0) errors.Add("editor-error-count:" + ErrorCount.ToString(CultureInfo.InvariantCulture));
                summary.errors = errors.ToArray();
                if (!string.IsNullOrWhiteSpace(resultPath) && IsWithinFrustumRoot(resultPath))
                {
                    string parent = Path.GetDirectoryName(resultPath);
                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                    File.WriteAllText(resultPath, JsonUtility.ToJson(summary, true), new UTF8Encoding(false));
                }
            }
            Assert.That(summary.status, Is.EqualTo("success"), string.Join(";", summary.errors));
        }

        [Serializable]
        sealed class PackageInfoRecord { public string version; public string source; public string resolvedPath; }

        static PackageInfoRecord ReadVfxPackage()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.unity.visualeffectgraph");
            if (package == null) return new PackageInfoRecord();
            return new PackageInfoRecord { version = package.version, source = package.source.ToString(), resolvedPath = package.resolvedPath };
        }

        static void Require(bool condition, string code)
        {
            if (!condition) throw new ProbeGateException(code);
        }

        static void CapturePreparedSceneSave(Scene savedScene)
        {
            if (!savedScene.IsValid()) return;
            string savedPath = (savedScene.path ?? "").Replace('\\', '/');
            string preparedPath = (ProbePreparedScenePath ?? "").Replace('\\', '/');
            if (string.Equals(savedPath, preparedPath, StringComparison.Ordinal)) ProbeSceneSaveObserved = true;
        }

        static bool IsWithinFrustumRoot(string path)
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../../.."));
            string root = Path.GetFullPath(Path.Combine(repositoryRoot, ".local/windows-urp-vfx-frustum-memory-20261013"));
            string full = Path.GetFullPath(path);
            return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static string FullProjectPath(string projectRoot, string assetPath)
            => Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));

        static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        static string HierarchyPath(Transform transform)
        {
            var pieces = new Stack<string>();
            for (Transform current = transform; current != null; current = current.parent) pieces.Push(current.name);
            return string.Join("/", pieces.ToArray());
        }

        static bool IsCopiedSampleScript(MonoBehaviour behaviour)
        {
            if (behaviour is ViewWeave.ViewWeaveRig || behaviour is ViewWeave.QuiltRenderer) return false;
            MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
            if (script == null) return false;
            string path = AssetDatabase.GetAssetPath(script).Replace('\\', '/');
            return path.StartsWith("Assets/Samples/", StringComparison.Ordinal);
        }

        static void CaptureEditorErrorCategory(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            ErrorCount++;
            string text = condition ?? "";
            ErrorCategories.Add(text.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0 ? "script" :
                text.IndexOf("shader", StringComparison.OrdinalIgnoreCase) >= 0 ? "shader" :
                text.IndexOf("asset", StringComparison.OrdinalIgnoreCase) >= 0 ? "asset" : "other");
        }
    }
}
#endif

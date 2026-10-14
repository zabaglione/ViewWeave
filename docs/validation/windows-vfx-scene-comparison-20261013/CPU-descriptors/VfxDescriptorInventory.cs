#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;
using ViewWeave;

namespace ViewWeave.SceneComparison
{
    public static class VfxDescriptorInventory
    {
        const string SampleRoot = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/";
        const string KnownUnresolvedEyesPath = "Decal Particles/TurntableWithBackdrop_VFX/VFXG Decal Particles/Hoodie/joint33/joint0/Sphere/VFXG_Decal_Eyes";
        const string KnownUnresolvedEyesPrefab = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Decal Particles.prefab";
        const string KnownUnresolvedEyesGuid = "25b93743b12040c41b7aaa6097c9ca16";

        [Serializable]
        sealed class Summary
        {
            public string status;
            public string expectedPipeline;
            public string graphicsPipeline;
            public string vfxBinderType;
            public string graphicsDevice;
            public bool noGraphics;
            public string scenePath;
            public string priorPreflightSnapshotPath;
            public string priorPreflightSnapshotSha256;
            public bool priorPreflightEyesEntryFound;
            public bool priorPreflightEyesWasInactive;
            public bool sceneSaved;
            public int effectComponentCount;
            public int unresolvedEffectAssetCount;
            public string unresolvedEffectPaths;
            public bool knownUnresolvedEyesPrefabGuidVerified;
            public bool knownUnresolvedEyesWasInactive;
            public int missingScriptComponentCount;
            public int disabledSampleScriptCount;
            public string disabledSampleScriptPaths;
            public bool allSampleScriptsStopped;
            public int uniqueGraphCount;
            public int activeEffectCountAfterIsolation;
            public int rigCount;
            public int cameraCount;
            public bool allSceneCamerasDisabled;
            public bool isolationGate;
            public bool wholeSceneReferenceGate;
            public GraphRecord[] graphs;
            public string[] errors;
        }

        [Serializable]
        sealed class GraphRecord
        {
            public string assetPath;
            public string assetGuid;
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
            public string errorType;
        }

        static readonly List<string> Errors = new List<string>();

        public static void Run()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string expectedPipeline = Environment.GetEnvironmentVariable("VW_DESCRIPTOR_EXPECTED_PIPELINE") ?? "";
            string scenePath = Environment.GetEnvironmentVariable("VW_DESCRIPTOR_SCENE") ?? "";
            string priorPreflightPath = Environment.GetEnvironmentVariable("VW_DESCRIPTOR_PREFLIGHT_SNAPSHOT") ?? "";
            string resultPath = Environment.GetEnvironmentVariable("VW_DESCRIPTOR_RESULT") ?? "";
            var summary = new Summary
            {
                status = "started",
                expectedPipeline = expectedPipeline,
                scenePath = scenePath,
                priorPreflightSnapshotPath = priorPreflightPath,
                noGraphics = Environment.GetEnvironmentVariable("VW_DESCRIPTOR_NOGRAPHICS") == "1",
                sceneSaved = false,
                wholeSceneReferenceGate = false,
                graphs = Array.Empty<GraphRecord>(),
                errors = Array.Empty<string>()
            };

            try
            {
                if (string.IsNullOrWhiteSpace(resultPath)) throw new InvalidOperationException("result-path-missing");
                if (!summary.noGraphics) throw new InvalidOperationException("nographics-flag-missing");
                if (expectedPipeline != "URP" && expectedPipeline != "HDRP") throw new InvalidOperationException("expected-pipeline-invalid");
                if (string.IsNullOrWhiteSpace(scenePath) || !scenePath.StartsWith(SampleRoot + "Scenes/", StringComparison.Ordinal)) throw new InvalidOperationException("scene-path-outside-sample-root");
                VerifyPriorPreflightSnapshot(projectRoot, scenePath, priorPreflightPath, summary);

                summary.graphicsPipeline = DescribePipeline(GraphicsSettings.currentRenderPipeline);
                summary.graphicsDevice = SystemInfo.graphicsDeviceType.ToString();
                if (summary.graphicsDevice != GraphicsDeviceType.Null.ToString()) throw new InvalidOperationException("graphics-device-not-null");
                string pipelineToken = expectedPipeline == "URP" ? "UniversalRenderPipelineAsset" : "HDRenderPipelineAsset";
                if (summary.graphicsPipeline.IndexOf(pipelineToken, StringComparison.Ordinal) < 0) throw new InvalidOperationException("graphics-pipeline-mismatch");
                summary.vfxBinderType = CurrentBinderType();
                if (summary.vfxBinderType.IndexOf(expectedPipeline, StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("vfx-binder-mismatch");

                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("scene-load-failed");
                GameObject[] sceneObjects = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .Select(transform => transform.gameObject)
                    .Distinct()
                    .ToArray();
                var effects = sceneObjects.SelectMany(gameObject => gameObject.GetComponents<VisualEffect>()).Where(effect => effect != null).ToArray();
                summary.effectComponentCount = effects.Length;
                VisualEffect[] unresolvedEffects = effects.Where(effect => effect.visualEffectAsset == null).ToArray();
                summary.unresolvedEffectAssetCount = unresolvedEffects.Length;
                summary.unresolvedEffectPaths = string.Join(";", unresolvedEffects.Select(effect => HierarchyPath(effect.transform)).ToArray());
                string eyesPrefabFullPath = Path.GetFullPath(Path.Combine(projectRoot, KnownUnresolvedEyesPrefab));
                summary.knownUnresolvedEyesPrefabGuidVerified = File.Exists(eyesPrefabFullPath) && File.ReadAllText(eyesPrefabFullPath).IndexOf(KnownUnresolvedEyesGuid, StringComparison.OrdinalIgnoreCase) >= 0;
                summary.knownUnresolvedEyesWasInactive = unresolvedEffects.Length == 1 && HierarchyPath(unresolvedEffects[0].transform) == KnownUnresolvedEyesPath && !unresolvedEffects[0].gameObject.activeInHierarchy;

                var disabledSampleScripts = new List<string>();
                foreach (MonoBehaviour behaviour in sceneObjects.SelectMany(gameObject => gameObject.GetComponents<MonoBehaviour>()).Where(value => value != null))
                {
                    string assetPath = ScriptAssetPath(behaviour);
                    bool copiedSampleScript = IsCopiedSampleScript(assetPath);
                    if (!copiedSampleScript || behaviour is ViewWeaveRig || behaviour is QuiltRenderer) continue;
                    if (behaviour.enabled)
                    {
                        behaviour.enabled = false;
                        disabledSampleScripts.Add(assetPath + "|" + behaviour.GetType().FullName);
                    }
                }
                summary.disabledSampleScriptCount = disabledSampleScripts.Count;
                summary.disabledSampleScriptPaths = string.Join(";", disabledSampleScripts.ToArray());
                summary.allSampleScriptsStopped = sceneObjects.SelectMany(gameObject => gameObject.GetComponents<MonoBehaviour>()).Where(behaviour => behaviour != null)
                    .All(behaviour => !IsCopiedSampleScript(ScriptAssetPath(behaviour)) || behaviour is ViewWeaveRig || behaviour is QuiltRenderer || !behaviour.enabled);

                summary.missingScriptComponentCount = sceneObjects.Sum(gameObject => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject));
                if (summary.missingScriptComponentCount != 0) throw new InvalidOperationException("unexpected-missing-script-components");

                foreach (VisualEffect effect in effects)
                {
                    effect.enabled = false;
                    effect.gameObject.SetActive(false);
                }
                ViewWeaveRig[] rigs = sceneObjects.SelectMany(gameObject => gameObject.GetComponents<ViewWeaveRig>()).Where(value => value != null).ToArray();
                summary.rigCount = rigs.Length;
                bool rigIsolationValid = true;
                foreach (ViewWeaveRig rig in rigs)
                {
                    QuiltRenderer renderer = rig.GetComponent<QuiltRenderer>();
                    bool retentionClear = renderer == null;
                    if (renderer != null)
                    {
                        renderer.autoRender = false;
                        var serializedRenderer = new SerializedObject(renderer);
                        SerializedProperty owner = serializedRenderer.FindProperty("editorPreviewRetentionOwner");
                        SerializedProperty required = serializedRenderer.FindProperty("editorPreviewRetentionRequired");
                        if (owner != null) owner.objectReferenceValue = null;
                        if (required != null) required.boolValue = false;
                        serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
                        retentionClear = (owner == null || owner.objectReferenceValue == null) && (required == null || !required.boolValue);
                    }
                    rig.AutoRender = false;
                    rig.SetOutput(null);
                    rig.SetGuard(null);
                    Camera sourceCamera = renderer == null ? rig.GetComponent<Camera>() : renderer.sourceCamera;
                    if (sourceCamera != null) sourceCamera.enabled = false;
                    renderer = rig.GetComponent<QuiltRenderer>();
                    rigIsolationValid &= !rig.AutoRender && renderer != null && !renderer.autoRender && renderer.OutputBehaviour == null && renderer.GuardBehaviour == null &&
                        sourceCamera != null && !sourceCamera.enabled && retentionClear;
                }
                Camera[] sceneCameras = sceneObjects.SelectMany(gameObject => gameObject.GetComponents<Camera>()).Where(camera => camera != null).ToArray();
                summary.cameraCount = sceneCameras.Length;
                foreach (Camera camera in sceneCameras) camera.enabled = false;
                summary.allSceneCamerasDisabled = sceneCameras.Length > 0 && sceneCameras.All(camera => !camera.enabled);
                summary.activeEffectCountAfterIsolation = effects.Count(effect => effect.enabled || effect.gameObject.activeInHierarchy);
                summary.isolationGate = summary.activeEffectCountAfterIsolation == 0 && rigs.Length > 0 && rigIsolationValid && summary.allSampleScriptsStopped && summary.allSceneCamerasDisabled;
                if (!summary.isolationGate) throw new InvalidOperationException("scene-isolation-gate-failed");

                var assets = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (VisualEffect effect in effects)
                {
                    if (effect.visualEffectAsset == null) continue;
                    string path = AssetDatabase.GetAssetPath(effect.visualEffectAsset).Replace('\\', '/');
                    if (!path.StartsWith(SampleRoot, StringComparison.Ordinal)) throw new InvalidOperationException("effect-asset-outside-sample-root");
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException("effect-asset-guid-unavailable");
                    if (!assets.ContainsKey(guid)) assets.Add(guid, path);
                }
                summary.uniqueGraphCount = assets.Count;
                var records = new List<GraphRecord>(assets.Count);
                foreach (KeyValuePair<string, string> asset in assets)
                {
                    GraphRecord record = CompileAndDescribe(asset.Value, asset.Key, projectRoot);
                    records.Add(record);
                }
                summary.graphs = records.ToArray();
                bool graphPass = records.Count == 32 && records.All(record => record.compileStatus == "success" && record.descriptorFieldsComplete && record.mappingIndicesValid &&
                    record.sourceSha256Before.Length == 64 && record.sourceSha256Before == record.sourceSha256After);
                if (effects.Length != 33 || summary.unresolvedEffectAssetCount != 1 || !summary.knownUnresolvedEyesPrefabGuidVerified || !summary.knownUnresolvedEyesWasInactive ||
                    !summary.priorPreflightEyesEntryFound || !summary.priorPreflightEyesWasInactive)
                    Errors.Add("scene-effect-count-differs-from-known-32-assets-plus-one-inactive-unresolved-eyes-component");
                if (!graphPass) Errors.Add("one-or-more-graph-descriptor-records-incomplete");
                summary.status = graphPass && Errors.Count == 0 ? "success" : "failed";
            }
            catch (Exception exception)
            {
                summary.status = "failed";
                Errors.Add("exception:" + exception.GetType().Name);
            }
            finally
            {
                summary.errors = Errors.ToArray();
                if (!string.IsNullOrWhiteSpace(resultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
                    File.WriteAllText(resultPath, JsonUtility.ToJson(summary, true), new UTF8Encoding(false));
                }
                EditorApplication.Exit(summary.status == "success" ? 0 : 1);
            }
        }

        static GraphRecord CompileAndDescribe(string assetPath, string guid, string projectRoot)
        {
            var record = new GraphRecord
            {
                assetPath = assetPath,
                assetGuid = guid,
                compileStatus = "not-started",
                gpuDescriptors = Array.Empty<string>(),
                cpuDescriptors = Array.Empty<string>(),
                temporaryDescriptors = Array.Empty<string>(),
                systemsAndMappings = Array.Empty<string>(),
                descriptorFieldsComplete = false,
                mappingIndicesValid = false
            };
            string fullPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            record.sourceSha256Before = Sha256(fullPath);
            try
            {
                Type resourceType = FindType("UnityEditor.VFX.VisualEffectResource");
                Type extensionType = FindType("UnityEditor.VFX.VisualEffectResourceExtensions");
                if (resourceType == null || extensionType == null) throw new MissingMemberException("vfx-resource-api");
                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo load = resourceType.GetMethods(staticFlags).FirstOrDefault(method => method.Name == "GetResourceAtPath" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(string));
                MethodInfo getGraph = extensionType.GetMethods(staticFlags).FirstOrDefault(method => method.Name == "GetGraph" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsAssignableFrom(resourceType));
                if (load == null || getGraph == null) throw new MissingMethodException("vfx-resource-load-or-graph");
                object resource = load.Invoke(null, new object[] { assetPath });
                object graph = getGraph.Invoke(null, new[] { resource });
                if (graph == null) throw new InvalidOperationException("graph-null");
                MethodInfo compile = graph.GetType().GetMethod("Compile", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (compile == null) throw new MissingMethodException("vfx-graph-compile");
                object compiled = compile.Invoke(graph, null);
                if (compiled == null) throw new InvalidOperationException("compile-output-null");
                record.compileOutputType = compiled.GetType().FullName;
                object successValue = ReadMember(compiled, "success");
                object descriptor = ReadMember(compiled, "assetDesc");
                if (!(successValue is bool) || !(bool)successValue || descriptor == null) throw new InvalidOperationException("graph-compile-unsuccessful");

                object gpuValue = ReadMember(descriptor, "gpuBufferDesc");
                object cpuValue = ReadMember(descriptor, "cpuBufferDesc");
                object tempValue = ReadMember(descriptor, "temporaryBufferDesc");
                object systemValue = ReadMember(descriptor, "systemDesc");
                var gpu = Items(gpuValue).ToArray();
                var cpu = Items(cpuValue).ToArray();
                var temporary = Items(tempValue).ToArray();
                var systems = Items(systemValue).ToArray();
                record.gpuDescriptorCount = gpu.Length;
                record.cpuDescriptorCount = cpu.Length;
                record.temporaryDescriptorCount = temporary.Length;
                record.systemDescriptorCount = systems.Length;
                bool complete = gpuValue != null && cpuValue != null && tempValue != null && systemValue != null;
                var gpuRows = new List<string>(gpu.Length);
                for (int index = 0; index < gpu.Length; index++)
                    gpuRows.Add(DescribeBuffer(index, gpu[index], ref complete));
                var cpuRows = new List<string>(cpu.Length);
                for (int index = 0; index < cpu.Length; index++)
                    cpuRows.Add(DescribeCpuBuffer(index, cpu[index], ref complete));
                var tempRows = new List<string>(temporary.Length);
                for (int index = 0; index < temporary.Length; index++)
                {
                    object desc = ReadMember(temporary[index], "desc");
                    string frameCount = Scalar(ReadMember(temporary[index], "frameCount"));
                    if (desc == null || frameCount == "unavailable") complete = false;
                    tempRows.Add("index=" + index.ToString(CultureInfo.InvariantCulture) + ",frameCount=" + frameCount + "," + DescribeBufferFields(desc, ref complete));
                }
                var mappingRows = new List<string>();
                bool mappingsValid = true;
                for (int systemIndex = 0; systemIndex < systems.Length; systemIndex++)
                {
                    object system = systems[systemIndex];
                    string systemName = Scalar(ReadMember(system, "name"));
                    string systemType = Scalar(ReadMember(system, "type"));
                    complete &= (systemName != "unavailable" || systemType == "Mesh") && systemType != "unavailable" && HasMember(system, "buffers") && HasMember(system, "tasks");
                    object[] systemBuffers = Items(ReadMember(system, "buffers")).ToArray();
                    foreach (object mapping in systemBuffers)
                    {
                        string name = Scalar(ReadMember(mapping, "name"));
                        string indexValue = Scalar(ReadMember(mapping, "index"));
                        complete &= name != "unavailable";
                        mappingsValid &= ValidSystemBufferIndex(name, indexValue, gpu.Length, cpu.Length);
                        mappingRows.Add("system=" + systemIndex + ",systemBufferMapping=" + name + ":" + indexValue);
                    }
                    object[] tasks = Items(ReadMember(system, "tasks")).ToArray();
                    for (int taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
                    {
                        object task = tasks[taskIndex];
                        string taskType = Scalar(ReadMember(task, "type"));
                        complete &= taskType != "unavailable" && HasMember(task, "buffers") && HasMember(task, "temporaryBuffers");
                        object[] buffers = Items(ReadMember(task, "buffers")).ToArray();
                        foreach (object mapping in buffers)
                        {
                            string name = Scalar(ReadMember(mapping, "name"));
                            string indexValue = Scalar(ReadMember(mapping, "index"));
                            complete &= name != "unavailable";
                            mappingsValid &= ValidSystemBufferIndex(name, indexValue, gpu.Length, cpu.Length);
                            mappingRows.Add("system=" + systemIndex + ",task=" + taskIndex + ",taskType=" + taskType + ",bufferMapping=" + name + ":" + indexValue);
                        }
                        object[] tempMappings = Items(ReadMember(task, "temporaryBuffers")).ToArray();
                        foreach (object temporaryMapping in tempMappings)
                        {
                            object mapping = ReadMember(temporaryMapping, "mapping");
                            string name = Scalar(ReadMember(mapping, "name"));
                            string indexValue = Scalar(ReadMember(mapping, "index"));
                            string pastFrameIndex = Scalar(ReadMember(temporaryMapping, "pastFrameIndex"));
                            string perCameraBuffer = Scalar(ReadMember(temporaryMapping, "perCameraBuffer"));
                            complete &= name != "unavailable";
                            mappingsValid &= ValidIndex(indexValue, temporary.Length) && pastFrameIndex != "unavailable" && perCameraBuffer != "unavailable";
                            mappingRows.Add("system=" + systemIndex + ",task=" + taskIndex + ",taskType=" + taskType + ",temporaryMapping=" + name + ":" + indexValue + ",pastFrameIndex=" + pastFrameIndex + ",perCameraBuffer=" + perCameraBuffer);
                        }
                    }
                    mappingRows.Add("system=" + systemIndex + ",name=" + systemName + ",type=" + systemType + ",taskCount=" + tasks.Length.ToString(CultureInfo.InvariantCulture));
                }
                record.gpuDescriptors = gpuRows.ToArray();
                record.cpuDescriptors = cpuRows.ToArray();
                record.temporaryDescriptors = tempRows.ToArray();
                record.systemsAndMappings = mappingRows.ToArray();
                record.descriptorFieldsComplete = complete;
                record.mappingIndicesValid = mappingsValid;
                record.compileStatus = complete && mappingsValid ? "success" : "partial-descriptor";
            }
            catch (Exception exception)
            {
                record.compileStatus = "failed";
                record.errorType = exception.GetType().Name;
            }
            record.sourceSha256After = Sha256(fullPath);
            if (record.sourceSha256Before != record.sourceSha256After) record.compileStatus = "source-changed-during-compile";
            return record;
        }

        static string DescribeBuffer(int index, object value, ref bool complete)
            => "index=" + index.ToString(CultureInfo.InvariantCulture) + "," + DescribeBufferFields(value, ref complete);

        static string DescribeCpuBuffer(int index, object value, ref bool complete)
        {
            string debugName = Scalar(ReadMember(value, "debugName"));
            string capacity = Scalar(ReadMember(value, "capacity"));
            string stride = Scalar(ReadMember(value, "stride"));
            if (value == null || debugName == "unavailable" || capacity == "unavailable" || stride == "unavailable") complete = false;
            return "index=" + index.ToString(CultureInfo.InvariantCulture) + ",debugName=" + debugName + ",capacity=" + capacity + ",stride=" + stride;
        }

        static string DescribeBufferFields(object value, ref bool complete)
        {
            string debugName = Scalar(ReadMember(value, "debugName"));
            string target = Scalar(ReadMember(value, "target"));
            string size = Scalar(ReadMember(value, "size"));
            string capacity = Scalar(ReadMember(value, "capacity"));
            string stride = Scalar(ReadMember(value, "stride"));
            if (value == null || debugName == "unavailable" || target == "unavailable" || size == "unavailable" || capacity == "unavailable" || stride == "unavailable") complete = false;
            return "debugName=" + debugName + ",target=" + target + ",size=" + size + ",capacity=" + capacity + ",stride=" + stride;
        }

        static bool ValidIndex(string value, int count)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return false;
            return index >= 0 && index < count;
        }

        static bool ValidSystemBufferIndex(string name, string value, int gpuCount, int cpuCount)
        {
            if (name == "unavailable") return false;
            if (name == "scratchBuffer" && value == "-1") return true;
            return name.StartsWith("spawner_", StringComparison.Ordinal)
                ? ValidIndex(value, cpuCount)
                : ValidIndex(value, gpuCount);
        }

        [Serializable]
        sealed class PreflightSnapshot
        {
            public string scenePath;
            public EffectSnapshot[] effects;
        }

        [Serializable]
        sealed class EffectSnapshot
        {
            public string objectPath;
            public bool activeInHierarchy;
        }

        static void VerifyPriorPreflightSnapshot(string projectRoot, string scenePath, string snapshotPath, Summary summary)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath)) throw new InvalidOperationException("prior-preflight-path-missing");
            string fullPath = Path.IsPathRooted(snapshotPath) ? Path.GetFullPath(snapshotPath) : Path.GetFullPath(Path.Combine(projectRoot, snapshotPath));
            if (!File.Exists(fullPath)) throw new FileNotFoundException("prior-preflight-snapshot-missing");
            string json = File.ReadAllText(fullPath);
            summary.priorPreflightSnapshotSha256 = Sha256(fullPath);
            PreflightSnapshot snapshot = JsonUtility.FromJson<PreflightSnapshot>(json);
            if (snapshot == null || snapshot.scenePath != scenePath || snapshot.effects == null) throw new InvalidOperationException("prior-preflight-snapshot-invalid");
            EffectSnapshot eyes = snapshot.effects.FirstOrDefault(effect => effect != null && effect.objectPath == KnownUnresolvedEyesPath);
            summary.priorPreflightEyesEntryFound = eyes != null;
            summary.priorPreflightEyesWasInactive = eyes != null && !eyes.activeInHierarchy;
            if (!summary.priorPreflightEyesEntryFound || !summary.priorPreflightEyesWasInactive) throw new InvalidOperationException("prior-preflight-eyes-state-mismatch");
        }

        static IEnumerable<object> Items(object value)
        {
            IEnumerable values = value as IEnumerable;
            return values == null ? Enumerable.Empty<object>() : values.Cast<object>();
        }

        static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target, null);
                FieldInfo field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }
            return null;
        }

        static bool HasMember(object target, string name)
        {
            if (target == null) return false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
                if (type.GetProperty(name, flags | BindingFlags.DeclaredOnly) != null || type.GetField(name, flags | BindingFlags.DeclaredOnly) != null) return true;
            return false;
        }

        static string Scalar(object value)
        {
            if (value == null) return "unavailable";
            Type type = value.GetType();
            if (!(type.IsPrimitive || value is decimal || value is Enum || value is string || value is IFormattable)) return "unavailable";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "unavailable";
        }

        static Type FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);
        static string HierarchyPath(Transform transform)
        {
            var names = new Stack<string>();
            for (Transform current = transform; current != null; current = current.parent) names.Push(current.name);
            return string.Join("/", names.ToArray());
        }

        static string ScriptAssetPath(MonoBehaviour behaviour)
        {
            MonoScript script = behaviour == null ? null : MonoScript.FromMonoBehaviour(behaviour);
            return script == null ? "" : AssetDatabase.GetAssetPath(script).Replace('\\', '/');
        }
        static bool IsCopiedSampleScript(string path) => path.StartsWith("Assets/Samples/Visual Effect Graph/17.6.0/", StringComparison.Ordinal) ||
            path.StartsWith("Assets/Samples/Scriptable Render Pipeline Core/17.6.0/", StringComparison.Ordinal);
        static string CurrentBinderType()
        {
            Type library = FindType("UnityEditor.VFX.VFXLibrary");
            if (library == null) return "";
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = library.GetProperty("currentSRPBinder", flags);
            FieldInfo field = library.GetField("currentSRPBinder", flags);
            object binder = property != null ? property.GetValue(null, null) : field == null ? null : field.GetValue(null);
            return binder == null ? "" : binder.GetType().FullName;
        }

        static string DescribePipeline(RenderPipelineAsset asset) => asset == null ? "" : asset.GetType().FullName + "|" + asset.name;
        static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }
}
#endif

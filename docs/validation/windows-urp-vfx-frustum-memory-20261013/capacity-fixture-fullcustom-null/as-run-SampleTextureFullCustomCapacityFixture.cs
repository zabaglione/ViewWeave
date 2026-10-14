#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;

namespace ViewWeave.SceneComparison.Tests
{
    public sealed class SampleTextureFullCustomCapacityFixture
    {
        const string SourcePath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/SampleTexture2D.vfx";
        const string ControlPath = "Assets/FrustumMemoryFixturesGrid40/Control/SampleTexture2D.vfx";
        const string FullCustomRoot = "Assets/FrustumMemoryFullCustom";
        const string FullCustomFolder = FullCustomRoot + "/FullCustom";
        const string FullCustomPath = FullCustomFolder + "/SampleTexture2D.vfx";
        const string EvidenceEnvironment = "VW_SAMPLE_TEXTURE_FULL_CUSTOM_RESULT";
        const string ExpectedSourceGuid = "e166f36ceb1831f46bea1e17fbc67c5a";
        const string ExpectedSourceSha256 = "78D34F482963B47E6555E9218389A98A045F614F3F5B300776C78948CFE96556";
        const string ExpectedControlGuid = "2f97a7e6edd4b5c4489e69768872259b";
        const string ExpectedControlSha256 = "55AE59AD9656256C04291B14FFAADECAF1BD622561A8E1B8BE53C66328CA6155";
        const uint ParticleCapacity = 160000;
        const float GridResolution = 400f;
        const int CustomInstancingCapacity = 1;

        string evidencePath;
        string sourceAbsolutePath;
        string sourceHashBefore;
        string sourceGuidBefore;
        string controlAbsolutePath;
        string controlHashBefore;
        string controlGuidBefore;
        string sceneFingerprintBefore;
        FixtureResult result;
        Camera[] loadedCameras = Array.Empty<Camera>();
        bool[] priorCameraEnabled = Array.Empty<bool>();

        [Test]
        public void CreatesFullCapacityCustomCloneAndDescribesCompiledAsset()
        {
            result = new FixtureResult
            {
                schema = "viewweave-sample-texture-full-custom-capacity-v1",
                status = "started",
                sourceAssetPath = SourcePath,
                controlAssetPath = ControlPath,
                fullCustomAssetPath = FullCustomPath,
                sourceParticleCapacity = ParticleCapacity,
                fullCustomParticleCapacity = ParticleCapacity,
                sourceGridResolutionX = GridResolution,
                sourceGridResolutionY = GridResolution,
                fullCustomGridResolutionX = GridResolution,
                fullCustomGridResolutionY = GridResolution,
                controlInstancingMode = "Auto",
                controlInstancingModeValue = 0,
                controlInstancingCapacity = 64,
                fullCustomInstancingMode = "Custom",
                fullCustomInstancingModeValue = 1,
                fullCustomInstancingCapacity = CustomInstancingCapacity,
                normalizedFields = new[] { "m_InstancingMode", "m_InstancingCapacity" },
                sceneSaveCalled = false,
                runtimeGraphActivation = false,
                nativeMeasurement = false
            };

            try
            {
                evidencePath = Environment.GetEnvironmentVariable(EvidenceEnvironment);
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidencePath), EvidenceEnvironment + " was not provided by the safe runner.");
                evidencePath = Path.GetFullPath(evidencePath);
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string resultsRoot = Path.GetFullPath(Path.Combine(projectRoot, "TestResults")) + Path.DirectorySeparatorChar;
                Assert.IsTrue(evidencePath.StartsWith(resultsRoot, StringComparison.OrdinalIgnoreCase), "Evidence must stay under this clone's TestResults directory.");
                Assert.IsFalse(File.Exists(evidencePath), "Refusing to overwrite prior fixture evidence.");
                Assert.IsFalse(Application.isPlaying, "This is an EditMode-only Null graphics fixture.");
                Assert.AreEqual(UnityEngine.Rendering.GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType, "The runner must use the Null graphics device.");

                sourceAbsolutePath = ToAbsoluteAssetPath(SourcePath);
                controlAbsolutePath = ToAbsoluteAssetPath(ControlPath);
                sceneFingerprintBefore = SceneFilesFingerprint();
                sourceHashBefore = File.Exists(sourceAbsolutePath) ? Sha256(sourceAbsolutePath) : "";
                sourceGuidBefore = AssetDatabase.AssetPathToGUID(SourcePath);
                controlHashBefore = File.Exists(controlAbsolutePath) ? Sha256(controlAbsolutePath) : "";
                controlGuidBefore = AssetDatabase.AssetPathToGUID(ControlPath);
                result.sourceAssetGuid = sourceGuidBefore;
                result.sourceAssetSha256Before = sourceHashBefore;
                result.controlAssetGuid = controlGuidBefore;
                result.controlAssetSha256Before = controlHashBefore;
                result.openSceneFingerprintBefore = sceneFingerprintBefore;

                Assert.AreEqual(ExpectedSourceGuid, sourceGuidBefore, "The original SampleTexture2D source GUID changed.");
                Assert.AreEqual(ExpectedSourceSha256, sourceHashBefore, "The original SampleTexture2D source hash changed.");
                Assert.AreEqual(ExpectedControlGuid, controlGuidBefore, "The saved Grid40 Control clone GUID is unavailable or unexpected.");
                Assert.AreEqual(ExpectedControlSha256, controlHashBefore, "The saved Grid40 Control clone hash is unavailable or unexpected.");
                Assert.IsFalse(AssetDatabase.IsValidFolder(FullCustomRoot), "Refusing to reuse a prior FullCustom fixture asset root.");
                Assert.IsFalse(File.Exists(ToAbsoluteAssetPath(FullCustomPath)), "Refusing to overwrite a prior FullCustom fixture graph.");

                UnityEditor.PackageManager.PackageInfo[] packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(value => value != null && value.name == "com.unity.visualeffectgraph").ToArray();
                Assert.AreEqual(1, packages.Length, "Registered Visual Effect Graph package provenance is unavailable or ambiguous.");
                Assert.AreEqual("17.6.0", packages[0].version, "Unexpected Visual Effect Graph version.");
                result.unityVersion = Application.unityVersion;
                result.graphicsDevice = SystemInfo.graphicsDeviceType.ToString();
                result.vfxGraphVersion = packages[0].version;
                result.vfxGraphResolvedPath = packages[0].resolvedPath ?? "";

                loadedCameras = Resources.FindObjectsOfTypeAll<Camera>()
                    .Where(camera => camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded).ToArray();
                priorCameraEnabled = loadedCameras.Select(camera => camera.enabled).ToArray();
                result.loadedSceneCameraCount = loadedCameras.Length;
                result.loadedSceneCameraEnabledAtLoad = priorCameraEnabled;
                foreach (Camera camera in loadedCameras)
                {
                    if (camera.enabled && camera.gameObject.activeInHierarchy) camera.enabled = false;
                }
                Assert.AreEqual(0, CountActiveSceneCameras(), "A loaded scene camera remained active during Null metadata preparation.");
                Assert.AreEqual(0, CountActiveSceneVfx(), "A loaded scene VFX component remained active during Null metadata preparation.");
                result.activeSceneCameraCountDuringProbe = CountActiveSceneCameras();
                result.activeSceneVfxCountDuringProbe = CountActiveSceneVfx();

                ParticleDataInfo sourceData = FindParticleData(SourcePath);
                ParticleDataInfo controlData = FindParticleData(ControlPath);
                Assert.AreEqual(1, sourceData.matches, "The source graph must contain exactly one particle system at capacity 160000.");
                Assert.AreEqual(1, controlData.matches, "The saved Control clone must contain exactly one particle system at capacity 160000.");
                Assert.AreEqual(sourceData.name, controlData.name, "The Control clone particle system identity differs from the source.");
                Assert.AreEqual(ParticleCapacity, sourceData.capacity, "The original source capacity changed.");
                Assert.AreEqual(ParticleCapacity, controlData.capacity, "The saved Control capacity is not 160000.");
                result.sourceParticleDataName = sourceData.name;
                result.controlParticleDataName = controlData.name;
                result.controlParticleCapacity = controlData.capacity;
                GridInfo sourceGrid = FindGridParameter(SourcePath);
                GridInfo controlGrid = FindGridParameter(ControlPath);
                Assert.AreEqual(new Vector2(GridResolution, GridResolution), sourceGrid.value, "The original source Grid Resolution changed.");
                Assert.AreEqual(new Vector2(GridResolution, GridResolution), controlGrid.value, "The Control Grid Resolution is not 400x400.");
                result.controlGridResolutionX = controlGrid.value.x;
                result.controlGridResolutionY = controlGrid.value.y;
                result.gridParameterName = sourceGrid.name;
                result.gridParameterValueType = sourceGrid.typeName;
                result.gridParameterExposed = sourceGrid.exposed;
                result.controlGridParameterFileId = ParseFileId(controlGrid.parameterFileId, "Control Grid Resolution parameter");
                result.controlGridOutputSlotFileId = ParseFileId(controlGrid.outputSlotFileId, "Control Grid Resolution output slot");
                Assert.AreEqual("Grid Resolution", sourceGrid.name, "Unexpected grid parameter identity.");
                Assert.IsFalse(sourceGrid.exposed, "Grid Resolution unexpectedly became exposed.");
                Assert.AreEqual(typeof(Vector2).FullName, sourceGrid.typeName, "Grid Resolution is not a Vector2 parameter.");

                ReadModeAndCapacity(ControlPath, out int controlMode, out string controlModeName, out int controlInstancingCapacity);
                Assert.AreEqual(0, controlMode, "The saved Control is not Auto mode.");
                Assert.AreEqual("Auto", controlModeName, "The saved Control instancing mode name is unexpected.");
                Assert.AreEqual(64, controlInstancingCapacity, "The saved Control instancing capacity is unexpected.");
                result.controlInstancingMode = controlModeName;
                result.controlInstancingModeValue = controlMode;
                result.controlInstancingCapacity = controlInstancingCapacity;

                result.controlDescriptors = ReadCompiledDescriptors(ControlPath);
                AssertDescriptorShape(result.controlDescriptors, "Control");
                result.controlComparableStructureSha256 = InstancingComparableStructureHash(ControlPath, controlMode, controlInstancingCapacity);

                EnsureFolder(FullCustomRoot);
                EnsureFolder(FullCustomFolder);
                Assert.IsTrue(AssetDatabase.CopyAsset(ControlPath, FullCustomPath), "Could not copy the saved Control graph for FullCustom.");
                AssetDatabase.ImportAsset(FullCustomPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                string copyHashBeforeEdit = Sha256(ToAbsoluteAssetPath(FullCustomPath));
                result.fullCustomAssetGuid = AssetDatabase.AssetPathToGUID(FullCustomPath);
                result.fullCustomAssetSha256BeforeEdit = copyHashBeforeEdit;
                Assert.IsNotEmpty(result.fullCustomAssetGuid, "FullCustom clone GUID is unavailable.");
                ParticleDataInfo copiedData = FindParticleData(FullCustomPath);
                Assert.AreEqual(1, copiedData.matches, "The copied FullCustom asset must retain exactly one 160000-capacity particle system.");
                Assert.AreEqual(sourceData.name, copiedData.name, "Copying the Control changed the particle system identity.");
                Assert.AreEqual(ParticleCapacity, copiedData.capacity, "Copying the Control changed particle capacity.");
                GridInfo copiedGrid = FindGridParameter(FullCustomPath);
                Assert.AreEqual(sourceGrid.value, copiedGrid.value, "Copying the Control changed Grid Resolution.");
                ReadModeAndCapacity(FullCustomPath, out int copiedMode, out string copiedModeName, out int copiedInstancingCapacity);
                Assert.AreEqual(controlMode, copiedMode, "The copied FullCustom asset did not begin with Control instancing mode.");
                Assert.AreEqual(controlInstancingCapacity, copiedInstancingCapacity, "The copied FullCustom asset did not begin with Control instancing capacity.");
                Assert.AreEqual(result.controlComparableStructureSha256, InstancingComparableStructureHash(FullCustomPath, copiedMode, copiedInstancingCapacity), "The unmodified copy differs from the saved Control beyond its generated asset identity.");

                int customMode = ResolveInstancingEnumValue("Custom");
                SetInstancingModeAndCapacity(FullCustomPath, customMode, CustomInstancingCapacity);
                AssetDatabase.ImportAsset(FullCustomPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                ReadModeAndCapacity(FullCustomPath, out int fullCustomMode, out string fullCustomModeName, out int fullCustomCapacity);
                Assert.AreEqual(customMode, fullCustomMode, "FullCustom instancing mode was not serialized as Custom.");
                Assert.AreEqual("Custom", fullCustomModeName, "FullCustom instancing mode name is unexpected.");
                Assert.AreEqual(CustomInstancingCapacity, fullCustomCapacity, "FullCustom instancing capacity is not one.");
                ParticleDataInfo finalData = FindParticleData(FullCustomPath);
                GridInfo finalGrid = FindGridParameter(FullCustomPath);
                Assert.AreEqual(1, finalData.matches, "FullCustom must retain exactly one 160000-capacity particle system.");
                Assert.AreEqual(ParticleCapacity, finalData.capacity, "FullCustom particle capacity changed.");
                Assert.AreEqual(sourceData.name, finalData.name, "FullCustom particle system identity changed.");
                Assert.AreEqual(sourceGrid.value, finalGrid.value, "FullCustom Grid Resolution changed.");
                Assert.AreEqual(sourceGrid.parameterFileId, finalGrid.parameterFileId, "FullCustom Grid Resolution parameter identity changed.");
                Assert.AreEqual(sourceGrid.outputSlotFileId, finalGrid.outputSlotFileId, "FullCustom Grid Resolution output slot identity changed.");
                result.fullCustomGridParameterFileId = ParseFileId(finalGrid.parameterFileId, "FullCustom Grid Resolution parameter");
                result.fullCustomGridOutputSlotFileId = ParseFileId(finalGrid.outputSlotFileId, "FullCustom Grid Resolution output slot");
                result.fullCustomParticleDataName = finalData.name;
                result.fullCustomParticleCapacity = finalData.capacity;
                result.fullCustomGridResolutionX = finalGrid.value.x;
                result.fullCustomGridResolutionY = finalGrid.value.y;
                result.fullCustomInstancingMode = fullCustomModeName;
                result.fullCustomInstancingModeValue = fullCustomMode;
                result.fullCustomInstancingCapacity = fullCustomCapacity;

                result.fullCustomDescriptors = ReadCompiledDescriptors(FullCustomPath);
                AssertDescriptorShape(result.fullCustomDescriptors, "FullCustom");
                ReadCompiledDisabledReason(FullCustomPath, out string disabledReason, out int disabledValue, out int disabledNoneValue, out string disabledEnumType);
                result.fullCustomDisabledReasonName = disabledReason;
                result.fullCustomDisabledReasonValue = disabledValue;
                result.fullCustomDisabledReasonNoneValue = disabledNoneValue;
                result.fullCustomDisabledReasonEnumType = disabledEnumType;
                Assert.AreEqual(disabledNoneValue, disabledValue, "FullCustom compiled disabled reason is not the formal None enum value.");
                Assert.AreEqual("None", disabledReason, "FullCustom is not supported for instancing by the compiler.");

                result.fullCustomComparableStructureSha256 = InstancingComparableStructureHash(FullCustomPath, fullCustomMode, fullCustomCapacity);
                Assert.AreEqual(result.controlComparableStructureSha256, result.fullCustomComparableStructureSha256, "FullCustom changed serialized graph structure outside the two instancing fields.");
                result.sourceAssetUnchanged = true;
                result.controlAssetUnchanged = true;
                result.openSceneFilesUnchanged = true;
                result.status = "success";
            }
            catch (Exception exception)
            {
                result.status = "failed";
                result.failureType = exception.GetType().Name;
                result.failureMessage = Ascii(exception.Message);
                throw;
            }
            finally
            {
                for (int index = 0; index < loadedCameras.Length; index++)
                {
                    Camera camera = loadedCameras[index];
                    if (camera != null && index < priorCameraEnabled.Length) camera.enabled = priorCameraEnabled[index];
                }
                try
                {
                    result.sourceAssetGuidAfter = AssetDatabase.AssetPathToGUID(SourcePath);
                    result.sourceAssetSha256After = File.Exists(sourceAbsolutePath) ? Sha256(sourceAbsolutePath) : "";
                    result.sourceAssetUnchanged = result.sourceAssetGuid == result.sourceAssetGuidAfter && result.sourceAssetSha256Before == result.sourceAssetSha256After;
                }
                catch (Exception exception) { result.sourceAssetUnchanged = false; result.sourceAfterCheckError = exception.GetType().Name; }
                try
                {
                    result.controlAssetGuidAfter = AssetDatabase.AssetPathToGUID(ControlPath);
                    result.controlAssetSha256After = File.Exists(controlAbsolutePath) ? Sha256(controlAbsolutePath) : "";
                    result.controlAssetUnchanged = result.controlAssetGuid == result.controlAssetGuidAfter && result.controlAssetSha256Before == result.controlAssetSha256After;
                }
                catch (Exception exception) { result.controlAssetUnchanged = false; result.controlAfterCheckError = exception.GetType().Name; }
                try
                {
                    if (File.Exists(ToAbsoluteAssetPath(FullCustomPath))) result.fullCustomAssetSha256After = Sha256(ToAbsoluteAssetPath(FullCustomPath));
                }
                catch (Exception exception) { result.fullCustomAssetSha256After = ""; result.failureMessage = Ascii((result.failureMessage ?? "") + " fullCustomHashAfter=" + exception.GetType().Name); }
                try
                {
                    result.openSceneFingerprintAfter = SceneFilesFingerprint();
                    result.openSceneFilesUnchanged = result.openSceneFingerprintBefore == result.openSceneFingerprintAfter;
                }
                catch (Exception exception) { result.openSceneFilesUnchanged = false; result.sceneAfterCheckError = exception.GetType().Name; }
                result.activeSceneCameraCountAfterRestore = CountActiveSceneCameras();
                result.activeSceneVfxCountAfterRestore = CountActiveSceneVfx();
                result.finalSourceAndControlIntegrityGate = result.sourceAssetUnchanged && result.controlAssetUnchanged && result.openSceneFilesUnchanged;
                if (result.status == "success" && !result.finalSourceAndControlIntegrityGate)
                {
                    result.status = "failed-final-integrity-gate";
                }
                try
                {
                    if (!string.IsNullOrEmpty(evidencePath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath));
                        if (!File.Exists(evidencePath)) File.WriteAllText(evidencePath, JsonUtility.ToJson(result, true), new UTF8Encoding(false));
                    }
                }
                catch { }
            }
        }

        static void AssertDescriptorShape(DescriptorInfo info, string label)
        {
            Assert.IsNotNull(info, label + " descriptor result is missing.");
            Assert.AreEqual("success", info.compileStatus, label + " descriptor compile failed.");
            Assert.IsTrue(info.descriptorFieldsComplete, label + " descriptor fields are incomplete.");
            Assert.IsTrue(info.mappingIndicesValid, label + " descriptor mapping indices are invalid.");
            Assert.IsTrue(info.temporaryDescriptors.Any(value => value != null && value.Contains("debugName=VFXMovecsBuffer") && value.Contains("target=Raw") && value.Contains("size=8320000") && value.Contains("frameCount=2")), label + " lacks the expected 8,320,000 Raw two-frame motion descriptor.");
            Assert.IsTrue(info.systemsAndMappings.Any(value => value != null && value.Contains("perCameraBuffer=True")), label + " lacks a per-camera temporary mapping.");
        }

        static void SetInstancingModeAndCapacity(string assetPath, int modeValue, int capacityValue)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            var serialized = new SerializedObject(resource);
            SerializedProperty mode = serialized.FindProperty("m_Infos.m_InstancingMode");
            SerializedProperty capacity = serialized.FindProperty("m_Infos.m_InstancingCapacity");
            Assert.IsNotNull(mode, "VFX resource instancing mode property is unavailable.");
            Assert.IsNotNull(capacity, "VFX resource instancing capacity property is unavailable.");
            mode.intValue = modeValue;
            capacity.intValue = capacityValue;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(resource);
            WriteResource(assetPath);
        }

        static void ReadModeAndCapacity(string assetPath, out int modeValue, out string modeName, out int capacityValue)
        {
            var serialized = new SerializedObject(GetResource(assetPath));
            SerializedProperty mode = serialized.FindProperty("m_Infos.m_InstancingMode");
            SerializedProperty capacity = serialized.FindProperty("m_Infos.m_InstancingCapacity");
            Assert.IsNotNull(mode, "VFX resource instancing mode property is unavailable.");
            Assert.IsNotNull(capacity, "VFX resource instancing capacity property is unavailable.");
            modeValue = mode.intValue;
            capacityValue = capacity.intValue;
            modeName = Enum.GetName(ResolveInstancingEnumType(), modeValue);
            Assert.IsNotEmpty(modeName, "VFX instancing mode enum value is invalid.");
        }

        static int ResolveInstancingEnumValue(string name)
        {
            Type enumType = ResolveInstancingEnumType();
            Assert.IsTrue(Enum.IsDefined(enumType, name), "VFXInstancingMode does not define " + name + ".");
            return Convert.ToInt32(Enum.Parse(enumType, name, false), CultureInfo.InvariantCulture);
        }

        static Type ResolveInstancingEnumType()
        {
            Type[] enumTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .Where(type => type != null && type.Name == "VFXInstancingMode" && type.IsEnum).ToArray();
            Assert.AreEqual(1, enumTypes.Length, "VFXInstancingMode enum type is unavailable or ambiguous.");
            return enumTypes[0];
        }

        static void ReadCompiledDisabledReason(string assetPath, out string name, out int rawValue, out int noneValue, out string enumName)
        {
            VisualEffectAsset asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
            Assert.IsNotNull(asset, "Compiled FullCustom VisualEffectAsset is unavailable.");
            SerializedProperty property = new SerializedObject(asset).FindProperty("m_Infos.m_InstancingDisabledReason");
            Assert.IsNotNull(property, "Compiled instancing disabled-reason property is unavailable.");
            Type[] types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .Where(type => type != null && type.Name == "VFXInstancingDisabledReason" && type.IsEnum).ToArray();
            Assert.AreEqual(1, types.Length, "VFXInstancingDisabledReason enum is unavailable or ambiguous.");
            rawValue = property.intValue;
            object none = Enum.Parse(types[0], "None", false);
            noneValue = Convert.ToInt32(none, CultureInfo.InvariantCulture);
            name = Enum.Format(types[0], rawValue, "F");
            enumName = types[0].FullName + "," + types[0].Assembly.GetName().Name;
        }

        static ParticleDataInfo FindParticleData(string assetPath)
        {
            object graph = GetGraph(assetPath);
            HashSet<ScriptableObject> dependencies = CollectDependencies(graph);
            var candidates = new List<ParticleDataInfo>();
            foreach (ScriptableObject value in dependencies)
            {
                if (value == null || value.GetType().Name != "VFXDataParticle") continue;
                SerializedProperty capacity = new SerializedObject(value).FindProperty("capacity");
                if (capacity != null && unchecked((uint)capacity.intValue) == ParticleCapacity)
                    candidates.Add(new ParticleDataInfo { target = value, name = value.name, capacity = unchecked((uint)capacity.intValue) });
            }
            return new ParticleDataInfo
            {
                matches = candidates.Count,
                target = candidates.Count == 1 ? candidates[0].target : null,
                name = candidates.Count == 1 ? candidates[0].name : "",
                capacity = candidates.Count == 1 ? candidates[0].capacity : 0
            };
        }

        static GridInfo FindGridParameter(string assetPath)
        {
            var matches = new List<GridInfo>();
            foreach (ScriptableObject dependency in CollectDependencies(GetGraph(assetPath)))
            {
                if (dependency == null || dependency.GetType().Name != "VFXParameter") continue;
                Type type = dependency.GetType();
                PropertyInfo exposedName = type.GetProperty("exposedName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (exposedName == null || !string.Equals(Convert.ToString(exposedName.GetValue(dependency, null), CultureInfo.InvariantCulture), "Grid Resolution", StringComparison.Ordinal)) continue;
                PropertyInfo exposed = type.GetProperty("exposed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo valueType = type.GetProperty("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo value = type.GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.IsNotNull(exposed, "VFXParameter.exposed property is unavailable.");
                Assert.IsNotNull(valueType, "VFXParameter.type property is unavailable.");
                Assert.IsNotNull(value, "VFXParameter.value property is unavailable.");
                Assert.IsFalse(Convert.ToBoolean(exposed.GetValue(dependency, null), CultureInfo.InvariantCulture), "Grid Resolution must remain non-exposed.");
                Assert.AreEqual(typeof(Vector2), valueType.GetValue(dependency, null), "Grid Resolution must remain a Vector2 parameter.");
                matches.Add(new GridInfo
                {
                    name = "Grid Resolution",
                    typeName = typeof(Vector2).FullName,
                    exposed = false,
                    value = (Vector2)value.GetValue(dependency, null),
                    parameterFileId = ReadParameterFileId(assetPath),
                    outputSlotFileId = ReadGridOutputSlotFileId(assetPath)
                });
            }
            Assert.AreEqual(1, matches.Count, "Expected exactly one non-exposed Grid Resolution Vector2 parameter.");
            return matches[0];
        }

        static string ReadParameterFileId(string assetPath)
        {
            Match[] docs = FindYamlDocuments(File.ReadAllText(ToAbsoluteAssetPath(assetPath)))
                .Where(match => match.Groups["body"].Value.Contains("m_ExposedName: Grid Resolution")).ToArray();
            Assert.AreEqual(1, docs.Length, "Grid Resolution parameter YAML identity is missing or ambiguous.");
            return docs[0].Groups["id"].Value;
        }

        static string ReadGridOutputSlotFileId(string assetPath)
        {
            string text = File.ReadAllText(ToAbsoluteAssetPath(assetPath));
            Match[] docs = FindYamlDocuments(text).Where(match => match.Groups["body"].Value.Contains("m_ExposedName: Grid Resolution")).ToArray();
            Assert.AreEqual(1, docs.Length, "Grid Resolution parameter YAML identity is missing or ambiguous.");
            Match slot = Regex.Match(docs[0].Groups["body"].Value, @"(?m)^  m_OutputSlots:[ \t]*\r?\n  - \{fileID: (?<id>\d+)\}[ \t]*$");
            Assert.IsTrue(slot.Success, "Grid Resolution output slot identity is unavailable.");
            return slot.Groups["id"].Value;
        }

        static long ParseFileId(string value, string label)
        {
            Assert.IsTrue(long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed), label + " fileID is not a signed 64-bit integer.");
            return parsed;
        }

        static Match[] FindYamlDocuments(string text)
        {
            return Regex.Matches(text, @"(?ms)^--- !u!(?<class>\d+) &(?<id>-?\d+)(?: stripped)?\r?\n(?<body>.*?)(?=^--- !u!|\z)")
                .Cast<Match>().ToArray();
        }

        static object GetGraph(string assetPath)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            Type extensions = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.Name == "VisualEffectResourceExtensions" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(extensions, "VisualEffectResourceExtensions is unavailable.");
            MethodInfo method = extensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == "GetGraph" && candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(method, "VisualEffectResourceExtensions.GetGraph is unavailable.");
            object graph = method.Invoke(null, new[] { resource });
            Assert.IsNotNull(graph, "VFX Graph could not be loaded from " + assetPath);
            return graph;
        }

        static HashSet<ScriptableObject> CollectDependencies(object graph)
        {
            MethodInfo method = graph.GetType().GetMethod("CollectDependencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(HashSet<ScriptableObject>), typeof(bool) }, null);
            Assert.IsNotNull(method, "VFXGraph.CollectDependencies(HashSet<ScriptableObject>, bool) is unavailable.");
            var dependencies = new HashSet<ScriptableObject>();
            method.Invoke(graph, new object[] { dependencies, false });
            return dependencies;
        }

        static UnityEngine.Object GetResource(string assetPath)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(value => value != null && value.Name == "VisualEffectResource" && value.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(type, "UnityEditor.VFX.VisualEffectResource is unavailable.");
            MethodInfo method = type.GetMethod("GetResourceAtPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(method, "VisualEffectResource.GetResourceAtPath(string) is unavailable.");
            UnityEngine.Object resource = method.Invoke(null, new object[] { assetPath }) as UnityEngine.Object;
            Assert.IsNotNull(resource, "VFX editor resource could not be loaded from " + assetPath);
            return resource;
        }

        static void WriteResource(string assetPath)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            Type extensions = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.Name == "VisualEffectResourceExtensions" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(extensions, "VisualEffectResourceExtensions is unavailable.");
            MethodInfo method = extensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == "WriteAssetWithSubAssets" && candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(method, "VisualEffectResourceExtensions.WriteAssetWithSubAssets is unavailable.");
            method.Invoke(null, new object[] { resource });
        }

        static DescriptorInfo ReadCompiledDescriptors(string assetPath)
        {
            Type inventory = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.FullName == "ViewWeave.SceneComparison.VfxDescriptorInventory");
            Assert.IsNotNull(inventory, "Existing CPU VFX descriptor inventory is unavailable.");
            MethodInfo method = inventory.GetMethod("CompileAndDescribe", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "VfxDescriptorInventory.CompileAndDescribe is unavailable.");
            string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            object record = method.Invoke(null, new object[] { assetPath, assetGuid, projectRoot });
            Assert.IsNotNull(record, "VFX descriptor inventory returned no result.");
            Type recordType = record.GetType();
            string status = Convert.ToString(ReadPublicField(recordType, record, "compileStatus"), CultureInfo.InvariantCulture);
            bool fieldsComplete = Convert.ToBoolean(ReadPublicField(recordType, record, "descriptorFieldsComplete"), CultureInfo.InvariantCulture);
            bool indicesValid = Convert.ToBoolean(ReadPublicField(recordType, record, "mappingIndicesValid"), CultureInfo.InvariantCulture);
            string before = Convert.ToString(ReadPublicField(recordType, record, "sourceSha256Before"), CultureInfo.InvariantCulture);
            string after = Convert.ToString(ReadPublicField(recordType, record, "sourceSha256After"), CultureInfo.InvariantCulture);
            Assert.AreEqual("success", status, "VFX CPU descriptor compile failed.");
            Assert.IsTrue(fieldsComplete, "VFX CPU descriptor fields are incomplete.");
            Assert.IsTrue(indicesValid, "VFX descriptor mapping indices are invalid.");
            Assert.AreEqual(before, after, "VFX graph changed during CPU descriptor compilation.");
            Assert.AreEqual(Sha256(ToAbsoluteAssetPath(assetPath)), after, "Descriptor inventory source hash differs from the graph file.");
            return new DescriptorInfo
            {
                compileStatus = status,
                descriptorFieldsComplete = fieldsComplete,
                mappingIndicesValid = indicesValid,
                sourceSha256Before = before,
                sourceSha256After = after,
                temporaryDescriptors = (string[])ReadPublicField(recordType, record, "temporaryDescriptors"),
                systemsAndMappings = (string[])ReadPublicField(recordType, record, "systemsAndMappings")
            };
        }

        static object ReadPublicField(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, "VFX descriptor inventory field is missing: " + name);
            return field.GetValue(instance);
        }

        static Type[] SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null).ToArray(); }
        }

        static string InstancingComparableStructureHash(string assetPath, int expectedMode, int expectedCapacity)
        {
            string text = File.ReadAllText(ToAbsoluteAssetPath(assetPath));
            text = NormalizeYamlInteger(text, "m_InstancingMode", expectedMode);
            text = NormalizeYamlInteger(text, "m_InstancingCapacity", expectedCapacity);
            return HashText(text);
        }

        static string NormalizeYamlInteger(string text, string fieldName, int expectedValue)
        {
            string pattern = "(?m)^([ \\t]*" + Regex.Escape(fieldName) + ":[ \\t]*)(-?\\d+)([ \\t]*)$";
            MatchCollection matches = Regex.Matches(text, pattern);
            Assert.AreEqual(1, matches.Count, "Expected exactly one serialized " + fieldName + " value.");
            Assert.IsTrue(int.TryParse(matches[0].Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actual), "Serialized " + fieldName + " is not an integer.");
            Assert.AreEqual(expectedValue, actual, "Serialized " + fieldName + " differs from the expected value.");
            return Regex.Replace(text, pattern, "$1normalized-value$3", RegexOptions.Multiline);
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            int separator = assetFolder.LastIndexOf('/');
            Assert.Greater(separator, 0, "Invalid fixture folder path.");
            string parent = assetFolder.Substring(0, separator);
            string name = assetFolder.Substring(separator + 1);
            EnsureFolder(parent);
            Assert.IsNotEmpty(AssetDatabase.CreateFolder(parent, name), "Could not create fixture folder " + assetFolder);
        }

        static int CountActiveSceneCameras()
        {
            return Resources.FindObjectsOfTypeAll<Camera>().Count(camera => camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded && camera.enabled && camera.gameObject.activeInHierarchy);
        }

        static int CountActiveSceneVfx()
        {
            return Resources.FindObjectsOfTypeAll<VisualEffect>().Count(effect => effect != null && effect.gameObject.scene.IsValid() && effect.gameObject.scene.isLoaded && effect.gameObject.activeInHierarchy);
        }

        static string SceneFilesFingerprint()
        {
            var rows = new List<string>();
            foreach (SceneSetup setup in EditorSceneManager.GetSceneManagerSetup())
            {
                if (string.IsNullOrEmpty(setup.path)) { rows.Add("untitled|" + setup.isLoaded); continue; }
                string absolute = Path.GetFullPath(Path.Combine(Application.dataPath, "..", setup.path));
                rows.Add(setup.path.Replace('\\', '/') + "|" + (File.Exists(absolute) ? Sha256(absolute) : "missing") + "|" + setup.isLoaded);
            }
            rows.Sort(StringComparer.Ordinal);
            return HashText(string.Join("\n", rows.ToArray()));
        }

        static string ToAbsoluteAssetPath(string assetPath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        static string HashText(string text)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        static string Ascii(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var output = new StringBuilder(value.Length);
            foreach (char character in value) output.Append(character >= 32 && character <= 126 ? character : '?');
            return output.ToString();
        }

        sealed class ParticleDataInfo
        {
            public UnityEngine.Object target;
            public string name;
            public uint capacity;
            public int matches;
        }

        sealed class GridInfo
        {
            public string name, typeName, parameterFileId, outputSlotFileId;
            public bool exposed;
            public Vector2 value;
        }

        [Serializable]
        sealed class DescriptorInfo
        {
            public string compileStatus, sourceSha256Before, sourceSha256After;
            public bool descriptorFieldsComplete, mappingIndicesValid;
            public string[] temporaryDescriptors, systemsAndMappings;
        }

        [Serializable]
        sealed class FixtureResult
        {
            public string schema, status, failureType, failureMessage, unityVersion, graphicsDevice, vfxGraphVersion, vfxGraphResolvedPath;
            public string sourceAssetPath, sourceAssetGuid, sourceAssetGuidAfter, sourceAssetSha256Before, sourceAssetSha256After, sourceAfterCheckError;
            public string controlAssetPath, controlAssetGuid, controlAssetGuidAfter, controlAssetSha256Before, controlAssetSha256After, controlAfterCheckError;
            public string fullCustomAssetPath, fullCustomAssetGuid, fullCustomAssetSha256BeforeEdit, fullCustomAssetSha256After;
            public string sourceParticleDataName, controlParticleDataName, fullCustomParticleDataName;
            public string gridParameterName, gridParameterValueType, openSceneFingerprintBefore, openSceneFingerprintAfter, sceneAfterCheckError;
            public string controlInstancingMode, fullCustomInstancingMode, fullCustomDisabledReasonName, fullCustomDisabledReasonEnumType;
            public string controlComparableStructureSha256, fullCustomComparableStructureSha256;
            public string[] normalizedFields;
            public bool[] loadedSceneCameraEnabledAtLoad;
            public long controlGridParameterFileId, controlGridOutputSlotFileId, fullCustomGridParameterFileId, fullCustomGridOutputSlotFileId;
            public uint sourceParticleCapacity, controlParticleCapacity, fullCustomParticleCapacity;
            public float sourceGridResolutionX, sourceGridResolutionY, controlGridResolutionX, controlGridResolutionY, fullCustomGridResolutionX, fullCustomGridResolutionY;
            public int controlInstancingModeValue, controlInstancingCapacity, fullCustomInstancingModeValue, fullCustomInstancingCapacity;
            public int fullCustomDisabledReasonValue, fullCustomDisabledReasonNoneValue, loadedSceneCameraCount, activeSceneCameraCountDuringProbe, activeSceneVfxCountDuringProbe, activeSceneCameraCountAfterRestore, activeSceneVfxCountAfterRestore;
            public bool gridParameterExposed, sourceAssetUnchanged, controlAssetUnchanged, openSceneFilesUnchanged, finalSourceAndControlIntegrityGate;
            public bool sceneSaveCalled, runtimeGraphActivation, nativeMeasurement;
            public DescriptorInfo controlDescriptors, fullCustomDescriptors;
        }
    }
}
#endif

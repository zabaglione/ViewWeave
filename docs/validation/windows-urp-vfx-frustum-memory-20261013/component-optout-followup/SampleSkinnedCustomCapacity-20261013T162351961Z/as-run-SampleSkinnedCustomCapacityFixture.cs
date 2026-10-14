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
    public sealed class SampleSkinnedCustomCapacityFixture
    {
        const string SourceGraphPath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/SampleSkinnedMesh.vfx";
        const string SourceVariantPath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Sample Skinned Mesh.prefab";
        const string SourceScenePath = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const string FixtureRoot = "Assets/FrustumMemorySkinnedCustom";
        const string ControlFolder = FixtureRoot + "/Control";
        const string ControlPath = ControlFolder + "/SampleSkinnedMesh.vfx";
        const string CustomFolder = FixtureRoot + "/Custom";
        const string CustomPath = CustomFolder + "/SampleSkinnedMesh.vfx";
        const string EvidenceEnvironment = "VW_SAMPLE_SKINNED_CUSTOM_RESULT";
        const string SourceGraphGuid = "986cf4b6651f46246bdc1e5e7f81b4c2";
        const string SourceGraphSha256 = "65A6278E77854CC32DEB1AB748693C493AE6313F3F38E6465B4DE8CBA33D8EC1";
        const string SourceGraphMetaSha256 = "A6EDDDDE3FFC47B113CC4A97E98D52F2D78152828C8D572C58388E759E2BC3E0";
        const string SourceVariantGuid = "7f90fd4140e9047438f25d2a806668af";
        const string SourceVariantSha256 = "61FE39002BCF1CADCD7FEA78B0521EF2F9287F40B375F5C17DBF7D698EEFC6C1";
        const string SourceVariantMetaSha256 = "2624FC344ACB51C158E2EF87EB38090CAF0F4DEBE65073895A88382A3EB45B3B";
        const string CellPrefabGuid = "7837c49e89b12db43b2a59edba6bff6b";
        const string CellPrefabSha256 = "F6AA75AF015ABD052C078567AE5C1DF77F3BAB5075D2D6C175D49EF5D96FF9D6";
        const string CellPrefabMetaSha256 = "81B423C4D3F97545D3F712F486F1BC3BBEFF3F3C248544A54921546A378C58DA";
        const string OriginalSceneSha256 = "A47FECCDA2F348A0598A8E58133ABA539C1D1F94625BCC0A069676155F2D335D";
        const uint ParticleCapacity = 30000;
        const int CustomCapacity = 1;
        const string ExpectedTemporaryDescriptor = "debugName=VFXMovecsBuffer;target=Raw;size=1080000;frameCount=2";
        static readonly string[] ExpectedHdrpClosureIds = {
            "8926484042661617170", "8926484042661616652", "8926484042661616705", "8926484042661617136",
            "8926484042661616715", "8926484042661616713", "8926484042661617169", "8926484042661616724",
            "8926484042661617134", "8926484042661616709", "8926484042661616701", "8926484042661616651",
            "8926484042661616698", "8926484042661616714", "8926484042661617171", "8926484042661616708",
            "8926484042661616706", "8926484042661616712", "8926484042661616711", "8926484042661616720",
            "8926484042661616700", "8926484042661616653", "8926484042661616721", "8926484042661616710",
            "8926484042661616656", "8926484042661616722", "8926484042661617135", "8926484042661617168",
            "8926484042661616699", "8926484042661616723", "8926484042661616697"
        };
        static readonly string[] ExpectedChangedCommonDocumentIds = {
            "114340500867371532", "114350483966674976", "8926484042661614568",
            "8926484042661616954", "8926484042661617028", "8926484042661617183"
        };
        const string ExpectedUrpSubOutputId = "8926484042661617230";

        string evidencePath;
        string sourceGraphAbsolute, sourceVariantAbsolute, sourceSceneAbsolute;
        string sceneFingerprintBefore;
        string sourceGraphHashBefore, sourceGraphMetaHashBefore, sourceVariantHashBefore, sourceVariantMetaHashBefore, sourceSceneHashBefore;
        string controlHashBefore, customHashBefore;
        string sourceGraphGuidBefore, sourceVariantGuidBefore, controlGuidBefore, customGuidBefore;
        FixtureResult result;
        Camera[] loadedCameras = Array.Empty<Camera>();
        bool[] priorCameraEnabled = Array.Empty<bool>();

        [Test]
        public void BuildsSampleSkinnedCustomCapacityOneCloneAndChecksCompiledData()
        {
            result = new FixtureResult
            {
                schema = "viewweave-sample-skinned-custom-capacity-v1",
                status = "started",
                sourceGraphPath = SourceGraphPath,
                sourceVariantPath = SourceVariantPath,
                sourceScenePath = SourceScenePath,
                controlPath = ControlPath,
                customPath = CustomPath,
                sourceParticleCapacity = ParticleCapacity,
                controlParticleCapacity = ParticleCapacity,
                customParticleCapacity = ParticleCapacity,
                normalizedFields = new[] { "m_InstancingMode", "m_InstancingCapacity", "URP projection: m_Infos.m_InstancingMode and m_Infos.m_InstancingCapacity" },
                controlMode = "Auto",
                controlModeValue = 0,
                controlInstancingCapacity = 64,
                customMode = "Custom",
                customModeValue = 1,
                customInstancingCapacity = CustomCapacity,
                sceneSaveCalled = false,
                runtimeActivation = false,
                nativeMeasurement = false
            };
            try
            {
                evidencePath = Environment.GetEnvironmentVariable(EvidenceEnvironment);
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidencePath), EvidenceEnvironment + " was not provided.");
                evidencePath = Path.GetFullPath(evidencePath);
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string testResultsRoot = Path.GetFullPath(Path.Combine(projectRoot, "TestResults")) + Path.DirectorySeparatorChar;
                Assert.IsTrue(evidencePath.StartsWith(testResultsRoot, StringComparison.OrdinalIgnoreCase), "Evidence path must remain in the clone TestResults folder.");
                Assert.IsFalse(File.Exists(evidencePath), "Refusing to overwrite prior evidence.");
                Assert.IsFalse(Application.isPlaying, "This fixture is EditMode-only.");
                Assert.AreEqual(UnityEngine.Rendering.GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType, "A Null graphics device is required.");

                sourceGraphAbsolute = ToAbsolute(SourceGraphPath);
                sourceVariantAbsolute = ToAbsolute(SourceVariantPath);
                sourceSceneAbsolute = ToAbsolute(SourceScenePath);
                sourceGraphHashBefore = Sha256(sourceGraphAbsolute);
                sourceGraphMetaHashBefore = Sha256(sourceGraphAbsolute + ".meta");
                sourceVariantHashBefore = Sha256(sourceVariantAbsolute);
                sourceVariantMetaHashBefore = Sha256(sourceVariantAbsolute + ".meta");
                sourceSceneHashBefore = Sha256(sourceSceneAbsolute);
                sourceGraphGuidBefore = AssetDatabase.AssetPathToGUID(SourceGraphPath);
                sourceVariantGuidBefore = AssetDatabase.AssetPathToGUID(SourceVariantPath);
                sceneFingerprintBefore = SceneFilesFingerprint();
                result.sourceGraphGuid = sourceGraphGuidBefore;
                result.sourceGraphSha256Before = sourceGraphHashBefore;
                result.sourceGraphMetaSha256Before = sourceGraphMetaHashBefore;
                result.sourceVariantGuid = sourceVariantGuidBefore;
                result.sourceVariantSha256Before = sourceVariantHashBefore;
                result.sourceVariantMetaSha256Before = sourceVariantMetaHashBefore;
                result.sourceSceneSha256Before = sourceSceneHashBefore;
                result.openSceneFingerprintBefore = sceneFingerprintBefore;

                Assert.AreEqual(SourceGraphGuid, sourceGraphGuidBefore, "SampleSkinnedMesh source graph GUID changed.");
                Assert.AreEqual(SourceGraphSha256, sourceGraphHashBefore, "SampleSkinnedMesh source graph bytes changed.");
                Assert.AreEqual(SourceGraphMetaSha256, sourceGraphMetaHashBefore, "SampleSkinnedMesh source graph meta changed.");
                Assert.AreEqual(SourceVariantGuid, sourceVariantGuidBefore, "Sample Skinned Mesh variant GUID changed.");
                Assert.AreEqual(SourceVariantSha256, sourceVariantHashBefore, "Sample Skinned Mesh variant bytes changed.");
                Assert.AreEqual(SourceVariantMetaSha256, sourceVariantMetaHashBefore, "Sample Skinned Mesh variant meta changed.");
                Assert.AreEqual(CellPrefabGuid, ReadMetaGuid(ToAbsolute("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/Cell_01.prefab.meta")), "Cell_01 base GUID changed.");
                Assert.AreEqual(CellPrefabSha256, Sha256(ToAbsolute("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/Cell_01.prefab")), "Cell_01 base bytes changed.");
                Assert.AreEqual(CellPrefabMetaSha256, Sha256(ToAbsolute("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/Cell_01.prefab.meta")), "Cell_01 base meta bytes changed.");
                Assert.AreEqual(OriginalSceneSha256, sourceSceneHashBefore, "Prepared source scene bytes changed.");
                ValidateSourceVariantReferences();
                Assert.IsFalse(AssetDatabase.IsValidFolder(FixtureRoot), "Refusing to reuse existing fixture assets.");

                UnityEditor.PackageManager.PackageInfo[] packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(value => value != null && value.name == "com.unity.visualeffectgraph").ToArray();
                Assert.AreEqual(1, packages.Length, "Registered VFX Graph package provenance is unavailable or ambiguous.");
                Assert.AreEqual("17.6.0", packages[0].version, "Unexpected VFX Graph version.");
                result.unityVersion = Application.unityVersion;
                result.graphicsDevice = SystemInfo.graphicsDeviceType.ToString();
                result.vfxGraphVersion = packages[0].version;
                result.vfxGraphResolvedPath = packages[0].resolvedPath ?? "";

                loadedCameras = Resources.FindObjectsOfTypeAll<Camera>().Where(IsLoadedSceneObject).ToArray();
                priorCameraEnabled = loadedCameras.Select(camera => camera.enabled).ToArray();
                result.loadedCameraCount = loadedCameras.Length;
                result.loadedCameraEnabledAtLoad = priorCameraEnabled;
                foreach (Camera camera in loadedCameras)
                    if (camera.enabled && camera.gameObject.activeInHierarchy) camera.enabled = false;
                Assert.AreEqual(0, CountActiveSceneCameras(), "A scene camera remained active during the Null fixture.");
                Assert.AreEqual(0, CountActiveSceneVfx(), "A scene VisualEffect remained active during the Null fixture.");
                result.activeCameraCountDuringProbe = CountActiveSceneCameras();
                result.activeVfxCountDuringProbe = CountActiveSceneVfx();

                GraphInfo sourceInfo = ReadGraphInfo(SourceGraphPath);
                Assert.AreEqual(1, sourceInfo.particleSystemCount, "Expected exactly one particle system in the source graph.");
                Assert.AreEqual(ParticleCapacity, sourceInfo.capacity, "Original sample capacity changed.");
                Assert.AreEqual(0, sourceInfo.mode, "Original sample graph is not Auto mode.");
                Assert.AreEqual(64, sourceInfo.instancingCapacity, "Original sample instancing capacity changed.");
                result.sourceParticleDataName = sourceInfo.particleDataName;
                result.sourceGraphMotionParameter = sourceInfo.motionParameter;
                result.sourceGraphMotionVectorEnabledFieldCount = sourceInfo.motionVectorEnabledFieldCount;
                result.sourceUrpProjectionSha256 = sourceInfo.urpProjectionSha256;
                result.sourceUrpProjectionRows = sourceInfo.urpProjectionRows;
                result.sourceRawDocumentIds = sourceInfo.rawDocumentIds;
                result.sourceDescriptors = ReadCompiledDescriptors(SourceGraphPath);
                AssertDescriptorShape(result.sourceDescriptors, "Source");
                result.sourceStructureSha256 = ComparableStructureHash(SourceGraphPath, sourceInfo.mode, sourceInfo.instancingCapacity);

                EnsureFolder(FixtureRoot);
                EnsureFolder(ControlFolder);
                EnsureFolder(CustomFolder);
                Assert.IsTrue(AssetDatabase.CopyAsset(SourceGraphPath, ControlPath), "Could not copy source graph into Control fixture.");
                AssetDatabase.ImportAsset(ControlPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                WriteResource(ControlPath);
                AssetDatabase.ImportAsset(ControlPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                controlGuidBefore = AssetDatabase.AssetPathToGUID(ControlPath);
                controlHashBefore = Sha256(ToAbsolute(ControlPath));
                result.controlGuid = controlGuidBefore;
                result.controlSha256Before = controlHashBefore;
                GraphInfo controlInfo = ReadGraphInfo(ControlPath);
                AssertGraphMatchesSource(sourceInfo, controlInfo, "Control");
                AssertRawUrpProjectionDelta(sourceInfo, controlInfo, "Control");
                Assert.AreEqual(sourceInfo.urpProjectionSha256, controlInfo.urpProjectionSha256, "Control URP-loaded VFX model projection differs from the source graph.");
                result.controlUrpProjectionSha256 = controlInfo.urpProjectionSha256;
                result.controlUrpProjectionRows = controlInfo.urpProjectionRows;
                result.controlRawDocumentIds = controlInfo.rawDocumentIds;
                result.controlRawDroppedDocumentIds = controlInfo.rawDroppedDocumentIds;
                result.controlRawAddedDocumentIds = controlInfo.rawAddedDocumentIds;
                result.controlRawChangedCommonDocumentIds = controlInfo.rawChangedCommonDocumentIds;
                ReadModeAndCapacity(ControlPath, out int controlMode, out string controlModeName, out int controlCapacity);
                Assert.AreEqual(0, controlMode, "Control clone is not Auto mode.");
                Assert.AreEqual("Auto", controlModeName, "Unexpected Control mode enum value.");
                Assert.AreEqual(64, controlCapacity, "Control instancing capacity changed.");
                result.controlModeValue = controlMode;
                result.controlMode = controlModeName;
                result.controlInstancingCapacity = controlCapacity;
                result.controlParticleDataName = controlInfo.particleDataName;
                result.controlDescriptors = ReadCompiledDescriptors(ControlPath);
                AssertDescriptorShape(result.controlDescriptors, "Control");
                AssertDescriptorSemanticsEqual(result.sourceDescriptors, result.controlDescriptors, "Source to Control");
                result.controlStructureSha256 = ComparableStructureHash(ControlPath, controlMode, controlCapacity);

                Assert.IsTrue(AssetDatabase.CopyAsset(ControlPath, CustomPath), "Could not copy Control graph into Custom fixture.");
                AssetDatabase.ImportAsset(CustomPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                customGuidBefore = AssetDatabase.AssetPathToGUID(CustomPath);
                Assert.IsNotEmpty(customGuidBefore, "Custom fixture graph GUID is missing.");
                ReadModeAndCapacity(CustomPath, out int copiedMode, out _, out int copiedCapacity);
                Assert.AreEqual(controlMode, copiedMode, "Custom clone did not start with the exact saved Control mode.");
                Assert.AreEqual(controlCapacity, copiedCapacity, "Custom clone did not start with the exact saved Control capacity.");
                Assert.AreEqual(result.controlStructureSha256, ComparableStructureHash(CustomPath, copiedMode, copiedCapacity), "Copied graph differs from Control beyond generated identity.");

                int customMode = ResolveEnumValue("Custom");
                SetModeAndCapacity(CustomPath, customMode, CustomCapacity);
                AssetDatabase.ImportAsset(CustomPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                ReadModeAndCapacity(CustomPath, out int actualMode, out string actualModeName, out int actualCapacity);
                Assert.AreEqual(1, actualMode, "Custom mode was not saved as enum value 1.");
                Assert.AreEqual("Custom", actualModeName, "Custom enum name did not persist.");
                Assert.AreEqual(CustomCapacity, actualCapacity, "Custom capacity is not one.");
                GraphInfo customInfo = ReadGraphInfo(CustomPath);
                AssertGraphMatchesSource(sourceInfo, customInfo, "Custom");
                AssertRawUrpProjectionDelta(sourceInfo, customInfo, "Custom");
                Assert.AreEqual(sourceInfo.urpProjectionSha256, customInfo.urpProjectionSha256, "Custom URP-loaded VFX model projection differs from the source graph.");
                result.customUrpProjectionSha256 = customInfo.urpProjectionSha256;
                result.customUrpProjectionRows = customInfo.urpProjectionRows;
                result.customRawDocumentIds = customInfo.rawDocumentIds;
                result.customRawDroppedDocumentIds = customInfo.rawDroppedDocumentIds;
                result.customRawAddedDocumentIds = customInfo.rawAddedDocumentIds;
                result.customRawChangedCommonDocumentIds = customInfo.rawChangedCommonDocumentIds;
                result.customModeValue = actualMode;
                result.customMode = actualModeName;
                result.customInstancingCapacity = actualCapacity;
                result.customParticleDataName = customInfo.particleDataName;
                result.customSha256Before = Sha256(ToAbsolute(CustomPath));
                result.customDescriptors = ReadCompiledDescriptors(CustomPath);
                AssertDescriptorShape(result.customDescriptors, "Custom");
                AssertDescriptorSemanticsEqual(result.controlDescriptors, result.customDescriptors, "Control to Custom");
                ReadDisabledReason(CustomPath, out string reasonName, out int reasonValue, out int noneValue, out string reasonEnumType);
                result.customDisabledReasonName = reasonName;
                result.customDisabledReasonValue = reasonValue;
                result.customDisabledReasonNoneValue = noneValue;
                result.customDisabledReasonEnumType = reasonEnumType;
                Assert.AreEqual("None", reasonName, "Custom1 is unsupported by VFX compiler.");
                Assert.AreEqual(noneValue, reasonValue, "Custom1 disabled reason is not None.");
                result.customStructureSha256 = ComparableStructureHash(CustomPath, actualMode, actualCapacity);
                Assert.AreEqual(result.controlStructureSha256, result.customStructureSha256, "Control vs Custom changed data outside m_InstancingMode and m_InstancingCapacity.");
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
                for (int i = 0; i < loadedCameras.Length; i++)
                    if (loadedCameras[i] != null && i < priorCameraEnabled.Length) loadedCameras[i].enabled = priorCameraEnabled[i];
                try
                {
                    result.sourceGraphGuidAfter = AssetDatabase.AssetPathToGUID(SourceGraphPath);
                    result.sourceGraphSha256After = File.Exists(sourceGraphAbsolute) ? Sha256(sourceGraphAbsolute) : "";
                    result.sourceGraphMetaSha256After = File.Exists(sourceGraphAbsolute + ".meta") ? Sha256(sourceGraphAbsolute + ".meta") : "";
                    result.sourceVariantGuidAfter = AssetDatabase.AssetPathToGUID(SourceVariantPath);
                    result.sourceVariantSha256After = File.Exists(sourceVariantAbsolute) ? Sha256(sourceVariantAbsolute) : "";
                    result.sourceVariantMetaSha256After = File.Exists(sourceVariantAbsolute + ".meta") ? Sha256(sourceVariantAbsolute + ".meta") : "";
                    result.sourceSceneSha256After = File.Exists(sourceSceneAbsolute) ? Sha256(sourceSceneAbsolute) : "";
                    result.openSceneFingerprintAfter = SceneFilesFingerprint();
                    result.sourceAssetsUnchanged = SourceAssetsUnchanged();
                    result.openSceneFilesUnchanged = sceneFingerprintBefore == result.openSceneFingerprintAfter;
                }
                catch (Exception exception) { result.sourceAssetsUnchanged = false; result.integrityCheckError = exception.GetType().Name; }
                try
                {
                    if (!string.IsNullOrEmpty(controlGuidBefore))
                    {
                        result.controlGuidAfter = AssetDatabase.AssetPathToGUID(ControlPath);
                        result.controlSha256After = File.Exists(ToAbsolute(ControlPath)) ? Sha256(ToAbsolute(ControlPath)) : "";
                        result.controlUnchangedAfterCreation = result.controlGuidAfter == controlGuidBefore && result.controlSha256After == controlHashBefore;
                    }
                    if (!string.IsNullOrEmpty(customGuidBefore))
                    {
                        result.customGuidAfter = AssetDatabase.AssetPathToGUID(CustomPath);
                        result.customSha256After = File.Exists(ToAbsolute(CustomPath)) ? Sha256(ToAbsolute(CustomPath)) : "";
                        result.customAssetPresent = result.customGuidAfter == customGuidBefore && !string.IsNullOrEmpty(result.customSha256After);
                    }
                }
                catch (Exception exception) { result.fixtureAfterCheckError = exception.GetType().Name; }
                result.activeCameraCountAfterRestore = CountActiveSceneCameras();
                result.activeVfxCountAfterRestore = CountActiveSceneVfx();
                result.finalIntegrityGate = result.sourceAssetsUnchanged && result.openSceneFilesUnchanged && result.controlUnchangedAfterCreation && result.customAssetPresent;
                if (result.status == "success" && !result.finalIntegrityGate) result.status = "failed-final-integrity-gate";
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

        static bool IsLoadedSceneObject(Camera camera) => camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded;
        static bool IsLoadedSceneObject(VisualEffect effect) => effect != null && effect.gameObject.scene.IsValid() && effect.gameObject.scene.isLoaded;
        static int CountActiveSceneCameras() => Resources.FindObjectsOfTypeAll<Camera>().Count(camera => IsLoadedSceneObject(camera) && camera.enabled && camera.gameObject.activeInHierarchy);
        static int CountActiveSceneVfx() => Resources.FindObjectsOfTypeAll<VisualEffect>().Count(effect => IsLoadedSceneObject(effect) && effect.gameObject.activeInHierarchy);

        void ValidateSourceVariantReferences()
        {
            string text = File.ReadAllText(ToAbsolute(SourceVariantPath));
            Assert.AreEqual(1, Regex.Matches(text, @"(?m)^      propertyPath: m_Asset$").Count, "Expected one nested graph reference override.");
            Assert.IsTrue(text.Contains("guid: " + CellPrefabGuid), "Source variant does not point to expected Cell_01 base.");
            Assert.IsTrue(text.Contains("objectReference: {fileID: 8926484042661614526, guid: " + SourceGraphGuid), "Source variant graph reference identity changed.");
            Assert.IsTrue(text.Contains("propertyPath: m_PropertySheet.m_NamedObject.m_Array.Array.data[0].m_Name\n      value: SkinnedMeshRenderer"), "Source variant named property identity changed.");
            Assert.IsTrue(text.Contains("objectReference: {fileID: 8829781623851436467}"), "Source variant does not bind the expected nested SkinnedMeshRenderer instance.");
            long[] tmpFileIds = { 3385902144265741256L, 3796978554488964388L, 6393118192857248175L, 6424475011069275086L };
            result.showcaseTmpOverrideFileIds = tmpFileIds.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
            foreach (long fileId in tmpFileIds)
                Assert.IsTrue(Regex.IsMatch(text, @"(?s)target:\s*\{fileID:\s*" + fileId.ToString(CultureInfo.InvariantCulture) + @",\s*guid:\s*" + CellPrefabGuid + @",\s*type:\s*3\}\s*propertyPath:\s*m_text"), "Showcase TMP source override is missing for fileID " + fileId.ToString(CultureInfo.InvariantCulture));
        }

        GraphInfo ReadGraphInfo(string assetPath)
        {
            string text = File.ReadAllText(ToAbsolute(assetPath));
            Match[] capacities = Regex.Matches(text, @"(?m)^  capacity: (\d+)$").Cast<Match>().ToArray();
            Assert.AreEqual(1, capacities.Length, "Expected exactly one serialized particle capacity.");
            Assert.AreEqual(1, Regex.Matches(text, @"(?m)^    m_InstancingMode: -?\d+$").Count, "Expected exactly one serialized instancing mode.");
            Assert.AreEqual(1, Regex.Matches(text, @"(?m)^    m_InstancingCapacity: -?\d+$").Count, "Expected exactly one serialized instancing capacity.");
            Assert.IsTrue(text.Contains("m_ExposedName: SkinnedMeshRenderer"), "SkinnedMeshRenderer graph parameter is missing.");
            Assert.IsTrue(text.Contains("m_SerializableType: UnityEngine.SkinnedMeshRenderer, UnityEngine.CoreModule"), "Graph parameter no longer declares SkinnedMeshRenderer.");
            ReadModeAndCapacity(assetPath, out int mode, out _, out int instancingCapacity);
            ParticleDataInfo particle = FindParticleData(assetPath);
            Dictionary<string, string> rawDocuments = ReadYamlDocuments(text);
            string[] projectionRows = ReadUrpLoadedProjection(assetPath);
            string urpOutputDocument = ReadYamlDocument(text, 8926484042661616954L);
            Assert.AreEqual(1, Regex.Matches(urpOutputDocument, @"(?m)^  generateMotionVector: 1$").Count, "The surviving URP planar output must keep generateMotionVector enabled.");
            return new GraphInfo
            {
                particleSystemCount = particle.matches,
                particleDataName = particle.name,
                capacity = particle.capacity,
                mode = mode,
                instancingCapacity = instancingCapacity,
                motionParameter = text.Contains("m_ExposedName: SkinnedMeshRenderer") ? "SkinnedMeshRenderer" : "",
                motionVectorEnabledFieldCount = Regex.Matches(text, @"(?m)^\s*generateMotionVector:\s*1\s*$").Count,
                urpProjectionRows = projectionRows,
                urpProjectionSha256 = HashText(string.Join("\n", projectionRows)),
                rawDocuments = rawDocuments,
                rawDocumentIds = rawDocuments.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            };
        }

        static void AssertGraphMatchesSource(GraphInfo source, GraphInfo candidate, string label)
        {
            Assert.AreEqual(source.particleSystemCount, candidate.particleSystemCount, label + " particle system count changed.");
            Assert.AreEqual(1, candidate.particleSystemCount, label + " must contain one particle system.");
            Assert.AreEqual(source.capacity, candidate.capacity, label + " particle capacity changed.");
            Assert.AreEqual(ParticleCapacity, candidate.capacity, label + " capacity is not 30000.");
            Assert.AreEqual(source.particleDataName, candidate.particleDataName, label + " particle data identity changed.");
            Assert.AreEqual(source.motionParameter, candidate.motionParameter, label + " SkinnedMeshRenderer input name changed.");
            Assert.AreEqual(1, candidate.motionVectorEnabledFieldCount, label + " must retain only the reachable URP motion-vector output; raw HDRP output removal is not treated as a runtime equivalence pass.");
        }

        static void AssertRawUrpProjectionDelta(GraphInfo source, GraphInfo candidate, string label)
        {
            HashSet<string> sourceIds = new HashSet<string>(source.rawDocuments.Keys, StringComparer.Ordinal);
            HashSet<string> candidateIds = new HashSet<string>(candidate.rawDocuments.Keys, StringComparer.Ordinal);
            candidate.rawDroppedDocumentIds = sourceIds.Except(candidateIds).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            candidate.rawAddedDocumentIds = candidateIds.Except(sourceIds).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            candidate.rawChangedCommonDocumentIds = sourceIds.Intersect(candidateIds)
                .Where(id => CanonicalizeSerializableType(source.rawDocuments[id]) != CanonicalizeSerializableType(candidate.rawDocuments[id]))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEquivalent(ExpectedHdrpClosureIds, candidate.rawDroppedDocumentIds, label + " removed documents outside the exact unsupported HDRP output closure.");
            CollectionAssert.AreEqual(new[] { ExpectedUrpSubOutputId }, candidate.rawAddedDocumentIds, label + " added an unexpected serialized model outside the single generated URP suboutput.");
            string[] expectedChanged = source.mode == candidate.mode && source.instancingCapacity == candidate.instancingCapacity
                ? ExpectedChangedCommonDocumentIds
                : ExpectedChangedCommonDocumentIds.Concat(new[] { "8926484042661614527" }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEquivalent(expectedChanged, candidate.rawChangedCommonDocumentIds, label + " changed unexpected common serialized documents.");
        }

        static string CanonicalizeSerializableType(string body)
        {
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            List<string> canonical = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match field = Regex.Match(line, @"^(?<prefix>\s*(?:-\s*)?m_SerializableType:\s*)(?<value>.*)$");
                if (!field.Success) { canonical.Add(line); continue; }
                StringBuilder joined = new StringBuilder(field.Groups["prefix"].Value).Append(field.Groups["value"].Value.TrimEnd());
                while (i + 1 < lines.Length)
                {
                    string continuation = lines[i + 1].Trim();
                    if (!Regex.IsMatch(continuation, @"^(Version|Culture|PublicKeyToken)=", RegexOptions.CultureInvariant)) break;
                    joined.Append(' ').Append(continuation);
                    i++;
                }
                canonical.Add(joined.ToString());
            }
            return string.Join("\n", canonical);
        }

        static Dictionary<string, string> ReadYamlDocuments(string text)
        {
            Dictionary<string, string> documents = new Dictionary<string, string>(StringComparer.Ordinal);
            MatchCollection matches = Regex.Matches(text, @"(?ms)^--- !u!\d+ &(?<id>\d+)\s*\r?\n(?<body>.*?)(?=^--- !u!|\z)");
            foreach (Match match in matches)
                Assert.IsTrue(documents.TryAdd(match.Groups["id"].Value, match.Groups["body"].Value), "Duplicate serialized graph document ID.");
            Assert.IsTrue(documents.Count > 0, "No serialized graph documents were found.");
            return documents;
        }

        string[] ReadUrpLoadedProjection(string assetPath)
        {
            object graph = GetGraph(assetPath);
            HashSet<ScriptableObject> dependencies = CollectDependencies(graph);
            string graphGuid = AssetDatabase.AssetPathToGUID(assetPath);
            ScriptableObject urpOutput = dependencies.SingleOrDefault(value => value != null && TryGetLocalId(value, graphGuid, out long id) && id == 8926484042661616954L);
            Assert.IsNotNull(urpOutput, "URP-loaded graph must contain exactly one expected URP planar output.");
            PropertyInfo subOutputProperty = urpOutput.GetType().GetProperty("subOutput", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(subOutputProperty, "URP planar output subOutput property is unavailable.");
            UnityEngine.Object generatedSubOutput = subOutputProperty.GetValue(urpOutput, null) as UnityEngine.Object;
            Assert.IsNotNull(generatedSubOutput, "URP planar output did not resolve its active SRP suboutput.");
            Assert.AreEqual("UnityEditor.VFX.VFXURPSubOutput", generatedSubOutput.GetType().FullName, "URP planar output resolved an unexpected SRP suboutput type.");
            PropertyInfo ownerProperty = generatedSubOutput.GetType().GetProperty("owner", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(ownerProperty, "URP suboutput owner property is unavailable.");
            Assert.AreSame(urpOutput, ownerProperty.GetValue(generatedSubOutput, null), "URP suboutput does not belong to the expected planar output.");
            MethodInfo getSubOutputs = urpOutput.GetType().GetMethod("GetSubOutputs", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(getSubOutputs, "URP output suboutput list API is unavailable.");
            object subOutputList = getSubOutputs.Invoke(urpOutput, null);
            IEnumerable<UnityEngine.Object> listedSubOutputs = (subOutputList as System.Collections.IEnumerable)?.Cast<object>().OfType<UnityEngine.Object>();
            Assert.IsNotNull(listedSubOutputs, "URP output suboutput list could not be enumerated.");
            Assert.AreEqual(1, listedSubOutputs.Count(value => value != null && value.GetType() == generatedSubOutput.GetType()), "URP output must have one unique active suboutput of the expected type.");
            bool hasPersistentSubOutputId = TryGetLocalId(generatedSubOutput, graphGuid, out long subOutputLocalId);
            if (!hasPersistentSubOutputId)
            {
                Assert.AreEqual(SourceGraphPath, assetPath, "Only the source graph may provide a nonpersistent generated URP suboutput.");
                Assert.IsFalse(EditorUtility.IsPersistent(generatedSubOutput), "A persistent source suboutput must have a graph-local file ID.");
                subOutputLocalId = 0;
            }
            resultUrpSubOutputIdentity(assetPath, subOutputLocalId, generatedSubOutput.GetType().FullName);

            Dictionary<UnityEngine.Object, string> stableObjectKeys = new Dictionary<UnityEngine.Object, string>(UnityObjectReferenceComparer.Instance);
            Dictionary<long, string> stableLocalKeys = new Dictionary<long, string>();
            foreach (ScriptableObject model in dependencies.Where(value => value != null))
            {
                if (IsSubOutputModel(model.GetType()))
                {
                    PropertyInfo modelOwner = model.GetType().GetProperty("owner", BindingFlags.Public | BindingFlags.Instance);
                    UnityEngine.Object owner = modelOwner?.GetValue(model, null) as UnityEngine.Object;
                    Assert.IsNotNull(owner, "Persistent URP suboutput owner is missing.");
                    Assert.IsTrue(TryGetLocalId(owner, graphGuid, out long ownerLocalId), "Persistent URP suboutput owner identity is unavailable.");
                    string stableKey = "suboutput-owner:" + ownerLocalId.ToString(CultureInfo.InvariantCulture) + "|" + model.GetType().FullName;
                    stableObjectKeys.Add(model, stableKey);
                    bool persistent = TryGetLocalId(model, graphGuid, out long persistentSubOutputId);
                    if (persistent)
                    {
                        Assert.IsTrue(persistentSubOutputId != 0, "Serialized URP suboutput must have a nonzero local file ID.");
                        stableLocalKeys[persistentSubOutputId] = stableKey;
                    }
                    else
                    {
                        Assert.AreEqual(SourceGraphPath, assetPath, "Only the source graph may contain a generated nonpersistent URP suboutput model.");
                        Assert.IsFalse(EditorUtility.IsPersistent(model), "An unidentifiable persistent suboutput is not allowed.");
                        Assert.AreSame(generatedSubOutput, model, "An unknown nonpersistent suboutput model was found.");
                    }
                }
                else
                {
                    Assert.IsTrue(TryGetLocalId(model, graphGuid, out long localId), "Could not identify a reachable VFX model subasset.");
                    stableObjectKeys.Add(model, "model:" + localId.ToString(CultureInfo.InvariantCulture) + "|" + model.GetType().FullName);
                    stableLocalKeys[localId] = "model:" + localId.ToString(CultureInfo.InvariantCulture) + "|" + model.GetType().FullName;
                }
            }
            if (!stableObjectKeys.ContainsKey(generatedSubOutput))
            {
                Assert.IsTrue(subOutputLocalId == 0, "Only a nonpersistent URP generated suboutput may be absent from dependency identities.");
                stableObjectKeys.Add(generatedSubOutput, "suboutput-owner:8926484042661616954|" + generatedSubOutput.GetType().FullName);
            }
            if (assetPath == ControlPath || assetPath == CustomPath)
                Assert.AreEqual(8926484042661617230L, subOutputLocalId, "Serialized URP suboutput local file ID changed.");
            else
            {
                Assert.AreEqual(0L, subOutputLocalId, "Source graph should resolve the URP generated suboutput in memory without a serialized local file ID.");
                Assert.IsFalse(EditorUtility.IsPersistent(generatedSubOutput), "Source suboutput unexpectedly became a persistent serialized model.");
            }
            List<string> rows = new List<string>();
            List<ScriptableObject> projectionModels = dependencies.Where(value => value != null).ToList();
            if (!projectionModels.Contains(generatedSubOutput as ScriptableObject)) projectionModels.Add(generatedSubOutput as ScriptableObject);
            foreach (ScriptableObject model in projectionModels)
            {
                Assert.IsTrue(stableObjectKeys.TryGetValue(model, out string stableKey), "Reachable VFX model is missing a stable projection identity.");
                rows.Add("model|" + stableKey);

                SerializedObject serialized = new SerializedObject(model);
                SerializedProperty iterator = serialized.GetIterator();
                bool enterChildren = true;
                while (iterator.Next(enterChildren))
                {
                    enterChildren = true;
                    if (iterator.propertyPath == "m_UIInfos" || iterator.propertyPath.StartsWith("m_UIInfos.", StringComparison.Ordinal))
                    {
                        enterChildren = false;
                        continue;
                    }
                    if (iterator.propertyType == SerializedPropertyType.Generic)
                        continue;
                    bool normalizeInstancing = iterator.propertyPath == "m_Infos.m_InstancingMode" || iterator.propertyPath == "m_Infos.m_InstancingCapacity";
                    string value = normalizeInstancing ? "normalized-instancing-setting" : CanonicalPropertyValue(iterator, graphGuid, stableObjectKeys, stableLocalKeys);
                    rows.Add("property|" + stableKey + "|" + iterator.propertyPath + "|" + iterator.propertyType + "|" + value);
                }
            }
            rows.Sort(StringComparer.Ordinal);
            if (assetPath == SourceGraphPath) result.sourceUrpProjectionRows = rows.ToArray();
            else if (assetPath == ControlPath) result.controlUrpProjectionRows = rows.ToArray();
            else if (assetPath == CustomPath) result.customUrpProjectionRows = rows.ToArray();
            Assert.IsTrue(rows.Any(value => value.StartsWith("model|model:8926484042661616954|", StringComparison.Ordinal)), "URP-loaded projection lacks the expected planar output model.");
            Assert.IsTrue(rows.Any(value => value.StartsWith("model|model:8926484042661614568|", StringComparison.Ordinal)), "URP-loaded projection lacks the shared particle data model.");
            Assert.IsTrue(rows.Any(value => value.StartsWith("model|model:8926484042661617028|", StringComparison.Ordinal)), "URP-loaded projection lacks the shared update context.");
            return rows.ToArray();
        }

        static bool IsSubOutputModel(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
                if (current.FullName == "UnityEditor.VFX.VFXSRPSubOutput") return true;
            return false;
        }

        static string CanonicalPropertyValue(SerializedProperty property, string graphGuid, Dictionary<UnityEngine.Object, string> stableObjectKeys, Dictionary<long, string> stableLocalKeys)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    UnityEngine.Object reference = property.objectReferenceValue;
                    if (reference == null) return "null";
                    if (stableObjectKeys.TryGetValue(reference, out string stableReference)) return "local:" + stableReference;
                    if (!EditorUtility.IsPersistent(reference) && IsSubOutputModel(reference.GetType()))
                    {
                        PropertyInfo ownerProperty = reference.GetType().GetProperty("owner", BindingFlags.Public | BindingFlags.Instance);
                        UnityEngine.Object owner = ownerProperty?.GetValue(reference, null) as UnityEngine.Object;
                        Assert.IsNotNull(owner, "Nonpersistent URP suboutput reference has no owner.");
                        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(owner, out string ownerGuid, out long ownerId) && string.Equals(ownerGuid, graphGuid, StringComparison.OrdinalIgnoreCase), "Nonpersistent URP suboutput owner is not in the current graph.");
                        Assert.AreEqual(8926484042661616954L, ownerId, "Nonpersistent suboutput belongs to an unexpected output model.");
                        return "local:suboutput-owner:" + ownerId.ToString(CultureInfo.InvariantCulture) + "|" + reference.GetType().FullName;
                    }
                    Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reference, out string guid, out long fileId), "Could not identify a VFX projection object reference.");
                    return string.Equals(guid, graphGuid, StringComparison.OrdinalIgnoreCase)
                        ? "local:" + (stableLocalKeys.TryGetValue(fileId, out string localReference) ? localReference : fileId.ToString(CultureInfo.InvariantCulture))
                        : "external:" + guid + ":" + fileId.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue ?? "";
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Integer: return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float: return property.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Color: return property.colorValue.ToString("R");
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString("R");
                case SerializedPropertyType.Vector2Int: return property.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int: return property.vector3IntValue.ToString();
                case SerializedPropertyType.Quaternion: return JoinInvariant(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w);
                case SerializedPropertyType.Rect: return JoinInvariant(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                case SerializedPropertyType.RectInt: return property.rectIntValue.ToString();
                case SerializedPropertyType.Bounds: return JoinInvariant(property.boundsValue.center.x, property.boundsValue.center.y, property.boundsValue.center.z, property.boundsValue.extents.x, property.boundsValue.extents.y, property.boundsValue.extents.z);
                case SerializedPropertyType.BoundsInt: return property.boundsIntValue.ToString();
                case SerializedPropertyType.ArraySize: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.AnimationCurve:
                    AnimationCurve curve = property.animationCurveValue;
                    return curve.preWrapMode + "|" + curve.postWrapMode + "|" + string.Join(";", curve.keys.Select(key => JoinInvariant(key.time, key.value, key.inTangent, key.outTangent, key.inWeight, key.outWeight) + "," + key.weightedMode));
                case SerializedPropertyType.Gradient:
                    Gradient gradient = property.gradientValue;
                    string colorKeys = string.Join(";", gradient.colorKeys.Select(key => key.time.ToString("R", CultureInfo.InvariantCulture) + "," + CanonicalColor(key.color)));
                    string alphaKeys = string.Join(";", gradient.alphaKeys.Select(key => key.time.ToString("R", CultureInfo.InvariantCulture) + "," + key.alpha.ToString("R", CultureInfo.InvariantCulture)));
                    return gradient.mode + "|" + colorKeys + "|" + alphaKeys;
                default:
                    Assert.Fail("Unsupported serialized property type in URP projection: " + property.propertyType + " at " + property.propertyPath);
                    return "";
            }
        }

        static string JoinInvariant(params float[] values) => string.Join(",", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        static string CanonicalColor(Color color) => JoinInvariant(color.r, color.g, color.b, color.a);

        static bool TryGetLocalId(UnityEngine.Object value, string expectedGuid, out long localId)
        {
            localId = 0;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out localId)) return false;
            return string.Equals(guid, expectedGuid, StringComparison.OrdinalIgnoreCase);
        }

        void resultUrpSubOutputIdentity(string assetPath, long localId, string typeName)
        {
            if (assetPath == SourceGraphPath)
            {
                result.sourceUrpSubOutputLocalFileId = localId;
                result.sourceUrpSubOutputType = typeName;
            }
            else if (assetPath == ControlPath)
            {
                result.controlUrpSubOutputLocalFileId = localId;
                result.controlUrpSubOutputType = typeName;
            }
            else if (assetPath == CustomPath)
            {
                result.customUrpSubOutputLocalFileId = localId;
                result.customUrpSubOutputType = typeName;
            }
        }

        sealed class UnityObjectReferenceComparer : IEqualityComparer<UnityEngine.Object>
        {
            public static readonly UnityObjectReferenceComparer Instance = new UnityObjectReferenceComparer();
            public bool Equals(UnityEngine.Object left, UnityEngine.Object right) => ReferenceEquals(left, right);
            public int GetHashCode(UnityEngine.Object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }

        static string ReadYamlDocument(string text, long fileId)
        {
            Match match = Regex.Match(text, @"(?ms)^--- !u!\d+ &" + fileId.ToString(CultureInfo.InvariantCulture) + @"\s*\r?\n(?<body>.*?)(?=^--- !u!|\z)");
            Assert.IsTrue(match.Success, "Expected serialized graph document is missing: " + fileId.ToString(CultureInfo.InvariantCulture));
            return match.Groups["body"].Value;
        }

        static ParticleDataInfo FindParticleData(string assetPath)
        {
            object graph = GetGraph(assetPath);
            HashSet<ScriptableObject> dependencies = CollectDependencies(graph);
            List<ParticleDataInfo> candidates = new List<ParticleDataInfo>();
            foreach (ScriptableObject value in dependencies)
            {
                if (value == null || value.GetType().Name != "VFXDataParticle") continue;
                SerializedProperty capacity = new SerializedObject(value).FindProperty("capacity");
                if (capacity != null && unchecked((uint)capacity.intValue) == ParticleCapacity)
                    candidates.Add(new ParticleDataInfo { name = value.name, capacity = unchecked((uint)capacity.intValue) });
            }
            return new ParticleDataInfo { matches = candidates.Count, name = candidates.Count == 1 ? candidates[0].name : "", capacity = candidates.Count == 1 ? candidates[0].capacity : 0 };
        }

        static void SetModeAndCapacity(string assetPath, int modeValue, int capacityValue)
        {
            var serialized = new SerializedObject(GetResource(assetPath));
            SerializedProperty mode = serialized.FindProperty("m_Infos.m_InstancingMode");
            SerializedProperty capacity = serialized.FindProperty("m_Infos.m_InstancingCapacity");
            Assert.IsNotNull(mode, "VFX resource mode property is unavailable.");
            Assert.IsNotNull(capacity, "VFX resource capacity property is unavailable.");
            mode.intValue = modeValue;
            capacity.intValue = capacityValue;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(serialized.targetObject);
            WriteResource(assetPath);
        }

        static void ReadModeAndCapacity(string assetPath, out int modeValue, out string modeName, out int capacityValue)
        {
            var serialized = new SerializedObject(GetResource(assetPath));
            SerializedProperty mode = serialized.FindProperty("m_Infos.m_InstancingMode");
            SerializedProperty capacity = serialized.FindProperty("m_Infos.m_InstancingCapacity");
            Assert.IsNotNull(mode, "VFX resource mode property is unavailable.");
            Assert.IsNotNull(capacity, "VFX resource capacity property is unavailable.");
            modeValue = mode.intValue;
            capacityValue = capacity.intValue;
            modeName = Enum.GetName(ResolveInstancingEnumType(), modeValue);
            Assert.IsNotEmpty(modeName, "VFX instancing mode enum value is invalid.");
        }

        static int ResolveEnumValue(string name)
        {
            Type type = ResolveInstancingEnumType();
            Assert.IsTrue(Enum.IsDefined(type, name), "VFXInstancingMode does not define " + name + ".");
            return Convert.ToInt32(Enum.Parse(type, name, false), CultureInfo.InvariantCulture);
        }

        static Type ResolveInstancingEnumType()
        {
            Type[] types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).Where(type => type != null && type.Name == "VFXInstancingMode" && type.IsEnum).ToArray();
            Assert.AreEqual(1, types.Length, "VFXInstancingMode enum is unavailable or ambiguous.");
            return types[0];
        }

        static void ReadDisabledReason(string assetPath, out string name, out int rawValue, out int noneValue, out string enumName)
        {
            VisualEffectAsset asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
            Assert.IsNotNull(asset, "Custom VisualEffectAsset is unavailable.");
            SerializedProperty property = new SerializedObject(asset).FindProperty("m_Infos.m_InstancingDisabledReason");
            Assert.IsNotNull(property, "Compiled disabled-reason property is unavailable.");
            Type[] types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).Where(type => type != null && type.Name == "VFXInstancingDisabledReason" && type.IsEnum).ToArray();
            Assert.AreEqual(1, types.Length, "VFXInstancingDisabledReason enum is unavailable or ambiguous.");
            rawValue = property.intValue;
            noneValue = Convert.ToInt32(Enum.Parse(types[0], "None", false), CultureInfo.InvariantCulture);
            name = Enum.Format(types[0], rawValue, "F");
            enumName = types[0].FullName + "," + types[0].Assembly.GetName().Name;
        }

        static object GetGraph(string assetPath)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            Type extensions = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(type => type != null && type.Name == "VisualEffectResourceExtensions" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(extensions, "VFX resource extensions unavailable.");
            MethodInfo method = extensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(candidate => candidate.Name == "GetGraph" && candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(method, "VFXGraph GetGraph API unavailable.");
            object graph = method.Invoke(null, new[] { resource });
            Assert.IsNotNull(graph, "Could not read VFX graph resource.");
            return graph;
        }

        static HashSet<ScriptableObject> CollectDependencies(object graph)
        {
            MethodInfo method = graph.GetType().GetMethod("CollectDependencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(HashSet<ScriptableObject>), typeof(bool) }, null);
            Assert.IsNotNull(method, "VFXGraph.CollectDependencies API unavailable.");
            HashSet<ScriptableObject> result = new HashSet<ScriptableObject>();
            method.Invoke(graph, new object[] { result, false });
            return result;
        }

        static UnityEngine.Object GetResource(string assetPath)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(value => value != null && value.Name == "VisualEffectResource" && value.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(type, "UnityEditor.VFX.VisualEffectResource unavailable.");
            MethodInfo method = type.GetMethod("GetResourceAtPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(method, "GetResourceAtPath API unavailable.");
            UnityEngine.Object resource = method.Invoke(null, new object[] { assetPath }) as UnityEngine.Object;
            Assert.IsNotNull(resource, "Could not load VFX graph resource at " + assetPath);
            return resource;
        }

        static void WriteResource(string assetPath)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            Type extensions = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(type => type != null && type.Name == "VisualEffectResourceExtensions" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(extensions, "VFX resource extensions unavailable.");
            MethodInfo method = extensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(candidate => candidate.Name == "WriteAssetWithSubAssets" && candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(method, "VFX WriteAssetWithSubAssets API unavailable.");
            method.Invoke(null, new object[] { resource });
        }

        static DescriptorInfo ReadCompiledDescriptors(string assetPath)
        {
            Type inventory = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(type => type != null && type.FullName == "ViewWeave.SceneComparison.VfxDescriptorInventory");
            Assert.IsNotNull(inventory, "CPU VFX descriptor inventory unavailable.");
            MethodInfo method = inventory.GetMethod("CompileAndDescribe", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "CompileAndDescribe is unavailable.");
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            object record = method.Invoke(null, new object[] { assetPath, guid, projectRoot });
            Assert.IsNotNull(record, "VFX CPU descriptor inventory returned no result.");
            Type type = record.GetType();
            string status = Convert.ToString(ReadPublicField(type, record, "compileStatus"), CultureInfo.InvariantCulture);
            bool complete = Convert.ToBoolean(ReadPublicField(type, record, "descriptorFieldsComplete"), CultureInfo.InvariantCulture);
            bool valid = Convert.ToBoolean(ReadPublicField(type, record, "mappingIndicesValid"), CultureInfo.InvariantCulture);
            string before = Convert.ToString(ReadPublicField(type, record, "sourceSha256Before"), CultureInfo.InvariantCulture);
            string after = Convert.ToString(ReadPublicField(type, record, "sourceSha256After"), CultureInfo.InvariantCulture);
            Assert.AreEqual("success", status, "VFX CPU descriptor compile failed.");
            Assert.IsTrue(complete, "VFX descriptor fields incomplete.");
            Assert.IsTrue(valid, "VFX descriptor mapping indices invalid.");
            Assert.AreEqual(before, after, "Graph changed during CPU compilation.");
            Assert.AreEqual(Sha256(ToAbsolute(assetPath)), after, "Compiler source hash differs from fixture graph bytes.");
            return new DescriptorInfo
            {
                compileStatus = status,
                descriptorFieldsComplete = complete,
                mappingIndicesValid = valid,
                sourceSha256Before = before,
                sourceSha256After = after,
                temporaryDescriptors = (string[])ReadPublicField(type, record, "temporaryDescriptors"),
                systemsAndMappings = (string[])ReadPublicField(type, record, "systemsAndMappings")
            };
        }

        static object ReadPublicField(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, "VFX inventory field missing: " + name);
            return field.GetValue(instance);
        }

        static void AssertDescriptorShape(DescriptorInfo info, string label)
        {
            Assert.IsNotNull(info, label + " descriptors missing.");
            Assert.AreEqual("success", info.compileStatus, label + " compile failed.");
            Assert.IsTrue(info.descriptorFieldsComplete, label + " descriptor fields incomplete.");
            Assert.IsTrue(info.mappingIndicesValid, label + " descriptor mapping invalid.");
            Assert.IsTrue(info.temporaryDescriptors.Any(value => value != null && value.Contains("debugName=VFXMovecsBuffer") && value.Contains("target=Raw") && value.Contains("size=1080000") && value.Contains("frameCount=2")), label + " lacks the expected 1,080,000-byte-unit two-frame VFXMovecs descriptor.");
            Assert.IsTrue(info.systemsAndMappings.Any(value => value != null && value.Contains("perCameraBuffer=True")), label + " lacks a per-camera temporary mapping.");
        }

        static void AssertDescriptorSemanticsEqual(DescriptorInfo expected, DescriptorInfo actual, string label)
        {
            CollectionAssert.AreEquivalent(expected.temporaryDescriptors, actual.temporaryDescriptors, label + " temporary descriptor inventory changed.");
            CollectionAssert.AreEquivalent(expected.systemsAndMappings, actual.systemsAndMappings, label + " system or mapping inventory changed.");
        }

        static string ComparableStructureHash(string assetPath, int mode, int capacity)
        {
            string text = File.ReadAllText(ToAbsolute(assetPath));
            text = NormalizeInteger(text, "m_InstancingMode", mode);
            text = NormalizeInteger(text, "m_InstancingCapacity", capacity);
            return HashText(text);
        }

        static string NormalizeInteger(string text, string field, int expected)
        {
            string pattern = "(?m)^([ \\t]*" + Regex.Escape(field) + ":[ \\t]*)(-?\\d+)([ \\t]*)$";
            MatchCollection matches = Regex.Matches(text, pattern);
            Assert.AreEqual(1, matches.Count, "Expected exactly one " + field + " field.");
            Assert.AreEqual(expected, int.Parse(matches[0].Groups[2].Value, CultureInfo.InvariantCulture), field + " differs from expected.");
            return Regex.Replace(text, pattern, "$1normalized-value$3", RegexOptions.Multiline);
        }

        static string SceneFilesFingerprint()
        {
            List<string> rows = new List<string>();
            foreach (SceneSetup setup in EditorSceneManager.GetSceneManagerSetup())
            {
                if (string.IsNullOrEmpty(setup.path)) { rows.Add("untitled|" + setup.isLoaded); continue; }
                string absolute = Path.GetFullPath(Path.Combine(Application.dataPath, "..", setup.path));
                rows.Add(setup.path.Replace('\\', '/') + "|" + (File.Exists(absolute) ? Sha256(absolute) : "missing") + "|" + setup.isLoaded);
            }
            rows.Sort(StringComparer.Ordinal);
            return HashText(string.Join("\n", rows.ToArray()));
        }

        static string ReadMetaGuid(string metaPath)
        {
            Match match = Regex.Match(File.ReadAllText(metaPath), @"(?m)^guid: (?<guid>[0-9a-f]{32})$");
            Assert.IsTrue(match.Success, "Asset meta GUID unavailable.");
            return match.Groups["guid"].Value;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int split = folder.LastIndexOf('/');
            Assert.Greater(split, 0, "Invalid fixture folder.");
            string parent = folder.Substring(0, split), name = folder.Substring(split + 1);
            EnsureFolder(parent);
            Assert.IsNotEmpty(AssetDatabase.CreateFolder(parent, name), "Could not create fixture folder.");
        }

        bool SourceAssetsUnchanged()
        {
            return sourceGraphGuidBefore == AssetDatabase.AssetPathToGUID(SourceGraphPath) && sourceGraphHashBefore == Sha256(sourceGraphAbsolute) && sourceGraphMetaHashBefore == Sha256(sourceGraphAbsolute + ".meta") &&
                sourceVariantGuidBefore == AssetDatabase.AssetPathToGUID(SourceVariantPath) && sourceVariantHashBefore == Sha256(sourceVariantAbsolute) &&
                sourceVariantMetaHashBefore == Sha256(sourceVariantAbsolute + ".meta") && sourceSceneHashBefore == Sha256(sourceSceneAbsolute) &&
                CellPrefabSha256 == Sha256(ToAbsolute("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/Cell_01.prefab")) &&
                CellPrefabMetaSha256 == Sha256(ToAbsolute("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/Cell_01.prefab.meta"));
        }

        static string ToAbsolute(string path) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
        static string Sha256(string path) { using (FileStream stream = File.OpenRead(path)) using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        static string HashText(string text) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", ""); }
        static string Ascii(string value) { if (string.IsNullOrEmpty(value)) return ""; StringBuilder output = new StringBuilder(value.Length); foreach (char ch in value) output.Append(ch >= 32 && ch <= 126 ? ch : '?'); return output.ToString(); }
        static Type[] SafeTypes(Assembly assembly) { try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type != null).ToArray(); } }

        sealed class ParticleDataInfo { public string name; public uint capacity; public int matches; }
        sealed class GraphInfo { public string particleDataName, motionParameter, urpProjectionSha256; public string[] urpProjectionRows, rawDocumentIds, rawDroppedDocumentIds, rawAddedDocumentIds, rawChangedCommonDocumentIds; public Dictionary<string, string> rawDocuments; public uint capacity; public int particleSystemCount, mode, instancingCapacity, motionVectorEnabledFieldCount; }
        [Serializable] sealed class DescriptorInfo { public string compileStatus, sourceSha256Before, sourceSha256After; public bool descriptorFieldsComplete, mappingIndicesValid; public string[] temporaryDescriptors, systemsAndMappings; }
        [Serializable] sealed class FixtureResult
        {
            public string schema, status, failureType, failureMessage, unityVersion, graphicsDevice, vfxGraphVersion, vfxGraphResolvedPath;
            public string sourceGraphPath, sourceGraphGuid, sourceGraphGuidAfter, sourceGraphSha256Before, sourceGraphSha256After, sourceGraphMetaSha256Before, sourceGraphMetaSha256After;
            public string sourceVariantPath, sourceVariantGuid, sourceVariantGuidAfter, sourceVariantSha256Before, sourceVariantSha256After, sourceVariantMetaSha256Before, sourceVariantMetaSha256After;
            public string sourceScenePath, sourceSceneSha256Before, sourceSceneSha256After;
            public string controlPath, controlGuid, controlGuidAfter, controlSha256Before, controlSha256After, customPath, customGuid, customGuidAfter, customSha256Before, customSha256After;
            public string sourceParticleDataName, controlParticleDataName, customParticleDataName, sourceGraphMotionParameter, openSceneFingerprintBefore, openSceneFingerprintAfter;
            public string sourceUrpProjectionSha256, controlUrpProjectionSha256, customUrpProjectionSha256;
            public string[] sourceUrpProjectionRows, controlUrpProjectionRows, customUrpProjectionRows;
            public string sourceUrpSubOutputType, controlUrpSubOutputType, customUrpSubOutputType;
            public long sourceUrpSubOutputLocalFileId, controlUrpSubOutputLocalFileId, customUrpSubOutputLocalFileId;
            public string[] sourceRawDocumentIds, controlRawDocumentIds, controlRawDroppedDocumentIds, controlRawAddedDocumentIds, controlRawChangedCommonDocumentIds;
            public string[] customRawDocumentIds, customRawDroppedDocumentIds, customRawAddedDocumentIds, customRawChangedCommonDocumentIds;
            public string controlMode, customMode, customDisabledReasonName, customDisabledReasonEnumType, sourceStructureSha256, controlStructureSha256, customStructureSha256, integrityCheckError, fixtureAfterCheckError;
            public string[] normalizedFields, showcaseTmpOverrideFileIds; public bool[] loadedCameraEnabledAtLoad;
            public uint sourceParticleCapacity, controlParticleCapacity, customParticleCapacity;
            public int controlModeValue, controlInstancingCapacity, customModeValue, customInstancingCapacity, customDisabledReasonValue, customDisabledReasonNoneValue, sourceGraphMotionVectorEnabledFieldCount;
            public int loadedCameraCount, activeCameraCountDuringProbe, activeVfxCountDuringProbe, activeCameraCountAfterRestore, activeVfxCountAfterRestore;
            public bool sourceAssetsUnchanged, openSceneFilesUnchanged, controlUnchangedAfterCreation, customAssetPresent, finalIntegrityGate;
            public bool sceneSaveCalled, runtimeActivation, nativeMeasurement;
            public DescriptorInfo sourceDescriptors, controlDescriptors, customDescriptors;
        }
    }
}
#endif

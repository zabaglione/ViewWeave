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
    public sealed class SampleTextureCapacityFixture
    {
        const string SourcePath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/SampleTexture2D.vfx";
        const string OutputRoot = "Assets/FrustumMemoryFixturesGrid40";
        const string EvidenceEnvironment = "VW_SAMPLE_TEXTURE_CAPACITY_RESULT";
        const uint SourceParticleCapacity = 160000;
        const uint FixtureParticleCapacity = 1600;
        const float SourceGridResolution = 400f;
        const float FixtureGridResolution = 40f;
        const int CustomInstancingCapacity = 1;

        string evidencePath;
        string sourceAbsolutePath;
        string sourceHashBefore;
        string sourceGuidBefore;
        string sceneSnapshotBefore;
        FixtureResult result;
        Camera[] loadedSceneCamerasBeforeIsolation = Array.Empty<Camera>();
        bool[] loadedSceneCameraEnabledBeforeIsolation = Array.Empty<bool>();
        CameraStateRecord[] loadedSceneCameraStatesBeforeIsolation = Array.Empty<CameraStateRecord>();
        int loadedSceneCameraCountBeforeIsolation;
        int activeSceneCameraCountBeforeIsolation;
        int activeSceneCameraCountDisabledInMemory;

        [Test]
        public void CreatesAutoAndCustomCapacityOnlyClonesAndDescribesCompiledAssets()
        {
            evidencePath = Environment.GetEnvironmentVariable(EvidenceEnvironment);
            Assert.IsFalse(string.IsNullOrWhiteSpace(evidencePath), EvidenceEnvironment + " was not provided by the safe runner.");
            evidencePath = Path.GetFullPath(evidencePath);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string allowedResultsRoot = Path.GetFullPath(Path.Combine(projectRoot, "TestResults")) + Path.DirectorySeparatorChar;
            Assert.IsTrue(evidencePath.StartsWith(allowedResultsRoot, StringComparison.OrdinalIgnoreCase), "Evidence must stay under this clone's TestResults directory.");
            Assert.IsFalse(File.Exists(evidencePath), "Refusing to overwrite prior fixture evidence.");

            sourceAbsolutePath = ToAbsoluteAssetPath(SourcePath);
            sceneSnapshotBefore = SceneFilesFingerprint();
            sourceHashBefore = File.Exists(sourceAbsolutePath) ? Sha256(sourceAbsolutePath) : "";
            sourceGuidBefore = AssetDatabase.AssetPathToGUID(SourcePath);

            Assert.IsFalse(Application.isPlaying, "This is an EditMode-only Null-graphics fixture.");
            Assert.AreEqual(UnityEngine.Rendering.GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType, "The runner must use the Null graphics device.");
            loadedSceneCamerasBeforeIsolation = GetLoadedSceneCameras();
            loadedSceneCameraEnabledBeforeIsolation = loadedSceneCamerasBeforeIsolation.Select(camera => camera.enabled).ToArray();
            loadedSceneCameraCountBeforeIsolation = loadedSceneCamerasBeforeIsolation.Length;
            loadedSceneCameraStatesBeforeIsolation = loadedSceneCamerasBeforeIsolation.Select(camera => new CameraStateRecord
            {
                sceneName = camera.gameObject.scene.name,
                cameraName = camera.name,
                enabledAtLoad = camera.enabled,
                activeInHierarchyAtLoad = camera.gameObject.activeInHierarchy
            }).ToArray();
            activeSceneCameraCountBeforeIsolation = loadedSceneCamerasBeforeIsolation.Count(camera => camera.enabled && camera.gameObject.activeInHierarchy);
            foreach (Camera camera in loadedSceneCamerasBeforeIsolation)
            {
                if (camera.enabled && camera.gameObject.activeInHierarchy)
                {
                    camera.enabled = false;
                    activeSceneCameraCountDisabledInMemory++;
                }
            }
            Assert.AreEqual(0, CountActiveSceneCameras(), "A loaded scene camera is active.");
            Assert.AreEqual(0, CountActiveSceneVfx(), "A loaded scene VFX component is active.");

            Assert.IsTrue(File.Exists(sourceAbsolutePath), "The imported VFX 17.6 SampleTexture2D source graph is missing.");
            Assert.IsNotEmpty(sourceGuidBefore, "Source graph GUID is unavailable.");
            UnityEditor.PackageManager.PackageInfo[] vfxPackages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(value => value != null && value.name == "com.unity.visualeffectgraph").ToArray();
            Assert.AreEqual(1, vfxPackages.Length, "Registered Visual Effect Graph package provenance is unavailable or ambiguous.");
            UnityEditor.PackageManager.PackageInfo vfxPackage = vfxPackages[0];
            Assert.AreEqual("17.6.0", vfxPackage.version, "Unexpected Visual Effect Graph package version.");

            EnsureFolder(OutputRoot);
            const string controlPath = OutputRoot + "/Control/SampleTexture2D.vfx";
            const string autoPath = OutputRoot + "/Auto/SampleTexture2D.vfx";
            const string customPath = OutputRoot + "/Custom/SampleTexture2D.vfx";
            EnsureFolder(OutputRoot + "/Control");
            EnsureFolder(OutputRoot + "/Auto");
            EnsureFolder(OutputRoot + "/Custom");
            Assert.IsFalse(File.Exists(ToAbsoluteAssetPath(controlPath)), "Refusing to overwrite the Control fixture graph.");
            Assert.IsFalse(File.Exists(ToAbsoluteAssetPath(autoPath)), "Refusing to overwrite the Auto fixture graph.");
            Assert.IsFalse(File.Exists(ToAbsoluteAssetPath(customPath)), "Refusing to overwrite the Custom fixture graph.");

            string originalModeName;
            int originalModeValue;
            int customModeValue;
            originalModeValue = ReadInstancingMode(SourcePath, out originalModeName);
            int originalInstancingCapacity = ReadInstancingCapacity(SourcePath);
            string sourceStructure = StructureHash(SourcePath, SourceParticleCapacity, originalModeValue, originalInstancingCapacity, new Vector2(SourceGridResolution, SourceGridResolution));
            Assert.IsTrue(AssetDatabase.CopyAsset(SourcePath, controlPath), "Could not make the Control clone.");
            Assert.IsTrue(AssetDatabase.CopyAsset(SourcePath, autoPath), "Could not make the Auto clone.");
            Assert.IsTrue(AssetDatabase.CopyAsset(SourcePath, customPath), "Could not make the Custom clone.");
            AssetDatabase.ImportAsset(controlPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(autoPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(customPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            ParticleDataInfo sourceData = FindParticleData(SourcePath, SourceParticleCapacity);
            Assert.AreEqual(1, sourceData.matches, "Expected exactly one SampleTexture2D particle system with capacity 160000.");
            string sourceDataName = sourceData.name;

            customModeValue = ResolveInstancingEnumValue("Custom");
            SetCloneSettings(controlPath, sourceDataName, sourceStructure, originalModeValue, originalInstancingCapacity);
            SetCloneSettings(autoPath, sourceDataName, sourceStructure, originalModeValue, originalInstancingCapacity);
            SetCloneSettings(customPath, sourceDataName, sourceStructure, originalModeValue, originalInstancingCapacity);
            GridParameterInfo controlGridBefore = SetGridResolution(controlPath, SourceGridResolution);
            GridParameterInfo autoGridBefore = SetGridResolution(autoPath, FixtureGridResolution);
            GridParameterInfo customGridBefore = SetGridResolution(customPath, FixtureGridResolution);
            WriteResource(controlPath);
            AssetDatabase.ImportAsset(controlPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            WriteResource(controlPath);
            AssetDatabase.ImportAsset(controlPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            ApplyParticleCapacity(autoPath, sourceDataName);
            int autoModeValue = ResolveInstancingEnumValue("Automatic", "Auto");
            SetInstancingSettings(autoPath, "Auto", autoModeValue, 0, out string autoModeName);
            Assert.AreEqual(autoModeValue, ReadInstancingMode(autoPath, out autoModeName), "The Auto fixture mode was not serialized.");

            ApplyParticleCapacity(customPath, sourceDataName);
            SetInstancingSettings(customPath, "Custom", customModeValue, CustomInstancingCapacity, out string customModeName);

            AssetDatabase.ImportAsset(autoPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(customPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            ParticleDataInfo autoData = FindParticleData(autoPath, FixtureParticleCapacity);
            ParticleDataInfo customData = FindParticleData(customPath, FixtureParticleCapacity);
            ParticleDataInfo controlData = FindParticleData(controlPath, SourceParticleCapacity);
            Assert.AreEqual(1, controlData.matches, "The Control fixture does not contain exactly one source-capacity particle system.");
            Assert.AreEqual(1, autoData.matches, "The Auto fixture does not contain exactly one capacity-1600 particle system.");
            Assert.AreEqual(1, customData.matches, "The Custom fixture does not contain exactly one capacity-1600 particle system.");
            Assert.AreEqual(sourceDataName, controlData.name, "The Control fixture particle data identity changed.");
            Assert.AreEqual(sourceDataName, autoData.name, "The Auto fixture particle data identity changed.");
            Assert.AreEqual(sourceDataName, customData.name, "The Custom fixture particle data identity changed.");
            int autoInstancingCapacity = ReadInstancingCapacity(autoPath);
            Assert.AreEqual(originalInstancingCapacity, autoInstancingCapacity, "The Auto fixture changed the source instancing capacity.");
            Assert.AreEqual(CustomInstancingCapacity, ReadInstancingCapacity(customPath), "The Custom instancing capacity is not one.");
            string controlModeName;
            int controlModeValue = ReadInstancingMode(controlPath, out controlModeName);
            int controlInstancingCapacity = ReadInstancingCapacity(controlPath);
            Assert.AreEqual(originalModeValue, controlModeValue, "The Control fixture changed the source instancing mode.");
            Assert.AreEqual(originalInstancingCapacity, controlInstancingCapacity, "The Control fixture changed the source instancing capacity.");
            Assert.AreEqual(SourceParticleCapacity, controlData.capacity, "The Control fixture particle capacity changed.");
            DescriptorInfo controlDescriptors = ReadCompiledDescriptors(controlPath);
            DescriptorInfo autoDescriptors = ReadCompiledDescriptors(autoPath);
            DescriptorInfo customDescriptors = ReadCompiledDescriptors(customPath);
            string disabledReasonName;
            int disabledReasonValue;
            int disabledReasonNoneValue;
            string disabledReasonEnumType;
            ReadCompiledDisabledReason(customPath, out disabledReasonName, out disabledReasonValue, out disabledReasonNoneValue, out disabledReasonEnumType);
            GridParameterInfo controlGrid = ReadGridResolution(controlPath, SourceGridResolution);
            GridParameterInfo autoGrid = ReadGridResolution(autoPath, FixtureGridResolution);
            GridParameterInfo customGrid = ReadGridResolution(customPath, FixtureGridResolution);
            Assert.AreEqual(controlGridBefore.outputSlotFileId, controlGrid.outputSlotFileId, "The Control Grid Resolution slot identity changed during serialization.");
            Assert.AreEqual(autoGridBefore.outputSlotFileId, autoGrid.outputSlotFileId, "The Auto Grid Resolution slot identity changed during serialization.");
            Assert.AreEqual(customGridBefore.outputSlotFileId, customGrid.outputSlotFileId, "The Custom Grid Resolution slot identity changed during serialization.");
            string controlGraphStructure = StructureHash(controlPath, SourceParticleCapacity, controlModeValue, controlInstancingCapacity, new Vector2(SourceGridResolution, SourceGridResolution));
            string controlComparableStructure = ComparableStructureHash(controlPath);
            string autoComparableStructure = ComparableStructureHash(autoPath);
            string customComparableStructure = ComparableStructureHash(customPath);
            result = new FixtureResult
            {
                schema = "viewweave-sample-texture-capacity-fixture-v2-grid40",
                status = "started",
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                vfxPackageVersion = vfxPackage.version,
                vfxPackageResolvedPath = vfxPackage.resolvedPath ?? "",
                sourceAssetPath = SourcePath,
                sourceAssetGuid = sourceGuidBefore,
                sourceAssetSha256Before = sourceHashBefore,
                sourceAssetSha256After = sourceHashBefore,
                sourceAssetGuidAfter = sourceGuidBefore,
                sourceAssetUnchanged = true,
                sourceGraphStructureSha256 = sourceStructure,
                loadedSceneCameraCountBeforeIsolation = loadedSceneCameraCountBeforeIsolation,
                activeSceneCameraCountBeforeIsolation = activeSceneCameraCountBeforeIsolation,
                activeSceneCameraCountDisabledInMemory = activeSceneCameraCountDisabledInMemory,
                loadedSceneCameraStatesBeforeIsolation = loadedSceneCameraStatesBeforeIsolation,
                sourceParticleDataName = sourceDataName,
                sourceParticleCapacity = SourceParticleCapacity,
                fixtureParticleCapacity = FixtureParticleCapacity,
                gridParameterExposedName = "Grid Resolution",
                gridParameterExposed = false,
                gridParameterValueType = typeof(Vector2).FullName,
                controlGridResolutionX = controlGrid.value.x,
                controlGridResolutionY = controlGrid.value.y,
                autoGridResolutionX = autoGrid.value.x,
                autoGridResolutionY = autoGrid.value.y,
                customGridResolutionX = customGrid.value.x,
                customGridResolutionY = customGrid.value.y,
                controlGridParameterFileId = long.Parse(controlGrid.parameterFileId, CultureInfo.InvariantCulture),
                controlGridOutputSlotFileId = long.Parse(controlGrid.outputSlotFileId, CultureInfo.InvariantCulture),
                autoGridParameterFileId = long.Parse(autoGrid.parameterFileId, CultureInfo.InvariantCulture),
                autoGridOutputSlotFileId = long.Parse(autoGrid.outputSlotFileId, CultureInfo.InvariantCulture),
                customGridParameterFileId = long.Parse(customGrid.parameterFileId, CultureInfo.InvariantCulture),
                customGridOutputSlotFileId = long.Parse(customGrid.outputSlotFileId, CultureInfo.InvariantCulture),
                normalizedStructureFields = new[] { "capacity", "Grid Resolution Vector2 parameter value", "m_InstancingMode", "m_InstancingCapacity" },
                originalInstancingModeName = originalModeName,
                originalInstancingModeValue = originalModeValue,
                originalInstancingCapacity = originalInstancingCapacity,
                autoInstancingModeName = autoModeName,
                autoInstancingModeValue = autoModeValue,
                autoInstancingCapacity = autoInstancingCapacity,
                customInstancingModeName = customModeName,
                customInstancingModeValue = customModeValue,
                customInstancingCapacity = CustomInstancingCapacity,
                customCompilerDisabledReasonName = disabledReasonName,
                customCompilerDisabledReasonValue = disabledReasonValue,
                customCompilerDisabledReasonNoneValue = disabledReasonNoneValue,
                customCompilerDisabledReasonEnumType = disabledReasonEnumType,
                controlAssetPath = controlPath,
                controlAssetGuid = AssetDatabase.AssetPathToGUID(controlPath),
                controlAssetSha256 = Sha256(ToAbsoluteAssetPath(controlPath)),
                autoAssetPath = autoPath,
                autoAssetGuid = AssetDatabase.AssetPathToGUID(autoPath),
                autoAssetSha256 = Sha256(ToAbsoluteAssetPath(autoPath)),
                customAssetPath = customPath,
                customAssetGuid = AssetDatabase.AssetPathToGUID(customPath),
                customAssetSha256 = Sha256(ToAbsoluteAssetPath(customPath)),
                controlInstancingModeName = controlModeName,
                controlInstancingModeValue = controlModeValue,
                controlInstancingCapacity = controlInstancingCapacity,
                controlDescriptors = controlDescriptors,
                autoDescriptors = autoDescriptors,
                customDescriptors = customDescriptors,
                activeSceneCameraCount = CountActiveSceneCameras(),
                activeSceneVfxCount = CountActiveSceneVfx(),
                autoGraphStructureSha256 = StructureHash(autoPath, FixtureParticleCapacity, autoModeValue, autoInstancingCapacity, new Vector2(FixtureGridResolution, FixtureGridResolution)),
                customGraphStructureSha256 = StructureHash(customPath, FixtureParticleCapacity, customModeValue, CustomInstancingCapacity, new Vector2(FixtureGridResolution, FixtureGridResolution)),
                controlGraphStructureSha256 = controlGraphStructure,
                controlComparableStructureSha256 = controlComparableStructure,
                autoComparableStructureSha256 = autoComparableStructure,
                customComparableStructureSha256 = customComparableStructure,
                openSceneFilesFingerprintBefore = sceneSnapshotBefore,
                openSceneFilesFingerprintAfter = sceneSnapshotBefore,
                openSceneFilesUnchanged = true,
                nunitOutcome = "Started",
                descriptorScope = "Editor CPU compiler inventory from VfxDescriptorInventory; descriptors are metadata, not native allocation or GPU rendering evidence. Control, Auto, and Custom use two matched WriteResource/import cycles. Control retains source capacity 160000 and Grid Resolution 400x400. Auto and Custom use capacity 1600 and Grid Resolution 40x40. Pairwise YAML comparison normalizes only capacity, the uniquely identified non-exposed Grid Resolution Vector2 value, instancing mode, and instancing capacity; graph nodes, slots, connections, and all other serialized data must match."
            };
            Assert.AreEqual(disabledReasonNoneValue, disabledReasonValue, "The compiled VFX instancing disabled-reason value is not the formal None enum value.");
            Assert.AreEqual("None", disabledReasonName, "The compiler reports a reason that prevents instancing on the Custom fixture.");

            string autoGraphStructure = StructureHash(autoPath, FixtureParticleCapacity, autoModeValue, autoInstancingCapacity, new Vector2(FixtureGridResolution, FixtureGridResolution));
            string customGraphStructure = StructureHash(customPath, FixtureParticleCapacity, customModeValue, CustomInstancingCapacity, new Vector2(FixtureGridResolution, FixtureGridResolution));
            Assert.AreEqual(controlComparableStructure, autoComparableStructure, "The Auto clone differs from the same-serialization Control beyond the four normalized settings.");
            Assert.AreEqual(controlComparableStructure, customComparableStructure, "The Custom clone differs from the same-serialization Control beyond the four normalized settings.");
            Assert.AreEqual(0, CountActiveSceneCameras(), "A scene camera became active during fixture preparation.");
            Assert.AreEqual(0, CountActiveSceneVfx(), "A scene VFX component became active during fixture preparation.");
            Assert.IsFalse(Application.isPlaying, "The fixture entered Play Mode.");
            Assert.AreEqual(UnityEngine.Rendering.GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType, "The graphics backend changed during fixture preparation.");

            result = new FixtureResult
            {
                schema = "viewweave-sample-texture-capacity-fixture-v2-grid40",
                status = "started",
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                vfxPackageVersion = vfxPackage.version,
                vfxPackageResolvedPath = vfxPackage.resolvedPath ?? "",
                sourceAssetPath = SourcePath,
                sourceAssetGuid = sourceGuidBefore,
                instancingEnumType = ResolveInstancingEnumTypeName(),
                sourceAssetSha256Before = sourceHashBefore,
                sourceAssetSha256After = "",
                sourceGraphStructureSha256 = sourceStructure,
                sourceParticleDataName = sourceDataName,
                sourceParticleCapacity = SourceParticleCapacity,
                fixtureParticleCapacity = FixtureParticleCapacity,
                gridParameterExposedName = "Grid Resolution",
                gridParameterExposed = false,
                gridParameterValueType = typeof(Vector2).FullName,
                controlGridResolutionX = controlGrid.value.x,
                controlGridResolutionY = controlGrid.value.y,
                autoGridResolutionX = autoGrid.value.x,
                autoGridResolutionY = autoGrid.value.y,
                customGridResolutionX = customGrid.value.x,
                customGridResolutionY = customGrid.value.y,
                controlGridParameterFileId = controlGrid.parameterFileId,
                controlGridOutputSlotFileId = controlGrid.outputSlotFileId,
                autoGridParameterFileId = autoGrid.parameterFileId,
                autoGridOutputSlotFileId = autoGrid.outputSlotFileId,
                customGridParameterFileId = customGrid.parameterFileId,
                customGridOutputSlotFileId = customGrid.outputSlotFileId,
                normalizedStructureFields = new[] { "capacity", "Grid Resolution Vector2 parameter value", "m_InstancingMode", "m_InstancingCapacity" },
                originalInstancingModeName = originalModeName,
                originalInstancingModeValue = originalModeValue,
                originalInstancingCapacity = originalInstancingCapacity,
                autoInstancingModeName = autoModeName,
                autoInstancingModeValue = autoModeValue,
                autoInstancingCapacity = autoInstancingCapacity,
                customInstancingModeName = customModeName,
                customInstancingModeValue = customModeValue,
                customInstancingCapacity = CustomInstancingCapacity,
                customCompilerDisabledReasonName = disabledReasonName,
                customCompilerDisabledReasonValue = disabledReasonValue,
                autoAssetPath = autoPath,
                autoAssetGuid = AssetDatabase.AssetPathToGUID(autoPath),
                autoAssetSha256 = Sha256(ToAbsoluteAssetPath(autoPath)),
                customAssetPath = customPath,
                customAssetGuid = AssetDatabase.AssetPathToGUID(customPath),
                customAssetSha256 = Sha256(ToAbsoluteAssetPath(customPath)),
                activeSceneCameraCount = CountActiveSceneCameras(),
                activeSceneVfxCount = CountActiveSceneVfx(),
                autoGraphStructureSha256 = autoGraphStructure,
                customGraphStructureSha256 = customGraphStructure,
                controlGraphStructureSha256 = controlGraphStructure,
                controlComparableStructureSha256 = controlComparableStructure,
                autoComparableStructureSha256 = autoComparableStructure,
                customComparableStructureSha256 = customComparableStructure,
                controlInstancingModeName = controlModeName,
                controlInstancingModeValue = controlModeValue,
                controlInstancingCapacity = controlInstancingCapacity,
                controlAssetPath = controlPath,
                controlAssetGuid = AssetDatabase.AssetPathToGUID(controlPath),
                controlAssetSha256 = Sha256(ToAbsoluteAssetPath(controlPath)),
                autoDescriptors = autoDescriptors,
                customDescriptors = customDescriptors,
                controlDescriptors = controlDescriptors,
                loadedSceneCameraCountBeforeIsolation = loadedSceneCameraCountBeforeIsolation,
                activeSceneCameraCountBeforeIsolation = activeSceneCameraCountBeforeIsolation,
                activeSceneCameraCountDisabledInMemory = activeSceneCameraCountDisabledInMemory,
                loadedSceneCameraStatesBeforeIsolation = loadedSceneCameraStatesBeforeIsolation,
                descriptorScope = "Editor CPU compiler inventory from VfxDescriptorInventory; descriptors are metadata, not native allocation or GPU rendering evidence. Control, Auto, and Custom use two matched WriteResource/import cycles. Control retains source capacity 160000 and Grid Resolution 400x400. Auto and Custom use capacity 1600 and Grid Resolution 40x40. Pairwise YAML comparison normalizes only capacity, the uniquely identified non-exposed Grid Resolution Vector2 value, instancing mode, and instancing capacity; graph nodes, slots, connections, and all other serialized data must match."
            };
        }

        [TearDown]
        public void FinalizeEvidenceAndVerifyInputs()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(evidencePath)) return;
                string sourceHashAfter = !string.IsNullOrWhiteSpace(sourceAbsolutePath) && File.Exists(sourceAbsolutePath) ? Sha256(sourceAbsolutePath) : "";
                string sourceGuidAfter = AssetDatabase.AssetPathToGUID(SourcePath);
                string sceneSnapshotAfter = SceneFilesFingerprint();
                bool sourceUnchanged = !string.IsNullOrEmpty(sourceHashBefore) && sourceHashBefore == sourceHashAfter && sourceGuidBefore == sourceGuidAfter;
                bool scenesUnchanged = !string.IsNullOrEmpty(sceneSnapshotBefore) && sceneSnapshotBefore == sceneSnapshotAfter;
                bool nunitPassed = TestContext.CurrentContext.Result.Outcome.Status.ToString() == "Passed";
                if (result == null) result = new FixtureResult { schema = "viewweave-sample-texture-capacity-fixture-v2-grid40" };
                result.sourceAssetSha256Before = sourceHashBefore ?? "";
                result.sourceAssetSha256After = sourceHashAfter;
                result.sourceAssetUnchanged = sourceUnchanged;
                result.sourceAssetGuidAfter = sourceGuidAfter ?? "";
                result.openSceneFilesFingerprintBefore = sceneSnapshotBefore ?? "";
                result.openSceneFilesFingerprintAfter = sceneSnapshotAfter;
                result.openSceneFilesUnchanged = scenesUnchanged;
                result.nunitOutcome = TestContext.CurrentContext.Result.Outcome.Status.ToString();
                result.failureSummary = nunitPassed ? "" : Ascii(TestContext.CurrentContext.Result.Message ?? "test-failed");
                result.loadedSceneCameraCountBeforeIsolation = loadedSceneCameraCountBeforeIsolation;
                result.activeSceneCameraCountBeforeIsolation = activeSceneCameraCountBeforeIsolation;
                result.activeSceneCameraCountDisabledInMemory = activeSceneCameraCountDisabledInMemory;
                result.loadedSceneCameraStatesBeforeIsolation = loadedSceneCameraStatesBeforeIsolation;
                result.activeSceneCameraCount = CountActiveSceneCameras();
                result.status = nunitPassed && sourceUnchanged && scenesUnchanged ? "success" : "failed";
                Directory.CreateDirectory(Path.GetDirectoryName(evidencePath));
                if (!File.Exists(evidencePath)) File.WriteAllText(evidencePath, JsonUtility.ToJson(result, true), new UTF8Encoding(false));
                Assert.IsTrue(sourceUnchanged, "The imported source VFX graph hash or GUID changed during fixture preparation.");
                Assert.IsTrue(scenesUnchanged, "An open scene file changed during fixture preparation.");
            }
            finally
            {
                for (int index = 0; index < loadedSceneCamerasBeforeIsolation.Length; index++)
                {
                    Camera camera = loadedSceneCamerasBeforeIsolation[index];
                    if (camera != null && index < loadedSceneCameraEnabledBeforeIsolation.Length)
                        camera.enabled = loadedSceneCameraEnabledBeforeIsolation[index];
                }
            }
        }

        static void SetCloneSettings(string assetPath, string expectedDataName, string expectedStructure, int expectedMode, int expectedCapacity)
        {
            ParticleDataInfo data = FindParticleData(assetPath, SourceParticleCapacity);
            Assert.AreEqual(1, data.matches, "The untouched VFX clone does not contain the original particle capacity.");
            Assert.AreEqual(expectedDataName, data.name, "The untouched VFX clone particle data identity changed.");
            Assert.AreEqual(expectedMode, ReadInstancingMode(assetPath, out string modeName), "The untouched VFX clone instancing mode changed.");
            Assert.IsFalse(modeName == "Custom", "The source graph is already using Custom instancing.");
            Assert.AreEqual(expectedCapacity, ReadInstancingCapacity(assetPath), "The untouched VFX clone instancing capacity changed.");
            Assert.AreEqual(expectedStructure, StructureHash(assetPath, SourceParticleCapacity, expectedMode, expectedCapacity, new Vector2(SourceGridResolution, SourceGridResolution)), "The untouched clone differs from the source graph.");
        }

        static GridParameterInfo SetGridResolution(string assetPath, float resolution)
        {
            GridParameterInfo info = FindGridParameter(assetPath);
            Assert.AreEqual(new Vector2(SourceGridResolution, SourceGridResolution), info.value, "The source Grid Resolution value changed before fixture setup.");
            PropertyInfo valueProperty = info.parameter.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(valueProperty, "VFXParameter.value property was not found.");
            Assert.IsTrue(valueProperty.CanWrite, "VFXParameter.value is not writable.");
            Vector2 requested = new Vector2(resolution, resolution);
            valueProperty.SetValue(info.parameter, requested, null);
            Assert.AreEqual(requested, valueProperty.GetValue(info.parameter, null), "The Grid Resolution VFXParameter value did not update.");
            UnityEngine.Object parameterObject = info.parameter as UnityEngine.Object;
            Assert.IsNotNull(parameterObject, "VFXParameter is not a Unity object that can be marked dirty.");
            EditorUtility.SetDirty(parameterObject);
            info.value = requested;
            return info;
        }

        static GridParameterInfo ReadGridResolution(string assetPath, float expectedResolution)
        {
            GridParameterInfo info = FindGridParameter(assetPath);
            Vector2 expected = new Vector2(expectedResolution, expectedResolution);
            Assert.AreEqual(expected, info.value, "The Grid Resolution VFXParameter value is incorrect after import.");
            GridSlotIdentity identity = ReadGridSlotIdentity(File.ReadAllText(ToAbsoluteAssetPath(assetPath)), expected);
            Assert.AreEqual(info.parameterFileId, identity.parameterFileId, "The Grid Resolution parameter document identity differs from the editor graph model.");
            Assert.AreEqual(info.outputSlotFileId, identity.outputSlotFileId, "The Grid Resolution output slot identity differs from the editor graph model.");
            return info;
        }

        static GridParameterInfo FindGridParameter(string assetPath)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            Type extensions = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.Name == "VisualEffectResourceExtensions" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(extensions, "VisualEffectResourceExtensions type was not found.");
            MethodInfo getGraph = extensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "GetGraph" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(getGraph, "VisualEffectResourceExtensions.GetGraph(VisualEffectResource) was not found.");
            object graph = getGraph.Invoke(null, new[] { resource });
            Assert.IsNotNull(graph, "The editor VFX graph could not be loaded from " + assetPath);
            MethodInfo collectDependencies = graph.GetType().GetMethod("CollectDependencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(HashSet<ScriptableObject>), typeof(bool) }, null);
            Assert.IsNotNull(collectDependencies, "VFXGraph.CollectDependencies(HashSet<ScriptableObject>, bool) was not found.");
            var dependencies = new HashSet<ScriptableObject>();
            collectDependencies.Invoke(graph, new object[] { dependencies, false });
            var matches = new List<GridParameterInfo>();
            foreach (ScriptableObject dependency in dependencies)
            {
                if (dependency == null || dependency.GetType().Name != "VFXParameter") continue;
                Type type = dependency.GetType();
                PropertyInfo exposedName = type.GetProperty("exposedName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (exposedName == null || !string.Equals(Convert.ToString(exposedName.GetValue(dependency, null), CultureInfo.InvariantCulture), "Grid Resolution", StringComparison.Ordinal)) continue;
                PropertyInfo exposed = type.GetProperty("exposed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo valueType = type.GetProperty("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo value = type.GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.IsNotNull(exposed, "VFXParameter.exposed property was not found.");
                Assert.IsNotNull(valueType, "VFXParameter.type property was not found.");
                Assert.IsNotNull(value, "VFXParameter.value property was not found.");
                Assert.IsFalse(Convert.ToBoolean(exposed.GetValue(dependency, null), CultureInfo.InvariantCulture), "Grid Resolution must remain non-exposed.");
                Assert.AreEqual(typeof(Vector2), valueType.GetValue(dependency, null), "Grid Resolution is not a Vector2 parameter.");
                Vector2 gridValue = (Vector2)value.GetValue(dependency, null);
                GridSlotIdentity identity = ReadGridSlotIdentity(File.ReadAllText(ToAbsoluteAssetPath(assetPath)), gridValue);
                matches.Add(new GridParameterInfo
                {
                    parameter = dependency,
                    parameterFileId = identity.parameterFileId,
                    outputSlotFileId = identity.outputSlotFileId,
                    exposedName = "Grid Resolution",
                    exposed = false,
                    valueType = typeof(Vector2).FullName,
                    value = gridValue
                });
            }
            Assert.AreEqual(1, matches.Count, "Expected exactly one non-exposed Vector2 VFXParameter named Grid Resolution.");
            return matches[0];
        }

        static void ApplyParticleCapacity(string assetPath, string expectedDataName)
        {
            ParticleDataInfo before = FindParticleData(assetPath, SourceParticleCapacity);
            Assert.AreEqual(1, before.matches, "Expected one source-capacity particle data object in clone.");
            Assert.AreEqual(expectedDataName, before.name, "Unexpected selected particle data object.");
            MethodInfo setter = FindMethod(before.target.GetType(), "SetSettingValue", typeof(string), typeof(object));
            Assert.IsNotNull(setter, "VFXGraph editor setting API SetSettingValue(string, object) was not found.");
            setter.Invoke(before.target, new object[] { "capacity", FixtureParticleCapacity });
            EditorUtility.SetDirty(before.target);
            WriteResource(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        static void SetInstancingSettings(string assetPath, string requestedMode, int modeValue, int capacity, out string actualModeName)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            var serialized = new SerializedObject(resource);
            SerializedProperty mode = serialized.FindProperty("m_Infos.m_InstancingMode");
            SerializedProperty capacityProperty = serialized.FindProperty("m_Infos.m_InstancingCapacity");
            Assert.IsNotNull(mode, "VFX resource instancing mode property was not found.");
            Assert.IsNotNull(capacityProperty, "VFX resource instancing capacity property was not found.");
            mode.intValue = modeValue;
            if (requestedMode == "Custom") capacityProperty.intValue = capacity;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(resource);
            WriteResource(assetPath);
            int writtenMode = ReadInstancingMode(assetPath, out actualModeName);
            Assert.AreEqual(modeValue, writtenMode, "The requested VFX instancing mode value was not serialized.");
            bool modeMatches = requestedMode == "Custom"
                ? actualModeName == "Custom"
                : actualModeName == "Auto" || actualModeName == "Automatic";
            Assert.IsTrue(modeMatches, "The requested VFX instancing mode could not be applied.");
            if (requestedMode == "Custom") Assert.AreEqual(capacity, ReadInstancingCapacity(assetPath), "The Custom instancing capacity was not serialized.");
        }

        static void ReadCompiledDisabledReason(string assetPath, out string reasonName, out int reasonValue, out int noneValue, out string enumTypeName)
        {
            VisualEffectAsset asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
            Assert.IsNotNull(asset, "Compiled VisualEffectAsset is unavailable.");
            var serialized = new SerializedObject(asset);
            SerializedProperty reason = serialized.FindProperty("m_Infos.m_InstancingDisabledReason");
            Assert.IsNotNull(reason, "Compiled VFX instancing disabled-reason property is unavailable.");
            reasonValue = reason.intValue;
            Type enumType = ResolveInstancingDisabledReasonEnumType();
            object none = Enum.Parse(enumType, "None", false);
            noneValue = Convert.ToInt32(none, CultureInfo.InvariantCulture);
            enumTypeName = enumType.FullName + "," + enumType.Assembly.GetName().Name;
            reasonName = Enum.Format(enumType, reasonValue, "F");
        }

        static int ReadInstancingMode(string assetPath, out string modeName)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            var serialized = new SerializedObject(resource);
            SerializedProperty mode = serialized.FindProperty("m_Infos.m_InstancingMode");
            Assert.IsNotNull(mode, "VFX resource instancing mode property was not found.");
            int raw = mode.intValue;
            modeName = Enum.GetName(ResolveInstancingEnumType(), raw);
            Assert.IsNotEmpty(modeName, "VFX instancing mode enum value is invalid.");
            return raw;
        }

        static int ReadInstancingCapacity(string assetPath)
        {
            var serialized = new SerializedObject(GetResource(assetPath));
            SerializedProperty capacity = serialized.FindProperty("m_Infos.m_InstancingCapacity");
            Assert.IsNotNull(capacity, "VFX resource instancing capacity property was not found.");
            return capacity.intValue;
        }

        static int ResolveInstancingEnumValue(params string[] names)
        {
            Type enumType = ResolveInstancingEnumType();
            foreach (string name in names)
            {
                if (Enum.IsDefined(enumType, name)) return Convert.ToInt32(Enum.Parse(enumType, name, false), CultureInfo.InvariantCulture);
            }
            Assert.Fail("VFX instancing enum does not define any expected name: " + string.Join(",", names));
            return -1;
        }

        static string ResolveInstancingEnumTypeName()
        {
            Type enumType = ResolveInstancingEnumType();
            return enumType.FullName + "," + enumType.Assembly.GetName().Name;
        }

        static Type ResolveInstancingEnumType()
        {
            Type[] enumTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .Where(type => type != null && type.Name == "VFXInstancingMode" && type.IsEnum).ToArray();
            Assert.AreEqual(1, enumTypes.Length, "VFXInstancingMode enum type is unavailable or ambiguous.");
            return enumTypes[0];
        }

        static Type ResolveInstancingDisabledReasonEnumType()
        {
            Type[] enumTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .Where(type => type != null && type.Name == "VFXInstancingDisabledReason" && type.IsEnum).ToArray();
            Assert.AreEqual(1, enumTypes.Length, "VFXInstancingDisabledReason enum type is unavailable or ambiguous.");
            return enumTypes[0];
        }

        static UnityEngine.Object GetResource(string assetPath)
        {
            Type resourceType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.Name == "VisualEffectResource" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(resourceType, "UnityEditor.VFX.VisualEffectResource type was not found.");
            MethodInfo getResource = resourceType.GetMethod("GetResourceAtPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(getResource, "VisualEffectResource.GetResourceAtPath(string) was not found.");
            UnityEngine.Object resource = getResource.Invoke(null, new object[] { assetPath }) as UnityEngine.Object;
            Assert.IsNotNull(resource, "VFX Graph editor resource could not be loaded from " + assetPath);
            return resource;
        }

        static void WriteResource(string assetPath)
        {
            UnityEngine.Object resource = GetResource(assetPath);
            Type extensions = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.Name == "VisualEffectResourceExtensions" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(extensions, "VisualEffectResourceExtensions type was not found.");
            MethodInfo write = extensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "WriteAssetWithSubAssets" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(write, "VisualEffectResource.WriteAssetWithSubAssets() was not found.");
            write.Invoke(null, new object[] { resource });
        }

        static ParticleDataInfo FindParticleData(string assetPath, uint requiredCapacity)
        {
            var candidates = new List<ParticleDataInfo>();
            UnityEngine.Object resource = GetResource(assetPath);
            Type extensions = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.Name == "VisualEffectResourceExtensions" && type.Namespace == "UnityEditor.VFX");
            Assert.IsNotNull(extensions, "VisualEffectResourceExtensions type was not found.");
            MethodInfo getGraph = extensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "GetGraph" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(getGraph, "VisualEffectResourceExtensions.GetGraph(VisualEffectResource) was not found.");
            object graph = getGraph.Invoke(null, new[] { resource });
            Assert.IsNotNull(graph, "The editor VFX graph could not be loaded from " + assetPath);
            MethodInfo collectDependencies = graph.GetType().GetMethod("CollectDependencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(HashSet<ScriptableObject>), typeof(bool) }, null);
            Assert.IsNotNull(collectDependencies, "VFXGraph.CollectDependencies(HashSet<ScriptableObject>, bool) was not found.");
            var dependencies = new HashSet<ScriptableObject>();
            collectDependencies.Invoke(graph, new object[] { dependencies, false });
            foreach (ScriptableObject obj in dependencies)
            {
                if (obj == null || obj.GetType().Name != "VFXDataParticle") continue;
                var serialized = new SerializedObject(obj);
                SerializedProperty capacity = serialized.FindProperty("capacity");
                if (capacity == null) continue;
                candidates.Add(new ParticleDataInfo { target = obj, name = obj.name, capacity = unchecked((uint)capacity.intValue) });
            }
            ParticleDataInfo[] matches = candidates.Where(value => value.capacity == requiredCapacity).ToArray();
            return new ParticleDataInfo
            {
                target = matches.Length == 1 ? matches[0].target : null,
                name = matches.Length == 1 ? matches[0].name : "",
                capacity = requiredCapacity,
                matches = matches.Length
            };
        }

        static string StructureHash(string assetPath, uint particleCapacity, int modeValue, int instancingCapacity, Vector2 gridResolution)
        {
            string text = File.ReadAllText(ToAbsoluteAssetPath(assetPath));
            text = NormalizeYamlInteger(text, "capacity", checked((int)particleCapacity));
            text = NormalizeGridVector(text, gridResolution);
            text = NormalizeYamlInteger(text, "m_InstancingMode", modeValue);
            text = NormalizeYamlInteger(text, "m_InstancingCapacity", instancingCapacity);
            return HashText(text);
        }

        static string ComparableStructureHash(string assetPath)
        {
            string text = File.ReadAllText(ToAbsoluteAssetPath(assetPath));
            text = NormalizeYamlIntegerWithoutExpectedValue(text, "capacity");
            text = NormalizeGridVectorWithoutExpectedValue(text);
            text = NormalizeYamlIntegerWithoutExpectedValue(text, "m_InstancingMode");
            text = NormalizeYamlIntegerWithoutExpectedValue(text, "m_InstancingCapacity");
            return HashText(text);
        }

        static string NormalizeGridVector(string text, Vector2 expectedValue)
        {
            GridSlotIdentity identity = ReadGridSlotIdentity(text, expectedValue);
            return NormalizeGridVectorDocument(text, identity.outputSlotFileId);
        }

        static string NormalizeGridVectorWithoutExpectedValue(string text)
        {
            Vector2 value = ReadGridValueFromYaml(text);
            GridSlotIdentity identity = ReadGridSlotIdentity(text, value);
            return NormalizeGridVectorDocument(text, identity.outputSlotFileId);
        }

        static Vector2 ReadGridValueFromYaml(string text)
        {
            Match[] parameterDocuments = FindGridParameterDocuments(text);
            string parameterBody = parameterDocuments[0].Groups["body"].Value;
            Match outputSlot = Regex.Match(parameterBody, @"(?m)^  m_OutputSlots:[ \t]*\r?\n  - \{fileID: (?<id>\d+)\}[ \t]*$");
            Assert.IsTrue(outputSlot.Success, "Grid Resolution parameter output slot is unavailable.");
            Match[] slots = FindYamlDocuments(text).Where(document => document.Groups["id"].Value == outputSlot.Groups["id"].Value).ToArray();
            Assert.AreEqual(1, slots.Length, "Grid Resolution output slot document is missing or ambiguous.");
            Match serializedValue = Regex.Match(slots[0].Groups["body"].Value, @"(?m)^[ \t]*m_SerializableObject: '(?<json>\{""x"":-?[0-9.]+,""y"":-?[0-9.]+\})'[ \t]*$");
            Assert.IsTrue(serializedValue.Success, "Grid Resolution Vector2 serialized value is unavailable.");
            return JsonUtility.FromJson<Vector2>(serializedValue.Groups["json"].Value);
        }

        static string NormalizeGridVectorDocument(string text, string outputSlotFileId)
        {
            Match[] slots = FindYamlDocuments(text).Where(document => document.Groups["id"].Value == outputSlotFileId).ToArray();
            Assert.AreEqual(1, slots.Length, "Grid Resolution Vector2 output slot document is missing or ambiguous.");
            string body = slots[0].Groups["body"].Value;
            string pattern = @"(?m)^([ \t]*m_SerializableObject:[ \t]*)'(\{""x"":-?[0-9.]+,""y"":-?[0-9.]+\})'([ \t]*)$";
            Match value = Regex.Match(body, pattern);
            Assert.IsTrue(value.Success, "Grid Resolution Vector2 value could not be normalized.");
            Assert.AreEqual(1, Regex.Matches(body, pattern).Count, "Grid Resolution slot must contain exactly one serialized Vector2 value.");
            string normalizedBody = Regex.Replace(body, pattern, "$1'normalized-grid-resolution'$3");
            return text.Substring(0, slots[0].Index) + slots[0].Value.Replace(body, normalizedBody) + text.Substring(slots[0].Index + slots[0].Length);
        }

        static GridSlotIdentity ReadGridSlotIdentity(string text, Vector2 expectedValue)
        {
            Match[] parameterDocuments = FindGridParameterDocuments(text);
            string parameterFileId = parameterDocuments[0].Groups["id"].Value;
            string parameterBody = parameterDocuments[0].Groups["body"].Value;
            Match outputSlot = Regex.Match(parameterBody, @"(?m)^  m_OutputSlots:[ \t]*\r?\n  - \{fileID: (?<id>\d+)\}[ \t]*$");
            Assert.IsTrue(outputSlot.Success, "Grid Resolution parameter must own one output slot.");
            Assert.AreEqual(1, Regex.Matches(parameterBody, @"(?m)^  m_OutputSlots:[ \t]*\r?\n  - \{fileID: \d+\}[ \t]*$").Count, "Grid Resolution parameter output slot count is unexpected.");
            string outputSlotFileId = outputSlot.Groups["id"].Value;
            Match[] slots = FindYamlDocuments(text).Where(document => document.Groups["id"].Value == outputSlotFileId).ToArray();
            Assert.AreEqual(1, slots.Length, "Grid Resolution output slot document is missing or ambiguous.");
            string slotBody = slots[0].Groups["body"].Value;
            Assert.IsTrue(Regex.IsMatch(slotBody, @"(?m)^    m_Owner: \{fileID: " + Regex.Escape(parameterFileId) + @"\}\s*$"), "Grid Resolution slot master data is not owned by its parameter document.");
            Assert.IsTrue(slotBody.Contains("UnityEngine.Vector2,"), "Grid Resolution slot value type is not UnityEngine.Vector2.");
            Vector2 actual = ReadGridValueFromYaml(text);
            Assert.AreEqual(expectedValue, actual, "Serialized Grid Resolution Vector2 value does not match the expected value.");
            return new GridSlotIdentity { parameterFileId = parameterFileId, outputSlotFileId = outputSlotFileId };
        }

        static Match[] FindGridParameterDocuments(string text)
        {
            Match[] parameters = FindYamlDocuments(text).Where(document =>
                Regex.IsMatch(document.Groups["body"].Value, @"(?m)^  m_ExposedName: Grid Resolution\s*$") &&
                Regex.IsMatch(document.Groups["body"].Value, @"(?m)^  m_Exposed: 0\s*$")).ToArray();
            Assert.AreEqual(1, parameters.Length, "Serialized graph must contain exactly one non-exposed Grid Resolution parameter document.");
            return parameters;
        }

        static Match[] FindYamlDocuments(string text)
        {
            return Regex.Matches(text, @"(?ms)^--- !u!114 &(?<id>\d+)\s*\r?\n(?<body>.*?)(?=^--- !u!|\z)").Cast<Match>().ToArray();
        }

        static string NormalizeYamlInteger(string text, string fieldName, int expectedValue)
        {
            string pattern = "(?m)^([ \\t]*" + Regex.Escape(fieldName) + ":[ \\t]*)(-?\\d+)([ \\t]*)$";
            MatchCollection matches = Regex.Matches(text, pattern);
            Assert.AreEqual(1, matches.Count, "Expected exactly one serialized " + fieldName + " value.");
            int actual;
            Assert.IsTrue(int.TryParse(matches[0].Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out actual), "Serialized " + fieldName + " value is not an integer.");
            Assert.AreEqual(expectedValue, actual, "Serialized " + fieldName + " value does not match the permitted fixture input.");
            return Regex.Replace(text, pattern, "$1normalized-value$3", RegexOptions.Multiline);
        }

        static string NormalizeYamlIntegerWithoutExpectedValue(string text, string fieldName)
        {
            string pattern = "(?m)^([ \\t]*" + Regex.Escape(fieldName) + ":[ \\t]*)(-?\\d+)([ \\t]*)$";
            MatchCollection matches = Regex.Matches(text, pattern);
            Assert.AreEqual(1, matches.Count, "Expected exactly one serialized " + fieldName + " value for pairwise comparison.");
            int actual;
            Assert.IsTrue(int.TryParse(matches[0].Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out actual), "Serialized " + fieldName + " value is not an integer.");
            return Regex.Replace(text, pattern, "$1normalized-value$3", RegexOptions.Multiline);
        }

        static DescriptorInfo ReadCompiledDescriptors(string assetPath)
        {
            Type inventory = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(type => type != null && type.FullName == "ViewWeave.SceneComparison.VfxDescriptorInventory");
            Assert.IsNotNull(inventory, "The existing CPU VFX descriptor inventory helper is unavailable.");
            MethodInfo compile = inventory.GetMethod("CompileAndDescribe", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(compile, "The existing CompileAndDescribe API was not found.");
            string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            object record = compile.Invoke(null, new object[] { assetPath, assetGuid, projectRoot });
            Assert.IsNotNull(record, "CPU VFX descriptor inventory returned no record.");
            Type recordType = record.GetType();
            string status = Convert.ToString(ReadPublicField(recordType, record, "compileStatus"), CultureInfo.InvariantCulture);
            Assert.AreEqual("success", status, "CPU VFX descriptor compile or descriptor mapping validation failed.");
            Assert.IsTrue(Convert.ToBoolean(ReadPublicField(recordType, record, "descriptorFieldsComplete"), CultureInfo.InvariantCulture), "VFX CPU descriptor fields are incomplete.");
            Assert.IsTrue(Convert.ToBoolean(ReadPublicField(recordType, record, "mappingIndicesValid"), CultureInfo.InvariantCulture), "VFX descriptor mapping indices are invalid.");
            string sourceBefore = Convert.ToString(ReadPublicField(recordType, record, "sourceSha256Before"), CultureInfo.InvariantCulture);
            string sourceAfter = Convert.ToString(ReadPublicField(recordType, record, "sourceSha256After"), CultureInfo.InvariantCulture);
            Assert.AreEqual(sourceBefore, sourceAfter, "VFX graph changed during CPU compilation.");
            Assert.AreEqual(Sha256(ToAbsoluteAssetPath(assetPath)), sourceAfter, "CPU descriptor inventory hash does not match the compiled graph file.");
            return new DescriptorInfo
            {
                compileOutputType = Convert.ToString(ReadPublicField(recordType, record, "compileOutputType"), CultureInfo.InvariantCulture),
                compileStatus = status,
                sourceSha256Before = sourceBefore,
                sourceSha256After = sourceAfter,
                gpuDescriptorCount = Convert.ToInt32(ReadPublicField(recordType, record, "gpuDescriptorCount"), CultureInfo.InvariantCulture),
                cpuDescriptorCount = Convert.ToInt32(ReadPublicField(recordType, record, "cpuDescriptorCount"), CultureInfo.InvariantCulture),
                temporaryDescriptorCount = Convert.ToInt32(ReadPublicField(recordType, record, "temporaryDescriptorCount"), CultureInfo.InvariantCulture),
                systemDescriptorCount = Convert.ToInt32(ReadPublicField(recordType, record, "systemDescriptorCount"), CultureInfo.InvariantCulture),
                gpuDescriptors = (string[])ReadPublicField(recordType, record, "gpuDescriptors"),
                cpuDescriptors = (string[])ReadPublicField(recordType, record, "cpuDescriptors"),
                temporaryDescriptors = (string[])ReadPublicField(recordType, record, "temporaryDescriptors"),
                systemsAndMappings = (string[])ReadPublicField(recordType, record, "systemsAndMappings")
            };
        }

        static object ReadPublicField(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, "CPU descriptor inventory field is missing: " + name);
            return field.GetValue(instance);
        }

        static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
        }

        static MethodInfo FindMethod(Type type, string name, params Type[] arguments)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type current = type; current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(name, flags, null, arguments ?? Type.EmptyTypes, null);
                if (method != null) return method;
            }
            return null;
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            int separator = assetFolder.LastIndexOf('/');
            Assert.Greater(separator, 0, "Invalid fixture folder path.");
            string parent = assetFolder.Substring(0, separator);
            string name = assetFolder.Substring(separator + 1);
            EnsureFolder(parent);
            string guid = AssetDatabase.CreateFolder(parent, name);
            Assert.IsNotEmpty(guid, "Could not create fixture folder " + assetFolder);
        }

        static int CountActiveSceneVfx()
        {
            return Resources.FindObjectsOfTypeAll<VisualEffect>().Count(effect => effect != null && effect.gameObject.scene.IsValid() && effect.gameObject.scene.isLoaded && effect.gameObject.activeInHierarchy);
        }

        static int CountActiveSceneCameras()
        {
            return Resources.FindObjectsOfTypeAll<Camera>().Count(camera => camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded && camera.enabled && camera.gameObject.activeInHierarchy);
        }

        static Camera[] GetLoadedSceneCameras()
        {
            return Resources.FindObjectsOfTypeAll<Camera>()
                .Where(camera => camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded)
                .ToArray();
        }

        static string SceneFilesFingerprint()
        {
            var rows = new List<string>();
            foreach (UnityEditor.SceneManagement.SceneSetup setup in EditorSceneManager.GetSceneManagerSetup())
            {
                string path = setup.path ?? "";
                if (string.IsNullOrEmpty(path))
                {
                    rows.Add("untitled-scene|" + setup.isLoaded.ToString());
                    continue;
                }
                string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
                rows.Add(path.Replace('\\', '/') + "|" + (File.Exists(fullPath) ? Sha256(fullPath) : "missing") + "|" + setup.isLoaded.ToString());
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

        [Serializable]
        sealed class DescriptorInfo
        {
            public string compileStatus;
            public string sourceSha256Before, sourceSha256After;
            public string compileOutputType;
            public int gpuDescriptorCount, cpuDescriptorCount, temporaryDescriptorCount, systemDescriptorCount;
            public string[] gpuDescriptors, cpuDescriptors, temporaryDescriptors, systemsAndMappings;
        }

        [Serializable]
        sealed class FixtureResult
        {
            public string schema, status, unityVersion, graphicsDevice, vfxPackageVersion, vfxPackageResolvedPath;
            public string sourceAssetPath, sourceAssetGuid, sourceAssetGuidAfter, sourceAssetSha256Before, sourceAssetSha256After, sourceGraphStructureSha256, sourceParticleDataName, instancingEnumType;
            public string gridParameterExposedName, gridParameterValueType;
            public string originalInstancingModeName, autoInstancingModeName, customInstancingModeName, customCompilerDisabledReasonName, customCompilerDisabledReasonEnumType;
            public string controlAssetPath, controlAssetGuid, controlAssetSha256, autoAssetPath, autoAssetGuid, autoAssetSha256, customAssetPath, customAssetGuid, customAssetSha256, descriptorScope;
            public string controlGraphStructureSha256, autoGraphStructureSha256, customGraphStructureSha256, controlComparableStructureSha256, autoComparableStructureSha256, customComparableStructureSha256, openSceneFilesFingerprintBefore, openSceneFilesFingerprintAfter, nunitOutcome, failureSummary;
            public string controlInstancingModeName;
            public string[] normalizedStructureFields;
            public long controlGridParameterFileId, controlGridOutputSlotFileId, autoGridParameterFileId, autoGridOutputSlotFileId, customGridParameterFileId, customGridOutputSlotFileId;
            public float controlGridResolutionX, controlGridResolutionY, autoGridResolutionX, autoGridResolutionY, customGridResolutionX, customGridResolutionY;
            public int originalInstancingModeValue, originalInstancingCapacity, controlInstancingModeValue, controlInstancingCapacity, autoInstancingModeValue, autoInstancingCapacity, customInstancingModeValue, customInstancingCapacity, customCompilerDisabledReasonValue, customCompilerDisabledReasonNoneValue, activeSceneCameraCount, activeSceneVfxCount;
            public int loadedSceneCameraCountBeforeIsolation, activeSceneCameraCountBeforeIsolation, activeSceneCameraCountDisabledInMemory;
            public uint sourceParticleCapacity, fixtureParticleCapacity;
            public bool sourceAssetUnchanged, openSceneFilesUnchanged, gridParameterExposed;
            public CameraStateRecord[] loadedSceneCameraStatesBeforeIsolation;
            public DescriptorInfo controlDescriptors, autoDescriptors, customDescriptors;
        }

        [Serializable]
        sealed class CameraStateRecord
        {
            public string sceneName, cameraName;
            public bool enabledAtLoad, activeInHierarchyAtLoad;
        }

        sealed class GridParameterInfo
        {
            public object parameter;
            public string parameterFileId, outputSlotFileId, exposedName, valueType;
            public bool exposed;
            public Vector2 value;
        }

        sealed class GridSlotIdentity
        {
            public string parameterFileId, outputSlotFileId;
        }
    }
}
#endif

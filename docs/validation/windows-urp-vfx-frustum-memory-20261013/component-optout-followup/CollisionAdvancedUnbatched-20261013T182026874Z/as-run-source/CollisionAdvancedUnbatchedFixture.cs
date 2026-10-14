#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
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
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;

namespace ViewWeave.SceneComparison.Tests
{
    public sealed class CollisionAdvancedUnbatchedFixture
    {
        const string SourceScenePath = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const string SourceGraphPath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/CollisionAdvanced.vfx";
        const string SourcePrefabPath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Collision Advanced.prefab";
        const string FixtureRoot = "Assets/FrustumMemoryCollisionUnbatched";
        const string FixturePrefabPath = FixtureRoot + "/Collision Advanced.prefab";
        const string FixtureScenePath = FixtureRoot + "/URP_SelectedPilot.unity";
        const string ResultEnvironment = "VW_COLLISION_ADVANCED_UNBATCHED_FIXTURE_RESULT";
        const string SourceGraphGuid = "fdd4e278b167d3f41a9bfeabcdb3ff21";
        const string SourceGraphSha256 = "D12815212B21A7C9F20C3DE7A635AA3800BD80DC7DC0FC6904C965F25FBBE3BD";
        const string SourcePrefabGuid = "87f78a0aa2642774ea793b42f5984c6a";
        const string SourcePrefabSha256 = "DA7D26C4040489F13DB8276CDDCF5C0A72B5F9ABF1689452970803B8501513CD";
        const string SourceSceneGuid = "ba07c3d71e7f23846a0a2a39ae625622";
        const string SourceSceneSha256 = "A47FECCDA2F348A0598A8E58133ABA539C1D1F94625BCC0A069676155F2D335D";
        const int ParticleCapacity = 162000;
        const string FixtureRootName = "CollisionAdvancedUnbatchedFixture";
        const string SharedPrefabGuid = "7837c49e89b12db43b2a59edba6bff6b";
        const string ShowcaseSeedKey = "Collision Advanced";
        const long DescriptionTextFileId = 6393118192857248175L;
        static readonly long[] TitleTextFileIds = { 3385902144265741256L, 3796978554488964388L, 6424475011069275086L };

        FieldInfo showcaseTitlesField;
        FieldInfo showcaseDescriptionsField;
        FieldInfo showcaseIntroductionField;
        object previousShowcaseTitles;
        object previousShowcaseDescriptions;
        object previousShowcaseIntroduction;
        bool showcaseReferencesCaptured;

        [Serializable]
        sealed class FixtureRecord
        {
            public string schema = "viewweave-collision-advanced-unbatched-fixture-v1";
            public string status = "started";
            public string failureType = "";
            public string failureMessage = "";
            public string unityVersion = "";
            public string graphicsApi = "";
            public string sourceScenePath = SourceScenePath;
            public string sourceSceneGuid = "";
            public string sourceSceneSha256Before = "";
            public string sourceSceneSha256After = "";
            public string fixtureScenePath = FixtureScenePath;
            public string fixtureSceneGuid = "";
            public string fixtureSceneSha256 = "";
            public string sourceGraphPath = SourceGraphPath;
            public string sourceGraphGuid = "";
            public string sourceGraphSha256Before = "";
            public string sourceGraphSha256After = "";
            public string sourcePrefabPath = SourcePrefabPath;
            public string sourcePrefabGuid = "";
            public string sourcePrefabSha256Before = "";
            public string sourcePrefabSha256After = "";
            public string fixturePrefabPath = FixturePrefabPath;
            public string fixturePrefabGuid = "";
            public string fixturePrefabSha256 = "";
            public string fixturePrefabAllowInstancing = "";
            public string sourceParticleCapacity = "";
            public string sourceGraphMotionVectorEnabledField = "";
            public string sourceGraphStaticMeshOutputCount = "";
            public string fixtureVfxComponentCount = "";
            public string fixtureSelectedGraphComponentCount = "";
            public string fixtureSelectedAllowInstancing = "";
            public string componentNonAllowOverridesMatch = "";
            public string componentAllowOverrideSaved = "";
            public string originalSceneInstanceInactive = "";
            public string originalSceneVfxComponentRemoved = "";
            public string fixtureSceneSelectedGraphComponentCount = "";
            public string fixtureSceneSaved = "";
            public string sourceFilesUnchanged = "";
            public string noGraphicsNullDevice = "";
            public string editorNotPlaying = "";
            public string showcaseMetadataSeedApplied = "";
            public string showcaseMetadataMappingValidated = "";
            public string showcaseMetadataReferencesRestored = "";
        }

        [Test]
        public void BuildsIsolatedCollisionAdvancedUnbatchedSceneAndPrefab()
        {
            FixtureRecord record = new FixtureRecord();
            string resultPath = Environment.GetEnvironmentVariable(ResultEnvironment);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string sourceGraphAbsolute = Absolute(SourceGraphPath);
            string sourcePrefabAbsolute = Absolute(SourcePrefabPath);
            string sourceSceneAbsolute = Absolute(SourceScenePath);
            try
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(resultPath), ResultEnvironment + " is required.");
                resultPath = Path.GetFullPath(resultPath);
                Assert.IsTrue(resultPath.StartsWith(Path.Combine(projectRoot, "TestResults") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture evidence must be under clone TestResults.");
                Assert.IsFalse(File.Exists(resultPath), "Refusing to overwrite fixture evidence.");
                Assert.AreEqual("6000.6.4f1", Application.unityVersion);
                Assert.AreEqual(RuntimePlatform.WindowsEditor, Application.platform);
                Assert.IsFalse(Application.isPlaying || EditorApplication.isPlaying, "Fixture preparation must run in EditMode, not PlayMode.");
                Assert.AreEqual(UnityEngine.Rendering.GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType, "Fixture preparation must use the Null graphics device.");
                record.unityVersion = Application.unityVersion;
                record.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
                record.editorNotPlaying = "true";
                record.noGraphicsNullDevice = "true";
                SeedShowcaseMetadataFromRawPrefab(sourcePrefabAbsolute);
                record.showcaseMetadataSeedApplied = "true";
                record.sourceGraphGuid = AssetDatabase.AssetPathToGUID(SourceGraphPath);
                record.sourcePrefabGuid = AssetDatabase.AssetPathToGUID(SourcePrefabPath);
                record.sourceSceneGuid = AssetDatabase.AssetPathToGUID(SourceScenePath);
                Assert.AreEqual(SourceGraphGuid, record.sourceGraphGuid);
                Assert.AreEqual(SourcePrefabGuid, record.sourcePrefabGuid);
                Assert.AreEqual(SourceSceneGuid, record.sourceSceneGuid);
                record.sourceGraphSha256Before = HashFile(sourceGraphAbsolute);
                record.sourcePrefabSha256Before = HashFile(sourcePrefabAbsolute);
                record.sourceSceneSha256Before = HashFile(sourceSceneAbsolute);
                Assert.AreEqual(SourceGraphSha256, record.sourceGraphSha256Before);
                Assert.AreEqual(SourcePrefabSha256, record.sourcePrefabSha256Before);
                Assert.AreEqual(SourceSceneSha256, record.sourceSceneSha256Before);

                Assert.IsFalse(AssetDatabase.IsValidFolder(FixtureRoot), "Refusing to reuse a fixture folder.");
                Assert.IsFalse(File.Exists(Absolute(FixtureScenePath)), "Refusing to overwrite fixture scene.");
                EnsureFolder(FixtureRoot);
                Assert.IsTrue(AssetDatabase.CopyAsset(SourcePrefabPath, FixturePrefabPath), "Could not copy the Collision Advanced prefab into the isolated fixture.");
                AssetDatabase.ImportAsset(FixturePrefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                AssetDatabase.ImportAsset(SourcePrefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                GameObject sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
                GameObject fixturePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FixturePrefabPath);
                Assert.IsNotNull(sourcePrefab);
                Assert.IsNotNull(fixturePrefab);
                ValidateShowcaseMetadataMapping(sourcePrefab);
                record.showcaseMetadataMappingValidated = "true";
                VisualEffect sourceEffect = FindSelectedEffect(sourcePrefab, SourceGraphGuid);
                VisualEffect fixtureEffect = FindSelectedEffect(fixturePrefab, SourceGraphGuid);
                Assert.IsNotNull(sourceEffect);
                Assert.IsNotNull(fixtureEffect);
                bool sourceAllow = ReadAllowInstancing(sourceEffect);
                Assert.IsTrue(sourceAllow, "The source component is expected to retain allowInstancing=true.");
                var sourceOverrides = CanonicalOverrides(PrefabUtility.GetPropertyModifications(sourcePrefab), "m_AllowInstancing");
                string[] sourceAllowOverrides = CanonicalOverridesForPath(PrefabUtility.GetPropertyModifications(sourcePrefab), "m_AllowInstancing");

                string sourceGraphYaml = File.ReadAllText(sourceGraphAbsolute);
                record.sourceParticleCapacity = ReadParticleCapacity(sourceGraphYaml).ToString(CultureInfo.InvariantCulture);
                record.sourceGraphMotionVectorEnabledField = FindMotionVectorEnabled(sourceGraphYaml);
                record.sourceGraphStaticMeshOutputCount = CountStaticMeshOutputs(sourceGraphYaml).ToString(CultureInfo.InvariantCulture);
                Assert.AreEqual(ParticleCapacity.ToString(CultureInfo.InvariantCulture), record.sourceParticleCapacity);
                Assert.AreEqual("1", record.sourceGraphMotionVectorEnabledField, "The source motion-vector flag must remain enabled.");
                Assert.Greater(int.Parse(record.sourceGraphStaticMeshOutputCount, CultureInfo.InvariantCulture), 0, "The source graph must retain its StaticMesh output.");

                GameObject contents = PrefabUtility.LoadPrefabContents(FixturePrefabPath);
                try
                {
                    VisualEffect contentsEffect = FindSelectedEffect(contents, SourceGraphGuid);
                    Assert.IsNotNull(contentsEffect);
                    var serialized = new SerializedObject(contentsEffect);
                    SerializedProperty allow = serialized.FindProperty("m_AllowInstancing");
                    Assert.IsNotNull(allow, "VisualEffect.m_AllowInstancing is unavailable.");
                    allow.boolValue = false;
                    Assert.IsTrue(serialized.ApplyModifiedProperties(), "Could not apply the isolated component allowInstancing=false value.");
                    Assert.IsFalse(ReadAllowInstancing(contentsEffect));
                    GameObject saved = PrefabUtility.SaveAsPrefabAsset(contents, FixturePrefabPath);
                    Assert.IsNotNull(saved, "SaveAsPrefabAsset did not return the isolated Collision Advanced prefab.");
                }
                finally { PrefabUtility.UnloadPrefabContents(contents); }
                AssetDatabase.ImportAsset(FixturePrefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                fixturePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FixturePrefabPath);
                fixtureEffect = FindSelectedEffect(fixturePrefab, SourceGraphGuid);
                Assert.IsNotNull(fixtureEffect);
                record.fixturePrefabGuid = AssetDatabase.AssetPathToGUID(FixturePrefabPath);
                record.fixturePrefabSha256 = HashFile(Absolute(FixturePrefabPath));
                Assert.AreNotEqual(SourcePrefabGuid, record.fixturePrefabGuid, "The copied fixture prefab must have its own asset GUID.");
                record.fixturePrefabAllowInstancing = ReadAllowInstancing(fixtureEffect).ToString();
                Assert.IsFalse(ReadAllowInstancing(fixtureEffect), "Saved fixture prefab did not retain allowInstancing=false.");
                string[] fixtureOverrides = CanonicalOverrides(PrefabUtility.GetPropertyModifications(fixturePrefab), "m_AllowInstancing");
                Assert.AreEqual(sourceOverrides.Length, fixtureOverrides.Length, "The fixture prefab changed the count of non-allow component overrides.");
                CollectionAssert.AreEqual(sourceOverrides, fixtureOverrides, "The fixture prefab changed non-allow component overrides.");
                record.componentNonAllowOverridesMatch = "true";
                string[] fixtureAllowOverrides = CanonicalOverridesForPath(PrefabUtility.GetPropertyModifications(fixturePrefab), "m_AllowInstancing");
                Assert.AreEqual(1, fixtureAllowOverrides.Length, "The saved fixture must contain exactly one allowInstancing override.");
                Assert.IsTrue(fixtureAllowOverrides[0].EndsWith("|0||0", StringComparison.Ordinal) || fixtureAllowOverrides[0].EndsWith("|0|", StringComparison.Ordinal), "The fixture allowInstancing override is not serialized false.");
                if (sourceAllowOverrides.Length == 1)
                {
                    string sourceIdentity = string.Join("|", sourceAllowOverrides[0].Split('|').Take(3));
                    string fixtureIdentity = string.Join("|", fixtureAllowOverrides[0].Split('|').Take(3));
                    Assert.AreEqual(sourceIdentity, fixtureIdentity, "The allowInstancing override targets a different component than the source override.");
                }
                else Assert.AreEqual(0, sourceAllowOverrides.Length, "The source prefab contains ambiguous allowInstancing overrides.");
                record.componentAllowOverrideSaved = "true";

                Assert.IsTrue(AssetDatabase.CopyAsset(SourceScenePath, FixtureScenePath), "Could not copy the prepared pilot scene.");
                AssetDatabase.ImportAsset(FixtureScenePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Scene fixtureScene = EditorSceneManager.OpenScene(FixtureScenePath, OpenSceneMode.Single);
                Assert.IsTrue(fixtureScene.IsValid() && fixtureScene.isLoaded, "The isolated Collision Advanced scene did not load.");
                VisualEffect[] sceneEffects = Resources.FindObjectsOfTypeAll<VisualEffect>()
                    .Where(value => value != null && value.gameObject.scene == fixtureScene).ToArray();
                VisualEffect[] sourceMatches = sceneEffects.Where(value => value.visualEffectAsset != null &&
                    string.Equals(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value.visualEffectAsset)), SourceGraphGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
                Assert.AreEqual(1, sourceMatches.Length, "Expected exactly one original Collision Advanced instance in the copied pilot scene.");
                VisualEffect originalEffect = sourceMatches[0];
                GameObject originalPrefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(originalEffect.gameObject);
                Assert.IsNotNull(originalPrefabRoot, "The original Collision Advanced prefab instance root is missing.");
                Transform parent = originalPrefabRoot.transform.parent;
                Vector3 localPosition = originalPrefabRoot.transform.localPosition;
                Quaternion localRotation = originalPrefabRoot.transform.localRotation;
                Vector3 localScale = originalPrefabRoot.transform.localScale;
                int siblingIndex = originalPrefabRoot.transform.GetSiblingIndex();
                originalPrefabRoot.SetActive(false);
                record.originalSceneInstanceInactive = !originalPrefabRoot.activeSelf ? "true" : "false";
                UnityEngine.Object.DestroyImmediate(originalEffect, true);
                Assert.IsTrue(originalEffect == null, "The original disabled CollisionAdvanced VFX component was not removed from the clone scene.");
                Assert.IsTrue(originalPrefabRoot != null && !originalPrefabRoot.activeSelf, "The original clone-scene prefab root and its geometry must remain inactive.");
                record.originalSceneVfxComponentRemoved = "true";

                fixturePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FixturePrefabPath);
                GameObject instance = PrefabUtility.InstantiatePrefab(fixturePrefab, fixtureScene) as GameObject;
                Assert.IsNotNull(instance, "Could not instantiate the isolated prefab into the copied scene.");
                instance.name = FixtureRootName;
                instance.transform.SetParent(parent, false);
                instance.transform.localPosition = localPosition;
                instance.transform.localRotation = localRotation;
                instance.transform.localScale = localScale;
                instance.transform.SetSiblingIndex(Mathf.Min(siblingIndex + 1, parent == null ? fixtureScene.rootCount - 1 : parent.childCount - 1));
                instance.SetActive(true);
                VisualEffect[] fixtureEffects = instance.GetComponentsInChildren<VisualEffect>(true);
                Assert.AreEqual(1, fixtureEffects.Length, "Fixture prefab must contain exactly one VisualEffect component.");
                VisualEffect selected = fixtureEffects[0];
                Assert.AreSame(AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(SourceGraphPath), selected.visualEffectAsset);
                Assert.IsFalse(ReadAllowInstancing(selected), "Scene instance override did not retain allowInstancing=false.");
                selected.gameObject.SetActive(false);
                foreach (VisualEffect effect in sceneEffects.Where(value => value != originalEffect))
                    if (effect.gameObject.activeSelf) effect.gameObject.SetActive(false);

                VisualEffect[] remainingSceneMatches = Resources.FindObjectsOfTypeAll<VisualEffect>()
                    .Where(value => value != null && value.gameObject.scene == fixtureScene && value.visualEffectAsset != null &&
                        string.Equals(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value.visualEffectAsset)), SourceGraphGuid, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.AreEqual(1, remainingSceneMatches.Length, "The prepared clone scene must contain exactly one component linked to the CollisionAdvanced source graph.");
                Assert.AreSame(selected, remainingSceneMatches[0], "The unique CollisionAdvanced scene component must be the serialized opt-out fixture.");
                record.fixtureSceneSelectedGraphComponentCount = remainingSceneMatches.Length.ToString(CultureInfo.InvariantCulture);

                Assert.IsTrue(EditorSceneManager.SaveScene(fixtureScene), "Could not save isolated fixture scene.");
                record.fixtureSceneGuid = AssetDatabase.AssetPathToGUID(FixtureScenePath);
                Assert.AreNotEqual(SourceSceneGuid, record.fixtureSceneGuid, "The copied fixture scene must have its own asset GUID.");
                record.fixtureSceneSha256 = HashFile(Absolute(FixtureScenePath));
                record.fixturePrefabPath = FixturePrefabPath;
                record.fixtureSceneSaved = "true";
                Assert.AreEqual(SourceGraphSha256, HashFile(sourceGraphAbsolute), "Source graph changed while building the fixture.");
                Assert.AreEqual(SourcePrefabSha256, HashFile(sourcePrefabAbsolute), "Source prefab changed while building the fixture.");
                Assert.AreEqual(SourceSceneSha256, HashFile(sourceSceneAbsolute), "Source scene changed while building the fixture.");
                record.sourceGraphSha256After = HashFile(sourceGraphAbsolute);
                record.sourcePrefabSha256After = HashFile(sourcePrefabAbsolute);
                record.sourceSceneSha256After = HashFile(sourceSceneAbsolute);
                record.sourceFilesUnchanged = "true";
                record.fixtureVfxComponentCount = fixtureEffects.Length.ToString(CultureInfo.InvariantCulture);
                record.fixtureSelectedGraphComponentCount = fixtureEffects.Count(effect => effect.visualEffectAsset != null &&
                    AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(effect.visualEffectAsset)) == SourceGraphGuid).ToString(CultureInfo.InvariantCulture);
                record.fixtureSelectedAllowInstancing = ReadAllowInstancing(selected).ToString();
                Assert.IsFalse(ReadAllowInstancing(selected));
                record.status = "passed";
            }
            catch (Exception exception)
            {
                record.status = "failed";
                record.failureType = exception.GetType().Name;
                record.failureMessage = Ascii(exception.Message);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(sourceGraphAbsolute)) record.sourceGraphSha256After = HashFile(sourceGraphAbsolute);
                    if (File.Exists(sourcePrefabAbsolute)) record.sourcePrefabSha256After = HashFile(sourcePrefabAbsolute);
                    if (File.Exists(sourceSceneAbsolute)) record.sourceSceneSha256After = HashFile(sourceSceneAbsolute);
                    try
                    {
                        RestoreShowcaseMetadataReferences();
                        record.showcaseMetadataReferencesRestored = showcaseReferencesCaptured ? "false" : "true";
                    }
                    catch { record.showcaseMetadataReferencesRestored = "false"; }
                    record.sourceFilesUnchanged = record.sourceGraphSha256Before == record.sourceGraphSha256After &&
                        record.sourcePrefabSha256Before == record.sourcePrefabSha256After && record.sourceSceneSha256Before == record.sourceSceneSha256After ? "true" : "false";
                    if (record.showcaseMetadataReferencesRestored != "true" || record.sourceFilesUnchanged != "true") record.status = "failed";
                    if (!string.IsNullOrWhiteSpace(resultPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
                        File.WriteAllText(resultPath, JsonUtility.ToJson(record, true), new UTF8Encoding(false));
                    }
                }
                catch { }
            }
        }

        static VisualEffect FindSelectedEffect(GameObject root, string guid)
        {
            return root.GetComponentsInChildren<VisualEffect>(true).SingleOrDefault(effect => effect.visualEffectAsset != null &&
                string.Equals(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(effect.visualEffectAsset)), guid, StringComparison.OrdinalIgnoreCase));
        }

        static bool ReadAllowInstancing(VisualEffect effect)
        {
            var serialized = new SerializedObject(effect);
            SerializedProperty property = serialized.FindProperty("m_AllowInstancing");
            Assert.IsNotNull(property, "VisualEffect.m_AllowInstancing is unavailable.");
            return property.boolValue;
        }

        static string[] CanonicalOverrides(PropertyModification[] modifications, string excludedPath)
        {
            return (modifications ?? Array.Empty<PropertyModification>())
                .Where(value => value != null && value.propertyPath != excludedPath)
                .Select(value =>
                {
                    string targetGuid = "";
                    long targetFileId = 0;
                    if (value.target != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value.target, out targetGuid, out targetFileId);
                    string refGuid = "";
                    long refFileId = 0;
                    if (value.objectReference != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value.objectReference, out refGuid, out refFileId);
                    return targetGuid + "|" + targetFileId.ToString(CultureInfo.InvariantCulture) + "|" + value.propertyPath + "|" + (value.value ?? "") + "|" + refGuid + "|" + refFileId.ToString(CultureInfo.InvariantCulture);
                }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        static string[] CanonicalOverridesForPath(PropertyModification[] modifications, string propertyPath)
        {
            return (modifications ?? Array.Empty<PropertyModification>())
                .Where(value => value != null && value.propertyPath == propertyPath)
                .Select(value =>
                {
                    string targetGuid = "";
                    long targetFileId = 0;
                    if (value.target != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value.target, out targetGuid, out targetFileId);
                    string refGuid = "";
                    long refFileId = 0;
                    if (value.objectReference != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value.objectReference, out refGuid, out refFileId);
                    return targetGuid + "|" + targetFileId.ToString(CultureInfo.InvariantCulture) + "|" + value.propertyPath + "|" + (value.value ?? "") + "|" + refGuid + "|" + refFileId.ToString(CultureInfo.InvariantCulture);
                }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        static uint ReadParticleCapacity(string yaml)
        {
            var match = System.Text.RegularExpressions.Regex.Match(yaml, @"(?m)^  capacity: (\d+)\s*$");
            Assert.IsTrue(match.Success, "Could not read source VFX particle capacity from pinned graph YAML.");
            return uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        static string FindMotionVectorEnabled(string yaml)
        {
            var match = System.Text.RegularExpressions.Regex.Match(yaml, @"(?m)^  generateMotionVector: (\d+)\s*$");
            Assert.IsTrue(match.Success, "Could not read source motion-vector setting.");
            return match.Groups[1].Value;
        }

        static int CountStaticMeshOutputs(string yaml) => Regex.Matches(yaml,
            @"(?m)^  m_Script: \{fileID: 11500000, guid: 5c71b248ba46954449b47392dc6c3bf9, type: 3\}\s*$").Count;

        void SeedShowcaseMetadataFromRawPrefab(string sourcePrefabAbsolute)
        {
            string yaml = File.ReadAllText(sourcePrefabAbsolute, Encoding.UTF8);
            string[] titleValues = TitleTextFileIds.Select(fileId => ReadRawTextOverride(yaml, fileId)).ToArray();
            string description = ReadRawTextOverride(yaml, DescriptionTextFileId);
            Assert.AreEqual(3, titleValues.Length);
            Assert.IsTrue(titleValues.All(value => string.Equals(value, titleValues[0], StringComparison.Ordinal)), "Source showcase title overrides differ.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(titleValues[0]) || string.IsNullOrWhiteSpace(description), "Source showcase title or description is empty.");

            Type showcaseType = FindUniqueLoadedType("SamplesShowcase");
            showcaseTitlesField = showcaseType.GetField("SanitizedTitles", BindingFlags.Public | BindingFlags.Static);
            showcaseDescriptionsField = showcaseType.GetField("SanitizedDescriptions", BindingFlags.Public | BindingFlags.Static);
            showcaseIntroductionField = showcaseType.GetField("SanitizedIntroduction", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(showcaseTitlesField, "Public SamplesShowcase.SanitizedTitles field is unavailable.");
            Assert.IsNotNull(showcaseDescriptionsField, "Public SamplesShowcase.SanitizedDescriptions field is unavailable.");
            Assert.IsNotNull(showcaseIntroductionField, "Public SamplesShowcase.SanitizedIntroduction field is unavailable.");
            Assert.AreEqual(typeof(Dictionary<string, string>), showcaseTitlesField.FieldType);
            Assert.AreEqual(typeof(Dictionary<string, string>), showcaseDescriptionsField.FieldType);
            Assert.AreEqual(typeof(string), showcaseIntroductionField.FieldType);
            previousShowcaseTitles = showcaseTitlesField.GetValue(null);
            previousShowcaseDescriptions = showcaseDescriptionsField.GetValue(null);
            previousShowcaseIntroduction = showcaseIntroductionField.GetValue(null);
            showcaseReferencesCaptured = true;
            var titles = new Dictionary<string, string>(StringComparer.Ordinal) { [ShowcaseSeedKey] = titleValues[0] };
            var descriptions = new Dictionary<string, string>(StringComparer.Ordinal) { [ShowcaseSeedKey] = description };
            showcaseTitlesField.SetValue(null, titles);
            showcaseDescriptionsField.SetValue(null, descriptions);
            Assert.IsTrue(ReferenceEquals(showcaseTitlesField.GetValue(null), titles) && ReferenceEquals(showcaseDescriptionsField.GetValue(null), descriptions),
                "Could not apply source-bound showcase metadata dictionaries.");
        }

        static string ReadRawTextOverride(string yaml, long targetFileId)
        {
            string pattern = @"(?ms)^    - target: \{fileID: " + targetFileId.ToString(CultureInfo.InvariantCulture) +
                @", guid: " + SharedPrefabGuid + @",\s*type: 3\}\r?\n      propertyPath: m_text\r?\n      value: (?<value>.*?)(?=\r?\n      objectReference: \{fileID: 0\})";
            Match[] matches = Regex.Matches(yaml, pattern).Cast<Match>().ToArray();
            Assert.AreEqual(1, matches.Length, "The hash-pinned source prefab TMP m_text override is missing or ambiguous.");
            string raw = matches[0].Groups["value"].Value.Replace("\r\n", "\n");
            raw = Regex.Replace(raw, @"\n[ \t]+", " ").Trim();
            if (raw.StartsWith("\"", StringComparison.Ordinal))
            {
                Assert.IsTrue(raw.EndsWith("\"", StringComparison.Ordinal), "The source TMP quoted value is incomplete.");
                return Regex.Unescape(raw.Substring(1, raw.Length - 2));
            }
            return raw;
        }

        static void ValidateShowcaseMetadataMapping(GameObject sourcePrefab)
        {
            Assert.IsNotNull(sourcePrefab);
            Assert.IsTrue(EditorUtility.IsPersistent(sourcePrefab) && PrefabUtility.IsPartOfPrefabAsset(sourcePrefab),
                "Showcase metadata validation must use the persistent source prefab.");
            Type infoType = FindUniqueLoadedType("GetSampleInfos");
            FieldInfo typeField = infoType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo prefabField = infoType.GetField("prefab", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(typeField);
            Assert.IsNotNull(prefabField);
            Component[] infos = sourcePrefab.GetComponentsInChildren<Component>(true).Where(value => value != null && infoType.IsInstanceOfType(value)).ToArray();
            Assert.AreEqual(4, infos.Length, "The source prefab showcase metadata component count changed.");
            int titleCount = 0;
            int descriptionCount = 0;
            foreach (Component info in infos)
            {
                object rawType = typeField.GetValue(info);
                object rawPrefab = prefabField.GetValue(info);
                Assert.IsNotNull(rawType);
                Assert.IsTrue(rawPrefab is GameObject);
                Assert.AreEqual(ShowcaseSeedKey, ((GameObject)rawPrefab).name, "Showcase seed key differs from the source prefab name.");
                string typeName = rawType.ToString();
                if (typeName == "Title") titleCount++;
                else if (typeName == "Description") descriptionCount++;
                else Assert.Fail("Unexpected GetSampleInfos type in the source prefab: " + typeName);
            }
            Assert.AreEqual(3, titleCount);
            Assert.AreEqual(1, descriptionCount);
        }

        void RestoreShowcaseMetadataReferences()
        {
            if (!showcaseReferencesCaptured) return;
            showcaseTitlesField.SetValue(null, previousShowcaseTitles);
            showcaseDescriptionsField.SetValue(null, previousShowcaseDescriptions);
            showcaseIntroductionField.SetValue(null, previousShowcaseIntroduction);
            Assert.IsTrue(ReferenceEquals(showcaseTitlesField.GetValue(null), previousShowcaseTitles) &&
                ReferenceEquals(showcaseDescriptionsField.GetValue(null), previousShowcaseDescriptions) &&
                ReferenceEquals(showcaseIntroductionField.GetValue(null), previousShowcaseIntroduction),
                "SamplesShowcase metadata references were not restored.");
            showcaseReferencesCaptured = false;
        }

        static Type FindUniqueLoadedType(string fullName)
        {
            Type[] matches = AppDomain.CurrentDomain.GetAssemblies().SelectMany(assembly =>
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null).ToArray(); }
            }).Where(type => type != null && string.Equals(type.FullName, fullName, StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(1, matches.Length, "Expected exactly one loaded type: " + fullName);
            return matches[0];
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            Assert.IsFalse(string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name), "Invalid fixture folder path.");
            EnsureFolder(parent);
            Assert.IsTrue(AssetDatabase.CreateFolder(parent, name).Length > 0, "Could not create fixture folder: " + Ascii(path));
        }

        static string Absolute(string assetPath)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(root, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        static string Ascii(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var builder = new StringBuilder(value.Length);
            foreach (char character in value) builder.Append(character >= 32 && character <= 126 ? character : '?');
            return builder.ToString();
        }
    }
}
#endif

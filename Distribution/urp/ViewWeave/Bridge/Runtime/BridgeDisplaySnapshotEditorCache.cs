#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ViewWeave
{
    [Serializable]
    internal sealed class BridgeDisplaySnapshotEditorCache
    {
        const int CurrentSchemaVersion = 1;
        const string EditorEpochKey = "ViewWeave.BridgeDisplaySnapshot.EditorEpoch";

        [SerializeField] int schemaVersion;
        [SerializeField] string editorEpoch;
        [SerializeField] string runtimePath;
        [SerializeField] string applicationName;
        [SerializeField] uint displayOrdinal;
        [SerializeField] bool usesGlobalObjectId;
        [SerializeField] string globalObjectId;
        [SerializeField] int outputHash;
        [SerializeField] uint generationHigh;
        [SerializeField] uint generationLow;
        [SerializeField] CachedDisplay[] displays;

        [Serializable]
        struct CachedDisplay
        {
            [SerializeField] internal uint ordinal;
            [SerializeField] internal uint displayIndex;
            [SerializeField] internal uint width;
            [SerializeField] internal uint height;
            [SerializeField] internal float aspect;
            [SerializeField] internal float viewcone;
            [SerializeField] internal int quiltWidth;
            [SerializeField] internal int quiltHeight;
            [SerializeField] internal int columns;
            [SerializeField] internal int rows;
            [SerializeField] internal bool hasPhysicalCalibration;
            [SerializeField] internal float physicalWidthMillimeters;
            [SerializeField] internal float physicalHeightMillimeters;
            [SerializeField] internal float calibrationDpi;

            internal CachedDisplay(uint displayOrdinal, BridgeDisplayProfile profile)
            {
                ordinal = displayOrdinal;
                displayIndex = profile.DisplayIndex;
                width = profile.Width;
                height = profile.Height;
                aspect = profile.Aspect;
                viewcone = profile.Viewcone;
                quiltWidth = profile.QuiltWidth;
                quiltHeight = profile.QuiltHeight;
                columns = profile.Columns;
                rows = profile.Rows;
                hasPhysicalCalibration = profile.HasPhysicalCalibration;
                physicalWidthMillimeters = profile.PhysicalWidthMillimeters;
                physicalHeightMillimeters = profile.PhysicalHeightMillimeters;
                calibrationDpi = profile.CalibrationDpi;
            }

            internal bool TryCreateProfile(out BridgeDisplayProfile profile) =>
                BridgeDisplayProfile.TryCreateCached(displayIndex, width, height, aspect, viewcone,
                    quiltWidth, quiltHeight, columns, rows, hasPhysicalCalibration,
                    physicalWidthMillimeters, physicalHeightMillimeters, calibrationDpi, out profile);
        }

        internal static BridgeDisplaySnapshotEditorCache TryCreate(BridgeWorkerQuiltOutput output,
            BridgeDisplaySnapshot snapshot, bool requiresReconnect, string configuredRuntimePath,
            string configuredApplicationName, uint configuredDisplayOrdinal)
        {
            if (output == null || snapshot == null || requiresReconnect ||
                !TryGetOutputIdentity(output, out bool usesGlobalId, out string globalId, out int objectHash)) return null;

            snapshot.CopyEntries(out uint[] ordinals, out BridgeDisplayProfile[] profiles);
            if (ordinals == null || profiles == null || ordinals.Length != profiles.Length ||
                profiles.Length > BridgeDisplayDiscoveryNative.MaximumDisplays) return null;

            var entries = new CachedDisplay[profiles.Length];
            var uniqueOrdinals = new HashSet<uint>();
            for (int i = 0; i < profiles.Length; ++i)
            {
                BridgeDisplayProfile profile = profiles[i];
                if (!profile.IsValid || !uniqueOrdinals.Add(ordinals[i]) ||
                    !BridgeDisplayProfile.TryCreateCached(profile.DisplayIndex, profile.Width, profile.Height,
                        profile.Aspect, profile.Viewcone, profile.QuiltWidth, profile.QuiltHeight,
                        profile.Columns, profile.Rows, profile.HasPhysicalCalibration,
                        profile.PhysicalWidthMillimeters, profile.PhysicalHeightMillimeters,
                        profile.CalibrationDpi, out _)) return null;
                entries[i] = new CachedDisplay(ordinals[i], profile);
            }

            string epoch = GetEditorEpoch();
            if (string.IsNullOrEmpty(epoch)) return null;
            ulong generation = snapshot.Generation;
            return new BridgeDisplaySnapshotEditorCache
            {
                schemaVersion = CurrentSchemaVersion,
                editorEpoch = epoch,
                runtimePath = configuredRuntimePath ?? string.Empty,
                applicationName = configuredApplicationName ?? string.Empty,
                displayOrdinal = configuredDisplayOrdinal,
                usesGlobalObjectId = usesGlobalId,
                globalObjectId = globalId ?? string.Empty,
                outputHash = objectHash,
                generationHigh = (uint)(generation >> 32),
                generationLow = (uint)generation,
                displays = entries
            };
        }

        internal bool TryRestore(BridgeWorkerQuiltOutput output, bool requiresReconnect,
            string configuredRuntimePath, string configuredApplicationName, uint configuredDisplayOrdinal,
            out BridgeDisplaySnapshot snapshot)
        {
            snapshot = null;
            if (schemaVersion != CurrentSchemaVersion || requiresReconnect || output == null ||
                !string.Equals(editorEpoch, GetEditorEpoch(), StringComparison.Ordinal) ||
                !string.Equals(runtimePath, configuredRuntimePath ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(applicationName, configuredApplicationName ?? string.Empty, StringComparison.Ordinal) ||
                displayOrdinal != configuredDisplayOrdinal || displays == null ||
                displays.Length > BridgeDisplayDiscoveryNative.MaximumDisplays ||
                !MatchesOutputIdentity(output)) return false;

            var ordinals = new uint[displays.Length];
            var profiles = new BridgeDisplayProfile[displays.Length];
            var uniqueOrdinals = new HashSet<uint>();
            for (int i = 0; i < displays.Length; ++i)
            {
                CachedDisplay entry = displays[i];
                if (!uniqueOrdinals.Add(entry.ordinal) || !entry.TryCreateProfile(out profiles[i])) return false;
                ordinals[i] = entry.ordinal;
            }

            ulong generation = ((ulong)generationHigh << 32) | generationLow;
            snapshot = new BridgeDisplaySnapshot(generation, ordinals, profiles);
            return true;
        }

        bool MatchesOutputIdentity(BridgeWorkerQuiltOutput output)
        {
            if (!TryGetOutputIdentity(output, out bool currentUsesGlobalId, out string currentGlobalId, out int currentHash))
                return false;
            return usesGlobalObjectId
                ? currentUsesGlobalId && string.Equals(globalObjectId, currentGlobalId, StringComparison.Ordinal)
                : outputHash == currentHash;
        }

        static bool TryGetOutputIdentity(BridgeWorkerQuiltOutput output, out bool usesGlobalId,
            out string globalId, out int objectHash)
        {
            usesGlobalId = false;
            globalId = string.Empty;
            objectHash = output == null ? 0 : output.GetHashCode();
            if (output == null || EditorUtility.IsPersistent(output) || output.gameObject == null) return false;
            var scene = output.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene) ||
                PrefabStageUtility.GetPrefabStage(output.gameObject) != null) return false;

            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(output);
            string assetGuid = id.assetGUID.ToString();
            if (id.identifierType != 0 && id.targetObjectId != 0 &&
                !string.IsNullOrEmpty(assetGuid) && !string.Equals(assetGuid,
                    "00000000000000000000000000000000", StringComparison.OrdinalIgnoreCase))
            {
                usesGlobalId = true;
                globalId = id.ToString();
                return !string.IsNullOrEmpty(globalId);
            }

            return true;
        }

        static string GetEditorEpoch()
        {
            string epoch = SessionState.GetString(EditorEpochKey, string.Empty);
            if (!string.IsNullOrEmpty(epoch)) return epoch;
            epoch = Guid.NewGuid().ToString("N");
            SessionState.SetString(EditorEpochKey, epoch);
            return epoch;
        }
    }
}
#endif

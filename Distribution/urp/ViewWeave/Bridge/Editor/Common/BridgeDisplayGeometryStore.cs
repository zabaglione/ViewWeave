using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ViewWeave.Editor
{
    /// <summary>Retains authoring geometry from successful Bridge API results for both output platforms.</summary>
    [FilePath("UserSettings/ViewWeaveDisplayGeometry.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class BridgeDisplayGeometryStore : ScriptableSingleton<BridgeDisplayGeometryStore>
    {
        const int Schema = 1;
        const string PlayKey = "ViewWeave.DisplayGeometry.PlayIdentities";
        const string TransientKey = "ViewWeave.DisplayGeometry.Transient";
        const string EpochKey = "ViewWeave.DisplayGeometry.Epoch";
        [SerializeField] List<Entry> entries = new List<Entry>();
        static PlayIdentities playIdentities;
        static TransientEntries transient;

        [Serializable] internal sealed class Entry
        {
            public int schema = Schema;
            public string identity, runtimePath, applicationName;
            public uint ordinal, displayIndex, width, height;
            public float aspect, viewCone;
            public int quiltWidth, quiltHeight, columns, rows;
            public bool hasPhysicalCalibration;
            public float widthMillimeters, heightMillimeters, dpi;

            internal bool TryProfile(out BridgeDisplayProfile profile) => BridgeDisplayProfile.TryCreateCached(
                displayIndex, width, height, aspect, viewCone, quiltWidth, quiltHeight, columns, rows,
                hasPhysicalCalibration, widthMillimeters, heightMillimeters, dpi, out profile);

            internal bool Matches(string key, string runtime, string application, uint displayOrdinal) =>
                schema == Schema && identity == key && runtimePath == (runtime ?? string.Empty) &&
                applicationName == (application ?? string.Empty) && ordinal == displayOrdinal;

            internal static Entry Create(string key, string runtime, string application, uint displayOrdinal,
                BridgeDisplayProfile p) => new Entry
                {
                    identity = key, runtimePath = runtime ?? string.Empty, applicationName = application ?? string.Empty,
                    ordinal = displayOrdinal, displayIndex = p.DisplayIndex, width = p.Width, height = p.Height,
                    aspect = p.Aspect, viewCone = p.Viewcone, quiltWidth = p.QuiltWidth, quiltHeight = p.QuiltHeight,
                    columns = p.Columns, rows = p.Rows, hasPhysicalCalibration = p.HasPhysicalCalibration,
                    widthMillimeters = p.PhysicalWidthMillimeters, heightMillimeters = p.PhysicalHeightMillimeters,
                    dpi = p.CalibrationDpi
                };
        }

        [Serializable] sealed class PlayIdentity
        { public string identity, rig, renderer, output; }
        [Serializable] sealed class PlayIdentities
        {
            public string epoch;
            public bool bound;
            public List<PlayIdentity> items = new List<PlayIdentity>();
            public List<DeferredRecognition> pending = new List<DeferredRecognition>();
        }
        [Serializable] sealed class DeferredRecognition
        { public PlayIdentity owner; public Entry entry; }
        [Serializable] sealed class TransientEntries
        { public List<Entry> items = new List<Entry>(); }

        internal static void CaptureEditIdentities()
        {
            SessionState.SetString(EpochKey, Guid.NewGuid().ToString("N"));
            playIdentities = new PlayIdentities { epoch = SessionState.GetString(EpochKey, "") };
            PersistPlayIdentities();
            foreach (ViewWeaveRig rig in UnityEngine.Object.FindObjectsByType<ViewWeaveRig>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                QuiltRenderer renderer = rig.GetComponent<QuiltRenderer>();
                MonoBehaviour output = ConfiguredOutput(renderer);
                if (TryConfiguration(renderer, out _, out _, out _, out _) && Allowed(rig, renderer, output))
                    rig.CaptureEditorDisplayIdentity(EditIdentity(rig, renderer, output), output);
            }
        }

        internal static void BindPlayIdentities()
        {
            LoadPlayIdentities();
            List<DeferredRecognition> pending = playIdentities?.pending;
            playIdentities = new PlayIdentities { epoch = SessionState.GetString(EpochKey, ""), bound = true };
            var duplicate = new HashSet<string>();
            var rigs = new Dictionary<string, ViewWeaveRig>();
            foreach (ViewWeaveRig rig in UnityEngine.Object.FindObjectsByType<ViewWeaveRig>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                QuiltRenderer renderer = rig.GetComponent<QuiltRenderer>();
                MonoBehaviour output = ConfiguredOutput(renderer);
                if (!Allowed(rig, renderer, output) || !rig.TryGetEditorDisplayIdentity(output, renderer, out string key)) continue;
                if (playIdentities.items.Exists(item => item.identity == key)) duplicate.Add(key);
                playIdentities.items.Add(new PlayIdentity { identity = key, rig = rig.GetEntityId().ToString(),
                    renderer = renderer.GetEntityId().ToString(), output = output.GetEntityId().ToString() });
                rigs[rig.GetEntityId().ToString()] = rig;
            }
            playIdentities.items.RemoveAll(item => duplicate.Contains(item.identity));
            PersistPlayIdentities();
            // Awake can successfully acquire Bridge metadata before EnteredPlayMode. Revalidate
            // its original instance, captured authoring identity and current API configuration.
            if (pending == null) return;
            foreach (DeferredRecognition item in pending)
            {
                if (item?.owner == null || item.entry == null ||
                    !rigs.TryGetValue(item.owner.rig, out ViewWeaveRig rig) ||
                    !playIdentities.items.Exists(owner => SameOwner(owner, item.owner)) ||
                    !item.entry.TryProfile(out BridgeDisplayProfile profile)) continue;
                Remember(rig, ConfiguredOutput(rig.Renderer), item.entry.runtimePath,
                    item.entry.applicationName, item.entry.ordinal, profile);
            }
        }

        static bool SameOwner(PlayIdentity a, PlayIdentity b) => a.identity == b.identity &&
            a.rig == b.rig && a.renderer == b.renderer && a.output == b.output;

        static void LoadPlayIdentities()
        {
            if (playIdentities == null)
                playIdentities = JsonUtility.FromJson<PlayIdentities>(SessionState.GetString(PlayKey, "{}"));
        }

        static void PersistPlayIdentities() => SessionState.SetString(PlayKey, JsonUtility.ToJson(playIdentities));

        internal static void ClearPlayIdentities()
        {
            playIdentities = null;
            SessionState.EraseString(PlayKey);
            SessionState.EraseString(EpochKey);
            foreach (ViewWeaveRig rig in UnityEngine.Object.FindObjectsByType<ViewWeaveRig>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                rig.CaptureEditorDisplayIdentity(null, null);
        }

        static bool Allowed(ViewWeaveRig rig, QuiltRenderer renderer, MonoBehaviour output, bool requireLoaded = true) =>
            rig != null && renderer != null && output != null && rig.GetComponent<QuiltRenderer>() == renderer &&
            ConfiguredOutput(renderer) == output && !EditorUtility.IsPersistent(rig) &&
            rig.gameObject.scene.IsValid() && (!requireLoaded || rig.gameObject.scene.isLoaded) &&
            !EditorSceneManager.IsPreviewSceneObject(rig.gameObject) &&
            PrefabStageUtility.GetPrefabStage(rig.gameObject) == null &&
            (rig.gameObject.hideFlags & HideFlags.DontSave) == 0;

        static string ObjectIdentity(UnityEngine.Object value)
        {
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(value);
            return id.identifierType == 2 && id.targetObjectId != 0 &&
                id.assetGUID.ToString() != "00000000000000000000000000000000" ? id.ToString() : null;
        }

        static string EditIdentity(ViewWeaveRig rig, QuiltRenderer renderer, MonoBehaviour output)
        {
            string rigId = ObjectIdentity(rig), rendererId = ObjectIdentity(renderer), outputId = ObjectIdentity(output);
            return rigId != null && rendererId != null && outputId != null
                ? rigId + ":" + rendererId + ":" + outputId
                : "Temporary:" + rig.GetEntityId().ToString() + ":" + renderer.GetEntityId().ToString() + ":" + output.GetEntityId().ToString();
        }

        static bool TryIdentity(QuiltRenderer renderer, MonoBehaviour output, out string key)
        {
            key = null;
            ViewWeaveRig rig = renderer == null ? null : renderer.GetComponent<ViewWeaveRig>();
            if (!Allowed(rig, renderer, output)) return false;
            if (!Application.isPlaying) { key = EditIdentity(rig, renderer, output); return true; }
            if (!rig.TryGetEditorDisplayIdentity(output, renderer, out key)) return false;
            LoadPlayIdentities();
            if (playIdentities == null || playIdentities.items == null ||
                !playIdentities.bound || string.IsNullOrEmpty(playIdentities.epoch) ||
                playIdentities.epoch != SessionState.GetString(EpochKey, "")) return false;
            string identity = key;
            return playIdentities.items.Exists(item => item.identity == identity && item.rig == rig.GetEntityId().ToString() &&
                item.renderer == renderer.GetEntityId().ToString() && item.output == output.GetEntityId().ToString());
        }

        internal static bool TryConfiguration(QuiltRenderer renderer, out MonoBehaviour output,
            out string runtimePath, out string applicationName, out uint ordinal)
        {
            output = ConfiguredOutput(renderer);
            runtimePath = applicationName = string.Empty; ordinal = 0;
            if (output is BridgeMetalQuiltOutput metal)
            { runtimePath = metal.runtimePath; applicationName = metal.applicationName; ordinal = metal.displayOrdinal; return true; }
            if (output is BridgeWorkerQuiltOutput windows)
            {
                ViewWeaveBridgeSession session = renderer.GetComponent<ViewWeaveBridgeSession>();
                runtimePath = session == null ? windows.runtimePath : session.runtimePath;
                applicationName = session == null ? windows.applicationName : session.applicationName;
                ordinal = session == null ? windows.displayOrdinal : session.displayOrdinal;
                return true;
            }
            return false;
        }

        // A single attached transport also identifies a Rig before its first explicit output binding.
        // Multiple attached transports require an explicit binding; no platform is preferred.
        static MonoBehaviour ConfiguredOutput(QuiltRenderer renderer)
        {
            if (renderer == null) return null;
            if (renderer.OutputBehaviour != null) return renderer.OutputBehaviour;
            MonoBehaviour selected = null;
            foreach (MonoBehaviour component in renderer.GetComponents<MonoBehaviour>())
            {
                if (!(component is BridgeMetalQuiltOutput) && !(component is BridgeWorkerQuiltOutput)) continue;
                if (selected != null) return null;
                selected = component;
            }
            return selected;
        }

        static List<Entry> Transient
        {
            get
            {
                if (transient == null)
                    transient = JsonUtility.FromJson<TransientEntries>(SessionState.GetString(TransientKey, "{}"));
                if (transient == null) transient = new TransientEntries();
                if (transient.items == null) transient.items = new List<Entry>();
                return transient.items;
            }
        }

        internal static void Remember(ViewWeaveRig rig, MonoBehaviour source, string runtime,
            string application, uint ordinal, BridgeDisplayProfile profile)
        {
            QuiltRenderer renderer = rig == null ? null : rig.GetComponent<QuiltRenderer>();
            if (!TryConfiguration(renderer, out MonoBehaviour output, out string currentRuntime, out string currentApplication, out uint currentOrdinal) ||
                source != output || (runtime ?? string.Empty) != (currentRuntime ?? string.Empty) ||
                (application ?? string.Empty) != (currentApplication ?? string.Empty) || ordinal != currentOrdinal) return;
            if (!TryIdentity(renderer, output, out string key))
            {
                DeferEarlyRecognition(rig, renderer, output, runtime, application, ordinal, profile);
                return;
            }
            Entry entry = Entry.Create(key, runtime, application, ordinal, profile);
            if (!entry.TryProfile(out _)) return;
            ApplyPhysicalDimensions(renderer, profile);
            bool temporary = key.StartsWith("Temporary:", StringComparison.Ordinal);
            List<Entry> values = temporary ? Transient : instance.entries;
            Entry old = values.Find(value => value.identity == key);
            if (old != null && JsonUtility.ToJson(old) == JsonUtility.ToJson(entry)) return;
            values.RemoveAll(value => value.identity == key);
            values.Add(entry);
            if (temporary) SessionState.SetString(TransientKey, JsonUtility.ToJson(transient));
            else instance.Save(true);
            QuiltDisplaySettingsResolver.NotifyChanged();
        }

        static void DeferEarlyRecognition(ViewWeaveRig rig, QuiltRenderer renderer, MonoBehaviour output,
            string runtime, string application, uint ordinal, BridgeDisplayProfile profile)
        {
            LoadPlayIdentities();
            // During scene loading Awake may run before scene.isLoaded. Only defer here;
            // BindPlayIdentities and Remember both require the fully loaded original scene.
            if (!Application.isPlaying || !Allowed(rig, renderer, output, false) || playIdentities == null ||
                playIdentities.bound || string.IsNullOrEmpty(playIdentities.epoch) ||
                playIdentities.epoch != SessionState.GetString(EpochKey, "") ||
                !rig.TryGetEditorDisplayIdentity(output, renderer, out string key)) return;
            Entry entry = Entry.Create(key, runtime, application, ordinal, profile);
            if (!entry.TryProfile(out _)) return;
            var owner = new PlayIdentity { identity = key, rig = rig.GetEntityId().ToString(),
                renderer = renderer.GetEntityId().ToString(), output = output.GetEntityId().ToString() };
            if (playIdentities.pending == null) playIdentities.pending = new List<DeferredRecognition>();
            playIdentities.pending.RemoveAll(item => SameOwner(item.owner, owner));
            playIdentities.pending.Add(new DeferredRecognition { owner = owner, entry = entry });
            PersistPlayIdentities();
        }

        internal static bool TryResolve(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = default;
            if (!TryConfiguration(renderer, out MonoBehaviour output, out string runtime, out string application, out uint ordinal) ||
                !TryIdentity(renderer, output, out string key)) return false;
            List<Entry> values = key.StartsWith("Temporary:", StringComparison.Ordinal) ? Transient : instance.entries;
            Entry entry = values.Find(value => value.Matches(key, runtime, application, ordinal));
            if (entry == null || !entry.TryProfile(out BridgeDisplayProfile retained)) return false;
            profile = new QuiltEditorDisplayProfile(retained.Aspect, retained.Viewcone, QuiltDisplaySettingsSource.LastRecognizedDisplay,
                false, retained.HasPhysicalCalibration, retained.PhysicalWidthMillimeters, retained.PhysicalHeightMillimeters,
                retained.CalibrationDpi, "Last successful Bridge API result: " + retained);
            return true;
        }

        static void ApplyPhysicalDimensions(QuiltRenderer renderer, BridgeDisplayProfile profile)
        {
            if (Application.isPlaying || !profile.HasPhysicalCalibration || !renderer.TryGetFraming(out QuiltCameraFraming framing)) return;
            if (Mathf.Approximately(framing.ApertureWidthMillimeters, profile.PhysicalWidthMillimeters) &&
                Mathf.Approximately(framing.ApertureHeightMillimeters, profile.PhysicalHeightMillimeters)) return;
            var candidate = new QuiltCameraFraming(framing.VerticalFieldOfView, framing.FocusDistance, framing.PhysicalFramingEnabled,
                profile.PhysicalWidthMillimeters, profile.PhysicalHeightMillimeters, framing.UnitsPerMeter);
            if (!renderer.TryCheckMutation(new QuiltRendererMutationRequest(candidate), out _)) return;
            Undo.RecordObject(renderer, "Apply Bridge Display Dimensions");
            if (!renderer.TrySetFraming(candidate, out _)) return;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        internal static void ApplyToLocalPreview(QuiltRenderer source, QuiltSettings settings)
        {
            if (!TryResolve(source, out QuiltEditorDisplayProfile retained)) return;
            settings.viewAspect = retained.aspect;
            settings.viewCone = retained.viewCone;
        }
    }
}

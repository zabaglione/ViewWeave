using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ViewWeave
{
    /// <summary>Calibration-driven Looking Glass output with a Unity Metal producer and an owned presentation window.</summary>
    [DisallowMultipleComponent]
    public sealed class BridgeMetalQuiltOutput : MonoBehaviour, IQuiltOutput
    {
        public ViewWeaveRig rig;
        public bool startOnEnable;
        public string runtimePath;
        public string applicationName = "ViewWeave";
        public uint displayOrdinal;
        public QuiltQualityPreset quality = QuiltQualityPreset.Balanced;
        [SerializeField] BridgeSubpixelMode subpixelMode = BridgeSubpixelMode.Automatic;
        public BridgeSubpixelMode SubpixelMode => subpixelMode;
        public bool IsUsingSubpixelCells => IsRunning && activeUsesSubpixelCells;
        public RenderTexture QuiltTexture { get; private set; }
        public QuiltLayout Layout { get; private set; }
        public BridgeDisplayProfile Profile { get; private set; }
        public int CellPatternMode { get; private set; }
        public int SubpixelCellCount { get; private set; }
        string lastError = string.Empty;
        public string LastError
        {
            get => string.IsNullOrEmpty(lastError) ? BridgeMetalRetirementRecord.Failure(closingSession) : lastError;
            private set => lastError = value;
        }
        public bool IsRunning => session != 0;
        public bool IsClosing => closingSession != 0 && (Native.lkg_metal_display_status(closingSession, out _) == 0 ||
            BridgeMetalRetirementRecord.IsRetiring(closingSession));
        public uint SubmittedFrames => status.submitted;
        public uint CompletedFrames => status.completed;
        public uint DroppedFrames => status.dropped;
        public static int PendingRetirementCount => BridgeMetalRetirementRecord.PendingCount;
        ulong session;
        ulong closingSession;
        NativeStatus status;
        BridgeSubpixelMode activeSubpixelMode;
        bool activeUsesSubpixelCells;
        Material interlace;
        RenderTexture screenTexture;
        CommandBuffer command;
        IntPtr renderEvent;
        bool pendingStart, snapshotCaptured, stopRequested;
        QuiltQualityProfile originalProfile;
        string activeRuntimePath, activeApplicationName;
        uint activeDisplayOrdinal;
        MonoBehaviour originalOutput;
        bool originalAutoRender;
        IDisposable mutationOwner;
        object playbackToken;
        QuiltRendererOperationLease playbackLease;
        bool playbackAutoRender;
        internal event Action PlaybackStopping;
        BridgeMetalRetirementRecord resources;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeDisplay
        {
            internal BridgeNative.DisplayInfo display;
            internal float center, pitch, slope, dpi, flipX;
            internal int invertedViews;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeCell
        {
            internal float redX, redY, greenX, greenY, blueX, blueY;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeDisplayV3
        {
            internal uint structSize, apiVersion;
            internal NativeDisplay calibration;
            internal int redIndex, blueIndex;
            internal int cellPatternMode, cellCount;
            internal NativeCell cell0, cell1, cell2, cell3;

            internal NativeCell Cell(int index)
            {
                switch (index)
                {
                    case 0: return cell0;
                    case 1: return cell1;
                    case 2: return cell2;
                    case 3: return cell3;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        struct NativeStatus { internal uint submitted, completed, dropped, error, pending, closing; }
        static class Native
        {
            const string Library = "lookingglass_bridge_shim";
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ulong lkg_metal_display_open_v4([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint ordinal, uint requestedMode, ref NativeDisplayV3 info);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_display_submit(ulong session, IntPtr texture, out IntPtr data);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_display_status(ulong session, out NativeStatus status);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void lkg_metal_display_close(ulong session);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr lkg_metal_display_error();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr lkg_metal_display_render_event();
        }

        void OnEnable() { pendingStart = startOnEnable; }
        void OnDisable() { StopOutput(); }
        void OnDestroy() { StopOutput(); }
        void Update()
        {
            if (pendingStart && Application.isPlaying && !IsClosing && PendingRetirementCount == 0)
            { pendingStart = false; StartOutput(); }
            PumpStatus();
        }

        internal void PumpStatus()
        {
            if (stopRequested) { stopRequested = false; StopOutput(); }
            if (session == 0) return;
            if (!ConfigurationMatchesActiveDisplay)
            { Fail("Bridge runtime, application or display selection changed during Metal output. Stop output before changing it."); return; }
            if (subpixelMode != activeSubpixelMode)
            { subpixelMode = activeSubpixelMode; Fail("Subpixel processing cannot change while Metal output is active. Stop output before changing it."); return; }
            if (Native.lkg_metal_display_status(session, out status) != 0 || status.error != 0)
            { Fail("Metal presentation failed (code " + status.error + ")"); return; }
            if (rig == null || rig.Renderer == null || !rig.HasOutputBinding(this))
            { Fail("Metal output lost its ViewWeaveRig binding"); return; }
            if (!string.IsNullOrEmpty(GetPhysicalOutputError())) { Fail(GetPhysicalOutputError()); return; }
            if (!string.IsNullOrEmpty(rig.Renderer.LastAutoRenderError)) Fail(rig.Renderer.LastAutoRenderError);
        }

        public bool CanChangeConfiguration(out string error)
        {
            error = string.Empty;
            if (IsRunning || IsClosing || PendingRetirementCount != 0)
            {
                error = "Stop Metal output and wait for GPU retirement before changing quality, subpixel processing or transport.";
                return false;
            }
            ViewWeaveRig ownerRig = rig != null ? rig : GetComponent<ViewWeaveRig>();
            return ownerRig == null || ownerRig.Renderer == null ||
                ownerRig.Renderer.TryCheckMutation(QuiltRendererMutationKind.Quality, out error);
        }

        public bool TrySetQuality(QuiltQualityPreset value, out string error)
        {
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), value))
            {
                error = "Output quality is invalid.";
                return false;
            }
            if (!CanChangeConfiguration(out error)) return false;
            quality = value;
            return true;
        }

        public bool TrySetSubpixelMode(BridgeSubpixelMode value, out string error)
        {
            if (!BridgeSubpixelPolicy.Validate(value, out error) || !CanChangeConfiguration(out error)) return false;
            subpixelMode = value;
            return true;
        }

        [ContextMenu("Start Metal Output")]
        public bool StartOutput() => StartOutputCore(false);

#if UNITY_EDITOR
        internal bool StartEditorPreview()
        {
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode ||
                (gameObject.hideFlags & HideFlags.HideAndDontSave) != HideFlags.HideAndDontSave)
                return Fail("Edit Mode output requires a private Editor preview producer.");
            return StartOutputCore(true);
        }
#endif

        bool StartOutputCore(bool editorPreview)
        {
            if (IsRunning) return true;
            if (IsClosing)
            {
                string failure = BridgeMetalRetirementRecord.Failure(closingSession);
                LastError = string.IsNullOrEmpty(failure) ? "The previous Metal frame is still retiring" : failure;
                return false;
            }
            if (PendingRetirementCount != 0)
            {
                LastError = "Another Metal output still has retained GPU resources; wait for retirement or restart Unity after a proof failure";
                return false;
            }
            if ((!Application.isPlaying && !editorPreview) ||
                (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.OSXPlayer) ||
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                return Fail("Metal output requires macOS Play mode or macOS Player with Metal");
            if (Marshal.SizeOf<NativeDisplay>() != 60 || Marshal.SizeOf<NativeCell>() != 24 ||
                Marshal.SizeOf<NativeDisplayV3>() != 180 || IntPtr.Size != 8 ||
                Marshal.OffsetOf<NativeDisplayV3>(nameof(NativeDisplayV3.calibration)).ToInt32() != 8 ||
                Marshal.OffsetOf<NativeDisplayV3>(nameof(NativeDisplayV3.redIndex)).ToInt32() != 68 ||
                Marshal.OffsetOf<NativeDisplayV3>(nameof(NativeDisplayV3.blueIndex)).ToInt32() != 72 ||
                Marshal.OffsetOf<NativeDisplayV3>(nameof(NativeDisplayV3.cell0)).ToInt32() != 84)
                return Fail("Unsupported Metal display ABI");
            if (rig == null) rig = GetComponent<ViewWeaveRig>();
            if (rig == null) return Fail("A ViewWeaveRig is required");
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), quality)) return Fail("Output quality is invalid.");
            if (!BridgeSubpixelPolicy.Validate(subpixelMode, out string subpixelError)) return Fail(subpixelError);
            if (!rig.ValidateRig(out string rigError)) return Fail("Invalid ViewWeaveRig: " + rigError);
            if (!BridgeRuntimeLocator.TryResolve(runtimePath, out string path, out string error)) return Fail(error);
            if (rig.Renderer.IsRendering || !QuiltRendererMutationPolicy.TryCheck(rig.Renderer,
                QuiltRendererMutationKind.Transport, out error)) return Fail("The Rig is already in use: " + error);
            originalProfile = rig.Profile; originalOutput = rig.Renderer.OutputBehaviour;
            originalAutoRender = rig.AutoRender; snapshotCaptured = true;
            rig.AutoRender = false;
            try
            {
                mutationOwner = QuiltRendererMutationPolicy.RegisterOwner(rig.Renderer, this, GetMutationBlockReason, out error);
                if (mutationOwner == null) return Fail(error);
                var nativeInfo = new NativeDisplayV3 { structSize = 180, apiVersion = 3 };
                activeSubpixelMode = subpixelMode;
                session = Native.lkg_metal_display_open_v4(path, applicationName, displayOrdinal,
                    (uint)subpixelMode, ref nativeInfo);
                if (session == 0) return Fail(Marshal.PtrToStringAnsi(Native.lkg_metal_display_error()) ?? "Metal window could not be opened");
                if (!BridgeSubpixelPolicy.TryResolveCellUsage(subpixelMode, nativeInfo.cellCount > 0,
                    out bool useCells, out subpixelError)) return Fail(subpixelError);
                activeUsesSubpixelCells = useCells;
                NativeDisplay display = nativeInfo.calibration;
                Profile = new BridgeDisplayProfile(display.display, new BridgeNative.PhysicalDisplayInfoV1
                {
                    pixelWidth = display.display.width, pixelHeight = display.display.height,
                    dpi = display.dpi, valid = 1
                });
                if (!Profile.IsValid) return Fail("Invalid Looking Glass display profile");
                if (!string.IsNullOrEmpty(GetPhysicalOutputError())) return Fail(GetPhysicalOutputError());
                QuiltQualityProfile profile = QuiltQualityProfile.FromScaled(Profile.QuiltWidth, Profile.QuiltHeight,
                    Profile.Columns, Profile.Rows, Profile.Aspect, Profile.Viewcone,
                    quality == QuiltQualityPreset.Preview ? 0.25f : quality == QuiltQualityPreset.Balanced ? 0.5f : 1f);
                if (!rig.ApplyProfileForBridgeOwner(profile, this, out error) || !rig.SetOutputForBridgeOwner(this, this, out error)) return Fail(error);
                Shader shader = Resources.Load<Shader>("ViewWeaveMetalInterlace");
                if (shader == null || !shader.isSupported) return Fail("Metal interlace shader is unavailable");
                interlace = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                float calibratedPitch = display.pitch * Profile.Width / display.dpi * Mathf.Cos(Mathf.Atan(1f / display.slope));
                float direction = display.flipX == 1f ? -1f : 1f;
                interlace.SetVector("_Calibration", new Vector4(calibratedPitch, direction * Profile.Height / (Profile.Width * display.slope),
                    display.center, display.invertedViews));
                interlace.SetVector("_Screen", new Vector4(Profile.Width, Profile.Height, direction / (3f * Profile.Width), 0f));
                bool forceClassicRgb = subpixelMode == BridgeSubpixelMode.ClassicRgb;
                interlace.SetVector("_ChannelOrder", new Vector4(forceClassicRgb ? 0 : nativeInfo.redIndex, 1f,
                    forceClassicRgb ? 2 : nativeInfo.blueIndex, display.flipX));
                CellPatternMode = nativeInfo.cellPatternMode;
                SubpixelCellCount = nativeInfo.cellCount;
                interlace.SetVector("_SubpixelPattern", new Vector4(CellPatternMode, useCells ? SubpixelCellCount : 0, 0f, 0f));
                var offsets = new Vector4[12];
                for (int i = 0; i < SubpixelCellCount; ++i)
                {
                    NativeCell cell = nativeInfo.Cell(i);
                    offsets[i * 3] = new Vector4(cell.redX / Profile.Width, cell.redY / Profile.Height, 0f, 0f);
                    offsets[i * 3 + 1] = new Vector4(cell.greenX / Profile.Width, cell.greenY / Profile.Height, 0f, 0f);
                    offsets[i * 3 + 2] = new Vector4(cell.blueX / Profile.Width, cell.blueY / Profile.Height, 0f, 0f);
                }
                interlace.SetVectorArray("_SubpixelOffsets", offsets);
                screenTexture = new RenderTexture((int)Profile.Width, (int)Profile.Height, 0)
                {
                    graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                    useMipMap = false, antiAliasing = 1, filterMode = FilterMode.Point,
                    name = "ViewWeave Metal Display", hideFlags = HideFlags.HideAndDontSave
                };
                if (!screenTexture.Create()) return Fail("Metal display texture could not be created");
                resources = BridgeMetalRetirementRecord.Create(session, screenTexture, interlace);
                command = new CommandBuffer { name = "ViewWeave Metal Present" };
                renderEvent = Native.lkg_metal_display_render_event();
                if (renderEvent == IntPtr.Zero) return Fail("Metal render event is unavailable");
                status = default;
                if (!rig.SetAutoRenderForBridgeOwner(!editorPreview, this, out error)) return Fail(error);
                activeRuntimePath = runtimePath ?? string.Empty;
                activeApplicationName = applicationName ?? string.Empty;
                activeDisplayOrdinal = displayOrdinal;
                LastError = string.Empty;
                if (!editorPreview)
                    BridgeDisplayMetadata.Publish(rig, this, runtimePath, applicationName, displayOrdinal, Profile);
                return true;
            }
            catch (DllNotFoundException) { return Fail("Native macOS Bridge shim is missing; build the macOS plugin first"); }
            catch (EntryPointNotFoundException) { return Fail("Native macOS Bridge shim does not contain Metal display API v4; update the plugin and restart Unity"); }
            catch (Exception exception) { return Fail(exception.Message); }
        }

        bool ConfigurationMatchesActiveDisplay => activeRuntimePath == (runtimePath ?? string.Empty) &&
            activeApplicationName == (applicationName ?? string.Empty) && activeDisplayOrdinal == displayOrdinal;

        internal bool TryGetCurrentDisplayProfile(QuiltRenderer renderer, out BridgeDisplayProfile profile)
        {
            profile = default;
            if (!IsRunning || !ConfigurationMatchesActiveDisplay || rig == null || rig.Renderer != renderer ||
                !rig.HasOutputBinding(this) || !Profile.IsValid) return false;
            profile = Profile;
            return true;
        }

        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            if (playbackToken == null) SubmitCore(quiltTexture, layout);
        }

        internal bool TryBeginPlayback(object token, out string error)
        {
            error = string.Empty;
            if (!IsRunning || rig == null || !rig.HasOutputBinding(this) || playbackToken != null || token == null)
            { error = "Playback requires the Rig's running Metal output without another playback owner."; return false; }
            if (!QuiltRendererOperationLease.TryAcquire(rig.Renderer, QuiltRendererOperationKind.Playback, out playbackLease, out error)) return false;
            playbackAutoRender = rig.AutoRender;
            playbackToken = token;
            if (!rig.SetAutoRenderForBridgeOwner(false, this, out error))
            { EndPlayback(token, false); return false; }
            return true;
        }

        internal bool IsPlaybackCurrent(object token) => token != null && ReferenceEquals(token, playbackToken) && IsRunning &&
            rig != null && rig.HasOutputBinding(this);

        internal bool TrySubmitPlayback(object token, RenderTexture texture, QuiltLayout layout) =>
            IsPlaybackCurrent(token) && SubmitCore(texture, layout);

        internal void EndPlayback(object token, bool restore)
        {
            if (!ReferenceEquals(token, playbackToken) || token == null) return;
            playbackLease?.Dispose();
            playbackLease = null;
            playbackToken = null;
            QuiltTexture = null;
            if (restore && IsRunning && rig != null && rig.HasOutputBinding(this))
                if (!rig.SetAutoRenderForBridgeOwner(playbackAutoRender, this, out string error)) LastError = Sanitize(error);
        }

        bool SubmitCore(RenderTexture quiltTexture, QuiltLayout layout)
        {
            QuiltTexture = quiltTexture; Layout = layout;
            if (session == 0 || quiltTexture == null) return false;
            if (!BridgeQuiltInputPolicy.TryValidate(quiltTexture, layout, out string inputError))
            { LastError = inputError; stopRequested = true; return false; }
            interlace.SetVector("_Grid", new Vector4(layout.Columns, layout.Rows, layout.TileWidth, layout.TileHeight));
            interlace.SetVector("_QuiltSize", new Vector4(layout.QuiltWidth, layout.QuiltHeight,
                layout.AspectRatio / Profile.Aspect, 1f));
            bool linearOutput = QualitySettings.activeColorSpace == ColorSpace.Linear;
            bool sourceIsSrgb = GraphicsFormatUtility.IsSRGBFormat(quiltTexture.graphicsFormat);
            // Metal texture reads from an sRGB format are already transfer-decoded.
            interlace.SetVector("_SourceOptions", new Vector4(linearOutput ? 1f : 0f,
                sourceIsSrgb ? 1f : 0f, sourceIsSrgb ? 1f : 0f, 0f));
            // The native private copy is ordered after this draw on Unity's command buffer.
            RenderTexture previous = RenderTexture.active;
            try
            {
                resources.MarkRendered();
                Graphics.Blit(quiltTexture, screenTexture, interlace);
                int result = Native.lkg_metal_display_submit(session, screenTexture.GetNativeTexturePtr(), out IntPtr data);
                if (result == 1) return false; // A previous producer/consumer is still using the sole private slot.
                if (result != 0) { LastError = "Metal frame was rejected (code " + result + ")"; stopRequested = true; return false; }
                command.Clear(); command.IssuePluginEventAndData(renderEvent, 0, data);
                Graphics.ExecuteCommandBuffer(command);
                return true;
            }
            catch (Exception exception) { LastError = Sanitize(exception.Message); stopRequested = true; return false; }
            finally { RenderTexture.active = previous; }
        }

        [ContextMenu("Stop Metal Output")]
        public void StopOutput()
        {
            pendingStart = false; stopRequested = false;
            if (playbackToken != null)
            {
                object token = playbackToken;
                try { PlaybackStopping?.Invoke(); }
                catch (Exception exception) { LastError = Sanitize(exception.Message); }
                finally { EndPlayback(token, false); }
            }
            ulong closing = session; session = 0;
            if (closing != 0) closingSession = closing;
            try
            {
                if (snapshotCaptured && rig != null && rig.Renderer != null)
                    foreach (QuiltRecorder recorder in FindObjectsByType<QuiltRecorder>(FindObjectsInactive.Include))
                        if (recorder.Renderer == rig.Renderer && recorder.IsRecording) recorder.CancelRecording();
                if (rig != null && mutationOwner != null) rig.SetAutoRenderForBridgeOwner(false, this, out _);
                if (closing != 0) Native.lkg_metal_display_close(closing);
            }
            catch (Exception exception) { LastError = Sanitize(exception.Message); }
            finally
            {
                try
                {
                    mutationOwner?.Dispose();
                    if (snapshotCaptured && rig != null)
                    {
                        rig.AutoRender = false; rig.SetOutput(originalOutput); rig.ApplyProfile(originalProfile);
                        rig.AutoRender = originalAutoRender;
                    }
                }
                catch (Exception exception) { LastError = Sanitize(exception.Message); }
                finally
                {
                    snapshotCaptured = false; mutationOwner = null;
                    command?.Release(); command = null;
                    if (resources != null) { resources.Stop(); resources = null; }
                    else
                    {
                        if (screenTexture != null) { screenTexture.Release(); DestroyResource(screenTexture); }
                        if (interlace != null) DestroyResource(interlace);
                    }
                    screenTexture = null; interlace = null;
                }
            }
        }

        static void DestroyResource(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        internal static bool NativeFramePending(ulong id)
        {
            return id != 0 && Native.lkg_metal_display_status(id, out NativeStatus value) == 0 && value.pending != 0;
        }

        string GetPhysicalOutputError()
        {
            QuiltPhysicalFraming physical = rig == null || rig.Renderer == null ? null : rig.Renderer.physicalFraming;
            if (physical == null || !physical.enabled) return string.Empty;
            if (!rig.Renderer.TryGetEffectiveSettings(out _, out string error))
                return "Physical framing became invalid: " + Sanitize(error);
            return Profile.IsValid && !Profile.MatchesPhysicalAspect(physical.visibleApertureWidthMillimeters, physical.visibleApertureHeightMillimeters)
                ? "Physical aperture aspect does not match the connected display profile." : string.Empty;
        }

        string GetMutationBlockReason(QuiltRendererMutationRequest request)
        {
            if (!IsRunning || !Profile.IsValid) return "Metal output is starting or stopping.";
            if (request.Kind == QuiltRendererMutationKind.Framing)
            {
                if (request.HasFraming && request.Framing.PhysicalFramingEnabled &&
                    !Profile.MatchesPhysicalAspect(request.Framing.ApertureWidthMillimeters, request.Framing.ApertureHeightMillimeters))
                    return "Physical aperture aspect must match the connected display profile.";
                return string.Empty;
            }
            if (request.Kind == QuiltRendererMutationKind.Recording || request.Kind == QuiltRendererMutationKind.Playback || request.Kind == QuiltRendererMutationKind.SamplingMode)
                return string.Empty;
            if (request.Kind == QuiltRendererMutationKind.AutoRender && request.HasBooleanValue)
                return string.Empty;
            return "Stop Metal output before changing the Rig configuration.";
        }

        bool Fail(string error)
        {
            LastError = Sanitize(error);
            StopOutput();
            return false;
        }
        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Unknown Metal output error";
            var text = new System.Text.StringBuilder();
            foreach (char character in value) if (character >= 32 && character < 127) text.Append(character);
            return text.ToString();
        }
    }
}

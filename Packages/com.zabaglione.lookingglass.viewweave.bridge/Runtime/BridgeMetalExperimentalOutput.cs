using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ViewWeave
{
    /// <summary>
    /// Explicit, finite Metal experiment. The Bridge Metal consumer completion and texture retirement
    /// contract is unknown, so at most three quilt textures are accepted and retained per process.
    /// This component is not selected by the normal Bridge session route.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BridgeMetalExperimentalOutput : MonoBehaviour, IQuiltOutput
    {
        const uint ProviderApiVersion = 1;
        const uint MetalGraphicsApi = 4;
        const uint Rgba8Unorm = 70;
        const uint MaxAcceptedQuilts = 3;

        [Tooltip("Absolute path to the installed Looking Glass Bridge dylib.")]
        public string runtimePath;
        public string applicationName = "ViewWeave";
        public uint displayOrdinal = uint.MaxValue;
        public int quiltWidth = 7680;
        public int quiltHeight = 4320;
        public int columns = 8;
        public int rows = 6;
        [Range(0f, 178f)] public float viewCone = 40f;
        [Range(0.01f, 4f)] public float zoom = 1f;
        public ViewWeaveRig rig;

        public RenderTexture QuiltTexture { get; private set; }
        public QuiltLayout Layout { get; private set; }
        public string LastError { get; private set; }
        public bool RuntimeAvailable => status.runtimeAvailable != 0;
        public bool InteropReady => status.interopReady != 0;
        public uint AcceptedProducerSubmissions { get; private set; }
        public uint BridgeAcceptedDrawCalls => status.acceptedDrawCalls;

        CommandBuffer command;
        IntPtr renderEventFunction;
        int renderEventId;
        bool beginQueued;
        bool closed;
        bool halted;
        bool rigSnapshotCaptured;
        bool previousAutoRender;
        MonoBehaviour previousOutput;
        QuiltQualityProfile previousProfile;
        NativeProviderStatus status;

        [StructLayout(LayoutKind.Sequential)]
        struct NativeProviderStatus
        {
            public uint structSize;
            public uint apiVersion;
            public uint compiled;
            public uint experimentalPathAvailable;
            public uint runtimeAvailable;
            public uint interopReady;
            public uint providerState;
            public ulong deviceGeneration;
            public uint retainedSlots;
            public uint acceptedDrawCalls;
            public uint lastError;
            public uint producerApi;
            public uint bridgeConsumerApi;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct NativeBeginRequest
        {
            public uint structSize;
            public uint apiVersion;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string runtimePathUtf8;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string applicationNameUtf8;
            public uint displayIndex;
            public uint width;
            public uint height;
            public uint metalPixelFormat;
            public uint columns;
            public uint rows;
            public float aspect;
            public float zoom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeRenderRequest
        {
            public uint structSize;
            public uint apiVersion;
            public IntPtr sourceTexture;
            public ulong generation;
            public UIntPtr columns;
            public UIntPtr rows;
            public float aspect;
            public float zoom;
        }

        static class Native
        {
            const string Library = "lookingglass_bridge_shim";

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_provider_query_v1(ref NativeProviderStatus value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_provider_render_event_id(out int value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr lkg_metal_provider_render_event_func();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_provider_begin_v1(ref NativeBeginRequest value, out IntPtr eventData);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_provider_submit_v1(ref NativeRenderRequest value, out IntPtr eventData);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_provider_pump_v1(out IntPtr eventData);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int lkg_metal_provider_close();
        }

        public bool BeginExperimental(float aspect)
        {
            if (beginQueued || closed || !isActiveAndEnabled) return Fail("Metal experiment can only begin once on an active component");
            if (!Application.isPlaying || !IsMetalRuntime()) return Fail("Metal experiment requires macOS Play mode on a Metal graphics device");
            if (IntPtr.Size != 8) return Fail("Metal experiment requires a 64-bit process");
            if (string.IsNullOrWhiteSpace(runtimePath) || !System.IO.Path.IsPathRooted(runtimePath))
                return Fail("Set an absolute Bridge runtime dylib path");
            if (!NativeAbiLayoutsMatch())
                return Fail("Native Metal provider ABI layout is unsupported by this process");
            if (quiltWidth <= 0 || quiltHeight <= 0 || quiltWidth > 16384 || quiltHeight > 16384 ||
                columns <= 0 || rows <= 0 || columns > 16 || rows > 16 || columns * (long)rows > 64 ||
                quiltWidth % columns != 0 || quiltHeight % rows != 0 ||
                float.IsNaN(aspect) || float.IsInfinity(aspect) || aspect <= 0f ||
                float.IsNaN(viewCone) || float.IsInfinity(viewCone) || viewCone < 0f || viewCone >= 179f ||
                float.IsNaN(zoom) || float.IsInfinity(zoom) || zoom <= 0f)
                return Fail("Metal experiment quilt geometry is invalid");
            if (rig == null) return Fail("A ViewWeaveRig is required");
            if (!rig.ValidateRig(out string rigError))
                return Fail("ViewWeaveRig validation failed: " + Sanitize(rigError));
            previousAutoRender = rig.AutoRender;
            previousOutput = rig.Renderer.OutputBehaviour;
            previousProfile = rig.Profile;
            rigSnapshotCaptured = true;
            rig.AutoRender = false;

            if (command == null) command = new CommandBuffer { name = "ViewWeave Experimental Metal Begin" };
            try
            {
                NativeProviderStatus queried = new NativeProviderStatus
                {
                    structSize = (uint)Marshal.SizeOf(typeof(NativeProviderStatus)),
                    apiVersion = ProviderApiVersion
                };
                if (Native.lkg_metal_provider_query_v1(ref queried) != 0 || queried.compiled == 0 ||
                    queried.experimentalPathAvailable == 0 || queried.producerApi != MetalGraphicsApi)
                    return Fail("Native experimental Metal provider is unavailable");
                status = queried;
                if (Native.lkg_metal_provider_render_event_id(out renderEventId) != 0)
                    return Fail("Metal provider render event ID is unavailable");
                renderEventFunction = Native.lkg_metal_provider_render_event_func();
                if (renderEventFunction == IntPtr.Zero) return Fail("Metal provider render event function is unavailable");

                NativeBeginRequest request = new NativeBeginRequest
                {
                    structSize = (uint)Marshal.SizeOf(typeof(NativeBeginRequest)),
                    apiVersion = ProviderApiVersion,
                    runtimePathUtf8 = runtimePath,
                    applicationNameUtf8 = applicationName,
                    displayIndex = displayOrdinal,
                    width = (uint)quiltWidth,
                    height = (uint)quiltHeight,
                    metalPixelFormat = Rgba8Unorm,
                    columns = (uint)columns,
                    rows = (uint)rows,
                    aspect = aspect,
                    zoom = zoom
                };
                if (Native.lkg_metal_provider_begin_v1(ref request, out IntPtr eventData) != 0)
                    return Fail("Metal provider rejected begin request");
                beginQueued = true;
                rig.ApplyProfile(new QuiltQualityProfile(quiltWidth, quiltHeight, columns, rows,
                    checked(columns * rows), aspect, viewCone));
                rig.SetOutput(this);
                QueuePluginEvent(eventData);
                LastError = string.Empty;
                return true;
            }
            catch (DllNotFoundException) { return Fail("Native Bridge shim is missing"); }
            catch (EntryPointNotFoundException) { return Fail("Experimental Metal provider API is unavailable in this native shim"); }
            catch (Exception error) { return Fail(Sanitize(error.Message)); }
        }

        [ContextMenu("Begin Finite Metal Experiment From Rig Profile")]
        public void BeginFromRigProfile()
        {
            if (rig == null)
            {
                Fail("A ViewWeaveRig is required");
                return;
            }
            BeginExperimental(rig.Profile.Aspect);
        }

        [ContextMenu("Start Finite Metal Quilt Rendering")]
        public void StartFiniteRendering()
        {
            StartRendering();
        }

        [ContextMenu("Close Finite Metal Experiment")]
        public void CloseFiniteExperiment()
        {
            CloseExperimental();
        }

        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            QuiltTexture = quiltTexture;
            Layout = layout;
            if (!beginQueued || closed || halted || quiltTexture == null) return;
            if (!RuntimeAvailable)
            {
                LastError = "Metal provider runtime is not available yet";
                return;
            }
            if (AcceptedProducerSubmissions >= MaxAcceptedQuilts)
            {
                HaltAfterCapacity();
                return;
            }
            if (layout.QuiltWidth != quiltWidth || layout.QuiltHeight != quiltHeight ||
                layout.Columns != columns || layout.Rows != rows || layout.ViewCount != columns * rows ||
                quiltTexture.width != quiltWidth || quiltTexture.height != quiltHeight ||
                quiltTexture.graphicsFormat != GraphicsFormat.R8G8B8A8_UNorm)
            {
                Fail("Metal experiment requires a matching linear RGBA8 quilt texture and full grid");
                return;
            }

            try
            {
                NativeProviderStatus current = ReadStatus();
                NativeRenderRequest request = new NativeRenderRequest
                {
                    structSize = (uint)Marshal.SizeOf(typeof(NativeRenderRequest)),
                    apiVersion = ProviderApiVersion,
                    sourceTexture = quiltTexture.GetNativeTexturePtr(),
                    generation = current.deviceGeneration,
                    columns = new UIntPtr((uint)columns),
                    rows = new UIntPtr((uint)rows),
                    aspect = layout.AspectRatio,
                    zoom = zoom
                };
                if (request.sourceTexture == IntPtr.Zero || Native.lkg_metal_provider_submit_v1(ref request, out IntPtr eventData) != 0)
                {
                    HaltAfterCapacity();
                    return;
                }
                QueuePluginEvent(eventData);
                AcceptedProducerSubmissions++;
                LastError = string.Empty;
                if (AcceptedProducerSubmissions == MaxAcceptedQuilts) HaltAfterCapacity();
            }
            catch (DllNotFoundException) { Fail("Native Bridge shim is missing"); }
            catch (EntryPointNotFoundException) { Fail("Experimental Metal submit API is unavailable in this native shim"); }
            catch (Exception error) { Fail(Sanitize(error.Message)); }
        }

        void Update()
        {
            if (!beginQueued || closed) return;
            try
            {
                status = ReadStatus();
                if (status.providerState == 5)
                {
                    HaltAfterCapacity();
                    LastError = "Metal provider entered a faulted state (code " + status.lastError.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    return;
                }
                if (!RuntimeAvailable) return;
                if (Native.lkg_metal_provider_pump_v1(out IntPtr eventData) != 0)
                {
                    HaltAfterCapacity();
                    return;
                }
                QueuePluginEvent(eventData);
            }
            catch (DllNotFoundException) { Fail("Native Bridge shim is missing"); }
            catch (EntryPointNotFoundException) { Fail("Experimental Metal pump API is unavailable in this native shim"); }
            catch (Exception error) { Fail(Sanitize(error.Message)); }
        }

        public void CloseExperimental()
        {
            if (closed) return;
            closed = true;
            if (rigSnapshotCaptured && rig != null) rig.AutoRender = false;
            if (beginQueued)
            {
                try { Native.lkg_metal_provider_close(); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
            RestoreRig();
            if (command != null)
            {
                command.Release();
                command = null;
            }
        }

        void QueuePluginEvent(IntPtr eventData)
        {
            if (eventData == IntPtr.Zero || renderEventFunction == IntPtr.Zero)
                throw new InvalidOperationException("Metal provider returned invalid render event data");
            command.Clear();
            command.IssuePluginEventAndData(renderEventFunction, renderEventId, eventData);
            Graphics.ExecuteCommandBuffer(command);
        }

        NativeProviderStatus ReadStatus()
        {
            NativeProviderStatus value = new NativeProviderStatus
            {
                structSize = (uint)Marshal.SizeOf(typeof(NativeProviderStatus)),
                apiVersion = ProviderApiVersion
            };
            if (Native.lkg_metal_provider_query_v1(ref value) != 0)
                throw new InvalidOperationException("Metal provider status query failed");
            status = value;
            return value;
        }

        void HaltAfterCapacity()
        {
            halted = true;
            if (rig != null) rig.AutoRender = false;
            LastError = "Metal experiment stopped after bounded submission capacity or provider rejection";
        }

        public bool StartRendering()
        {
            if (!beginQueued || closed || halted || !RuntimeAvailable || rig == null)
            {
                LastError = "Metal experiment is not ready to render";
                return false;
            }
            try
            {
                rig.AutoRender = true;
                return true;
            }
            catch (Exception error)
            {
                LastError = Sanitize(error.Message);
                return false;
            }
        }

        void RestoreRig()
        {
            if (!rigSnapshotCaptured) return;
            rigSnapshotCaptured = false;
            if (rig == null) return;
            try
            {
                rig.AutoRender = false;
                rig.SetOutput(previousOutput);
                rig.ApplyProfile(previousProfile);
                rig.AutoRender = previousAutoRender;
            }
            catch (Exception error)
            {
                LastError = Sanitize("Rig restore failed: " + error.Message);
            }
        }

        bool Fail(string message)
        {
            LastError = Sanitize(message);
            if (beginQueued) CloseExperimental();
            else
            {
                RestoreRig();
                if (command != null) { command.Release(); command = null; }
            }
            halted = true;
            return false;
        }

        void OnDisable() { CloseExperimental(); }
        void OnDestroy() { CloseExperimental(); }

        static bool IsMetalRuntime()
        {
            return (Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer) &&
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal;
        }

        static bool NativeAbiLayoutsMatch()
        {
            if (Marshal.SizeOf(typeof(NativeProviderStatus)) != 64 ||
                Marshal.SizeOf(typeof(NativeBeginRequest)) != 56 ||
                Marshal.SizeOf(typeof(NativeRenderRequest)) != 48) return false;
            return Marshal.OffsetOf(typeof(NativeProviderStatus), nameof(NativeProviderStatus.deviceGeneration)).ToInt32() == 32 &&
                Marshal.OffsetOf(typeof(NativeProviderStatus), nameof(NativeProviderStatus.retainedSlots)).ToInt32() == 40 &&
                Marshal.OffsetOf(typeof(NativeBeginRequest), nameof(NativeBeginRequest.runtimePathUtf8)).ToInt32() == 8 &&
                Marshal.OffsetOf(typeof(NativeBeginRequest), nameof(NativeBeginRequest.width)).ToInt32() == 28 &&
                Marshal.OffsetOf(typeof(NativeRenderRequest), nameof(NativeRenderRequest.sourceTexture)).ToInt32() == 8 &&
                Marshal.OffsetOf(typeof(NativeRenderRequest), nameof(NativeRenderRequest.generation)).ToInt32() == 16 &&
                Marshal.OffsetOf(typeof(NativeRenderRequest), nameof(NativeRenderRequest.columns)).ToInt32() == 24;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            return new string(chars.ToArray());
        }
    }
}

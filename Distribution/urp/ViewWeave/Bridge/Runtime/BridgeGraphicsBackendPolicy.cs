using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    internal enum BridgeGraphicsBackendRoute
    {
        Worker,
        LegacyDirect,
        PlaybackWindowsEditorPlay
    }

    internal enum BridgeGraphicsApi
    {
        Unsupported,
        Direct3D11,
        Direct3D12
    }

    internal readonly struct BridgeGraphicsBackendDecision
    {
        internal readonly BridgeGraphicsBackendRoute Route;
        internal readonly BridgeGraphicsApi UnityProducer;
        internal readonly BridgeGraphicsApi BridgeConsumer;

        internal BridgeGraphicsBackendDecision(BridgeGraphicsBackendRoute route, BridgeGraphicsApi producer, BridgeGraphicsApi consumer)
        {
            Route = route;
            UnityProducer = producer;
            BridgeConsumer = consumer;
        }
    }

    internal static class BridgeGraphicsBackendPolicy
    {
        internal static bool TrySelect(
            BridgeGraphicsBackendRoute route,
            RuntimePlatform platform,
            bool isEditor,
            bool isPlaying,
            GraphicsDeviceType graphicsDevice,
            RenderingThreadingMode renderingThreadingMode,
            out BridgeGraphicsBackendDecision decision,
            out string error)
        {
            decision = default;
            error = string.Empty;

            bool windowsPlayer = platform == RuntimePlatform.WindowsPlayer && !isEditor;
            bool windowsEditor = platform == RuntimePlatform.WindowsEditor && isEditor;
            switch (route)
            {
                case BridgeGraphicsBackendRoute.Worker:
                    if (!windowsPlayer && !(windowsEditor && isPlaying))
                    {
                        error = "Bridge worker requires Windows Player or Windows Editor Play mode";
                        return false;
                    }
                    break;
                case BridgeGraphicsBackendRoute.LegacyDirect:
                    if (!windowsPlayer && !windowsEditor)
                    {
                        error = "Bridge D3D11 output is currently Windows-only";
                        return false;
                    }
                    break;
                case BridgeGraphicsBackendRoute.PlaybackWindowsEditorPlay:
                    if (!windowsEditor || !isPlaying)
                    {
                        error = "Quilt playback requires Windows Editor Play mode.";
                        return false;
                    }
                    break;
                default:
                    error = "Bridge graphics route is unsupported";
                    return false;
            }

            BridgeGraphicsApi producer;
            if (graphicsDevice == GraphicsDeviceType.Direct3D11)
                producer = BridgeGraphicsApi.Direct3D11;
            else if (graphicsDevice == GraphicsDeviceType.Direct3D12)
                producer = BridgeGraphicsApi.Direct3D12;
            else
            {
                error = route == BridgeGraphicsBackendRoute.LegacyDirect
                    ? "Bridge output currently supports Direct3D11 only"
                    : route == BridgeGraphicsBackendRoute.PlaybackWindowsEditorPlay
                        ? "Quilt playback requires a Direct3D11 or Direct3D12 Editor graphics device."
                        : "Bridge worker requires Direct3D11 or Direct3D12";
                return false;
            }

            if (route == BridgeGraphicsBackendRoute.LegacyDirect && producer != BridgeGraphicsApi.Direct3D11)
            {
                error = "Bridge output currently supports Direct3D11 only";
                return false;
            }

            if (route == BridgeGraphicsBackendRoute.LegacyDirect && renderingThreadingMode != RenderingThreadingMode.Direct)
            {
                error = "Bridge D3D11 interop requires Direct rendering threading; experimental support requires -force-gfx-direct";
                return false;
            }

            decision = new BridgeGraphicsBackendDecision(route, producer, BridgeGraphicsApi.Direct3D11);
            return true;
        }
    }
}

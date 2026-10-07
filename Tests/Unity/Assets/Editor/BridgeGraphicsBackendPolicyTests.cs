using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BridgeGraphicsBackendPolicyTests
{
    [MenuItem("Tools/ViewWeave/Run Graphics Backend Policy Validation")]
    public static void RunValidation()
    {
        RunPolicyAssertions();
        Debug.Log("Bridge graphics backend policy tests passed");
    }

    public static void RunPolicyAssertions()
    {
        Type bridgeAssemblyType = typeof(ViewWeave.ViewWeaveBridgeSession);
        Assembly bridgeAssembly = bridgeAssemblyType.Assembly;
        Type routeType = bridgeAssembly.GetType("ViewWeave.BridgeGraphicsBackendRoute", true);
        Type policyType = bridgeAssembly.GetType("ViewWeave.BridgeGraphicsBackendPolicy", true);
        Type decisionType = bridgeAssembly.GetType("ViewWeave.BridgeGraphicsBackendDecision", true);
        MethodInfo select = policyType.GetMethod("TrySelect", BindingFlags.Static | BindingFlags.NonPublic);
        Require(select != null, "shared backend policy is present");

        CheckWorkerD3D12KeepsProducerAndConsumerDistinct(routeType, decisionType, select);
        CheckWorkerPlatformBoundary(routeType, select);
        CheckUnsupportedApisAreRejected(routeType, select);
        CheckLegacyRouteIsDirectD3D11Only(routeType, select);
        CheckPlaybackRemainsWindowsEditorPlayOnly(routeType, decisionType, select);
        CheckUnknownRouteIsRejected(routeType, select);
    }

    static void CheckWorkerD3D12KeepsProducerAndConsumerDistinct(Type routeType, Type decisionType, MethodInfo select)
    {
        Require(TrySelect(select, routeType, "Worker", RuntimePlatform.WindowsEditor, true, true,
            GraphicsDeviceType.Direct3D12, RenderingThreadingMode.NativeGraphicsJobsSplitThreading, out object decision, out string error),
            "Windows Editor Play accepts the D3D12 worker producer: " + error);
        Require(ReadDecision(decisionType, decision, "UnityProducer").ToString() == "Direct3D12", "D3D12 remains the Unity producer API");
        Require(ReadDecision(decisionType, decision, "BridgeConsumer").ToString() == "Direct3D11", "D3D12 worker routes to the Direct3D11 Bridge consumer");

        Require(TrySelect(select, routeType, "Worker", RuntimePlatform.WindowsPlayer, false, false,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out decision, out error),
            "Windows Player accepts the D3D11 worker producer: " + error);
        Require(ReadDecision(decisionType, decision, "UnityProducer").ToString() == "Direct3D11", "D3D11 producer identity is preserved");
    }

    static void CheckWorkerPlatformBoundary(Type routeType, MethodInfo select)
    {
        Require(!TrySelect(select, routeType, "Worker", RuntimePlatform.WindowsEditor, true, false,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out _, out _), "worker rejects Windows Editor edit mode");
        Require(!TrySelect(select, routeType, "Worker", RuntimePlatform.LinuxPlayer, false, false,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out _, out _), "worker rejects a non-Windows player");
        Require(!TrySelect(select, routeType, "Worker", RuntimePlatform.WindowsEditor, false, true,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out _, out _), "worker rejects an Editor platform without the Editor flag");
        Require(!TrySelect(select, routeType, "Worker", RuntimePlatform.WindowsPlayer, true, true,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out _, out _), "worker rejects an Editor context reported as Windows Player");
    }

    static void CheckUnsupportedApisAreRejected(Type routeType, MethodInfo select)
    {
        GraphicsDeviceType[] unsupported = { GraphicsDeviceType.Vulkan, GraphicsDeviceType.Metal, (GraphicsDeviceType)999 };
        foreach (GraphicsDeviceType device in unsupported)
        {
            Require(!TrySelect(select, routeType, "Worker", RuntimePlatform.WindowsEditor, true, true,
                device, RenderingThreadingMode.Direct, out _, out _), "worker rejects unsupported API " + ((int)device).ToString());
            Require(!TrySelect(select, routeType, "PlaybackWindowsEditorPlay", RuntimePlatform.WindowsEditor, true, true,
                device, RenderingThreadingMode.Direct, out _, out _), "playback rejects unsupported API " + ((int)device).ToString());
            Require(!TrySelect(select, routeType, "LegacyDirect", RuntimePlatform.WindowsEditor, true, true,
                device, RenderingThreadingMode.Direct, out _, out _), "legacy route rejects unsupported API " + ((int)device).ToString());
        }
    }

    static void CheckLegacyRouteIsDirectD3D11Only(Type routeType, MethodInfo select)
    {
        Require(TrySelect(select, routeType, "LegacyDirect", RuntimePlatform.WindowsEditor, true, false,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out _, out string error),
            "legacy route accepts Direct D3D11: " + error);
        Require(!TrySelect(select, routeType, "LegacyDirect", RuntimePlatform.WindowsEditor, true, true,
            GraphicsDeviceType.Direct3D12, RenderingThreadingMode.Direct, out _, out _), "legacy route rejects D3D12");
        Require(!TrySelect(select, routeType, "LegacyDirect", RuntimePlatform.WindowsEditor, true, true,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.NativeGraphicsJobsSplitThreading, out _, out _),
            "legacy route rejects split rendering threading");
    }

    static void CheckPlaybackRemainsWindowsEditorPlayOnly(Type routeType, Type decisionType, MethodInfo select)
    {
        Require(TrySelect(select, routeType, "PlaybackWindowsEditorPlay", RuntimePlatform.WindowsEditor, true, true,
            GraphicsDeviceType.Direct3D12, RenderingThreadingMode.NativeGraphicsJobsSplitThreading, out object decision, out string error),
            "playback accepts the existing Windows Editor Play D3D12 route: " + error);
        Require(ReadDecision(decisionType, decision, "UnityProducer").ToString() == "Direct3D12", "playback producer remains D3D12");
        Require(ReadDecision(decisionType, decision, "BridgeConsumer").ToString() == "Direct3D11", "playback consumer remains Direct3D11");
        Require(!TrySelect(select, routeType, "PlaybackWindowsEditorPlay", RuntimePlatform.WindowsPlayer, false, false,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out _, out _), "playback rejects Windows Player");
        Require(!TrySelect(select, routeType, "PlaybackWindowsEditorPlay", RuntimePlatform.WindowsEditor, true, false,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, out _, out _), "playback rejects Windows Editor edit mode");
    }

    static void CheckUnknownRouteIsRejected(Type routeType, MethodInfo select)
    {
        object[] arguments =
        {
            Enum.ToObject(routeType, 999), RuntimePlatform.WindowsEditor, true, true,
            GraphicsDeviceType.Direct3D11, RenderingThreadingMode.Direct, null, null
        };
        bool accepted = (bool)select.Invoke(null, arguments);
        Require(!accepted && !string.IsNullOrEmpty(arguments[7] as string), "unknown backend route fails explicitly");
    }

    static bool TrySelect(MethodInfo select, Type routeType, string route, RuntimePlatform platform, bool isEditor, bool isPlaying,
        GraphicsDeviceType device, RenderingThreadingMode threadingMode, out object decision, out string error)
    {
        object[] arguments =
        {
            Enum.Parse(routeType, route), platform, isEditor, isPlaying, device, threadingMode, null, null
        };
        bool accepted = (bool)select.Invoke(null, arguments);
        decision = arguments[6];
        error = arguments[7] as string;
        return accepted;
    }

    static object ReadDecision(Type decisionType, object decision, string fieldName)
    {
        FieldInfo field = decisionType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Require(field != null, "backend decision exposes " + fieldName);
        return field.GetValue(decision);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Bridge graphics backend policy test failed: " + message);
    }
}

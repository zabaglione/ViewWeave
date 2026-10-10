public static class MetalValidationGate
{
    public static void Run()
    {
        BridgeRuntimeLocatorMacOSTests.RunValidation();
        MetalShaderValidation.Run();
        MetalNativeSessionValidation.Run();
    }
}

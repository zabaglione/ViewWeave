using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ViewWeave;

namespace ViewWeave.BridgeSessionValidation
{
    public sealed class BridgeNativeImageGuardTests
    {
        const string ShimName = "lookingglass_bridge_shim.dll";

        [Test]
        public void AllowsZeroOrOneMatchingImage()
        {
            Assert.IsTrue(Validate());
            Assert.IsTrue(Validate(new ModuleRecord(ShimName, 0x1000)));
        }

        [Test]
        public void RejectsTwoMatchingImagesAtDifferentBases()
        {
            Assert.IsFalse(Validate(new ModuleRecord(ShimName, 0x1000), new ModuleRecord(ShimName, 0x2000)));
            StringAssert.Contains("Restart the Unity Editor", lastError);
        }

        [Test]
        public void SameBaseAddressIsCountedOnce()
        {
            Assert.IsTrue(Validate(new ModuleRecord(ShimName, 0x1000), new ModuleRecord(ShimName, 0x1000)));
        }

        [Test]
        public void ModuleNameComparisonIgnoresCase()
        {
            Assert.IsFalse(Validate(new ModuleRecord(ShimName.ToUpperInvariant(), 0x1000), new ModuleRecord(ShimName, 0x2000)));
        }

        [Test]
        public void UnrelatedModulesDoNotCountAsBridgeImages()
        {
            Assert.IsTrue(Validate(new ModuleRecord("other.dll", 0x1000), new ModuleRecord("another.dll", 0x2000)));
            Assert.IsTrue(Validate(new ModuleRecord(ShimName, 0x1000), new ModuleRecord("other.dll", 0x2000)));
        }

        [Test]
        public void InspectionFailureRejectsTheBeginAttempt()
        {
            Type guard = GetGuardType();
            Type moduleType = GetModuleType(guard);
            Type sequenceType = typeof(IEnumerable<>).MakeGenericType(moduleType);
            Type providerType = typeof(Func<>).MakeGenericType(sequenceType);
            MethodInfo thrower = typeof(BridgeNativeImageGuardTests)
                .GetMethod(nameof(ThrowDuringInspection), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(moduleType);
            Delegate provider = Delegate.CreateDelegate(providerType, thrower);
            MethodInfo inspect = guard.GetMethod("TryInspect", BindingFlags.NonPublic | BindingFlags.Static);
            object[] arguments = { provider, null };
            bool accepted = (bool)inspect.Invoke(null, arguments);
            lastError = (string)arguments[1];

            Assert.IsFalse(accepted);
            StringAssert.Contains("Unable to inspect loaded native modules", lastError);
            StringAssert.Contains("Restart the Unity Editor", lastError);
            Assert.IsTrue(IsAscii(lastError));
        }

        static string lastError = string.Empty;

        static bool Validate(params ModuleRecord[] records)
        {
            Type guard = GetGuardType();
            Type moduleType = GetModuleType(guard);
            Array modules = Array.CreateInstance(moduleType, records.Length);
            ConstructorInfo constructor = moduleType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string), typeof(IntPtr) }, null);
            for (int i = 0; i < records.Length; ++i)
                modules.SetValue(constructor.Invoke(new object[] { records[i].name, records[i].baseAddress }), i);

            MethodInfo validate = guard.GetMethod("TryValidateModules", BindingFlags.NonPublic | BindingFlags.Static);
            object[] arguments = { modules, null };
            bool accepted = (bool)validate.Invoke(null, arguments);
            lastError = (string)arguments[1];
            return accepted;
        }

        static Type GetGuardType() => typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeNativeImageGuard", true);
        static Type GetModuleType(Type guard) => guard.GetNestedType("BridgeNativeModuleIdentity", BindingFlags.NonPublic)
            ?? guard.Assembly.GetType("ViewWeave.BridgeNativeModuleIdentity", true);

        static IEnumerable<T> ThrowDuringInspection<T>()
        {
            throw new InvalidOperationException("synthetic module inspection failure");
#pragma warning disable 162
            yield break;
#pragma warning restore 162
        }

        static bool IsAscii(string value)
        {
            foreach (char character in value)
                if (character < 0x20 || character > 0x7e) return false;
            return true;
        }

        readonly struct ModuleRecord
        {
            public readonly string name;
            public readonly IntPtr baseAddress;

            public ModuleRecord(string moduleName, int address)
            {
                name = moduleName;
                baseAddress = new IntPtr(address);
            }
        }
    }
}

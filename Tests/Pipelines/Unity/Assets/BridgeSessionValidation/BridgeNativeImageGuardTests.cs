using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
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
        public void LoadedOwnerMustMatchExpectedPackagePathAndImageBase()
        {
            string expected = @"E:\Packages\bridge\Plugins\x86_64\lookingglass_bridge_shim.dll";
            ModuleRecord owner = new ModuleRecord(ShimName, 0x1000, expected);
            Assert.IsTrue(ValidateOwner(new[] { owner }, expected, owner));
            Assert.IsFalse(ValidateOwner(new[] { owner }, @"E:\OldCache\lookingglass_bridge_shim.dll", owner));
            Assert.IsFalse(ValidateOwner(new[] { new ModuleRecord(ShimName, 0x2000, expected) }, expected, owner));
            Assert.IsFalse(ValidateOwner(Array.Empty<ModuleRecord>(), expected, owner));
        }

        [Test]
        public void ExpectedShimResolverAcceptsDirectBridgePackageLayout()
        {
            const string root = @"E:\Packages\com.zabaglione.lookingglass.viewweave.bridge";
            string expected = Path.GetFullPath(Path.Combine(root, "Plugins", "x86_64", ShimName));
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expected };

            Assert.IsTrue(BridgeNativeImageGuard.TryResolveExpectedNativePath(root, existing.Contains,
                out string resolved, out string error), error);
            Assert.AreEqual(expected, resolved);
        }

        [TestCase(@"E:\Packages\com.zabaglione.lookingglass.viewweave.urp")]
        [TestCase(@"E:\Packages\com.zabaglione.lookingglass.viewweave.hdrp")]
        public void ExpectedShimResolverAcceptsBundledPipelinePackageLayout(string root)
        {
            string expected = Path.GetFullPath(Path.Combine(root, "ViewWeave", "Bridge", "Plugins", "x86_64", ShimName));
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expected };

            Assert.IsTrue(BridgeNativeImageGuard.TryResolveExpectedNativePath(root, existing.Contains,
                out string resolved, out string error), error);
            Assert.AreEqual(expected, resolved);
        }

        [Test]
        public void ExpectedShimResolverRejectsMissingCandidates()
        {
            Assert.IsFalse(BridgeNativeImageGuard.TryResolveExpectedNativePath(@"E:\Packages\empty",
                _ => false, out string resolved, out string error));
            Assert.AreEqual(string.Empty, resolved);
            StringAssert.Contains("missing from supported package paths", error);
            StringAssert.Contains("Restart the Unity Editor", error);
        }

        [Test]
        public void ExpectedShimResolverRejectsAmbiguousCandidates()
        {
            const string root = @"E:\Packages\com.zabaglione.lookingglass.viewweave.urp";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(Path.Combine(root, "Plugins", "x86_64", ShimName)),
                Path.GetFullPath(Path.Combine(root, "ViewWeave", "Bridge", "Plugins", "x86_64", ShimName))
            };

            Assert.IsFalse(BridgeNativeImageGuard.TryResolveExpectedNativePath(root, existing.Contains,
                out string resolved, out string error));
            Assert.AreEqual(string.Empty, resolved);
            StringAssert.Contains("both direct and bundled", error);
            StringAssert.Contains("Restart the Unity Editor", error);
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

        [Test]
        public void ToolhelpEnumeratorRetriesBoundedBadLengthSnapshotRaces()
        {
            var api = new FakeToolhelpApi
            {
                CreateErrors = new Queue<int>(new[] { BridgeNativeModuleEnumerator.ErrorBadLength, BridgeNativeModuleEnumerator.ErrorBadLength, 0 }),
                Entries = new[] { Entry(ShimName, 0x1000, 10) },
                Paths = new Dictionary<IntPtr, string> { [new IntPtr(10)] = @"E:\Packages\bridge\lookingglass_bridge_shim.dll" }
            };

            Assert.IsTrue(BridgeNativeModuleEnumerator.TryEnumerate(api, out List<BridgeNativeModuleIdentity> modules, out string error), error);
            Assert.AreEqual(3, api.SnapshotAttempts);
            Assert.AreEqual(1, api.ClosedSnapshots);
            Assert.AreEqual(1, modules.Count);
            Assert.AreEqual(@"E:\Packages\bridge\lookingglass_bridge_shim.dll", modules[0].FullPath);
        }

        [Test]
        public void ToolhelpEnumeratorDiscardsPartialListOnMidEnumerationFailure()
        {
            var api = new FakeToolhelpApi
            {
                Entries = new[] { Entry(ShimName, 0x1000, 10), Entry("other.dll", 0x2000, 20) },
                Paths = new Dictionary<IntPtr, string> { [new IntPtr(10)] = @"E:\Packages\bridge\lookingglass_bridge_shim.dll" },
                NextError = 5
            };

            Assert.IsFalse(BridgeNativeModuleEnumerator.TryEnumerate(api, out List<BridgeNativeModuleIdentity> modules, out string error));
            Assert.IsNull(modules, "Incomplete module snapshots must not be published as a complete list.");
            Assert.AreEqual(1, api.ClosedSnapshots);
            StringAssert.Contains("Win32 error 5", error);
            Assert.IsTrue(IsAscii(error));
        }

        [Test]
        public void ToolhelpEnumeratorPreservesLongResolvedModulePaths()
        {
            string expectedPath = @"E:\Packages\" + new string('a', 420) + @"\lookingglass_bridge_shim.dll";
            var api = new FakeToolhelpApi
            {
                Entries = new[] { Entry(ShimName, 0x1000, 10) },
                Paths = new Dictionary<IntPtr, string> { [new IntPtr(10)] = expectedPath }
            };

            Assert.IsTrue(BridgeNativeModuleEnumerator.TryEnumerate(api, out List<BridgeNativeModuleIdentity> modules, out string error), error);
            Assert.AreEqual(expectedPath, modules[0].FullPath);
        }

        [Test]
        public void ToolhelpDuplicateBridgeImagesAreRejectedByOwnerValidation()
        {
            var api = new FakeToolhelpApi
            {
                Entries = new[] { Entry(ShimName, 0x1000, 10), Entry(ShimName, 0x2000, 20) },
                Paths = new Dictionary<IntPtr, string>
                {
                    [new IntPtr(10)] = @"E:\Packages\bridge\lookingglass_bridge_shim.dll",
                    [new IntPtr(20)] = @"E:\Packages\other\lookingglass_bridge_shim.dll"
                }
            };

            Assert.IsTrue(BridgeNativeModuleEnumerator.TryEnumerate(api, out List<BridgeNativeModuleIdentity> modules, out string enumerateError), enumerateError);
            Assert.IsFalse(ValidateOwner(modules, @"E:\Packages\bridge\lookingglass_bridge_shim.dll", new ModuleRecord(ShimName, 0x1000,
                @"E:\Packages\bridge\lookingglass_bridge_shim.dll")));
            StringAssert.Contains("Multiple loaded", lastError);
        }

        [Test]
        public void IncompleteToolhelpSnapshotKeepsNativeOwnerPaused()
        {
            Type guard = GetGuardType();
            MethodInfo getModules = guard.GetMethod("TryGetCurrentModules", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo readyField = guard.GetField("cacheValidationReady", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo pendingField = guard.GetField("cacheRefreshPending", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo errorField = guard.GetField("cacheValidationError", BindingFlags.NonPublic | BindingFlags.Static);
            bool oldReady = (bool)readyField.GetValue(null);
            bool oldPending = (bool)pendingField.GetValue(null);
            string oldError = (string)errorField.GetValue(null);
            var api = new FakeToolhelpApi { CreateErrors = new Queue<int>(new[] { 5 }) };
            GameObject ownerObject = new GameObject("native-image-toolhelp-retention-test");
            ownerObject.SetActive(false);
            BridgeWorkerQuiltOutput output = ownerObject.AddComponent<BridgeWorkerQuiltOutput>();
            FieldInfo handleField = typeof(BridgeWorkerQuiltOutput).GetField("handle", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo stateField = typeof(BridgeWorkerQuiltOutput).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo identityField = typeof(BridgeWorkerQuiltOutput).GetField("nativeModuleIdentity", BindingFlags.NonPublic | BindingFlags.Instance);
            Type identityType = guard.Assembly.GetType("ViewWeave.BridgeNativeModuleIdentity", true);
            ConstructorInfo identityConstructor = identityType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(IntPtr), typeof(string) }, null);
            object identity = identityConstructor.Invoke(new object[] { ShimName, new IntPtr(0x1000), @"E:\Packages\Bridge\Plugins\x86_64\lookingglass_bridge_shim.dll" });
            IntPtr fakeHandle = new IntPtr(0x4321);
            try
            {
                BridgeNativeModuleEnumerator.SetApiForTests(api);
                readyField.SetValue(null, true);
                pendingField.SetValue(null, false);
                errorField.SetValue(null, string.Empty);
                object[] arguments = { true, null, null };
                Assert.IsFalse((bool)getModules.Invoke(null, arguments));
                Assert.IsNull(arguments[1]);
                Assert.IsTrue((bool)pendingField.GetValue(null));

                handleField.SetValue(output, fakeHandle);
                stateField.SetValue(output, Enum.Parse(stateField.FieldType, "Ready"));
                identityField.SetValue(output, identity);
                output.Poll();

                Assert.AreEqual(fakeHandle, handleField.GetValue(output));
                Assert.AreEqual("Ready", stateField.GetValue(output).ToString());
            }
            finally
            {
                BridgeNativeModuleEnumerator.SetApiForTests(null);
                handleField.SetValue(output, IntPtr.Zero);
                stateField.SetValue(output, Enum.Parse(stateField.FieldType, "Closed"));
                identityField.SetValue(output, default(BridgeNativeModuleIdentity));
                readyField.SetValue(null, oldReady);
                pendingField.SetValue(null, oldPending);
                errorField.SetValue(null, oldError);
                UnityEngine.Object.DestroyImmediate(ownerObject);
            }
        }

        [Test]
        public void TransientCacheRefreshPausesOwnerWithoutTransferringIt()
        {
            Type guard = GetGuardType();
            FieldInfo readyField = guard.GetField("cacheValidationReady", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo pendingField = guard.GetField("cacheRefreshPending", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo errorField = guard.GetField("cacheValidationError", BindingFlags.NonPublic | BindingFlags.Static);
            bool oldReady = (bool)readyField.GetValue(null);
            bool oldPending = (bool)pendingField.GetValue(null);
            string oldError = (string)errorField.GetValue(null);
            var ownerObject = new UnityEngine.GameObject("native-image-validation-pause-test");
            ownerObject.SetActive(false);
            BridgeWorkerQuiltOutput output = ownerObject.AddComponent<BridgeWorkerQuiltOutput>();
            FieldInfo handleField = typeof(BridgeWorkerQuiltOutput).GetField("handle", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo stateField = typeof(BridgeWorkerQuiltOutput).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo identityField = typeof(BridgeWorkerQuiltOutput).GetField("nativeModuleIdentity", BindingFlags.NonPublic | BindingFlags.Instance);
            Type identityType = guard.Assembly.GetType("ViewWeave.BridgeNativeModuleIdentity", true);
            ConstructorInfo identityConstructor = identityType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(IntPtr), typeof(string) }, null);
            object identity = identityConstructor.Invoke(new object[] { ShimName, new IntPtr(0x1000), @"E:\Packages\Bridge\Plugins\x86_64\lookingglass_bridge_shim.dll" });
            IntPtr fakeHandle = new IntPtr(0x4321);
            try
            {
                readyField.SetValue(null, false);
                pendingField.SetValue(null, true);
                errorField.SetValue(null, string.Empty);
                handleField.SetValue(output, fakeHandle);
                stateField.SetValue(output, Enum.Parse(stateField.FieldType, "Ready"));
                identityField.SetValue(output, identity);

                output.Poll();
                Assert.AreEqual(fakeHandle, handleField.GetValue(output), "a transient Editor cache refresh must retain the active native owner");
                Assert.AreEqual("Ready", stateField.GetValue(output).ToString());

                output.Submit(null, default);
                Assert.AreEqual(fakeHandle, handleField.GetValue(output), "submission pauses without disposing the worker while validation is pending");
                Assert.AreEqual("Ready", stateField.GetValue(output).ToString());
            }
            finally
            {
                handleField.SetValue(output, IntPtr.Zero);
                stateField.SetValue(output, Enum.Parse(stateField.FieldType, "Closed"));
                identityField.SetValue(output, default(BridgeNativeModuleIdentity));
                readyField.SetValue(null, oldReady);
                pendingField.SetValue(null, oldPending);
                errorField.SetValue(null, oldError);
                UnityEngine.Object.DestroyImmediate(ownerObject);
            }
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

        static bool ValidateOwner(ModuleRecord[] records, string expectedPath, ModuleRecord owner)
        {
            Type guard = GetGuardType();
            Type moduleType = GetModuleType(guard);
            Array modules = Array.CreateInstance(moduleType, records.Length);
            ConstructorInfo constructor = moduleType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(IntPtr), typeof(string) }, null);
            for (int i = 0; i < records.Length; ++i)
                modules.SetValue(constructor.Invoke(new object[] { records[i].name, records[i].baseAddress, records[i].path }), i);
            object identity = constructor.Invoke(new object[] { owner.name, owner.baseAddress, owner.path });
            MethodInfo validate = guard.GetMethod("TryValidateLoadedModules", BindingFlags.NonPublic | BindingFlags.Static);
            object[] arguments = { modules, expectedPath, identity, true, null, null };
            bool accepted = (bool)validate.Invoke(null, arguments);
            lastError = (string)arguments[5];
            return accepted;
        }

        static bool ValidateOwner(List<BridgeNativeModuleIdentity> modules, string expectedPath, ModuleRecord owner)
        {
            Type guard = GetGuardType();
            Type identityType = guard.Assembly.GetType("ViewWeave.BridgeNativeModuleIdentity", true);
            ConstructorInfo constructor = identityType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(IntPtr), typeof(string) }, null);
            object identity = constructor.Invoke(new object[] { owner.name, owner.baseAddress, owner.path });
            MethodInfo validate = guard.GetMethod("TryValidateLoadedModules", BindingFlags.NonPublic | BindingFlags.Static);
            object[] arguments = { modules, expectedPath, identity, true, null, null };
            bool accepted = (bool)validate.Invoke(null, arguments);
            lastError = (string)arguments[5];
            return accepted;
        }

        static BridgeToolhelpModuleEntry Entry(string name, int baseAddress, int moduleHandle)
        {
            return new BridgeToolhelpModuleEntry
            {
                ModuleName = name,
                BaseAddress = new IntPtr(baseAddress),
                ModuleHandle = new IntPtr(moduleHandle)
            };
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
            public readonly string path;

            public ModuleRecord(string moduleName, int address)
            {
                name = moduleName;
                baseAddress = new IntPtr(address);
                path = string.Empty;
            }

            public ModuleRecord(string moduleName, int address, string modulePath)
            {
                name = moduleName;
                baseAddress = new IntPtr(address);
                path = modulePath;
            }
        }

        sealed class FakeToolhelpApi : IBridgeToolhelpModuleApi
        {
            public Queue<int> CreateErrors = new Queue<int>();
            public BridgeToolhelpModuleEntry[] Entries = Array.Empty<BridgeToolhelpModuleEntry>();
            public Dictionary<IntPtr, string> Paths = new Dictionary<IntPtr, string>();
            public int FirstError;
            public int NextError;
            public int SnapshotAttempts;
            public int ClosedSnapshots;
            int currentIndex;

            public IntPtr CreateSnapshot(out int errorCode)
            {
                SnapshotAttempts++;
                errorCode = CreateErrors.Count > 0 ? CreateErrors.Dequeue() : 0;
                if (errorCode != 0) return new IntPtr(-1);
                currentIndex = 0;
                return new IntPtr(SnapshotAttempts);
            }

            public bool ModuleFirst(IntPtr snapshot, out BridgeToolhelpModuleEntry entry, out int errorCode)
            {
                if (FirstError != 0)
                {
                    errorCode = FirstError;
                    entry = default;
                    return false;
                }
                if (Entries.Length == 0)
                {
                    errorCode = BridgeNativeModuleEnumerator.ErrorNoMoreFiles;
                    entry = default;
                    return false;
                }
                errorCode = 0;
                entry = Entries[0];
                return true;
            }

            public bool ModuleNext(IntPtr snapshot, out BridgeToolhelpModuleEntry entry, out int errorCode)
            {
                if (NextError != 0)
                {
                    errorCode = NextError;
                    entry = default;
                    return false;
                }
                currentIndex++;
                if (currentIndex >= Entries.Length)
                {
                    errorCode = BridgeNativeModuleEnumerator.ErrorNoMoreFiles;
                    entry = default;
                    return false;
                }
                errorCode = 0;
                entry = Entries[currentIndex];
                return true;
            }

            public bool TryGetModulePath(IntPtr moduleHandle, out string path, out int errorCode)
            {
                errorCode = 0;
                return Paths.TryGetValue(moduleHandle, out path);
            }

            public bool CloseSnapshot(IntPtr snapshot, out int errorCode)
            {
                ClosedSnapshots++;
                errorCode = 0;
                return true;
            }
        }
    }
}

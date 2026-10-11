#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Globalization;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave.WindowsQuickStartValidation
{
    public static class QuickStartValidationRunner
    {
        const string Prefix = "VW_QUICKSTART_";
        const string StateKey = Prefix + "STATE";
        const string PackageKey = Prefix + "PACKAGE";
        const string DisplayKey = Prefix + "DISPLAY";
        const string SampleNameKey = Prefix + "SAMPLE_NAME";
        const string SamplePathKey = Prefix + "SAMPLE_PATH";
        const string ScenePathKey = Prefix + "SCENE_PATH";
        const string PipelineKey = Prefix + "PIPELINE";
        const string DiscoveryStageKey = Prefix + "DISCOVERY_STAGE";
        const double TimeoutSeconds = 900.0;

        static double deadline;
        static bool polling;

        [InitializeOnLoadMethod]
        static void ResumeAfterReload()
        {
            if (SessionState.GetString(StateKey, string.Empty) == "load-scene")
                EditorApplication.delayCall += BeginSceneLoad;
            else if (SessionState.GetString(StateKey, string.Empty) == "render")
                EditorApplication.delayCall += BeginRenderPolling;
        }

        public static void Start()
        {
            string packageName = Environment.GetEnvironmentVariable("VW_QUICKSTART_PACKAGE") ?? string.Empty;
            string sampleName = Environment.GetEnvironmentVariable("VW_QUICKSTART_SAMPLE") ?? string.Empty;
            string pipeline = Environment.GetEnvironmentVariable("VW_QUICKSTART_PIPELINE") ?? string.Empty;
            string expectedPackageRoot = Environment.GetEnvironmentVariable("VW_QUICKSTART_EXPECTED_PACKAGE_ROOT") ?? string.Empty;
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(sampleName) ||
                (pipeline != "URP" && pipeline != "HDRP"))
            {
                Fail("Missing package, sample, or pipeline environment configuration.");
                return;
            }

            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64 ||
                    SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
                    throw new InvalidOperationException("Editor is not running Windows D3D12.");

                UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForPackageName(packageName);
                if (package == null) throw new InvalidOperationException("The expected local package is not resolved.");
                if (string.IsNullOrEmpty(expectedPackageRoot) ||
                    !string.Equals(Path.GetFullPath(package.resolvedPath), Path.GetFullPath(expectedPackageRoot), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Resolved package path does not match the expected Distribution package.");
                string manifestPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../Packages/manifest.json"));
                string expectedUri = pipeline == "URP" ? "file:E:/LookingGlassSDK/Distribution/urp" : "file:E:/LookingGlassSDK/Distribution/hdrp";
                if (!File.ReadAllText(manifestPath).Contains(expectedUri))
                    throw new InvalidOperationException("The sample project is not using the expected local Distribution package.");

                Type sampleType = FindType("UnityEditor.PackageManager.UI.Sample");
                if (sampleType == null) throw new InvalidOperationException("Package Manager Sample API is unavailable.");
                MethodInfo find = sampleType.GetMethod("FindByPackage", BindingFlags.Public | BindingFlags.Static);
                MethodInfo import = sampleType.GetMethod("Import", BindingFlags.Public | BindingFlags.Instance);
                Type optionsType = sampleType.GetNestedType("ImportOptions", BindingFlags.Public);
                if (find == null || import == null || optionsType == null)
                    throw new InvalidOperationException("Package Manager Sample import API is incomplete.");

                object[] findArgs = { package.name, package.version };
                var samples = ((IEnumerable)find.Invoke(null, findArgs)).Cast<object>().ToArray();
                PropertyInfo displayNameProperty = sampleType.GetProperty("displayName");
                object sample = samples.SingleOrDefault(value =>
                    string.Equals((string)displayNameProperty.GetValue(value), sampleName, StringComparison.Ordinal));
                if (sample == null) throw new InvalidOperationException("Expected Quick Start sample was not found exactly once.");

                string importPath = Path.Combine("Assets", "Samples", package.displayName, package.version, sampleName).Replace('\\', '/');
                string absoluteImportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", importPath));
                string expectedImportPath = Environment.GetEnvironmentVariable("VW_QUICKSTART_EXPECTED_IMPORT") ?? string.Empty;
                if (string.IsNullOrEmpty(expectedImportPath) ||
                    !string.Equals(absoluteImportPath, Path.GetFullPath(expectedImportPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Package Manager computed import path differs from the protected target.");
                if (Directory.Exists(absoluteImportPath) || File.Exists(absoluteImportPath))
                    throw new InvalidOperationException("Quick Start import target already exists; user assets were protected and not overwritten.");

                string packageRoot = package.resolvedPath;
                string sourceFolder = pipeline == "URP" ? "QuickStartURP" : "QuickStartHDRP";
                string sourceSample = Path.Combine(packageRoot, "Samples~", sourceFolder);
                if (!File.Exists(Path.Combine(sourceSample, "QuickStart.unity")))
                    throw new InvalidOperationException("Quick Start source scene is missing from the resolved package.");

                SessionState.SetString(PackageKey, package.name);
                SessionState.SetString(DisplayKey, package.displayName);
                SessionState.SetString(SampleNameKey, sampleName);
                SessionState.SetString(SamplePathKey, importPath);
                SessionState.SetString(ScenePathKey, importPath + "/QuickStart.unity");
                SessionState.SetString(PipelineKey, pipeline);
                SessionState.SetString(StateKey, "load-scene");

                string options = "HideImportWindow";
                object parsedOptions = Enum.Parse(optionsType, options);
                bool imported = (bool)import.Invoke(sample, new[] { parsedOptions });
                if (!imported) throw new InvalidOperationException("Sample.Import returned false.");

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EditorApplication.delayCall += BeginSceneLoad;
            }
            catch (Exception error)
            {
                Fail("Import/setup failed: " + Ascii(error.GetBaseException().Message));
            }
        }

        static void BeginSceneLoad()
        {
            if (SessionState.GetString(StateKey, string.Empty) != "load-scene") return;
            try
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    EditorApplication.delayCall += BeginSceneLoad;
                    return;
                }
                string scenePath = SessionState.GetString(ScenePathKey, string.Empty);
                if (!File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", scenePath))))
                    throw new FileNotFoundException("Imported Quick Start scene was not found.");

                VerifyImportedSource();

                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                SessionState.SetString(StateKey, "render");
                SessionState.SetString(DiscoveryStageKey, "automatic");
                deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
                EditorApplication.update += PollForSelection;
                polling = true;
            }
            catch (Exception error)
            {
                Fail("Scene load failed: " + Ascii(error.GetBaseException().Message));
            }
        }

        static void BeginRenderPolling()
        {
            if (SessionState.GetString(StateKey, string.Empty) != "render" || polling) return;
            deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            EditorApplication.update += PollForSelection;
            polling = true;
        }

        static void PollForSelection()
        {
            if (SessionState.GetString(StateKey, string.Empty) != "render")
            {
                EditorApplication.update -= PollForSelection;
                polling = false;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                string pipeline = SessionState.GetString(PipelineKey, string.Empty);
                if (!PipelineMatches(pipeline)) throw new InvalidOperationException("Active render pipeline does not match the Quick Start sample.");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
                    throw new InvalidOperationException("The active graphics API changed from D3D12.");

                ViewWeaveRig rig = UnityEngine.Object.FindObjectsByType<ViewWeaveRig>(FindObjectsSortMode.None).SingleOrDefault();
                if (rig == null)
                {
                    if (EditorApplication.timeSinceStartup < deadline) return;
                    throw new TimeoutException("Quick Start rig did not load.");
                }

                Type platformOutputType = FindType(pipeline == "URP"
                    ? "ViewWeave.Samples.QuickStartURP.QuickStartPlatformOutput"
                    : "ViewWeave.Samples.QuickStartHDRP.QuickStartPlatformOutput");
                if (platformOutputType == null || rig.GetComponent(platformOutputType) == null)
                    throw new InvalidOperationException("Quick Start platform output component is missing.");

                BridgeWorkerQuiltOutput worker = FindComponent(rig.gameObject, "ViewWeave.BridgeWorkerQuiltOutput") as BridgeWorkerQuiltOutput;
                BridgeMetalQuiltOutput metal = FindComponent(rig.gameObject, "ViewWeave.BridgeMetalQuiltOutput") as BridgeMetalQuiltOutput;
                ViewWeaveBridgeSession session = FindComponent(rig.gameObject, "ViewWeave.ViewWeaveBridgeSession") as ViewWeaveBridgeSession;
                if (worker == null || metal == null || session == null)
                    throw new InvalidOperationException("Quick Start output components are incomplete.");
                if (worker.enabled && session.enabled && !metal.enabled && rig.Renderer != null && rig.Renderer.OutputBehaviour == worker)
                {
                    string status = ViewWeave.Editor.BridgeEditorDisplayDiscovery.GetStatus(rig);
                    string discoveryStage = SessionState.GetString(DiscoveryStageKey, string.Empty);
                    if (status == "Bridge device geometry acquired without output or preview")
                    {
                        if (discoveryStage == "automatic")
                        {
                            ValidateResolvedProfile(rig, "automatic");
                            bool automaticEnabled = ReadAutomaticRequestsEnabled();
                            int priorRequestCount = GetDiscoveryRequestCount();
                            if (!automaticEnabled) throw new InvalidOperationException("Automatic display discovery was disabled before scene load.");
                            Log("DISCOVERY_AUTOMATIC", "status=" + status + " automaticRequestsEnabled=" + automaticEnabled +
                                " requestsAtSuccess=" + priorRequestCount + " source=scene-open");
                            if (!ViewWeave.Editor.BridgeEditorDisplayDiscovery.TryRefresh(rig, out string refreshError))
                                throw new InvalidOperationException("Explicit display refresh was rejected: " + Ascii(refreshError));
                            SessionState.SetString(DiscoveryStageKey, "explicit-refresh");
                            deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
                            Log("DISCOVERY_REFRESH", "accepted=true status=" + Ascii(ViewWeave.Editor.BridgeEditorDisplayDiscovery.GetStatus(rig)));
                            return;
                        }

                        if (discoveryStage == "explicit-refresh")
                        {
                            ValidateResolvedProfile(rig, "explicit-refresh");
                            if (worker.State != BridgeWorkerQuiltOutput.WorkerState.Closed ||
                                session.State != ViewWeaveBridgeSession.SessionState.Idle)
                                throw new InvalidOperationException("Metadata refresh left Worker or Session active.");
                            VerifyNativeImage();
                            EditorApplication.update -= PollForSelection;
                            polling = false;
                            Log("DISCOVERY_REFRESH", "status=" + status + " workerState=" + worker.State +
                                " sessionState=" + session.State + " previewActive=false");
                            ValidateAndRender(rig, worker, metal, session, pipeline);
                            return;
                        }
                    }
                    if (EditorApplication.timeSinceStartup >= deadline)
                        throw new TimeoutException("Bridge metadata discovery did not reach exact success status: " + Ascii(status));
                }

                if (EditorApplication.timeSinceStartup >= deadline)
                    throw new TimeoutException("Quick Start did not select the Windows Worker output.");
            }
            catch (Exception error)
            {
                EditorApplication.update -= PollForSelection;
                polling = false;
                Fail("Selection/scene validation failed: " + Ascii(error.GetBaseException().Message));
            }
        }

        static void ValidateAndRender(ViewWeaveRig rig, BridgeWorkerQuiltOutput worker, BridgeMetalQuiltOutput metal, ViewWeaveBridgeSession session, string pipeline)
        {
            if (!rig.ValidateRig(out string rigError)) throw new InvalidOperationException("Rig validation failed: " + Ascii(rigError));
            QuiltRenderer renderer = rig.Renderer;
            if (renderer == null || renderer.sourceCamera == null || renderer.sourceCamera.gameObject != rig.gameObject)
                throw new InvalidOperationException("Quilt renderer camera reference is invalid.");
            if (pipeline == "HDRP")
            {
                Volume[] volumes = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
                if (!volumes.Any(value => value.isGlobal && value.sharedProfile != null))
                    throw new InvalidOperationException("HDRP global Volume/profile reference is missing.");
            }

            Log("SCENE", "pipeline=" + pipeline + " rig=" + rig.name + " camera=" + renderer.sourceCamera.name +
                " workerSelected=true workerEnabled=" + worker.enabled + " sessionEnabled=" + session.enabled + " metalEnabled=" + metal.enabled +
                " output=" + renderer.OutputBehaviour.GetType().FullName);
            Log("RIG", "valid=true sourceCamera=" + renderer.sourceCamera.name + " guard=" + (renderer.GuardBehaviour == null ? "null" : renderer.GuardBehaviour.GetType().FullName));

            worker.enabled = false;
            metal.enabled = false;
            session.enabled = false;
            rig.SetOutput(null);
            renderer.RenderQuilt();
            if (!renderer.HasRenderedQuilt || renderer.LastRenderedViewCount <= 0)
                throw new InvalidOperationException("RenderQuilt did not complete any views.");
            RenderTexture quilt = renderer.QuiltTexture;
            if (quilt == null || !quilt.IsCreated()) throw new InvalidOperationException("Quilt texture is unavailable after rendering.");
            Color32[] pixels = ReadPixels(quilt);
            int nonBlack = pixels.Count(color => color.r > 4 || color.g > 4 || color.b > 4);
            int magenta = pixels.Count(color => color.r > 100 && color.b > 100 && color.g < 90);
            int distinct = pixels.Distinct().Count();
            if (nonBlack < pixels.Length / 100 || distinct < 8 || magenta > pixels.Length / 1000)
                throw new InvalidOperationException("Quilt pixel gate failed: nonblack=" + nonBlack + " distinct=" + distinct + " magenta=" + magenta);
            Log("RENDER", "success=true views=" + renderer.LastRenderedViewCount + " quilt=" + quilt.width + "x" + quilt.height +
                " pixels=" + pixels.Length + " nonBlack=" + nonBlack + " distinct=" + distinct + " magenta=" + magenta + " outputSubmit=disabled");

            SessionState.SetString(StateKey, "passed");
            Log("PASS", "pipeline=" + pipeline + " package=" + SessionState.GetString(PackageKey, string.Empty) +
                " sample=" + SessionState.GetString(SampleNameKey, string.Empty) + " scene=" + SessionState.GetString(ScenePathKey, string.Empty));
            EditorApplication.Exit(0);
        }

        static bool PipelineMatches(string pipeline)
        {
            string name = GraphicsSettings.currentRenderPipeline == null ? string.Empty : GraphicsSettings.currentRenderPipeline.GetType().FullName;
            return pipeline == "URP" ? name == "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset"
                : pipeline == "HDRP" && name == "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset";
        }

        static void VerifyImportedSource()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForPackageName(SessionState.GetString(PackageKey, string.Empty));
            if (package == null) throw new InvalidOperationException("Resolved package disappeared before source comparison.");
            string folder = SessionState.GetString(PipelineKey, string.Empty) == "URP" ? "QuickStartURP" : "QuickStartHDRP";
            string sourceRoot = Path.Combine(package.resolvedPath, "Samples~", folder);
            string destinationRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", SessionState.GetString(SamplePathKey, string.Empty)));
            string[] sourceFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            string[] destinationFiles = Directory.GetFiles(destinationRoot, "*", SearchOption.AllDirectories);
            if (sourceFiles.Length == 0 || sourceFiles.Length != destinationFiles.Length)
                throw new InvalidDataException("Imported Quick Start file count differs from package source.");
            var destinationByPath = destinationFiles.ToDictionary(path => RelativePath(destinationRoot, path), StringComparer.OrdinalIgnoreCase);
            foreach (string sourceFile in sourceFiles)
            {
                string relative = RelativePath(sourceRoot, sourceFile);
                if (!destinationByPath.TryGetValue(relative, out string destinationFile))
                    throw new InvalidDataException("Imported Quick Start file is missing: " + relative);
                string sourceHash = HashFile(sourceFile);
                string importedHash = HashFile(destinationFile);
                if (!string.Equals(sourceHash, importedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Imported Quick Start file differs from package source: " + relative);
                Log("SOURCE", "path=" + relative + " sha256=" + sourceHash);
            }
            Log("IMPORT", "package=" + package.name + " version=" + package.version + " sourceFiles=" + sourceFiles.Length + " importedFiles=" + destinationFiles.Length);
        }

        static string RelativePath(string root, string file)
        {
            return Path.GetRelativePath(root, file).Replace('\\', '/');
        }

        static string HashFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        static void ValidateResolvedProfile(ViewWeaveRig rig, string stage)
        {
            if (!ViewWeave.Editor.QuiltDisplaySettingsResolver.TryResolve(rig.Renderer,
                    out QuiltSettings effectiveSettings, out ViewWeave.Editor.QuiltEditorDisplayProfile profile, out string error))
                throw new InvalidOperationException("Resolved display profile is unavailable: " + Ascii(error));
            if (profile.source != ViewWeave.Editor.QuiltDisplaySettingsSource.LastRecognizedDisplay ||
                !Mathf.Approximately(effectiveSettings.viewAspect, profile.aspect) ||
                !Mathf.Approximately(effectiveSettings.viewCone, profile.viewCone))
                throw new InvalidOperationException("Resolved Quilt profile does not match acquired Bridge geometry.");
            if (profile.hasPhysicalCalibration)
            {
                if (!rig.Renderer.TryGetFraming(out QuiltCameraFraming framing) ||
                    !Mathf.Approximately(framing.ApertureWidthMillimeters, profile.widthMillimeters) ||
                    !Mathf.Approximately(framing.ApertureHeightMillimeters, profile.heightMillimeters))
                    throw new InvalidOperationException("Physical aperture dimensions do not match acquired calibration.");
            }
            Log("GEOMETRY", "stage=" + stage + " source=" + profile.source +
                " aspect=" + profile.aspect.ToString("R", CultureInfo.InvariantCulture) +
                " viewCone=" + profile.viewCone.ToString("R", CultureInfo.InvariantCulture) +
                " physicalCalibration=" + profile.hasPhysicalCalibration +
                " widthMm=" + profile.widthMillimeters.ToString("R", CultureInfo.InvariantCulture) +
                " heightMm=" + profile.heightMillimeters.ToString("R", CultureInfo.InvariantCulture) +
                " dpi=" + profile.dpi.ToString("R", CultureInfo.InvariantCulture) + " detail=" + Ascii(profile.detail));
        }

        static bool ReadAutomaticRequestsEnabled()
        {
            FieldInfo field = typeof(ViewWeave.Editor.BridgeEditorDisplayDiscovery)
                .GetField("AutomaticRequestsEnabled", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException("AutomaticRequestsEnabled is unavailable.");
            return (bool)field.GetValue(null);
        }

        static int GetDiscoveryRequestCount()
        {
            FieldInfo field = typeof(ViewWeave.Editor.BridgeEditorDisplayDiscovery)
                .GetField("requests", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException("Bridge discovery request state is unavailable.");
            object requests = field.GetValue(null);
            return (int)requests.GetType().GetProperty("Count").GetValue(requests);
        }

        static void VerifyNativeImage()
        {
            Type guard = FindType("ViewWeave.BridgeNativeImageGuard");
            Type native = FindType("ViewWeave.BridgeWorkerNative");
            if (guard == null || native == null) throw new InvalidOperationException("Bridge native image guard or worker API is unavailable.");
            MethodInfo inspect = guard.GetMethod("TryInspectEditorProcess", BindingFlags.Static | BindingFlags.NonPublic);
            object[] inspectArgs = { null };
            if (inspect == null || !(bool)inspect.Invoke(null, inspectArgs))
                throw new InvalidOperationException("Bridge native image guard rejected the Editor: " + Ascii(Convert.ToString(inspectArgs[0], CultureInfo.InvariantCulture)));
            MethodInfo validate = guard.GetMethod("TryValidateGlobalCalls", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo pendingMethod = native.GetMethod("lkg_worker_pending_count", BindingFlags.Static | BindingFlags.NonPublic);
            if (validate == null || pendingMethod == null) throw new MissingMethodException("Bridge native inspection API is incomplete.");
            Type identityType = validate.GetParameters()[1].ParameterType;
            if (identityType.IsByRef) identityType = identityType.GetElementType();
            object[] args = { false, Activator.CreateInstance(identityType), null };
            if (!(bool)validate.Invoke(null, args) || !(bool)args[0])
                throw new InvalidOperationException("Bridge native module identity validation failed: " + Ascii(Convert.ToString(args[2], CultureInfo.InvariantCulture)));
            uint pending = Convert.ToUInt32(pendingMethod.Invoke(null, null), CultureInfo.InvariantCulture);
            object identity = args[1];
            Type identityInfo = identity.GetType();
            string path = (string)identityInfo.GetField("FullPath", BindingFlags.Instance | BindingFlags.Public).GetValue(identity);
            IntPtr baseAddress = (IntPtr)identityInfo.GetField("BaseAddress", BindingFlags.Instance | BindingFlags.Public).GetValue(identity);
            string expectedPath = Path.GetFullPath(Path.Combine(
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeWorkerQuiltOutput).Assembly).resolvedPath,
                "ViewWeave", "Bridge", "Plugins", "x86_64", "lookingglass_bridge_shim.dll"));
            if (pending != 0 || baseAddress == IntPtr.Zero ||
                !string.Equals(Path.GetFullPath(path), expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native image path, base address, or pending-count gate failed.");
            string hash;
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            long bytes = new FileInfo(path).Length;
            if (!string.Equals(hash, "CEE089B1779752DAB4FFABDFFC44B879A0E4D09414C9A85F7590C8D9A9DDCF4D", StringComparison.OrdinalIgnoreCase) || bytes != 302080)
                throw new InvalidOperationException("Loaded Bridge native module hash or size is unexpected.");
            Log("NATIVE_MODULE", "path=" + path + " sha256=" + hash + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture) +
                " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                " base=" + baseAddress + " moduleCount=1 pending=" + pending.ToString(CultureInfo.InvariantCulture));
        }

        static Component FindComponent(GameObject target, string typeName)
        {
            Type type = FindType(typeName);
            return type == null ? null : target.GetComponent(type);
        }

        static Type FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }

        static Color32[] ReadPixels(RenderTexture source)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                copy.Apply();
                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        static void Log(string eventName, string message)
        {
            Debug.Log(Prefix + eventName + " " + Ascii(message));
        }

        static void Fail(string message)
        {
            SessionState.SetString(StateKey, "failed");
            Debug.LogError(Prefix + "FAILED " + Ascii(message));
            EditorApplication.Exit(1);
        }

        static string Ascii(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            char[] chars = value.Replace('\r', ' ').Replace('\n', ' ').ToCharArray();
            for (int index = 0; index < chars.Length; index++)
                if (chars[index] < 0x20 || chars[index] > 0x7e) chars[index] = '?';
            return new string(chars);
        }
    }
}
#endif

#nullable enable

using System;
using System.Reflection;
using UnityEngine;

namespace Mahjong.Unity.Platform
{
    /// <summary>
    /// Minimal runtime/editor probe for the generated Wire codec.
    ///
    /// The probe itself lives in the netstandard2.1 assembly
    /// <c>Mahjong.Contracts.Wire.AotSmoke</c>, which is compiled directly against
    /// the generated Protobuf types. This Unity assembly loads it by name so the
    /// Unity project still compiles before the build script has copied the
    /// generated assemblies into Assets/Plugins/Mahjong.
    ///
    /// Editor batch command:
    ///   Unity.exe -batchmode -quit -nographics -projectPath &lt;project&gt; \
    ///     -executeMethod Mahjong.Unity.Platform.WireAotSmoke.Run
    ///
    /// IL2CPP player: build with MAHJONG_WIRE_AOT_SMOKE to run the same probe at
    /// startup and exit with a machine-readable result.
    /// </summary>
    public static class WireAotSmoke
    {
        private const string ProbeAssemblyName = "Mahjong.Contracts.Wire.AotSmoke";
        private const string ProbeTypeName = "Mahjong.Contracts.Wire.AotSmoke.WireAotProbe";
        private const string PassMarker = "PASS";

        public static void Run()
        {
            try
            {
                Assembly assembly = LoadProbeAssembly();
                Type? probeType = assembly.GetType(ProbeTypeName, throwOnError: true);
                if (probeType == null)
                {
                    throw new TypeLoadException("Cannot find " + ProbeTypeName + ".");
                }

                MethodInfo? method = probeType.GetMethod(
                    "Run",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

                if (method == null)
                {
                    throw new MissingMethodException(ProbeTypeName, "Run");
                }

                object? value = method.Invoke(null, null);
                string result = value as string ?? string.Empty;
                if (!result.StartsWith(PassMarker, StringComparison.Ordinal))
                {
                    Finish(1, "FAIL probe returned: " + result);
                    return;
                }

                Finish(0, result);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Finish(1, "FAIL " + inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception ex)
            {
                Finish(1, "FAIL " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Assembly LoadProbeAssembly()
        {
            try
            {
                return Assembly.Load(new AssemblyName(ProbeAssemblyName));
            }
            catch (Exception loadException)
            {
                foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(candidate.GetName().Name, ProbeAssemblyName, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException(
                    "Cannot load " + ProbeAssemblyName +
                    ". Copy it and its dependencies into Assets/Plugins/Mahjong, then retry. " +
                    loadException.GetType().Name + ": " + loadException.Message,
                    loadException);
            }
        }

        private static void Finish(int exitCode, string message)
        {
            if (exitCode == 0)
            {
                Debug.Log("[WireAotSmoke] " + message);
            }
            else
            {
                Debug.LogError("[WireAotSmoke] " + message);
            }

#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(exitCode);
#elif MAHJONG_WIRE_AOT_SMOKE
            Application.Quit(exitCode);
#endif
        }

#if MAHJONG_WIRE_AOT_SMOKE && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RunOnPlayerStart()
        {
            Run();
        }
#endif
    }
}

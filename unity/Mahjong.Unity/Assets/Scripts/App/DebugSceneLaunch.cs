#if UNITY_EDITOR || DEVELOPMENT_BUILD || UNITY_INCLUDE_TESTS
#nullable enable
using System;
using System.IO;
using Mahjong.LocalHost;
using UnityEngine;

namespace Mahjong.Unity.App
{
    /// <summary>Compile-time gated opt-in for one isolated LocalHost debug scene.</summary>
    internal static class DebugSceneLaunch
    {
        private const string ArgumentName = "-debug-scene";

        public static bool Enabled { get; private set; }
        public static string ScenarioId { get; private set; } = string.Empty;
        public static string RoomId { get; private set; } = string.Empty;
        public static string RoundId { get; private set; } = string.Empty;
        public static string SaveDirectoryName { get; private set; } = string.Empty;

        public static bool WasRequested(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], ArgumentName, StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool TryEnable(string[] args, out string error)
        {
            error = string.Empty;
            int argumentIndex = Array.IndexOf(args, ArgumentName);
            if (argumentIndex < 0) return false;
            if (argumentIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[argumentIndex + 1]))
            {
                error = "$.file: -debug-scene requires a JSON file path.";
                return false;
            }

            string path;
            string json;
            try
            {
                path = Path.GetFullPath(args[argumentIndex + 1]);
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = "$.file: could not read scene file (" + ex.Message + ").";
                return false;
            }

            DebugSceneDocument? document;
            try
            {
                document = JsonUtility.FromJson<DebugSceneDocument>(json);
            }
            catch (Exception ex)
            {
                error = "$: invalid JSON (" + ex.Message + ").";
                return false;
            }

            if (document == null)
            {
                error = "$: expected a JSON object.";
                return false;
            }
            if (document.version != 1)
            {
                error = "$.version: unsupported version; expected 1.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(document.scenario))
            {
                error = "$.scenario: required string is missing.";
                return false;
            }

            try
            {
                LocalPlayerActionScenario.ValidateScenario(document.scenario);
            }
            catch (Exception ex)
            {
                error = "$.scenario: " + ex.Message;
                return false;
            }

            string id = Guid.NewGuid().ToString("N");
            ScenarioId = document.scenario;
            RoomId = "debug-scene-" + id;
            RoundId = "debug-round-" + id;
            SaveDirectoryName = id;
            Enabled = true;
            Debug.Log("DEBUG_SCENE_LOADED: " + path + " scenario=" + ScenarioId);
            return true;
        }

        [Serializable]
        private sealed class DebugSceneDocument
        {
            public int version;
            public string scenario = string.Empty;
        }
    }
}
#endif

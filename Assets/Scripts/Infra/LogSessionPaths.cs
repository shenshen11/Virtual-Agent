using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRPerception.Infra
{
    internal static class LogSessionPaths
    {
        private static readonly Dictionary<string, string> SessionIdsByRoot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string GetOrCreateSessionId(string rootFolderName)
        {
            var key = string.IsNullOrWhiteSpace(rootFolderName) ? "VRP_Logs" : rootFolderName.Trim();
            if (!SessionIdsByRoot.TryGetValue(key, out var sessionId))
            {
                sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                SessionIdsByRoot[key] = sessionId;
            }

            return sessionId;
        }

        public static string GetOrCreateSessionDirectory(string rootFolderName)
        {
            var key = string.IsNullOrWhiteSpace(rootFolderName) ? "VRP_Logs" : rootFolderName.Trim();
            var root = Path.Combine(Application.persistentDataPath, key);
            Directory.CreateDirectory(root);

            var sessionDir = Path.Combine(root, GetOrCreateSessionId(key));
            Directory.CreateDirectory(sessionDir);
            return sessionDir;
        }
    }
}

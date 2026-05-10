using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRPerception.Infra
{
    internal static class LogSessionPaths
    {
        private static readonly Dictionary<string, string> SessionIdsByRoot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SessionIdentity> SessionIdentitiesByRoot = new Dictionary<string, SessionIdentity>(StringComparer.OrdinalIgnoreCase);

        public sealed class SessionIdentity
        {
            public string sessionId;
            public string experimentId;
            public string participantId;
            public bool isHumanSession;
            public DateTime createdUtc;
        }

        public static void ConfigureHumanSessionIdentity(string rootFolderName, string experimentId, string participantId)
        {
            var key = NormalizeRootFolderName(rootFolderName);
            var safeExperimentId = SanitizeSegment(string.IsNullOrWhiteSpace(experimentId) ? "unknown_experiment" : experimentId.Trim());
            var safeParticipantId = SanitizeSegment(string.IsNullOrWhiteSpace(participantId) ? "unknown_participant" : participantId.Trim());
            var createdUtc = DateTime.UtcNow;
            var sessionId = $"{safeExperimentId}_{safeParticipantId}_{createdUtc:yyyyMMdd_HHmmss}";

            SessionIdsByRoot[key] = sessionId;
            SessionIdentitiesByRoot[key] = new SessionIdentity
            {
                sessionId = sessionId,
                experimentId = safeExperimentId,
                participantId = safeParticipantId,
                isHumanSession = true,
                createdUtc = createdUtc
            };
        }

        public static void ClearConfiguredSession(string rootFolderName)
        {
            var key = NormalizeRootFolderName(rootFolderName);
            SessionIdsByRoot.Remove(key);
            SessionIdentitiesByRoot.Remove(key);
        }

        public static SessionIdentity GetSessionIdentity(string rootFolderName)
        {
            var key = NormalizeRootFolderName(rootFolderName);
            if (SessionIdentitiesByRoot.TryGetValue(key, out var identity))
            {
                return identity;
            }

            var sessionId = GetOrCreateSessionId(key);
            var createdUtc = DateTime.UtcNow;
            identity = new SessionIdentity
            {
                sessionId = sessionId,
                experimentId = string.Empty,
                participantId = string.Empty,
                isHumanSession = false,
                createdUtc = createdUtc
            };
            SessionIdentitiesByRoot[key] = identity;
            return identity;
        }

        public static string GetOrCreateSessionId(string rootFolderName)
        {
            var key = NormalizeRootFolderName(rootFolderName);
            if (!SessionIdsByRoot.TryGetValue(key, out var sessionId))
            {
                sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                SessionIdsByRoot[key] = sessionId;
            }

            return sessionId;
        }

        public static string GetOrCreateSessionDirectory(string rootFolderName)
        {
            var key = NormalizeRootFolderName(rootFolderName);
            var root = Path.Combine(Application.persistentDataPath, key);
            Directory.CreateDirectory(root);

            var sessionDir = Path.Combine(root, GetOrCreateSessionId(key));
            Directory.CreateDirectory(sessionDir);
            return sessionDir;
        }

        private static string NormalizeRootFolderName(string rootFolderName)
        {
            return string.IsNullOrWhiteSpace(rootFolderName) ? "VRP_Logs" : rootFolderName.Trim();
        }

        private static string SanitizeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";

            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsWhiteSpace(chars[i]) || Array.IndexOf(invalidChars, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            var sanitized = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
        }
    }
}

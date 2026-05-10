namespace VRPerception.Orchestration
{
    /// <summary>
    /// Cross-scene cache for the playlist selected in MainMenu.
    /// </summary>
    public static class PlaylistLaunchState
    {
        private static TaskPlaylist _selectedPlaylist;
        private static string _experimentId;
        private static string _participantId;

        /// <summary>
        /// Current selected playlist (may be null).
        /// </summary>
        public static TaskPlaylist SelectedPlaylist => _selectedPlaylist;

        /// <summary>
        /// Store selected playlist for next scene.
        /// </summary>
        public static void SetSelectedPlaylist(TaskPlaylist playlist)
        {
            _selectedPlaylist = playlist;
        }

        public static void SetSessionIdentity(string experimentId, string participantId)
        {
            _experimentId = experimentId;
            _participantId = participantId;
        }

        /// <summary>
        /// Fetch and clear the stored playlist.
        /// </summary>
        public static TaskPlaylist ConsumeSelectedPlaylist()
        {
            var playlist = _selectedPlaylist;
            _selectedPlaylist = null;
            return playlist;
        }

        public static void ConsumeSessionIdentity(out string experimentId, out string participantId)
        {
            experimentId = _experimentId;
            participantId = _participantId;
            _experimentId = null;
            _participantId = null;
        }

        /// <summary>
        /// Clear stored playlist.
        /// </summary>
        public static void Clear()
        {
            _selectedPlaylist = null;
            _experimentId = null;
            _participantId = null;
        }
    }
}

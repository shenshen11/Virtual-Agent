namespace VRPerception.Orchestration
{
    /// <summary>
    /// Cross-scene cache for the playlist selected in MainMenu.
    /// </summary>
    public static class PlaylistLaunchState
    {
        private static TaskPlaylist _selectedPlaylist;

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

        /// <summary>
        /// Fetch and clear the stored playlist.
        /// </summary>
        public static TaskPlaylist ConsumeSelectedPlaylist()
        {
            var playlist = _selectedPlaylist;
            _selectedPlaylist = null;
            return playlist;
        }

        /// <summary>
        /// Clear stored playlist.
        /// </summary>
        public static void Clear()
        {
            _selectedPlaylist = null;
        }
    }
}

namespace SysBot.Pokemon.Helpers
{
    /// <summary>
    /// Provides mapping functions between different game progress representation systems.
    /// </summary>
    public static class GameProgressMapper
    {
        /// <summary>
        /// Gets the expected raid star difficulty range for a given progress level.
        /// </summary>
        /// <param name="progress">Game progress enum value</param>
        /// <returns>Tuple containing minimum and maximum star values</returns>
        public static (int Min, int Max) GetExpectedStarRange(GameProgressEnum progress)
        {
            return progress switch
            {
                GameProgressEnum.Unlocked3Stars => (1, 3),
                GameProgressEnum.Unlocked4Stars => (1, 4),
                GameProgressEnum.Unlocked5Stars => (3, 5),
                GameProgressEnum.Unlocked6Stars => (3, 6),
                _ => (1, 3), // Default to 3 stars range
            };
        }
    }
}

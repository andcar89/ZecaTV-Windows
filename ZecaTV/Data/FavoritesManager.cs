using System;
using System.Collections.Generic;

namespace ZecaTV.Data
{
    /// <summary>
    /// The set of favorited channels, keyed by <see cref="Models.Channel.Key"/> (the stream URL) —
    /// never Channel.Id, which quality variants share (the Android favorites bug). Port of
    /// FavoritesManager.kt; persisted in settings.json and saved on every change.
    /// </summary>
    public static class FavoritesManager
    {
        private static HashSet<string>? _keys;

        private static HashSet<string> Keys =>
            _keys ??= new HashSet<string>(SettingsStore.Current.Favorites, StringComparer.Ordinal);

        public static int Count => Keys.Count;

        public static bool IsFavorite(string channelKey) => Keys.Contains(channelKey);

        /// <summary>Adds or removes <paramref name="channelKey"/>. Returns the new state (true = now a favorite).</summary>
        public static bool Toggle(string channelKey)
        {
            var nowFavorite = Keys.Add(channelKey);
            if (!nowFavorite) Keys.Remove(channelKey);

            // Keep insertion order in the file (handy if favorites ever get a custom order).
            var list = SettingsStore.Current.Favorites;
            if (nowFavorite) list.Add(channelKey);
            else list.RemoveAll(k => k == channelKey);

            SettingsStore.Save();
            return nowFavorite;
        }
    }
}

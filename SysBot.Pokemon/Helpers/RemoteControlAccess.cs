using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace SysBot.Pokemon
{
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class RemoteControlAccess
    {
        public ulong ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;

        public override string ToString() => $"{Name} = {ID} // {Comment}";
    }

    /// <summary>
    /// Discord commands change these lists while the bot thread reads them, so every
    /// member locks. <see cref="List"/> stays public for config.json and the settings grid.
    /// </summary>
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class RemoteControlAccessList
    {
        private readonly object _gate = new();
        private List<RemoteControlAccess> _list = [];

        /// <summary>
        /// Don't mutate this list; use <see cref="AddIfNew"/> and <see cref="RemoveAll"/>.
        /// This is public for serialization purposes.
        /// </summary>
        public List<RemoteControlAccess> List
        {
            get { lock (_gate) return _list; }
            set { lock (_gate) _list = value ?? []; }
        }

        /// <summary>
        /// Allows Bot-Specific role lists to permit anyone to use the bot if there are no roles specified.
        /// </summary>
        /// <remarks>
        /// Also used by Channel White-lists. No channels whitelisted &amp; true => any channel can be used for bot commands.
        /// </remarks>
        public bool AllowIfEmpty { get; set; } = true;

        [JsonIgnore, Browsable(false)]
        public int Count { get { lock (_gate) return _list.Count; } }

        /// <summary>Used for unique IDs such as Users or Channels.</summary>
        public bool Contains(ulong id) => Find(id) is not null;

        /// <summary>Used for checking Role Names.</summary>
        public bool Contains(string name)
        {
            lock (_gate)
                return _list.Any(z => z.Name == name);
        }

        public RemoteControlAccess? Find(ulong id)
        {
            lock (_gate)
                return _list.Find(z => z.ID == id);
        }

        public List<RemoteControlAccess> Snapshot()
        {
            lock (_gate)
                return [.. _list];
        }

        public int RemoveAll(Predicate<RemoteControlAccess> item)
        {
            lock (_gate)
                return _list.RemoveAll(item);
        }

        public void Clear()
        {
            lock (_gate)
                _list.Clear();
        }

        /// <summary>
        /// Adds new items if not already present by <see cref="RemoteControlAccess.ID"/>.
        /// </summary>
        public void AddIfNew(IEnumerable<RemoteControlAccess> list)
        {
            lock (_gate)
            {
                foreach (var item in list)
                {
                    if (!_list.Exists(z => z.ID == item.ID))
                        _list.Add(item);
                }
            }
        }

        /// <summary>
        /// Enumerates a copy, so foreach is safe while commands change the list.
        /// </summary>
        public IEnumerator<RemoteControlAccess> GetEnumerator() => Snapshot().GetEnumerator();

        /// <summary>
        /// Used for summarizing this object in a PropertyGrid.
        /// </summary>
        public override string ToString()
        {
            var count = Count;
            return count == 0
                ? (AllowIfEmpty ? "Anyone allowed" : "None allowed (none specified).")
                : $"{count} entries specified.";
        }
    }
}

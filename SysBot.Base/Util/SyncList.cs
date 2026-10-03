using System;
using System.Collections;
using System.Collections.Generic;

namespace SysBot.Base
{
    /// <summary>
    /// A list that Discord commands can change while bot threads loop over it.
    /// Writers swap in a new copy; readers loop over whichever copy they started on,
    /// so a log line never fails with "Collection was modified".
    /// </summary>
    public sealed class SyncList<T> : IEnumerable<T>
    {
        private readonly object _gate = new();
        private T[] _items = [];

        public int Count => _items.Length;

        public void Add(T item)
        {
            lock (_gate)
                _items = [.. _items, item];
        }

        public bool Remove(T item)
        {
            lock (_gate)
            {
                int index = Array.IndexOf(_items, item);
                if (index < 0)
                    return false;
                var copy = new List<T>(_items);
                copy.RemoveAt(index);
                _items = [.. copy];
                return true;
            }
        }

        public int RemoveAll(Predicate<T> match)
        {
            lock (_gate)
            {
                var copy = new List<T>(_items);
                int removed = copy.RemoveAll(match);
                _items = [.. copy];
                return removed;
            }
        }

        public void Clear()
        {
            lock (_gate)
                _items = [];
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

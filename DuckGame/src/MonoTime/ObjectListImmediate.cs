using System.Collections;
using System.Collections.Generic;

namespace DuckGame
{
    public class ObjectListImmediate : IEnumerable<Thing>, IEnumerable
    {
        private HashSet<Thing> _emptyList = new HashSet<Thing>();
        private HashSet<Thing> _bigList = new HashSet<Thing>();
        private MultiMap<System.Type, Thing, HashSet<Thing>> _objectsByType = new MultiMap<System.Type, Thing, HashSet<Thing>>();

        public List<Thing> ToList()
        {
            List<Thing> list = new List<Thing>();
            list.AddRange(_bigList);
            return list;
        }

        public HashSet<Thing> this[System.Type key]
        {
            get
            {
                if (key == typeof(Thing))
                    return _bigList;
                return _objectsByType.ContainsKey(key) ? _objectsByType[key] : _emptyList;
            }
        }

        // this[key] as an array in the set's enumeration order, kept until the list changes: the quad tree
        // looks up and walks the same few sets for every bullet step and every point test.
        private readonly Dictionary<System.Type, Thing[]> _items = new Dictionary<System.Type, Thing[]>();
        private System.Type _lastKey;
        private Thing[] _lastItems;

        public Thing[] Items(System.Type key)
        {
            if (ReferenceEquals(key, _lastKey))
                return _lastItems;
            if (!_items.TryGetValue(key, out Thing[] items))
            {
                HashSet<Thing> set = this[key];
                items = new Thing[set.Count];
                set.CopyTo(items);
                _items[key] = items;
            }
            _lastKey = key;
            _lastItems = items;
            return items;
        }

        private void Changed()
        {
            _items.Clear();
            _lastKey = null;
            _lastItems = null;
        }

        public int Count => _bigList.Count;

        public void Add(Thing obj)
        {
            foreach (System.Type key in Editor.AllBaseTypes[obj.GetType()])
                _objectsByType.Add(key, obj);
            _bigList.Add(obj);
            Changed();
        }

        public void AddRange(ObjectListImmediate list)
        {
            foreach (Thing thing in list)
                Add(thing);
        }

        public void Remove(Thing obj)
        {
            foreach (System.Type key in Editor.AllBaseTypes[obj.GetType()])
                _objectsByType.Remove(key, obj);
            _bigList.Remove(obj);
            Changed();
        }

        public void Clear()
        {
            _bigList.Clear();
            _objectsByType.Clear();
            Changed();
        }

        public bool Contains(Thing obj) => _bigList.Contains(obj);

        public IEnumerator<Thing> GetEnumerator() => _bigList.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

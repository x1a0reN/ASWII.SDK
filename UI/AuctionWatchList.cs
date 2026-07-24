using System;
using System.Collections.Generic;
using System.Globalization;

[Serializable]
public class WatchItem
{
    public string Id;
    public string Name;
    public float Price;

    public WatchItem(string id, string name, float price)
    {
        Id = id;
        Name = name;
        Price = price;
    }
}

public static class AuctionWatchList
{
    private static readonly object _sync = new object();
    private static readonly Dictionary<string, WatchItem> _map = new Dictionary<string, WatchItem>();
    private static readonly List<WatchItem> _list = new List<WatchItem>();
    private static volatile WatchItem[] _snapshot = new WatchItem[0];
    private static volatile Dictionary<string, WatchItem> _snapshotById =
        new Dictionary<string, WatchItem>();

    public static bool AddOrUpdate(string id, string name, string priceInput)
    {
        if (string.IsNullOrEmpty(id)) return false;

        float price = 0f;
        TryParsePrice(priceInput, out price);

        lock (_sync)
        {
            WatchItem existing;
            if (_map.TryGetValue(id, out existing))
            {
                WatchItem replacement = new WatchItem(id, name, price);
                _map[id] = replacement;

                int index = _list.IndexOf(existing);
                if (index >= 0) _list[index] = replacement;
                PublishSnapshot();
                return false;
            }

            WatchItem item = new WatchItem(id, name, price);
            _map[id] = item;
            _list.Add(item);
            PublishSnapshot();
            return true;
        }
    }

    public static bool Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_sync)
        {
            WatchItem item;
            if (!_map.TryGetValue(id, out item)) return false;
            _map.Remove(id);
            _list.Remove(item);
            PublishSnapshot();
            return true;
        }
    }

    public static bool Contains(string id)
    {
        return !string.IsNullOrEmpty(id) && _snapshotById.ContainsKey(id);
    }

    public static IList<WatchItem> All
    {
        get { return Array.AsReadOnly(_snapshot); }
    }

    public static bool TryGet(string id, out WatchItem item)
    {
        if (string.IsNullOrEmpty(id))
        {
            item = null;
            return false;
        }
        return _snapshotById.TryGetValue(id, out item);
    }

    internal static WatchItem[] GetSnapshot()
    {
        return _snapshot;
    }

    internal static Dictionary<string, WatchItem> GetSnapshotById()
    {
        return _snapshotById;
    }

    public static bool TryParsePrice(string s, out float f)
    {
        return float.TryParse((s ?? "").Trim(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out f);
    }

    private static void PublishSnapshot()
    {
        WatchItem[] items = _list.ToArray();
        var byId = new Dictionary<string, WatchItem>(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            WatchItem item = items[i];
            if (item != null && !string.IsNullOrEmpty(item.Id))
                byId[item.Id] = item;
        }

        _snapshotById = byId;
        _snapshot = items;
    }
}

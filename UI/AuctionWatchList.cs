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
    private static readonly Dictionary<string, WatchItem> _map = new Dictionary<string, WatchItem>();
    private static readonly List<WatchItem> _list = new List<WatchItem>();

    public static bool AddOrUpdate(string id, string name, string priceInput)
    {
        if (string.IsNullOrEmpty(id)) return false;

        float price = 0f;
        TryParsePrice(priceInput, out price);

        WatchItem w;
        if (_map.TryGetValue(id, out w))
        {
            w.Name = name;
            w.Price = price;
            return false; // 更新
        }
        w = new WatchItem(id, name, price);
        _map[id] = w;
        _list.Add(w);
        return true; // 新增
    }

    public static bool Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        WatchItem w;
        if (!_map.TryGetValue(id, out w)) return false;
        _map.Remove(id);
        _list.Remove(w);
        return true;
    }

    public static bool Contains(string id) { return !string.IsNullOrEmpty(id) && _map.ContainsKey(id); }

    public static IList<WatchItem> All { get { return _list.AsReadOnly(); } }

    public static bool TryGet(string id, out WatchItem item) { return _map.TryGetValue(id, out item); }

    public static bool TryParsePrice(string s, out float f)
    {
        return float.TryParse((s ?? "").Trim(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out f);
    }
}

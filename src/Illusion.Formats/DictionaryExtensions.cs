namespace Illusion.Formats;

/// <summary>Small Dictionary helpers the frame graph relies on (ordinal lookup + tolerant add/remove).
/// The property-grid converters and WinForms flag editor that used to share this file are gone —
/// the format layer carries no editor UI.</summary>
internal static class DictionaryExtensions
{
    public static int IndexOfValue<TKey, TValue>(this Dictionary<TKey, TValue> dic, int key) where TKey : notnull
    {
        int index = 0;
        foreach (KeyValuePair<TKey, TValue> entry in dic)
        {
            if (Convert.ToInt32(entry.Key) == key)
                return index;
            index++;
        }
        return -1;
    }

    public static bool AddRange<TKey, TValue>(this Dictionary<TKey, TValue> dic, Dictionary<TKey, TValue> other) where TKey : notnull
    {
        bool result = true;
        foreach (var pair in other)
        {
            result = dic.TryAdd(pair.Key, pair.Value);
        }
        return result;
    }

    public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dic, TKey key, TValue value) where TKey : notnull
    {
        if (!dic.ContainsKey(key))
        {
            dic.Add(key, value);
            return true;
        }
        return false;
    }

    public static bool TryRemove<TKey, TValue>(this Dictionary<TKey, TValue> dic, TKey key) where TKey : notnull
    {
        return dic.ContainsKey(key) && dic.Remove(key);
    }

    public static TValue? TryGet<TKey, TValue>(this Dictionary<TKey, TValue> dic, TKey key) where TKey : notnull
    {
        return dic.ContainsKey(key) ? dic[key] : default(TValue);
    }
}

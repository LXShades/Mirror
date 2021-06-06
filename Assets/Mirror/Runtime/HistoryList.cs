using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mirror
{
    public class HistoryListBase { }

    /// <summary>
    /// A list optimised for buffering items with a paired timestamp. Provides handy functions to search for them.
    /// Useful for interpolating data, keyframes, etc.
    /// </summary>
    [Serializable]
    public class HistoryList<T> : HistoryListBase
    {
        [Serializable]
        public struct HistoryItem
        {
            public float time;
            public T item;
        }

        [SerializeField] private List<HistoryItem> items = new List<HistoryItem>();

        public T this[int index]
        {
            get => items[index].item;
            set => items[index] = new HistoryItem()
            {
                time = items[index].time,
                item = value
            };
        }

        public int Count => items.Count;

        public T Latest => items.Count > 0 ? items[0].item : default;

        public float LatestTime => items.Count > 0 ? items[0].time : 0.0f;

        public T ItemAt(float time, float tolerance = 0.01f)
        {
            return items.Find(a => a.time >= time - tolerance && a.time <= time + tolerance).item;
        }

        public int IndexAt(float time, float tolerance = 0.01f)
        {
            return items.FindIndex(a => a.time >= time - tolerance && a.time <= time + tolerance);
        }

        public float TimeAt(int index)
        {
            if (index == -1) return -1;

            return items[index].time;
        }

        public int ClosestIndexAfter(float time, float tolerance = 0.01f)
        {
            for (int index = items.Count - 1; index >= 0; index--)
            {
                if (items[index].time > time - tolerance)
                    return index;
            }

            return -1;
        }

        public int ClosestIndexBefore(float time, float tolerance = 0.01f)
        {
            // return the lowest-time index within the tolerance range (we still want to prioritise the ones actually _before_ the time, particularly in cases where we receive multiple things in the same time)
            // don't just pick up the first < time+tolernace result we find because there might be earlier ones still
            int index = -1;
            for (int idx = 0; idx < items.Count; idx++)
            {
                if (items[idx].time < time + tolerance)
                    index = idx;
            }

            return index;
        }

        public int ClosestIndexBeforeOrEarliest(float time, float tolerance = 0.01f)
        {
            int index = ClosestIndexBefore(time, tolerance);

            if (index == -1)
            {
                return items.Count - 1;
            }
            else
            {
                return index;
            }
        }

        public void Set(float time, T item, float tolerance = 0.01f)
        {
            for (int index = 0; index < items.Count; index++)
            {
                if (time - tolerance <= items[index].time && time + tolerance >= items[index].time)
                {
                    items[index] = new HistoryItem() { item = item, time = time };
                    return;
                }
            }

            // none to replace, insert instead
            Insert(time, item);
        }

        public void Insert(float time, T item)
        {
            int index;
            for (index = 0; index < items.Count; index++)
            {
                if (time >= items[index].time)
                    break;
            }

            items.Insert(index, new HistoryItem() { item = item, time = time });
        }

        public void Clear()
        {
            items.Clear();
        }

        public void RemoveAt(int index)
        {
            items.RemoveAt(index);
        }

        public void Prune(float minTime)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].time < minTime)
                {
                    items.RemoveRange(i, items.Count - i);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Shift-click range selection for one checkbox list. A Shift-click sets every visible row
    /// between the anchor (the last row clicked) and the clicked row, inclusive, to the clicked
    /// row's new state; the clicked row becomes the anchor. The anchor is a key, so it survives
    /// list rebuilds; a range only ever covers keys in the visible order.
    /// </summary>
    internal sealed class ShiftClickRange
    {
        private readonly IEqualityComparer<string> m_Comparer;
        private List<string> m_Order = new List<string>();
        private List<string> m_Pending = new List<string>();
        private bool m_Building;
        private string m_Anchor;

        public ShiftClickRange(IEqualityComparer<string> comparer = null)
        {
            m_Comparer = comparer ?? StringComparer.Ordinal;
        }

        public string Anchor => m_Anchor;

        /// <summary>A copy of the visible order published by the last completed draw pass.</summary>
        public IReadOnlyList<string> Order => m_Order.ToArray();

        /// <summary>Starts recording the visible order. A pass that never reaches
        /// <see cref="EndPass"/> is discarded by the next call.</summary>
        public void BeginPass()
        {
            m_Pending.Clear();
            m_Building = true;
        }

        public void Add(string key)
        {
            if (m_Building && !string.IsNullOrEmpty(key)) m_Pending.Add(key);
        }

        /// <summary>Publishes the recorded order. Clicks resolve against the last published
        /// order, never against a pass still being drawn.</summary>
        public void EndPass()
        {
            if (!m_Building) return;
            var published = m_Pending;
            m_Pending = m_Order;
            m_Order = published;
            m_Pending.Clear();
            m_Building = false;
        }

        /// <summary>Forgets the visible order and the anchor (the list is no longer shown).</summary>
        public void Clear()
        {
            m_Order.Clear();
            m_Pending.Clear();
            m_Building = false;
            m_Anchor = null;
        }

        public void ResetAnchor() => m_Anchor = null;

        /// <summary>Keys a click on <paramref name="clickedKey"/> should set, against the
        /// last published order.</summary>
        public IReadOnlyList<string> Resolve(string clickedKey, bool shift) => Resolve(m_Order, clickedKey, shift);

        /// <summary>Keys a click on <paramref name="clickedKey"/> should set: the run of
        /// <paramref name="order"/> from the anchor to the clicked key when Shift is held and
        /// both are in the order, otherwise only the clicked key. Moves the anchor to the
        /// clicked key.</summary>
        public IReadOnlyList<string> Resolve(IReadOnlyList<string> order, string clickedKey, bool shift)
        {
            if (string.IsNullOrEmpty(clickedKey)) return Array.Empty<string>();

            string anchor = m_Anchor;
            m_Anchor = clickedKey;

            if (!shift || anchor == null || order == null) return new[] { clickedKey };

            int from = IndexOf(order, anchor);
            int to = IndexOf(order, clickedKey);
            if (from < 0 || to < 0) return new[] { clickedKey };

            int lo = Math.Min(from, to);
            int hi = Math.Max(from, to);
            var keys = new List<string>(hi - lo + 1);
            for (int i = lo; i <= hi; i++) keys.Add(order[i]);
            return keys;
        }

        private int IndexOf(IReadOnlyList<string> order, string key)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (m_Comparer.Equals(order[i], key)) return i;
            }
            return -1;
        }
    }
}

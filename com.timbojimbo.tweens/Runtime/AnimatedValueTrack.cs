using System;
using System.Collections.Generic;
using TimboJimbo.PropertyBindings;
using UnityEngine;

namespace TimboJimbo
{
    /// Represents one animated property on the timeline — one row.
    /// Owns the target <see cref="BindableProperty"/> and a list of animated entries.
    /// Concrete entry start/end times are resolved by <see cref="TrackTimeSlotResolver"/>.
    [Serializable]
    public class AnimatedValueTrack
    {
        public BindableProperty Property;

        [SerializeField]
        private List<AnimatedValueTrackEntry> _entries = new List<AnimatedValueTrackEntry>();

        [NonSerialized] private readonly List<int> _sortedIndices = new List<int>();
        [NonSerialized] private int _sortedForCount = -1;

        public IReadOnlyList<AnimatedValueTrackEntry> Entries => _entries;

        public void AddEntry(AnimatedValueTrackEntry entry)
        {
            _entries.Add(entry);
            InvalidateSort();
        }

        public void RemoveEntryAt(int index)
        {
            if (index < 0 || index >= _entries.Count) return;
            _entries.RemoveAt(index);
            InvalidateSort();
        }

        public void InvalidateSort() => _sortedForCount = -1;

        /// Evaluates this track at the given timeline time.
        public ValueContainer Evaluate(float time, ValueContainer baseline, Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> timeSlots)
        {
            if (Property.Target == null) return baseline;

            EnsureSorted(timeSlots);

            var ctx = new AnimatorContext { Baseline = baseline, Accumulated = baseline };

            for (int i = 0; i < _sortedIndices.Count; i++)
            {
                var entry = _entries[_sortedIndices[i]];
                if (entry == null || entry.Animator == null) continue;
                if (!timeSlots.TryGetValue(entry, out var slot)) continue;
                if (time < slot.StartTime) break;

                ctx.Accumulated = entry.Sample(slot, time, in ctx);
            }

            return ctx.Accumulated;
        }

        private void EnsureSorted(Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> timeSlots)
        {
            if (_sortedForCount != _entries.Count || _sortedIndices.Count != _entries.Count)
            {
                _sortedIndices.Clear();
                for (int i = 0; i < _entries.Count; i++)
                    _sortedIndices.Add(i);
                _sortedForCount = _entries.Count;
            }

            SortIndices(timeSlots);
        }

        private void SortIndices(Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> timeSlots)
        {
            for (int i = 1; i < _sortedIndices.Count; i++)
            {
                int current = _sortedIndices[i];
                float currentStart = GetStartTime(current, timeSlots);
                int j = i - 1;
                while (j >= 0)
                {
                    int previous = _sortedIndices[j];
                    float previousStart = GetStartTime(previous, timeSlots);
                    if (previousStart <= currentStart) break;
                    _sortedIndices[j + 1] = previous;
                    j--;
                }
                _sortedIndices[j + 1] = current;
            }
        }

        private float GetStartTime(int entryIndex, Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> timeSlots)
        {
            var entry = _entries[entryIndex];
            return entry != null && timeSlots.TryGetValue(entry, out var slot) ? slot.StartTime : 0f;
        }
    }
}

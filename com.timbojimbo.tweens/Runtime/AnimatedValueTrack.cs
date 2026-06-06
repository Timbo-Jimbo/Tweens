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
        [NonSerialized] private bool _timingDirty = true;
        [NonSerialized] private float _duration;

        public IReadOnlyList<AnimatedValueTrackEntry> Entries => _entries;

        public float Duration
        {
            get
            {
                EnsureTiming();
                return _duration;
            }
        }

        public void AddEntry(AnimatedValueTrackEntry entry)
        {
            _entries.Add(entry);
            MarkTimingDirty();
        }

        public void RemoveEntryAt(int index)
        {
            if (index < 0 || index >= _entries.Count) return;
            _entries.RemoveAt(index);
        }

        public void MarkTimingDirty() => _timingDirty = true;

        /// Evaluates this track at the given timeline time.
        public ValueContainer Evaluate(float time, ValueContainer baseline)
        {
            if (Property.Target == null) return baseline;

            EnsureTiming();

            var ctx = new AnimatorContext { Baseline = baseline, Accumulated = baseline };

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry == null || entry.Animator == null) continue;
                if (time < entry.StartTime) break;

                ctx.Accumulated = entry.Sample(time, in ctx);
            }

            return ctx.Accumulated;
        }

        private void EnsureTiming()
        {   
            if (!_timingDirty) return;
            
            _timingDirty = false;
            _entries.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            if (_entries.Count == 0)
            {
                _duration = 0f;
            }
            else
            {
                var lastEntry = _entries[_entries.Count - 1];
                _duration = lastEntry.StartTime + lastEntry.Duration;
            }
        }

    }
}

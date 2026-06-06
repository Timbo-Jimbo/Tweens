using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace TimboJimbo
{
    public struct TrackTimeSlot
    {
        public float StartTime;
        public float Duration;

        public float EndTime => StartTime + Duration;
    }

    public static class TrackTimeSlotResolver
    {
        public static float Resolve(IReadOnlyList<AnimatedValueTrack> tracks, Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> output)
        {
            output.Clear();

            float duration = 0f;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (track == null) continue;
                duration = Mathf.Max(duration, ResolveTrack(track.Entries, output));
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (track == null) continue;
                ExtendTrailingFillGap(track.Entries, output, duration);
            }

            return duration;
        }

        public static float ResolveTrack(IReadOnlyList<AnimatedValueTrackEntry> entries, Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> output)
        {
            using (ListPool<AnimatedValueTrackEntry>.Get(out var sortedEntries))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry != null)
                        sortedEntries.Add(entry);
                }

                StableSortByAnchorTime(sortedEntries);

                float cursor = 0f;
                for (int i = 0; i < sortedEntries.Count; i++)
                {
                    var entry = sortedEntries[i];
                    float startTime;
                    float endTime;

                    switch (entry.TimeSlotMode)
                    {
                        case TimeSlotMode.Absolute:
                            endTime = entry.AnchorTime + Mathf.Max(0f, entry.Duration);
                            startTime = Mathf.Max(entry.AnchorTime, cursor);
                            if (endTime < startTime) endTime = startTime;
                            break;
                        case TimeSlotMode.FromPreviousOrStart:
                            startTime = cursor;
                            endTime = startTime + Mathf.Max(0f, entry.Duration);
                            break;
                        case TimeSlotMode.FillGap:
                            startTime = cursor;
                            endTime = i + 1 < sortedEntries.Count
                                ? sortedEntries[i + 1].AnchorTime
                                : entry.AnchorTime;
                            if (endTime < startTime) endTime = startTime;
                            break;
                        default:
                            endTime = entry.AnchorTime + Mathf.Max(0f, entry.Duration);
                            startTime = Mathf.Max(entry.AnchorTime, cursor);
                            if (endTime < startTime) endTime = startTime;
                            break;
                    }

                    var slot = new TrackTimeSlot
                    {
                        StartTime = startTime,
                        Duration = endTime - startTime
                    };

                    output[entry] = slot;
                    cursor = Mathf.Max(cursor, slot.EndTime);
                }

                return cursor;
            }
        }

        private static void ExtendTrailingFillGap(IReadOnlyList<AnimatedValueTrackEntry> entries,
            Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> output, float timelineDuration)
        {
            AnimatedValueTrackEntry lastEntry = null;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                if (lastEntry == null || entry.AnchorTime >= lastEntry.AnchorTime)
                    lastEntry = entry;
            }

            if (lastEntry == null || lastEntry.TimeSlotMode != TimeSlotMode.FillGap) return;
            if (!output.TryGetValue(lastEntry, out var slot)) return;

            float endTime = Mathf.Max(lastEntry.AnchorTime, timelineDuration);
            if (endTime < slot.StartTime) endTime = slot.StartTime;
            slot.Duration = endTime - slot.StartTime;
            output[lastEntry] = slot;
        }

        private static void StableSortByAnchorTime(List<AnimatedValueTrackEntry> entries)
        {
            for (int i = 1; i < entries.Count; i++)
            {
                var current = entries[i];
                int j = i - 1;
                while (j >= 0 && entries[j].AnchorTime > current.AnchorTime)
                {
                    entries[j + 1] = entries[j];
                    j--;
                }
                entries[j + 1] = current;
            }
        }
    }
}

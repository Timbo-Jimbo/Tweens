using System;
using TimboJimbo.PropertyBindings;
using UnityEngine;

namespace TimboJimbo
{
    public enum TimeSlotMode
    {
        Absolute,
        FromPreviousOrStart,
        FillGap
    }

    /// A single animated segment on an <see cref="AnimatedValueTrack"/>.
    /// <see cref="AnchorTime"/> is the authored ordering/handle time; the resolved
    /// runtime slot is produced by <see cref="TrackTimeSlotResolver"/>.
    [Serializable]
    public class AnimatedValueTrackEntry
    {
        public TimeSlotMode TimeSlotMode = TimeSlotMode.Absolute;
        public float AnchorTime;
        public float Duration = 1f;

        [SerializeReference]
        public ValueAnimator Animator;

        /// Evaluate this entry at the given timeline time, using the provided context.
        /// Returns the value after this entry's contribution.
        public ValueContainer Sample(TrackTimeSlot timeSlot, float time, in AnimatorContext ctx)
        {
            if (Animator == null) return ctx.Accumulated;

            float progress = (time - timeSlot.StartTime) / Mathf.Max(0.0001f, timeSlot.Duration);
            if (progress < 0f) progress = 0f;
            else if (progress > 1f) progress = 1f;

            return Animator.Evaluate(progress, ctx);
        }
    }
}

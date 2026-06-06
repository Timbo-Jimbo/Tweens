using System;
using TimboJimbo.PropertyBindings;
using UnityEngine;

namespace TimboJimbo
{
    /// A single animated segment on an <see cref="AnimatedValueTrack"/>.
    [Serializable]
    public class AnimatedValueTrackEntry
    {
        public float StartTime;
        public float Duration = 1f;

        [SerializeReference]
        public ValueAnimator Animator;

        /// Evaluate this entry at the given timeline time, using the provided context.
        /// Returns the value after this entry's contribution.
        public ValueContainer Sample(float time, in AnimatorContext ctx)
        {
            if (Animator == null) return ctx.Accumulated;

            float progress = (time - StartTime) / Mathf.Max(0.0001f, Duration);
            if (progress < 0f) progress = 0f;
            else if (progress > 1f) progress = 1f;

            return Animator.Evaluate(progress, ctx);
        }
    }
}

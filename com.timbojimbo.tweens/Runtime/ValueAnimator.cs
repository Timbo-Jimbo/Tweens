using System;
using TimboJimbo.PropertyBindings;

namespace TimboJimbo
{
    /// Sandboxed, serializable animation behaviour. A ValueAnimator receives
    /// a driver, progress (0→1), and context (baseline + accumulated value)
    /// and returns the value that the driver should be set to at that point.
    ///
    /// Concrete types (EasedAnimator, ShakeAnimator, PunchAnimator, ...)
    /// are serialized via [SerializeReference] inside AnimatedValueTrackEntry.
    /// Users can create their own by subclassing.
    [Serializable]
    public abstract class ValueAnimator
    {
        /// Evaluate this animator at the given progress through its duration.
        /// <param name="progress">Normalized time within this entry (0→1).</param>
        /// <param name="ctx">
        ///   Baseline = value at Play(); Accumulated = value at this entry's StartTime
        ///   after replaying all prior entries on the same track.
        /// </param>
        public abstract ValueContainer Evaluate(float progress, in AnimatorContext ctx);
    }
}

using TimboJimbo.PropertyBindings;

namespace TimboJimbo
{
    /// Context passed to <see cref="ValueAnimator.Evaluate"/>.
    /// Carries the baseline (value at <see cref="TweenSequence.Play"/>)
    /// and the accumulated value at this entry's StartTime (result of
    /// replaying all prior entries on the same track).
    public struct AnimatorContext
    {
        /// Value captured at the start of playback — before any entry
        /// on this track executed. The "pre-animation" state.
        public ValueContainer Baseline;

        /// Value at this entry's StartTime — the result of replaying
        /// all prior entries on this track. Equal to <see cref="Baseline"/>
        /// if this is the first entry or no prior entry has finished.
        public ValueContainer Accumulated;
    }
}

using System;
using TimboJimbo.PropertyBindings;
using TimboJimbo.Styling;

namespace TimboJimbo
{
    public enum EasedStartMode
    {
        StartFromAbsolute,
        StartFromCurrent
    }

    public enum EasedEndMode
    {
        EndAtAbsolute,
        EndAtRelative,
        EndAtInitial
    }

    /// Eased interpolation from a start value to an end value.
    /// Supports "from current", "from absolute", and for the end:
    /// absolute, relative (offset), or initial (baseline captured at Play).
    [Serializable]
    public class EasedAnimator : ValueAnimator
    {
        public EaseType Ease = EaseType.Linear;

        /// Controls how the start value is resolved at this entry's StartTime.
        public EasedStartMode StartMode = EasedStartMode.StartFromCurrent;

        /// Controls how the end value is resolved.
        public EasedEndMode EndMode = EasedEndMode.EndAtAbsolute;

        public ValueContainer StartValue;
        public ValueContainer EndValue;

        public InterpolationConfig Interpolation;
        public DiscreteValueSelectionMode DiscreteValueSelection = DiscreteValueSelectionMode.Nearest;

        public override ValueContainer Evaluate(float progress, in AnimatorContext ctx)
        {
            float eased = EaseUtility.Evaluate(Clamp01(progress), Ease);

            var start = StartMode == EasedStartMode.StartFromCurrent ? ctx.Accumulated : StartValue;

            ValueContainer end;
            switch (EndMode)
            {
                case EasedEndMode.EndAtInitial:
                    end = ctx.Baseline;
                    break;
                case EasedEndMode.EndAtRelative:
                    end = ValueContainer.Add(start, EndValue);
                    break;
                default:
                    end = EndValue;
                    break;
            }

            return ValueContainer.LerpUnclamped(start, end, eased, Interpolation, DiscreteValueSelection);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}

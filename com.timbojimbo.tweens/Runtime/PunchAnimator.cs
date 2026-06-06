using System;
using TimboJimbo.PropertyBindings;
using UnityEngine;

namespace TimboJimbo
{
    /// Directional punch — pushes away from the current value in a direction,
    /// then oscillates back with elastic decay. By default, punches from the
    /// accumulated value at this entry's StartTime so it composes with prior
    /// entries on the same track. Set <see cref="RelativeToCurrent"/> to false
    /// to punch from the baseline instead.
    [Serializable]
    public class PunchAnimator : ValueAnimator
    {
        /// Direction of the punch (normalized internally).
        public Vector3 Direction = Vector3.up;

        /// Peak displacement from baseline.
        public float Strength = 1f;

        /// Number of oscillations over the entry's duration.
        public int Vibrato = 10;

        /// How much bounce-back. 0 = no bounce (linear return),
        /// 1 = full elastic oscillation.
        [Range(0f, 1f)]
        public float Elasticity = 0.5f;

        /// If true, punch is centred on the accumulated value (value at this
        /// entry's StartTime, after prior entries on the track). If false,
        /// punch is centred on the baseline (value at Play).
        public bool RelativeToCurrent = true;

        public override ValueContainer Evaluate(float progress, in AnimatorContext ctx)
        {
            float p = Mathf.Clamp01(progress);

            // Damped sine: sin(p * vibrato * PI) * decay
            float oscillation = Mathf.Sin(p * Vibrato * Mathf.PI);
            float decay = Mathf.Pow(1f - p, Elasticity * Vibrato * 0.5f + 1f);
            float amplitude = Strength * oscillation * decay;

            Vector3 dir = Direction.magnitude > 0.0001f ? Direction.normalized : Vector3.up;

            var offset = new Vector3(dir.x * amplitude, dir.y * amplitude, dir.z * amplitude);

            var center = RelativeToCurrent ? ctx.Accumulated : ctx.Baseline;
            return AddOffset(center, offset);
        }

        private static ValueContainer AddOffset(in ValueContainer center, Vector3 offset)
        {
            switch (center.Kind)
            {
                case ValueKind.Float:
                    return ValueContainer.FromFloat(center.FloatValue + offset.x);
                case ValueKind.Vector2:
                    return ValueContainer.FromVector2(center.Vector2Value + new Vector2(offset.x, offset.y));
                case ValueKind.Vector3:
                    return ValueContainer.FromVector3(center.Vector3Value + offset);
                case ValueKind.Vector4:
                {
                    var v = center.Vector4Value;
                    v += new Vector4(offset.x, offset.y, offset.z, 0f);
                    return ValueContainer.FromVector4(v);
                }
                case ValueKind.Color:
                {
                    var c = center.ColorValue;
                    c.r += offset.x;
                    c.g += offset.y;
                    c.b += offset.z;
                    return ValueContainer.FromColor(c);
                }
                default:
                    return center;
            }
        }
    }
}

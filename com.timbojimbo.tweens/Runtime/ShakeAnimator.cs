using System;
using TimboJimbo.PropertyBindings;
using UnityEngine;

namespace TimboJimbo
{
    /// Random oscillation around the current value, decaying in amplitude
    /// over the entry's duration. By default, oscillates around the
    /// accumulated value at this entry's StartTime so it composes with
    /// prior entries on the same track. Set <see cref="RelativeToCurrent"/>
    /// to false to oscillate around the baseline instead.
    [Serializable]
    public class ShakeAnimator : ValueAnimator
    {
        /// Peak amplitude of the oscillation (in property-space units).
        public float Strength = 1f;

        /// Number of full oscillations over the entry's duration.
        /// Higher = faster, more chaotic.
        public int Vibrato = 10;

        /// Angular randomness in degrees. At 0, oscillation is purely along
        /// one axis. At 90+, it's spread across all available axes.
        [Range(0f, 180f)]
        public float Randomness = 90f;

        /// If true, amplitude decays to zero by progress = 1.
        /// If false, oscillation continues at full strength through the end.
        public bool FadeOut = true;

        /// If true, oscillation is centred on the accumulated value (value at
        /// this entry's StartTime, after prior entries on the track). If false,
        /// oscillation is centred on the baseline (value at Play).
        public bool RelativeToCurrent = true;

        // Fixed seed so noise is deterministic across frames at the same progress.
        private const float NoiseSeed = 0.7234f;

        public override ValueContainer Evaluate(float progress, in AnimatorContext ctx)
        {
            float amplitude = FadeOut ? Strength * (1f - progress) : Strength;

            // Smooth random using Perlin noise at the vibrato frequency.
            float phase = progress * Vibrato;

            float noiseX = (Mathf.PerlinNoise(phase * 0.73f + NoiseSeed, 0f) * 2f - 1f);
            float noiseY = (Mathf.PerlinNoise(0f, phase * 0.67f + NoiseSeed) * 2f - 1f);
            float noiseZ = (Mathf.PerlinNoise(phase * 0.59f + NoiseSeed, phase * 0.71f + NoiseSeed) * 2f - 1f);

            // Blend between axis-aligned and fully random based on Randomness.
            float r = Mathf.Clamp01(Randomness / 90f);
            float ax = Mathf.Lerp(1f, noiseX, r);
            float ay = Mathf.Lerp(0f, noiseY, r);
            float az = Mathf.Lerp(0f, noiseZ, r);

            var offset = new Vector3(ax * amplitude, ay * amplitude, az * amplitude);

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

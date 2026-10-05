using UnityEngine;

namespace FemDigitalTwin.Rendering
{
    /// <summary>
    /// Value-to-colour mapping shared by the point-cloud renderer and the colour legend,
    /// so that the legend always matches what is drawn on the geometry.
    ///
    /// The scale (min, max) is fixed once from the imported FEM field and kept constant
    /// across updates. Exponent and offset act on the normalized value only: they change
    /// the colour assignment, never the data.
    /// </summary>
    public static class ColorMapping
    {
        private static readonly Color Orange = new Color(1f, 0.5f, 0f);

        public static float Normalize(float value, float min, float max, float exponent, float offset)
        {
            if (max <= min || float.IsNaN(value)) return float.NaN;
            float t = Mathf.InverseLerp(min, max, value);
            t = Mathf.Pow(t, exponent);
            return Mathf.Clamp01(t + offset);
        }

        public static Color Evaluate(float value, float min, float max, Color colorMin, Color colorMax,
                                     bool useSpectrum, float exponent, float offset)
        {
            float t = Normalize(value, min, max, exponent, offset);
            if (float.IsNaN(t)) return Color.gray;
            return useSpectrum ? Spectrum(t, colorMin, colorMax) : Color.Lerp(colorMin, colorMax, t);
        }

        /// Five-segment piecewise-linear ramp (blue - cyan - green - yellow - orange - red),
        /// the jet-style scale used by common FEM post-processors.
        public static Color Spectrum(float t, Color colorMin, Color colorMax)
        {
            if (t < 0.2f) return Color.Lerp(colorMin, Color.cyan, t * 5f);
            if (t < 0.4f) return Color.Lerp(Color.cyan, Color.green, (t - 0.2f) * 5f);
            if (t < 0.6f) return Color.Lerp(Color.green, Color.yellow, (t - 0.4f) * 5f);
            if (t < 0.8f) return Color.Lerp(Color.yellow, Orange, (t - 0.6f) * 5f);
            return Color.Lerp(Orange, colorMax, (t - 0.8f) * 5f);
        }
    }
}

using System;

namespace FemDigitalTwin.Core
{
    /// <summary>
    /// Node-wise update rule of the digital twin (no Unity dependency).
    ///
    ///   Eq. (1)  r_T   = T_sensor / T_max
    ///   Eq. (2)  def_i = VM_i * r_T
    ///
    /// VM_i is the equivalent value of node i in the reference FEM solution, computed by the
    /// structural team at the allowable (catalogue) torque T_max. The rule reproduces a new FEM
    /// solve exactly only if the source analysis is linear and all its loads scale with the torque
    /// (no preload, self-weight or other torque-independent loads): see the paper, Section 5.4,
    /// and Tools/check_proportionality.py.
    /// </summary>
    public static class TorqueScaling
    {
        /// Eq. (1). Throws if the allowable torque is not strictly positive.
        public static float TorqueRatio(float sensorTorque, float allowableTorque)
        {
            if (!(allowableTorque > 0f))
                throw new ArgumentOutOfRangeException(nameof(allowableTorque), "Allowable torque must be > 0.");
            return sensorTorque / allowableTorque;
        }

        /// Eq. (2), applied to every node. 'output' must have the same length as 'reference'.
        /// NaN entries are propagated (and ignored for min/max), so node indices stay aligned.
        public static void ScaleField(float[] reference, float torqueRatio, float[] output,
                                      out float min, out float max)
        {
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            if (output == null || output.Length != reference.Length)
                throw new ArgumentException("Output array must match the reference length.", nameof(output));

            min = float.MaxValue;
            max = float.MinValue;
            for (int i = 0; i < reference.Length; i++)
            {
                float v = reference[i] * torqueRatio;
                output[i] = v;
                if (float.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (min > max) { min = 0f; max = 0f; }
        }
    }
}

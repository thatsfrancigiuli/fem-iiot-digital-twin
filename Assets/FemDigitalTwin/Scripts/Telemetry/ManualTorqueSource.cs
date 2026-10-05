using System;
using UnityEngine;

namespace FemDigitalTwin.Telemetry
{
    /// <summary>
    /// Torque source driven from the Inspector (or from code). Use it to run the digital twin
    /// without a cloud platform, e.g. with the synthetic sample dataset or when the monitored
    /// unit is not powered.
    /// </summary>
    public class ManualTorqueSource : MonoBehaviour, ITorqueSource
    {
        [Tooltip("Torque value returned to the digital twin.")]
        [SerializeField] private float torque = 0f;

        public bool HasValue => true;
        public float LatestTorque => torque;
        public DateTime? LatestTimestampUtc => DateTime.UtcNow;

        public void SetTorque(float value) { torque = value; }
    }
}

namespace FemDigitalTwin.Telemetry
{
    /// <summary>
    /// Anything that can provide the latest in-service torque to the digital twin:
    /// the cloud connector (CloudTelemetryClient) or a manual/test source (ManualTorqueSource).
    /// </summary>
    public interface ITorqueSource
    {
        /// True once at least one valid sample has been received.
        bool HasValue { get; }

        /// Latest torque value, in the unit published by the platform (e.g. Nm).
        float LatestTorque { get; }

        /// Timestamp of the latest sample (UTC), if known.
        System.DateTime? LatestTimestampUtc { get; }
    }
}

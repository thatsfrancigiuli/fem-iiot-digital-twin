using System;
using System.IO;
using UnityEngine;

namespace FemDigitalTwin.Telemetry
{
    /// <summary>
    /// Connection settings for a private-tenant IIoT cloud platform with a REST interface
    /// and cookie-based session authentication.
    ///
    /// Settings are read at run time from StreamingAssets/telemetry.local.json (excluded from
    /// version control; copy telemetry.example.json to create it). Credentials can also be
    /// supplied through the environment variables IIOT_EMAIL, IIOT_PASSWORD and IIOT_API_KEY,
    /// which take precedence over the file. No credential is ever stored in the source code.
    /// </summary>
    [Serializable]
    public class TelemetryConfig
    {
        public string baseUrl = "";              // e.g. https://api.example-iiot.com
        public string loginPath = "";            // e.g. auth/login
        public string valuesPath = "";           // e.g. data/values
        public string apiKey = "";               // appended as ?apiKey=... to the login request, if not empty
        public string tenantHeaderName = "";     // e.g. X-Tenant
        public string tenantId = "";
        public string sessionCookieName = "";    // name (or prefix) of the session cookie; empty = first cookie
        public string thingId = "";              // identifier of the monitored asset on the platform
        public string torqueMetricName = "";     // name of the torque metric on the platform
        public float pollIntervalSeconds = 60f;  // set equal to the publication period of the sensor
        public string email = "";
        public string password = "";

        public const string LocalFileName = "telemetry.local.json";

        public static TelemetryConfig Load()
        {
            var cfg = new TelemetryConfig();
            string path = Path.Combine(Application.streamingAssetsPath, LocalFileName);
            if (File.Exists(path))
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), cfg);
            else
                Debug.LogWarning($"[Telemetry] {path} not found: copy telemetry.example.json and fill it in.");

            cfg.email = Env("IIOT_EMAIL", cfg.email);
            cfg.password = Env("IIOT_PASSWORD", cfg.password);
            cfg.apiKey = Env("IIOT_API_KEY", cfg.apiKey);
            return cfg;
        }

        public bool IsComplete(out string missing)
        {
            missing = string.IsNullOrEmpty(baseUrl) ? "baseUrl"
                    : string.IsNullOrEmpty(loginPath) ? "loginPath"
                    : string.IsNullOrEmpty(valuesPath) ? "valuesPath"
                    : string.IsNullOrEmpty(thingId) ? "thingId"
                    : string.IsNullOrEmpty(torqueMetricName) ? "torqueMetricName"
                    : string.IsNullOrEmpty(email) ? "email"
                    : string.IsNullOrEmpty(password) ? "password" : null;
            return missing == null;
        }

        private static string Env(string name, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }
    }
}

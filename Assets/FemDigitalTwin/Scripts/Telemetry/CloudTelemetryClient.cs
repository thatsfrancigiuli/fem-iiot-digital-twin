using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FemDigitalTwin.Telemetry
{
    /// <summary>
    /// Authenticated REST connector to a private-tenant IIoT cloud platform.
    ///
    /// 1. Login: POST {baseUrl}/{loginPath}[?apiKey=...] with {"email","password"} and the tenant
    ///    header; the session cookie is taken from the Set-Cookie response header.
    /// 2. Polling: every pollIntervalSeconds, GET {baseUrl}/{valuesPath}?thingId=...&amp;metricName=...
    ///    with the session cookie and the tenant header; the most recent sample is kept.
    ///
    /// Expected response schema (adapt ParseLatestSample for other platforms):
    ///   { "data": [ { "timestamp": 1700000000000, "values": [ { "value": 123.4 } ] } ] }
    ///
    /// The poll period should equal the publication period of the sensor (60 s in the paper),
    /// so that no published sample is skipped and no sample is read twice.
    /// The session cookie is kept in memory only.
    /// </summary>
    public class CloudTelemetryClient : MonoBehaviour, ITorqueSource
    {
        [SerializeField] private bool connectOnStart = true;
        [SerializeField] private bool verboseLog = false;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private TelemetryConfig config;
        private string sessionCookie;
        private bool busy;

        public bool HasValue { get; private set; }
        public float LatestTorque { get; private set; }
        public DateTime? LatestTimestampUtc { get; private set; }
        public string LastError { get; private set; }

        /// Raised on the main thread whenever a new torque sample is received.
        public event Action<float> TorqueUpdated;

        private void Start()
        {
            config = TelemetryConfig.Load();
            if (!connectOnStart) return;
            if (!config.IsComplete(out string missing))
            {
                LastError = $"Telemetry configuration incomplete: '{missing}' is missing.";
                Debug.LogWarning("[Telemetry] " + LastError);
                return;
            }
            StartCoroutine(PollLoop());
        }

        private IEnumerator PollLoop()
        {
            var wait = new WaitForSeconds(Mathf.Max(1f, config.pollIntervalSeconds));
            while (true)
            {
                if (!busy)
                {
                    Task t = PollOnceAsync();
                    yield return new WaitUntil(() => t.IsCompleted);
                }
                yield return wait;
            }
        }

        public async Task PollOnceAsync()
        {
            busy = true;
            try
            {
                if (string.IsNullOrEmpty(sessionCookie) && !await LoginAsync()) return;

                HttpStatusCode status = await FetchTorqueAsync();
                if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
                {
                    sessionCookie = null;                     // session expired: log in again once
                    if (await LoginAsync()) await FetchTorqueAsync();
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogError("[Telemetry] " + ex.Message);
            }
            finally { busy = false; }
        }

        public async Task<bool> LoginAsync()
        {
            string url = $"{config.baseUrl.TrimEnd('/')}/{config.loginPath.TrimStart('/')}";
            if (!string.IsNullOrEmpty(config.apiKey))
                url += "?apiKey=" + Uri.EscapeDataString(config.apiKey);

            var body = new JObject { ["email"] = config.email, ["password"] = config.password };
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
            };
            AddTenantHeader(request);

            HttpResponseMessage response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"Login failed ({(int)response.StatusCode}).";
                Debug.LogError("[Telemetry] " + LastError);
                return false;
            }

            if (!response.Headers.TryGetValues("Set-Cookie", out System.Collections.Generic.IEnumerable<string> cookies))
            {
                LastError = "Login response has no Set-Cookie header.";
                Debug.LogError("[Telemetry] " + LastError);
                return false;
            }

            foreach (string c in cookies)
            {
                string pair = c.Split(';')[0].Trim();
                if (string.IsNullOrEmpty(config.sessionCookieName) || pair.StartsWith(config.sessionCookieName, StringComparison.Ordinal))
                {
                    sessionCookie = pair;
                    break;
                }
            }

            if (string.IsNullOrEmpty(sessionCookie))
            {
                LastError = "Session cookie not found in the login response.";
                Debug.LogError("[Telemetry] " + LastError);
                return false;
            }
            if (verboseLog) Debug.Log("[Telemetry] Login successful.");
            return true;
        }

        private async Task<HttpStatusCode> FetchTorqueAsync()
        {
            string url = $"{config.baseUrl.TrimEnd('/')}/{config.valuesPath.TrimStart('/')}" +
                         $"?thingId={Uri.EscapeDataString(config.thingId)}" +
                         $"&metricName={Uri.EscapeDataString(config.torqueMetricName)}&pageSize=1";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddTenantHeader(request);
            request.Headers.Add("Cookie", sessionCookie);

            HttpResponseMessage response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"Fetch failed ({(int)response.StatusCode}).";
                if (verboseLog) Debug.LogWarning("[Telemetry] " + LastError);
                return response.StatusCode;
            }

            string json = await response.Content.ReadAsStringAsync();
            if (ParseLatestSample(json, out float value, out DateTime? timestamp))
            {
                LatestTorque = value;
                LatestTimestampUtc = timestamp;
                HasValue = true;
                LastError = null;
                TorqueUpdated?.Invoke(value);
                if (verboseLog) Debug.Log($"[Telemetry] Torque = {value.ToString("F2", CultureInfo.InvariantCulture)}");
            }
            return response.StatusCode;
        }

        /// Extracts the most recent sample from the platform response (see schema above).
        public static bool ParseLatestSample(string json, out float value, out DateTime? timestampUtc)
        {
            value = 0f;
            timestampUtc = null;
            JArray data = JObject.Parse(json)["data"] as JArray;
            if (data == null || data.Count == 0) return false;

            JObject latest = data.Children<JObject>()
                                 .OrderByDescending(d => (long?)d["timestamp"] ?? 0L)
                                 .FirstOrDefault();
            JToken raw = (latest?["values"] as JArray)?.FirstOrDefault()?["value"];
            if (raw == null) return false;

            string s = raw.Type == JTokenType.String ? (string)raw : raw.ToString(Newtonsoft.Json.Formatting.None);
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;

            long? ms = (long?)latest["timestamp"];
            if (ms.HasValue) timestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).UtcDateTime;
            return true;
        }

        private void AddTenantHeader(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(config.tenantHeaderName))
                request.Headers.Add(config.tenantHeaderName, config.tenantId);
        }
    }
}

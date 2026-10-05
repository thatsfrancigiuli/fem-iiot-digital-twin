using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FemDigitalTwin.CameraControl;
using FemDigitalTwin.Rendering;
using FemDigitalTwin.Telemetry;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace FemDigitalTwin.Core
{
    /// <summary>
    /// Main component of the digital twin.
    ///
    ///  1. Start-up: the FEM table is parsed once on a worker thread and cached; every node is
    ///     drawn as a GPU-instanced point coloured by its equivalent value (fixed colour scale).
    ///  2. Update (operator commit): the latest torque is read from the torque source, the torque
    ///     ratio r_T = T_sensor / T_max is computed (Eq. 1) and every node value is rescaled,
    ///     def_i = VM_i * r_T (Eq. 2). The display is refreshed; optionally the updated field is
    ///     exported as a timestamped CSV together with a screenshot.
    ///  3. Benchmark: press the benchmark key in Play Mode to time the update stages over N
    ///     iterations; results are written to latency_measurements.csv.
    /// </summary>
    public class PointCloudGenerator : MonoBehaviour
    {
        // ------------------------------------------------------------ inputs
        [Header("FEM data")]
        [Tooltip("Node-wise FEM table (CSV or TXT) placed in the project as a TextAsset.")]
        [SerializeField] private TextAsset femDataFile;
        [Tooltip("Scale applied to the exported coordinates (e.g. 0.1 to bring mm into a convenient scene size).")]
        [SerializeField] private float positionScaleFactor = 0.1f;
        [Tooltip("Coordinates whose magnitude exceeds this value trigger a unit-mismatch warning.")]
        [SerializeField] private float coordinatePlausibilityLimit = 999f;

        [Header("Torque")]
        [Tooltip("Component implementing ITorqueSource (CloudTelemetryClient or ManualTorqueSource).")]
        [SerializeField] private MonoBehaviour torqueSource;
        [Tooltip("If > 0, overrides the live torque (tests with the unit switched off). Keep 0 in operation.")]
        [SerializeField] private float overrideTorque = 0f;

        [Header("UI (optional)")]
        [SerializeField] private Text liveTorqueText;
        [SerializeField] private InputField allowableTorqueInput;
        [SerializeField] private Button applyButton;
        [SerializeField] private string torqueUnit = "Nm";

        [Header("Rendering")]
        [SerializeField] private float pointSize = 0.1f;
        [Tooltip("Optional instancing material. If empty, the FemDigitalTwin/InstancedColored shader is used.")]
        [SerializeField] private Material instanceMaterial;
        [Tooltip("true = rebuild the instanced renderer at each update (configuration measured in the paper); " +
                 "false = recolour the existing batches in place.")]
        [SerializeField] private bool rebuildRendererOnUpdate = true;

        [Header("Colour scale")]
        [SerializeField] private Color colorMin = Color.blue;
        [SerializeField] private Color colorMax = Color.red;
        [SerializeField] private bool useSpectrum = true;
        [Tooltip("Display-only exponent applied to the normalized value (does not change the data).")]
        [SerializeField] private float colorExponent = 0.3f;
        [Tooltip("Display-only offset applied to the normalized value (does not change the data).")]
        [SerializeField] private float colorOffset = 0f;

        [Header("Export")]
        [SerializeField] private bool exportOnUpdate = true;
        [SerializeField] private bool screenshotOnUpdate = true;
        [SerializeField] private string exportFilePrefix = "updated_field";

        [Header("Camera")]
        [SerializeField] private bool autoFocusCamera = true;
        [SerializeField] private float cameraDistanceMultiplier = 2.5f;
        [SerializeField] private float boundsPadding = 1.2f;

        [Header("Latency benchmark")]
        [SerializeField] private KeyCode benchmarkKey = KeyCode.F9;
        [SerializeField] private int benchmarkIterations = 100;
        [SerializeField] private int benchmarkWarmup = 5;
        [Tooltip("If true, the CSV export is executed and timed during the benchmark as well.")]
        [SerializeField] private bool includeExportInBenchmark = false;

        // ------------------------------------------------------------ state
        private readonly List<Vector3> positions = new List<Vector3>();
        private float[] referenceValues;   // VM_i as imported (never modified)
        private float[] currentValues;     // def_i after the last update
        private FemFieldParser.Result parsed;
        private ColorScale scale;
        private GameObject cloudObject;
        private GPUInstancedRenderer cloudRenderer;
        private Mesh instanceMesh;
        private Material resolvedMaterial;
        private Bounds cloudBounds;
        private ITorqueSource torque;

        private bool benchmarkRunning;
        private readonly List<double> tCompute = new List<double>();
        private readonly List<double> tVisual = new List<double>();
        private readonly List<double> tExport = new List<double>();

        public bool IsLoaded { get; private set; }
        public int NodeCount => referenceValues?.Length ?? 0;
        public ColorScale Scale => scale;
        public float LastTorqueRatio { get; private set; } = 1f;
        public double LastLoadMilliseconds { get; private set; }

        /// Raised after the field has been loaded or updated (used by ColorLegend).
        public event Action FieldChanged;

        // ------------------------------------------------------------ lifecycle
        private void Awake()
        {
            torque = torqueSource as ITorqueSource;
            if (torqueSource != null && torque == null)
                Debug.LogError("[FEM] The assigned torque source does not implement ITorqueSource.");
        }

        private void Start()
        {
            if (femDataFile == null) { Debug.LogError("[FEM] No FEM data file assigned."); return; }
            if (applyButton != null) applyButton.onClick.AddListener(ApplyFromUI);
            StartCoroutine(LoadAndBuild());
        }

        private void Update()
        {
            if (liveTorqueText != null && torque != null)
                liveTorqueText.text = torque.HasValue
                    ? $"Live torque: {torque.LatestTorque.ToString("F2", CultureInfo.InvariantCulture)} {torqueUnit}"
                    : "Live torque: waiting for data...";

            if (IsLoaded && !benchmarkRunning && Input.GetKeyDown(benchmarkKey))
                StartCoroutine(RunBenchmark());
        }

        // ------------------------------------------------------------ 1. load
        private IEnumerator LoadAndBuild()
        {
            var sw = Stopwatch.StartNew();
            string text = femDataFile.text;                    // TextAsset must be read on the main thread
            Task<FemFieldParser.Result> task = Task.Run(() => FemFieldParser.Parse(text));
            while (!task.IsCompleted) yield return null;
            sw.Stop();
            LastLoadMilliseconds = sw.Elapsed.TotalMilliseconds;

            if (task.IsFaulted || task.Result == null || task.Result.Nodes.Count == 0)
            {
                Debug.LogError("[FEM] No valid node read from the file. " + task.Exception?.GetBaseException().Message);
                yield break;
            }

            parsed = task.Result;
            int n = parsed.Nodes.Count;
            referenceValues = new float[n];
            currentValues = new float[n];
            positions.Clear();
            positions.Capacity = n;

            float min = float.MaxValue, max = float.MinValue, maxAbsCoord = 0f;
            for (int i = 0; i < n; i++)
            {
                FemFieldParser.Node nd = parsed.Nodes[i];
                referenceValues[i] = currentValues[i] = nd.Vm;
                positions.Add(new Vector3(nd.X, nd.Y, nd.Z) * positionScaleFactor);
                maxAbsCoord = Mathf.Max(maxAbsCoord, Mathf.Abs(nd.X), Mathf.Abs(nd.Y), Mathf.Abs(nd.Z));
                if (nd.Vm < min) min = nd.Vm;
                if (nd.Vm > max) max = nd.Vm;
            }
            if (maxAbsCoord > coordinatePlausibilityLimit)
                Debug.LogWarning($"[FEM] Coordinates up to {maxAbsCoord:F1}: check the export units and positionScaleFactor.");

            // Colour scale fixed on the imported field and kept for every later update.
            scale = new ColorScale
            {
                Min = min, Max = max, Exponent = colorExponent, Offset = colorOffset,
                ColorMin = colorMin, ColorMax = colorMax, UseSpectrum = useSpectrum
            };

            Debug.Log($"[FEM] {n} nodes ({parsed.Format}; value column '{parsed.ValueColumnName}'; " +
                      $"tensor: {(parsed.HasTensor ? "yes" : "no")}) parsed in {LastLoadMilliseconds:F0} ms. " +
                      $"Skipped: {parsed.DroppedPadding} padding, {parsed.DroppedSumRows} summary, {parsed.DroppedMalformed} malformed rows.");

            ComputeBounds();
            instanceMesh = GPUInstancedRenderer.CreateOctahedron();
            resolvedMaterial = ResolveMaterial();
            cloudObject = new GameObject("InstancedPointCloud");
            cloudObject.transform.SetParent(transform, false);
            BuildRenderer();

            IsLoaded = true;
            FieldChanged?.Invoke();

            if (autoFocusCamera)
            {
                yield return new WaitForEndOfFrame();
                FocusCamera();
            }
        }

        // ------------------------------------------------------------ 2. update
        private void ApplyFromUI()
        {
            if (allowableTorqueInput == null) { Debug.LogError("[FEM] Allowable-torque input not assigned."); return; }
            if (!TryParseNumber(allowableTorqueInput.text, out float tMax) || tMax <= 0f)
            {
                Debug.LogError($"[FEM] Invalid allowable torque: '{allowableTorqueInput.text}'.");
                return;
            }
            ApplyTorqueUpdate(tMax);
        }

        /// Runs one update cycle with the given allowable torque T_max. Returns false if no torque is available.
        public bool ApplyTorqueUpdate(float allowableTorque)
        {
            if (!IsLoaded) { Debug.LogWarning("[FEM] Field not loaded yet."); return false; }

            float sensorTorque;
            if (overrideTorque > 0f) sensorTorque = overrideTorque;
            else if (torque != null && torque.HasValue) sensorTorque = torque.LatestTorque;
            else { Debug.LogWarning("[FEM] No torque available from the torque source."); return false; }

            float ratio = TorqueScaling.TorqueRatio(sensorTorque, allowableTorque);   // Eq. (1)
            LastTorqueRatio = ratio;

            // [1] field recomputation (critical path)
            var swC = Stopwatch.StartNew();
            TorqueScaling.ScaleField(referenceValues, ratio, currentValues, out float newMin, out float newMax); // Eq. (2)
            swC.Stop();

            // [2] visualization refresh (critical path; CPU-side rebuild/recolour of the instance batches)
            var swV = Stopwatch.StartNew();
            if (rebuildRendererOnUpdate) BuildRenderer();
            else cloudRenderer.UpdateValues(currentValues, scale);
            swV.Stop();

            // [3] archival export (off the critical path)
            var swE = Stopwatch.StartNew();
            string csvPath = null;
            bool doExport = exportOnUpdate && (!benchmarkRunning || includeExportInBenchmark);
            if (doExport) csvPath = ExportCsv(sensorTorque, allowableTorque, ratio);
            swE.Stop();

            if (benchmarkRunning)
            {
                tCompute.Add(Ms(swC)); tVisual.Add(Ms(swV)); tExport.Add(doExport ? Ms(swE) : double.NaN);
            }
            else
            {
                Debug.Log($"[FEM] T_sensor={sensorTorque:F2}, T_max={allowableTorque:F2}, r_T={ratio:F4} | " +
                          $"compute {Ms(swC):F2} ms, visual {Ms(swV):F2} ms, export {Ms(swE):F2} ms | " +
                          $"updated range {newMin:G4} - {newMax:G4}");
                if (csvPath != null && screenshotOnUpdate) StartCoroutine(CaptureScreenshot(csvPath));
                FieldChanged?.Invoke();
            }
            return true;
        }

        private void BuildRenderer()
        {
            if (cloudRenderer != null) DestroyImmediate(cloudRenderer);
            cloudRenderer = cloudObject.AddComponent<GPUInstancedRenderer>();
            cloudRenderer.Initialize(instanceMesh, resolvedMaterial, positions, currentValues, pointSize, scale);
        }

        private Material ResolveMaterial()
        {
            if (instanceMaterial != null) return instanceMaterial;
            Shader s = Shader.Find(GPUInstancedRenderer.ColoredShaderName);
            if (s == null)
            {
                Debug.LogWarning("[FEM] Shader '" + GPUInstancedRenderer.ColoredShaderName +
                                 "' not found (add it to 'Always Included Shaders'). Falling back to Standard: one colour per batch.");
                s = Shader.Find("Standard");
            }
            return new Material(s) { enableInstancing = true };
        }

        // ------------------------------------------------------------ export
        private string ExportCsv(float sensorTorque, float allowableTorque, float ratio)
        {
            var nodes = parsed.Nodes;
            bool hasId = nodes.Count > 0 && nodes[0].NodeId >= 0;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(nodes.Count * 64);
            sb.AppendLine($"# T_sensor={sensorTorque.ToString(inv)}; T_max={allowableTorque.ToString(inv)}; r_T={ratio.ToString(inv)}; utc={DateTime.UtcNow:o}");
            sb.AppendLine(hasId ? "NodeId;X;Y;Z;VM_reference;VM_updated" : "X;Y;Z;VM_reference;VM_updated");
            for (int i = 0; i < nodes.Count; i++)
            {
                var nd = nodes[i];
                if (hasId) sb.Append(nd.NodeId.ToString(inv)).Append(';');
                sb.Append(nd.X.ToString("F4", inv)).Append(';')
                  .Append(nd.Y.ToString("F4", inv)).Append(';')
                  .Append(nd.Z.ToString("F4", inv)).Append(';')
                  .Append(referenceValues[i].ToString("G9", inv)).Append(';')
                  .Append(currentValues[i].ToString("G9", inv)).AppendLine();
            }
            string path = Path.Combine(Application.persistentDataPath,
                                       $"{exportFilePrefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.csv");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        private IEnumerator CaptureScreenshot(string csvPath)
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            string png = Path.ChangeExtension(csvPath, null) + "_screenshot.png";
            ScreenCapture.CaptureScreenshot(png);
            Debug.Log($"[FEM] Exported {csvPath} and {png}");
        }

        // ------------------------------------------------------------ 3. benchmark
        private IEnumerator RunBenchmark()
        {
            if (allowableTorqueInput == null || !TryParseNumber(allowableTorqueInput.text, out float tMax) || tMax <= 0f)
            {
                Debug.LogError("[BENCH] Enter a valid allowable torque before starting the benchmark.");
                yield break;
            }

            benchmarkRunning = true;
            tCompute.Clear(); tVisual.Clear(); tExport.Clear();
            Debug.Log($"[BENCH] {benchmarkWarmup} warm-up + {benchmarkIterations} measured iterations on {NodeCount} nodes...");

            for (int i = 0; i < benchmarkWarmup + benchmarkIterations; i++)
            {
                if (i == benchmarkWarmup) { tCompute.Clear(); tVisual.Clear(); tExport.Clear(); }
                if (!ApplyTorqueUpdate(tMax)) { benchmarkRunning = false; yield break; }
                yield return null;   // one frame between iterations
            }

            benchmarkRunning = false;
            WriteBenchmarkReport();
            FieldChanged?.Invoke();
        }

        private void WriteBenchmarkReport()
        {
            var inv = CultureInfo.InvariantCulture;
            double medC = Percentile(tCompute, 50), medV = Percentile(tVisual, 50);
            var report = new StringBuilder();
            report.AppendLine($"[BENCH] {tCompute.Count} iterations, {NodeCount} nodes");
            report.AppendLine($"  field recomputation  median {medC:F3}  p95 {Percentile(tCompute, 95):F3} ms");
            report.AppendLine($"  visualization        median {medV:F3}  p95 {Percentile(tVisual, 95):F3} ms");
            report.AppendLine(includeExportInBenchmark
                ? $"  CSV export           median {Percentile(tExport, 50):F3}  p95 {Percentile(tExport, 95):F3} ms"
                : "  CSV export           not executed during the benchmark (includeExportInBenchmark = false)");
            report.AppendLine($"  critical path (recomputation + visualization), median {medC + medV:F2} ms");
            Debug.Log(report.ToString());

            var csv = new StringBuilder();
            csv.AppendLine($"# nodes={NodeCount}; unity={Application.unityVersion}; cpu={SystemInfo.processorType}; " +
                           $"gpu={SystemInfo.graphicsDeviceName}; ram_mb={SystemInfo.systemMemorySize}; " +
                           $"rebuild_renderer={rebuildRendererOnUpdate}; export_included={includeExportInBenchmark}");
            csv.AppendLine("iteration,t_compute_ms,t_visual_ms,t_export_ms,t_critical_ms");
            for (int i = 0; i < tCompute.Count; i++)
                csv.AppendLine(string.Join(",", (i + 1).ToString(inv), tCompute[i].ToString("F6", inv),
                    tVisual[i].ToString("F6", inv),
                    double.IsNaN(tExport[i]) ? "" : tExport[i].ToString("F6", inv),
                    (tCompute[i] + tVisual[i]).ToString("F6", inv)));

            string path = Path.Combine(Application.persistentDataPath, "latency_measurements.csv");
            File.WriteAllText(path, csv.ToString());
            Debug.Log("[BENCH] Results written to " + path);
        }

        // ------------------------------------------------------------ camera
        private void ComputeBounds()
        {
            cloudBounds = new Bounds(positions[0], Vector3.zero);
            for (int i = 1; i < positions.Count; i++) cloudBounds.Encapsulate(positions[i]);
            cloudBounds.Expand(cloudBounds.size.magnitude * (boundsPadding - 1f));
        }

        private void FocusCamera()
        {
            Camera cam = Camera.main;
            MouseOrbitZoom orbit = cam != null ? cam.GetComponent<MouseOrbitZoom>() : FindObjectOfType<MouseOrbitZoom>();
            if (orbit == null) { Debug.LogWarning("[FEM] No MouseOrbitZoom component found on the camera."); return; }
            Bounds worldBounds = new Bounds(transform.TransformPoint(cloudBounds.center), cloudBounds.size);
            orbit.FocusOnBounds(worldBounds, cameraDistanceMultiplier);
        }

        // ------------------------------------------------------------ helpers
        private static bool TryParseNumber(string s, out float value)
        {
            return float.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static double Ms(Stopwatch sw) => sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency;

        private static double Percentile(List<double> values, double p)
        {
            var s = values.FindAll(v => !double.IsNaN(v));
            if (s.Count == 0) return double.NaN;
            s.Sort();
            double idx = p / 100.0 * (s.Count - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return s[lo] + (s[hi] - s[lo]) * (idx - lo);
        }
    }
}

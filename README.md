# Telemetry-paced FEM field visualization for industrial digital twins (Unity)

Reference implementation accompanying the paper

> F. Giuliani, F. Debdoubi, L. Frizziero, *Telemetry-Paced Visualization of Precomputed FEM Stress Fields: A Digital Twin Architecture for Industrial Gear Reducers*, submitted to *Advances in Engineering Software* (2026).

The code binds a **precomputed FEM field** to **live torque telemetry** from an IIoT cloud platform and renders it on the asset geometry in Unity, without running the FEM solver at run time.

- The FEM result is exported by the structural team as a node-wise table (CSV/TXT) and parsed once, in parallel, on a worker thread.
- Every FEM node is drawn as a small GPU-instanced primitive, coloured on a fixed scale taken from the imported field.
- When the operator confirms the allowable torque, the latest torque is read from the cloud, and each node value is rescaled:

```
r_T   = T_sensor / T_max          (1)
def_i = VM_i × r_T                (2)
```

`VM_i` is the equivalent (von Mises) value of node *i* in the reference FEM solution, computed at the allowable torque `T_max`. Rule (2) reproduces a new FEM solve exactly only if the source analysis is linear and **all its loads scale with the torque** (no preload, self-weight or other torque-independent loads); see Section 5.4 of the paper and the [proportionality check](#checking-the-validity-of-the-update-rule).

> **Data.** The industrial FEM datasets, the platform credentials and the asset identifiers used in the paper are confidential and are **not** included. The repository ships a synthetic dataset that reproduces the file format and lets the whole pipeline run without a cloud connection.

---

## Repository structure

```
Assets/
  FemDigitalTwin/
    Scripts/
      Core/
        FemFieldParser.cs        solver-independent parser (header-name matching, parallel blocks)
        TorqueScaling.cs         Eq. (1) and Eq. (2), no Unity dependency
        PointCloudGenerator.cs   main component: load, render, update, export, latency benchmark
      Rendering/
        GPUInstancedRenderer.cs  batches of 1023 instances, per-instance colour via MaterialPropertyBlock
        ColorMapping.cs          fixed colour scale shared by renderer and legend
      Telemetry/
        ITorqueSource.cs         interface for any torque provider
        CloudTelemetryClient.cs  authenticated REST connector (cookie session, tenant header, polling)
        TelemetryConfig.cs       connection settings, read from a local JSON file / environment variables
        ManualTorqueSource.cs    torque from the Inspector (tests, demo, unit switched off)
      UI/
        ColorLegend.cs           discrete on-screen legend matching the renderer colours
      CameraControl/
        MouseOrbitZoom.cs        orbit / pan / zoom camera with auto-focus on the point cloud
    Shaders/
      InstancedColored.shader    unlit shader with per-instance colour (Built-in Render Pipeline)
  StreamingAssets/
    telemetry.example.json       template of the connection settings (no credentials)
SampleData/                      synthetic datasets (see below)
Tools/
  generate_sample_data.py        regenerates the synthetic datasets
  check_proportionality.py       checks whether the update rule is exact for a given FEM model
```

## Requirements

- Unity **2022.3 LTS** (developed and measured with 2022.3.62f2), **Built-in Render Pipeline**.
- Package **Newtonsoft Json** (`com.unity.nuget.newtonsoft-json`), used by the cloud connector: *Window → Package Manager → + → Add package by name*.
- Python ≥ 3.8 for the tools in `Tools/` (standard library only).

## Quick start (synthetic data, no cloud)

1. Create a Unity 2022.3 project from the **3D (Built-in)** template and copy the `Assets/FemDigitalTwin` folder into its `Assets` folder. Install the Newtonsoft Json package.
2. Copy `SampleData/synthetic_beam_T100.csv` anywhere under `Assets/` (Unity imports it as a `TextAsset`).
3. In a new scene:
   - add `MouseOrbitZoom` to the Main Camera;
   - create an empty GameObject `Torque` and add `ManualTorqueSource`; set *Torque* = 50;
   - create an empty GameObject `FEM` and add `PointCloudGenerator`; assign the CSV to *Fem Data File* and the `Torque` object to *Torque Source*;
   - (optional) create a Canvas with an `InputField` (allowable torque), a `Button` (apply) and a `Text` (live torque), and assign them in the *UI* section; add `ColorLegend` and assign a container `RectTransform` and a title `Text`.
4. Press **Play**. The beam appears coloured by its von Mises values (0–≈100 MPa at T = 100).
5. Type `100` in the allowable-torque field and press the button: with a torque of 50 the field is halved (r_T = 0.5) on the same, fixed colour scale. A timestamped CSV with the updated values and a screenshot are written to `Application.persistentDataPath`.

For player builds, add `FemDigitalTwin/InstancedColored` to *Project Settings → Graphics → Always Included Shaders*. Without this shader the renderer falls back to the Standard shader with one colour per batch (geometry preview only).

## FEM input format

One row per FEM node, with a header. Columns are found **by name**, not by position:

| Quantity | Recognised header names (case-insensitive) |
|---|---|
| Node id (optional) | contains `node` |
| Coordinates | `X`, `X Coord`, `X Coordinate`, `Pos X`, `X [mm]` … (same for Y, Z) |
| Equivalent value | contains `mises`, starts with `vm`, or contains `equivalent` |
| Tensor components (optional, kept for future use) | six columns containing `xx`, `yy`, `zz`, `xy`, `yz`, `zx` |

- Field separator `;`, `,` or TAB, detected from the header. With `;` or TAB, both `.` and `,` are accepted as decimal separator, so files saved with an Italian/German spreadsheet locale are read correctly.
- Empty padding rows, solver summary rows (e.g. `Sum`, `Maximum`) and repeated headers of concatenated files are skipped and counted in the log.
- Files larger than the spreadsheet limit (1,048,576 rows) can be exported as TAB-separated `.txt` and concatenated.

## Checking the validity of the update rule

Under a linear analysis the stress at node *i* is affine in the torque, `σ_i(T) = σ_i⁰ + T·σ_i¹`, where `σ_i⁰` is the response to torque-independent loads. Rule (2) is exact if and only if `σ_i⁰ = 0` everywhere. This can be verified once, before commissioning, with two FEM solutions of the same mesh at two different torques:

```bash
python Tools/check_proportionality.py solution_Ta.csv  Ta  solution_Tb.csv  Tb
```

The script computes `σ_i(T_a) − (T_a/T_b)·σ_i(T_b) = (1 − T_a/T_b)·σ_i⁰` on the six tensor components (exact test), or on the von Mises values if the tensor is not available (necessary condition only). Example with the synthetic data:

```bash
python Tools/check_proportionality.py SampleData/synthetic_beam_T100.csv 100 SampleData/synthetic_beam_T50.csv 50
# -> proportional loading holds: the update is exact
python Tools/check_proportionality.py SampleData/synthetic_beam_T100.csv 100 SampleData/synthetic_beam_T50_preload.csv 50
# -> torque-independent loads detected: the update is an approximation
```

## Cloud connector

`CloudTelemetryClient` implements the pattern described in the paper for private-tenant IIoT platforms:

1. **Login** – `POST {baseUrl}/{loginPath}[?apiKey=…]` with `{"email", "password"}` and a tenant header; the session cookie is read from `Set-Cookie` and kept in memory only.
2. **Polling** – every `pollIntervalSeconds`, `GET {baseUrl}/{valuesPath}?thingId=…&metricName=…&pageSize=1` with the cookie and the tenant header. The poll period should equal the publication period of the sensor (60 s in the paper), so that no sample is skipped or read twice. On `401/403` the client logs in again once.
3. **Response** – the most recent sample is taken from `{"data":[{"timestamp":…,"values":[{"value":…}]}]}`. For a platform with a different schema, adapt `CloudTelemetryClient.ParseLatestSample`.

Configuration: copy `Assets/StreamingAssets/telemetry.example.json` to `telemetry.local.json` in the same folder and fill it in. `telemetry.local.json` is listed in `.gitignore`. Credentials can instead be provided with the environment variables `IIOT_EMAIL`, `IIOT_PASSWORD`, `IIOT_API_KEY`, which take precedence. **Never commit credentials.**

Then add `CloudTelemetryClient` to a GameObject and assign it as *Torque Source* of `PointCloudGenerator` instead of `ManualTorqueSource`.

## Latency benchmark

In Play Mode, with a valid allowable torque in the input field, press **F9** (configurable). `PointCloudGenerator` runs `benchmarkWarmup` discarded iterations followed by `benchmarkIterations` measured iterations (default 5 + 100), one per frame, and times separately:

- field recomputation – Eq. (2) on every node;
- visualization refresh – CPU-side rebuild (or in-place recolouring) of the instance batches;
- CSV export – only if `includeExportInBenchmark` is enabled (by default the export is **not** executed during the benchmark).

The critical path is recomputation + visualization. Results are printed to the Console and written to `Application.persistentDataPath/latency_measurements.csv`, with the node count, Unity version, CPU, GPU and RAM in the first line.

`rebuildRendererOnUpdate = true` (default) rebuilds the instanced renderer at each update, which is the configuration measured in the paper; `false` recolours the existing batches in place and avoids the per-update allocation.

## Synthetic sample data

`SampleData/` contains three files generated by `Tools/generate_sample_data.py`: the surface nodes (8,080) of a hollow rectangular cantilever beam with a tip load proportional to the torque, using elementary beam theory (`σ_xx = M·z/I`). They are **not** FEM results and are unrelated to any industrial product; they only reproduce the file format and allow the parser, the update rule and the proportionality check to be tested.

| File | Torque | Torque-independent term |
|---|---|---|
| `synthetic_beam_T100.csv` | 100 | none |
| `synthetic_beam_T50.csv` | 50 | none |
| `synthetic_beam_T50_preload.csv` | 50 | +5 MPa uniform (simulated preload) |

## Citation

If you use this code, please cite the paper above (the reference will be updated after publication).

```bibtex
@article{Giuliani2026TelemetryPacedFEM,
  author  = {Giuliani, Francesca and Debdoubi, Fuad and Frizziero, Leonardo},
  title   = {Telemetry-Paced Visualization of Precomputed {FEM} Stress Fields: A Digital Twin Architecture for Industrial Gear Reducers},
  journal = {Advances in Engineering Software},
  year    = {2026},
  note    = {Submitted}
}
```

## License

Released under the MIT License (see `LICENSE`).

## Acknowledgements

Developed within an industrial PhD programme of the University of Bologna (Department of Industrial Engineering), co-funded by an industrial partner. The authors thank the partner's structural-analysis and IoT teams.

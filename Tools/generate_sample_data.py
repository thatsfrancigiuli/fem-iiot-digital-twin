"""
Generate SYNTHETIC node-wise FEM-like datasets for testing the digital twin.

The data are NOT the results of a real FEM analysis and are unrelated to any industrial
product. They describe the surface nodes of a hollow rectangular cantilever beam (clamped at
x = 0, transverse tip load proportional to a torque value T) with the elementary beam-theory
bending stress sigma_xx = M(x) * z / I. They only serve to exercise the parser, the
torque-ratio update and the proportionality check with realistic file sizes and formats.

Usage:
    python generate_sample_data.py            # writes the three files into ../SampleData
"""
import math
import os

L, B, H, T_WALL = 400.0, 100.0, 60.0, 8.0          # mm
STEP = 4.0                                         # mm, node spacing on the surface
LOAD_PER_TORQUE = 100.0                            # N of tip load per unit of torque
I = (B * H**3 - (B - 2 * T_WALL) * (H - 2 * T_WALL) ** 3) / 12.0   # mm^4

HEADER = ("Node ID;X Coord;Y Coord;Z Coord;Stress XX;Stress YY;Stress ZZ;"
          "Stress XY;Stress YZ;Stress ZX;Von Mises")


def surface_nodes():
    xs = [i * STEP for i in range(int(L / STEP) + 1)]
    ys = [-B / 2 + i * STEP for i in range(int(B / STEP) + 1)]
    zs = [-H / 2 + i * STEP for i in range(int(H / STEP) + 1)]
    seen = set()
    for x in xs:
        for y in ys:
            for z in (zs[0], zs[-1]):
                seen.add((x, y, z))
        for z in zs:
            for y in (ys[0], ys[-1]):
                seen.add((x, y, z))
    return sorted(seen)


def von_mises(sxx, syy, szz, sxy, syz, szx):
    return math.sqrt(0.5 * ((sxx - syy) ** 2 + (syy - szz) ** 2 + (szz - sxx) ** 2)
                     + 3.0 * (sxy ** 2 + syz ** 2 + szx ** 2))


def write(path, torque, preload_mpa=0.0):
    nodes = surface_nodes()
    force = LOAD_PER_TORQUE * torque                      # N
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "\n")
        for k, (x, y, z) in enumerate(nodes, start=1):
            moment = force * (L - x)                      # N*mm
            sxx = moment * z / I + preload_mpa            # MPa; preload = torque-independent term
            sxy = syz = szx = syy = szz = 0.0
            vm = von_mises(sxx, syy, szz, sxy, syz, szx)
            row = [k, x, y, z, sxx, syy, szz, sxy, syz, szx, vm]
            f.write(";".join(str(row[0]) if i == 0 else f"{v:.6f}" for i, v in enumerate(row)) + "\n")
        f.write(";;;;;;;;;;\n")                           # spreadsheet padding row (skipped by the parser)
    return len(nodes)


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "SampleData")
    os.makedirs(out, exist_ok=True)
    n = write(os.path.join(out, "synthetic_beam_T100.csv"), torque=100.0)
    write(os.path.join(out, "synthetic_beam_T50.csv"), torque=50.0)
    write(os.path.join(out, "synthetic_beam_T50_preload.csv"), torque=50.0, preload_mpa=5.0)
    print(f"{n} nodes per file written to {os.path.normpath(out)}")

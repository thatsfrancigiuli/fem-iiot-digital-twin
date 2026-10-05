"""
Check whether the torque-proportional update of the digital twin is exact for a given FEM model.

Two FEM solutions of the SAME mesh, computed at two different torques T_a and T_b, are compared
node by node. Under a linear analysis (Eq. 3 of the paper)

    sigma_i(T) = sigma_i^0 + T * sigma_i^1

the residual  r_i = sigma_i(T_a) - (T_a / T_b) * sigma_i(T_b) = (1 - T_a / T_b) * sigma_i^0

is zero at every node if and only if the model has no torque-independent loads (sigma^0 = 0),
i.e. if and only if the update def_i = VM_i * T / T_max reproduces a new solve exactly (Eq. 5).

The check uses the six tensor components when they are available (exact test). If only the
equivalent value is available, it falls back to the ratio of von Mises values, which is a
necessary but not sufficient condition.

Usage:
    python check_proportionality.py FILE_A T_A FILE_B T_B [--tol 1e-3]

Files: ';', ',' or TAB separated tables with a header (same conventions as the Unity parser).
Nodes are matched by node id if present, otherwise by row order.
"""
import argparse
import csv
import math
import sys

TENSOR_KEYS = ("xx", "yy", "zz", "xy", "yz", "zx")


def read_table(path):
    with open(path, encoding="utf-8-sig") as f:
        lines = [l for l in f.read().splitlines() if l.strip(" ;,\t\"")]
    header_line = lines[0]
    sep = max((";", "\t", ","), key=header_line.count)
    decimal_comma = sep != ","
    reader = csv.reader(lines, delimiter=sep)
    header = [h.strip().strip('"').lower().replace("_", " ").replace("-", " ") for h in next(reader)]

    def col(pred):
        for i, h in enumerate(header):
            if pred(h):
                return i
        return None

    c_id = col(lambda h: "node" in h)
    c_vm = col(lambda h: "mises" in h or h.startswith("vm"))
    c_t = [col(lambda h, k=k: h.endswith(k) or f" {k}" in h) for k in TENSOR_KEYS]
    has_tensor = all(c is not None for c in c_t)

    rows = {}
    for n, r in enumerate(reader):
        try:
            num = lambda i: float(r[i].replace(",", ".") if decimal_comma else r[i])
            key = r[c_id].strip() if c_id is not None else n
            vm = num(c_vm) if c_vm is not None else None
            tensor = [num(c) for c in c_t] if has_tensor else None
        except (ValueError, IndexError):
            continue                                   # summary or malformed row
        rows[key] = (vm, tensor)
    return rows, has_tensor


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("file_a"); ap.add_argument("t_a", type=float)
    ap.add_argument("file_b"); ap.add_argument("t_b", type=float)
    ap.add_argument("--tol", type=float, default=1e-3,
                    help="relative tolerance on the residual (default 1e-3)")
    a = ap.parse_args()
    if a.t_a == a.t_b:
        sys.exit("The two torques must be different.")

    A, tens_a = read_table(a.file_a)
    B, tens_b = read_table(a.file_b)
    common = [k for k in A if k in B]
    if not common:
        sys.exit("No common nodes between the two files.")
    ratio = a.t_a / a.t_b
    use_tensor = tens_a and tens_b

    worst, worst_node, scale = 0.0, None, 0.0
    for k in common:
        vm_a, t_a = A[k]
        vm_b, t_b = B[k]
        if use_tensor:
            res = math.sqrt(sum((x - ratio * y) ** 2 for x, y in zip(t_a, t_b)))
            ref = math.sqrt(sum(x * x for x in t_a))
        else:
            res = abs(vm_a - ratio * vm_b)
            ref = abs(vm_a)
        scale = max(scale, ref)
        if res > worst:
            worst, worst_node = res, k

    rel = worst / scale if scale > 0 else 0.0
    print(f"Nodes compared       : {len(common)}")
    print(f"Test                 : {'tensor components (exact)' if use_tensor else 'von Mises ratio (necessary only)'}")
    print(f"T_a / T_b            : {ratio:.6g}")
    print(f"Max residual         : {worst:.6g} (node {worst_node})")
    print(f"Relative to max field: {rel:.3e}  (tolerance {a.tol:.1e})")
    if rel <= a.tol:
        print("RESULT: proportional loading holds -> the torque-ratio update is exact for this model.")
        return 0
    print("RESULT: torque-independent loads detected -> the torque-ratio update is an approximation.")
    return 1


if __name__ == "__main__":
    sys.exit(main())

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace FemDigitalTwin.Core
{
    /// <summary>
    /// Solver-independent parser for node-wise FEM result tables (CSV / TXT).
    ///
    /// The parser does not rely on fixed column positions: the relevant columns are
    /// identified by matching their header names, so that exports from different
    /// solvers or software versions can be read without reconfiguration.
    ///
    /// Supported input:
    ///   - field separator: ';' ',' or TAB (detected from the header line);
    ///   - decimal separator: '.' or ',' (',' accepted when the field separator is not ',');
    ///   - several exported files concatenated in a single text file (repeated headers are skipped);
    ///   - solver-appended summary rows and empty spreadsheet padding (skipped and counted).
    ///
    /// Parsing runs in parallel on blocks of rows; the original row order is preserved.
    /// This class has no Unity dependency and can be called from a worker thread.
    /// </summary>
    public static class FemFieldParser
    {
        public const int BlockSize = 1000;

        public sealed class Node
        {
            public long NodeId = -1;   // -1 when the file has no node-id column
            public float X, Y, Z;      // coordinates as exported (typically mm)
            public float Vm;           // equivalent (von Mises) value used for visualization
        }

        public sealed class Result
        {
            public List<Node> Nodes = new List<Node>();
            public string Format = "";
            public bool HasTensor;           // true if six tensor components were found in the header
            public string ValueColumnName = "";
            public int DroppedPadding;       // empty / separator-only rows, repeated headers
            public int DroppedSumRows;       // rows whose first cell is text (e.g. "Sum", "Max")
            public int DroppedMalformed;     // rows with non-numeric coordinates or value
        }

        private struct Columns
        {
            public int Id, X, Y, Z, Value;
        }

        public static Result Parse(string text)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // 1) header line: first non-empty line containing letters
            int headerIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (ContainsLetter(lines[i])) { headerIndex = i; break; }
            }
            if (headerIndex < 0) return result;

            string headerLine = lines[headerIndex].Trim();
            char sep = DetectSeparator(headerLine);
            string[] header = SplitAndTrim(headerLine, sep);
            Columns cols = IdentifyColumns(header, out bool hasTensor);
            if (cols.X < 0 || cols.Y < 0 || cols.Z < 0 || cols.Value < 0)
                throw new FormatException("FEM file: could not identify X/Y/Z/value columns from header '" + headerLine + "'.");

            bool decimalComma = sep != ',';
            result.HasTensor = hasTensor;
            result.ValueColumnName = header[cols.Value];
            result.Format = (sep == '\t' ? "TAB" : sep.ToString()) + "-separated, decimal " + (decimalComma ? "'.' or ','" : "'.'");

            // 2) parallel parsing on blocks, results stored per block to preserve order
            int first = headerIndex + 1;
            int count = lines.Length - first;
            int blocks = (count + BlockSize - 1) / BlockSize;
            var blockNodes = new List<Node>[blocks];
            var blockStats = new int[blocks, 3];

            Parallel.For(0, blocks, b =>
            {
                var local = new List<Node>(BlockSize);
                int pad = 0, sums = 0, bad = 0;
                int start = first + b * BlockSize;
                int end = Math.Min(start + BlockSize, lines.Length);
                for (int i = start; i < end; i++)
                {
                    string line = lines[i].Trim();
                    if (IsPadding(line, sep) || line == headerLine) { pad++; continue; }

                    string[] f = SplitAndTrim(line, sep);
                    if (f.Length > 0 && ContainsLetter(f[0]) && !LooksNumeric(f[0])) { sums++; continue; }

                    if (!TryGet(f, cols.X, decimalComma, out float x) ||
                        !TryGet(f, cols.Y, decimalComma, out float y) ||
                        !TryGet(f, cols.Z, decimalComma, out float z) ||
                        !TryGet(f, cols.Value, decimalComma, out float v))
                    { bad++; continue; }

                    var n = new Node { X = x, Y = y, Z = z, Vm = v };
                    if (cols.Id >= 0 && cols.Id < f.Length &&
                        long.TryParse(f[cols.Id], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                        n.NodeId = id;
                    local.Add(n);
                }
                blockNodes[b] = local;
                blockStats[b, 0] = pad; blockStats[b, 1] = sums; blockStats[b, 2] = bad;
            });

            for (int b = 0; b < blocks; b++)
            {
                result.Nodes.AddRange(blockNodes[b]);
                result.DroppedPadding += blockStats[b, 0];
                result.DroppedSumRows += blockStats[b, 1];
                result.DroppedMalformed += blockStats[b, 2];
            }
            return result;
        }

        // ------------------------------------------------------------------ helpers

        private static Columns IdentifyColumns(string[] h, out bool hasTensor)
        {
            var c = new Columns { Id = -1, X = -1, Y = -1, Z = -1, Value = -1 };
            int tensorHits = 0;
            for (int i = 0; i < h.Length; i++)
            {
                string s = Normalize(h[i]);
                if (c.Id < 0 && s.Contains("node")) { c.Id = i; continue; }
                if (c.Value < 0 && (s.Contains("mises") || s == "vm" || s.StartsWith("vm") || s.Contains("equivalent"))) { c.Value = i; continue; }
                if (IsCoordinate(s, 'x') && c.X < 0) { c.X = i; continue; }
                if (IsCoordinate(s, 'y') && c.Y < 0) { c.Y = i; continue; }
                if (IsCoordinate(s, 'z') && c.Z < 0) { c.Z = i; continue; }
                if (s.Contains("xx") || s.Contains("yy") || s.Contains("zz") ||
                    s.Contains("xy") || s.Contains("yz") || s.Contains("zx") || s.Contains("xz"))
                    tensorHits++;
            }
            hasTensor = tensorHits >= 6;
            return c;
        }

        private static bool IsCoordinate(string s, char axis)
        {
            // matches "x", "x coord", "coordx", "x [mm]", "pos x" ... but not "xx", "xy"
            string a = axis.ToString();
            if (s == a) return true;
            bool coordWord = s.Contains("coord") || s.Contains("pos") || s.Contains("[mm]") || s.Contains("(mm)");
            if (!coordWord) return false;
            string stripped = s.Replace("coordinate", "").Replace("coord", "").Replace("position", "")
                               .Replace("pos", "").Replace("[mm]", "").Replace("(mm)", "").Trim();
            return stripped == a;
        }

        private static string Normalize(string s)
        {
            return s.Trim().Trim('"').ToLowerInvariant().Replace("_", " ").Replace("-", " ").Replace(".", " ").Trim();
        }

        private static char DetectSeparator(string header)
        {
            int sc = Count(header, ';'), tab = Count(header, '\t'), comma = Count(header, ',');
            if (sc >= tab && sc >= comma && sc > 0) return ';';
            if (tab >= comma && tab > 0) return '\t';
            return ',';
        }

        private static string[] SplitAndTrim(string line, char sep)
        {
            string[] parts = line.Split(sep);
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim().Trim('"');
            return parts;
        }

        private static bool TryGet(string[] f, int idx, bool decimalComma, out float value)
        {
            value = 0f;
            if (idx < 0 || idx >= f.Length || f[idx].Length == 0) return false;
            string s = decimalComma ? f[idx].Replace(',', '.') : f[idx];
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsPadding(string line, char sep)
        {
            if (line.Length == 0) return true;
            foreach (char ch in line) if (ch != sep && ch != ' ' && ch != '"') return false;
            return true;
        }

        private static bool ContainsLetter(string s)
        {
            foreach (char ch in s)
                if (char.IsLetter(ch) && ch != 'e' && ch != 'E') return true;
            return false;
        }

        private static bool LooksNumeric(string s)
        {
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        private static int Count(string s, char c)
        {
            int n = 0; foreach (char ch in s) if (ch == c) n++; return n;
        }
    }
}

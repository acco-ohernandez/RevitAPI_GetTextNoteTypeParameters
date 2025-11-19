using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;

namespace RevitAPI_Testing
{
    /// <summary>
    /// Infers a rectangular grid from a set of scope boxes in a plan/RCP view,
    /// assigning each box a (row, col) index and computing Left/Right/Up/Down neighbors.
    /// Rotation-agnostic by projecting onto the scope-box grid axes (not the view axes).
    /// Robust to small numerical drift by using median steps and tolerant rounding.
    /// </summary>
    public static class ScopeBoxGridInference
    {
        // ----- Public DTOs -----

        public sealed class GridIndex
        {
            public int Row { get; set; }
            public int Col { get; set; }
        }

        public sealed class BoxNeighbors
        {
            public bool HasLeft { get; set; }
            public bool HasRight { get; set; }
            public bool HasUp { get; set; }
            public bool HasDown { get; set; }
        }

        /// <summary>Value-equality comparer for GridIndex so it can be used as a dictionary key or in a HashSet.</summary>
        public sealed class GridIndexEqualityComparer : IEqualityComparer<GridIndex>
        {
            public static readonly GridIndexEqualityComparer Instance = new GridIndexEqualityComparer();
            public bool Equals(GridIndex x, GridIndex y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.Row == y.Row && x.Col == y.Col;
            }
            public int GetHashCode(GridIndex obj) => (obj.Row * 397) ^ obj.Col;
        }

        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public static void InferGridAndNeighbors(
            Document document,
            View planView,
            IList<ElementId> scopeBoxIds,
            out Dictionary<ElementId, GridIndex> indexByElementId,
            out Dictionary<ElementId, BoxNeighbors> neighborsByElementId,
            out int rowCount,
            out int colCount)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (planView == null) throw new ArgumentNullException(nameof(planView));
            if (scopeBoxIds == null || scopeBoxIds.Count == 0)
                throw new InvalidOperationException("No scope boxes were provided.");

            // 1) Inspect all selected scope boxes and grab a consistent grid basis
            //    Use the FIRST box's axes as the grid axes (Right = +X, Up = -Down).
            var inspectors = new List<ScopeBoxProperties>(scopeBoxIds.Count);
            foreach (var id in scopeBoxIds)
            {
                var e = document.GetElement(id);
                inspectors.Add(ScopeBoxInspector.Inspect(e, planView));
            }

            ScopeBoxProperties basis = inspectors[0];
            XYZ gridRight = basis.DirRight2D.Normalize();     // along columns
            XYZ gridUp = basis.DirDown2D.Negate().Normalize(); // along rows (upwards)

            // 2) Project centers into this grid basis (rotation-agnostic coordinates)
            var centersGrid = new List<(ElementId Id, double R, double U)>(inspectors.Count);
            foreach (var p in inspectors)
            {
                double r = p.CenterWorld.DotProduct(gridRight);
                double u = p.CenterWorld.DotProduct(gridUp);
                centersGrid.Add((p.Id, r, u));
            }

            // 3) Estimate a generous "same row/col" tolerance in GRID space
            double rowColTol = EstimateGenerousRowColTolerance(centersGrid);

            // 4) Collect samples for grid spacing using pairs that lie on (nearly) same row/col
            var deltaRsamples = new List<double>();
            var deltaUsamples = new List<double>();

            for (int i = 0; i < centersGrid.Count; ++i)
            {
                for (int j = i + 1; j < centersGrid.Count; ++j)
                {
                    double dR = Math.Abs(centersGrid[i].R - centersGrid[j].R);
                    double dU = Math.Abs(centersGrid[i].U - centersGrid[j].U);

                    // Same ROW → U nearly equal → collect ΔR
                    if (dU <= rowColTol && dR > 1e-9) deltaRsamples.Add(dR);

                    // Same COLUMN → R nearly equal → collect ΔU
                    if (dR <= rowColTol && dU > 1e-9) deltaUsamples.Add(dU);
                }
            }

            if (deltaRsamples.Count == 0 || deltaUsamples.Count == 0)
                throw new InvalidOperationException("Could not infer spacing from selection (no row/column samples).");

            double stepR = RobustMedianWithoutOutliers(deltaRsamples);
            double stepU = RobustMedianWithoutOutliers(deltaUsamples);
            if (stepR <= 1e-8 || stepU <= 1e-8)
                throw new InvalidOperationException("Could not infer spacing from selection (degenerate step).");

            // 5) Quantize coordinates to integer indices with tolerant rounding
            double rBinTol = stepR * 0.5; // half-step tolerance band
            double uBinTol = stepU * 0.5;

            var provisional = centersGrid
                .Select(x => new
                {
                    x.Id,
                    rIdx = TolerantRoundToInt(x.R / stepR, rBinTol / stepR),
                    uIdx = TolerantRoundToInt(x.U / stepU, uBinTol / stepU)
                })
                .ToList();

            int minR = provisional.Min(p => p.rIdx);
            int minU = provisional.Min(p => p.uIdx);

            indexByElementId = provisional.ToDictionary(
                p => p.Id,
                p => new GridIndex { Row = p.uIdx - minU, Col = p.rIdx - minR });

            rowCount = indexByElementId.Values.Max(ix => ix.Row) + 1;
            colCount = indexByElementId.Values.Max(ix => ix.Col) + 1;

            // Optionally require full rectangle.
            if (!IsPerfectRectangle(indexByElementId))
                throw new InvalidOperationException("Selection does not form a clean rectangular grid (missing cells).");

            // 6) Neighbor map via index lookups
            var elementIdByIndex = new Dictionary<(int Row, int Col), ElementId>();
            foreach (var kv in indexByElementId)
                elementIdByIndex[(kv.Value.Row, kv.Value.Col)] = kv.Key;

            neighborsByElementId = new Dictionary<ElementId, BoxNeighbors>(indexByElementId.Count);
            foreach (var kv in indexByElementId)
            {
                int r = kv.Value.Row;
                int c = kv.Value.Col;

                neighborsByElementId[kv.Key] = new BoxNeighbors
                {
                    HasLeft = elementIdByIndex.ContainsKey((r, c - 1)),
                    HasRight = elementIdByIndex.ContainsKey((r, c + 1)),
                    HasUp = elementIdByIndex.ContainsKey((r - 1, c)),
                    HasDown = elementIdByIndex.ContainsKey((r + 1, c))
                };
            }
        }

        // =====================================================================
        // Helpers (C# 7.3-friendly; no static local functions)
        // =====================================================================

        private static double EstimateGenerousRowColTolerance(
            List<(ElementId Id, double R, double U)> pts)
        {
            if (pts == null || pts.Count == 0) return 1e-4;
            double rSpan = pts.Max(x => x.R) - pts.Min(x => x.R);
            double uSpan = pts.Max(x => x.U) - pts.Min(x => x.U);
            double span = Math.Max(rSpan, uSpan);
            const double MinTol = 1e-4; // ~0.03 mm in internal feet
            return Math.Max(span * 0.01, MinTol);
        }

        private static double MedianOf(IList<double> values)
        {
            if (values == null || values.Count == 0) return 0.0;
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return (n % 2 == 1) ? arr[n / 2] : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
        }

        private static double RobustMedianWithoutOutliers(List<double> samples)
        {
            if (samples == null || samples.Count == 0) return 0.0;

            double rawMedian = MedianOf(samples);
            if (rawMedian <= 0) return 0.0;

            double cutoff = rawMedian * 3.0;
            var trimmed = samples.Where(x => x > 1e-9 && x <= cutoff).ToList();

            return trimmed.Count == 0 ? rawMedian : MedianOf(trimmed);
        }

        private static int TolerantRoundToInt(double valueUnits, double toleranceUnits)
        {
            double nearest = Math.Round(valueUnits);
            return Math.Abs(valueUnits - nearest) <= toleranceUnits
                 ? (int)nearest
                 : (int)Math.Round(valueUnits);
        }

        private static bool IsPerfectRectangle(Dictionary<ElementId, GridIndex> indexById)
        {
            int maxRow = indexById.Values.Max(ix => ix.Row);
            int maxCol = indexById.Values.Max(ix => ix.Col);

            var present = new HashSet<(int, int)>(
                indexById.Values.Select(ix => (ix.Row, ix.Col)));

            for (int r = 0; r <= maxRow; ++r)
            {
                for (int c = 0; c <= maxCol; ++c)
                {
                    if (!present.Contains((r, c))) return false;
                }
            }
            return true;
        }
    }
}












//using System;
//using System.Collections.Generic;
//using System.Linq;

//using Autodesk.Revit.DB;

//namespace RevitAPI_Testing
//{
//    /// <summary>
//    /// Infers a rectangular grid from a set of scope boxes selected in a plan/RCP view,
//    /// assigning each box a (row, col) index and computing Left/Right/Up/Down neighbors.
//    /// Rotation-agnostic and tolerant to small numeric drift.
//    /// </summary>
//    public static class ScopeBoxGridInference
//    {
//        public sealed class GridIndex
//        {
//            public int Row { get; set; }
//            public int Col { get; set; }
//        }

//        public sealed class BoxNeighbors
//        {
//            public bool HasLeft { get; set; }
//            public bool HasRight { get; set; }
//            public bool HasUp { get; set; }
//            public bool HasDown { get; set; }
//        }



//        public sealed class GridIndexEqualityComparer : IEqualityComparer<GridIndex>
//        {
//            public bool Equals(GridIndex x, GridIndex y)
//            {
//                if (ReferenceEquals(x, y)) return true;
//                if (x is null || y is null) return false;
//                return x.Row == y.Row && x.Col == y.Col;
//            }

//            public int GetHashCode(GridIndex obj)
//            {
//                unchecked
//                {
//                    // standard row/col hash combine
//                    int h = 17;
//                    h = h * 31 + obj.Row.GetHashCode();
//                    h = h * 31 + obj.Col.GetHashCode();
//                    return h;
//                }
//            }
//        }


//        /// <summary>
//        /// Main entry point.
//        /// </summary>
//        public static void InferGridAndNeighbors(
//    Document document,
//    View planView,
//    IList<ElementId> scopeBoxIds,
//    out Dictionary<ElementId, GridIndex> indexByElementId,
//    out Dictionary<ElementId, BoxNeighbors> neighborsByElementId,
//    out int rowCount,
//    out int colCount)
//        {
//            if (document == null) throw new ArgumentNullException(nameof(document));
//            if (planView == null) throw new ArgumentNullException(nameof(planView));
//            if (scopeBoxIds == null || scopeBoxIds.Count == 0)
//                throw new InvalidOperationException("No scope boxes were provided.");

//            // 1) View frame
//            XYZ viewRight = planView.RightDirection.Normalize();
//            XYZ viewUp = planView.UpDirection.Normalize();

//            // 2) RU centers (via your robust inspector)
//            var centersRU = new List<(ElementId Id, double R, double U)>(scopeBoxIds.Count);
//            foreach (ElementId id in scopeBoxIds)
//            {
//                Element e = document.GetElement(id);
//                var p = ScopeBoxInspector.Inspect(e, planView);
//                centersRU.Add((id, p.CenterWorld.DotProduct(viewRight), p.CenterWorld.DotProduct(viewUp)));
//            }

//            // 3) Infer stepR/stepU from pairwise deltas along same rows/cols (with generous same-row/col tol)
//            double rowColTol = EstimateGenerousRowColTolerance(centersRU);

//            var deltaRsamples = new List<double>();
//            var deltaUsamples = new List<double>();

//            for (int i = 0; i < centersRU.Count; ++i)
//            {
//                for (int j = i + 1; j < centersRU.Count; ++j)
//                {
//                    double dR = Math.Abs(centersRU[i].R - centersRU[j].R);
//                    double dU = Math.Abs(centersRU[i].U - centersRU[j].U);

//                    if (dU <= rowColTol && dR > 1e-9) deltaRsamples.Add(dR); // same row → ΔR
//                    if (dR <= rowColTol && dU > 1e-9) deltaUsamples.Add(dU); // same col → ΔU
//                }
//            }

//            if (deltaRsamples.Count == 0 || deltaUsamples.Count == 0)
//                throw new InvalidOperationException("Could not infer spacing from selection (no row/column samples).");

//            double stepR = RobustMedianWithoutOutliers(deltaRsamples);
//            double stepU = RobustMedianWithoutOutliers(deltaUsamples);

//            if (stepR <= 1e-8 || stepU <= 1e-8)
//                throw new InvalidOperationException("Could not infer spacing from selection (degenerate step).");

//            // 4) Quantize RU → (row,col) with tolerant rounding
//            //    Use half-step bins to absorb drift
//            double rBin = stepR * 0.5;
//            double uBin = stepU * 0.5;

//            var provisional = centersRU
//                .Select(x => new
//                {
//                    x.Id,
//                    Ridx = TolerantRoundToInt(x.R / stepR, rBin / stepR),
//                    Uidx = TolerantRoundToInt(x.U / stepU, uBin / stepU)
//                })
//                .ToList();

//            // Normalize so smallest index starts at 0 for both axes
//            int minR = provisional.Min(p => p.Ridx);
//            int minU = provisional.Min(p => p.Uidx);

//            indexByElementId = provisional.ToDictionary(
//                p => p.Id,
//                p => new GridIndex { Row = p.Uidx - minU, Col = p.Ridx - minR });

//            // IMPORTANT: do not require a perfect rectangle.
//            // Just compute counts from the unique indices that actually exist.
//            var uniqueRows = new HashSet<int>(indexByElementId.Values.Select(ix => ix.Row));
//            var uniqueCols = new HashSet<int>(indexByElementId.Values.Select(ix => ix.Col));
//            rowCount = uniqueRows.Count;
//            colCount = uniqueCols.Count;

//            // 5) Neighbors by index lookup
//            var elementIdByIndex = new Dictionary<(int Row, int Col), ElementId>();
//            foreach (var kv in indexByElementId)
//                elementIdByIndex[(kv.Value.Row, kv.Value.Col)] = kv.Key;

//            neighborsByElementId = new Dictionary<ElementId, BoxNeighbors>(indexByElementId.Count);
//            foreach (var kv in indexByElementId)
//            {
//                int r = kv.Value.Row;
//                int c = kv.Value.Col;

//                var nb = new BoxNeighbors
//                {
//                    HasLeft = elementIdByIndex.ContainsKey((r, c - 1)),
//                    HasRight = elementIdByIndex.ContainsKey((r, c + 1)),
//                    HasUp = elementIdByIndex.ContainsKey((r - 1, c)),
//                    HasDown = elementIdByIndex.ContainsKey((r + 1, c))
//                };
//                neighborsByElementId[kv.Key] = nb;
//            }
//        }

//        /// <summary>
//        /// A generous “same row/col” tolerance derived from the spread of RU centers.
//        /// We use 1% of the larger span (R or U), but never below a tiny floor.
//        /// </summary>
//        private static double EstimateGenerousRowColTolerance(
//            List<(Autodesk.Revit.DB.ElementId id, double R, double U)> centersRU)
//        {
//            double rSpan = centersRU.Max(x => x.R) - centersRU.Min(x => x.R);
//            double uSpan = centersRU.Max(x => x.U) - centersRU.Min(x => x.U);
//            double span = Math.Max(rSpan, uSpan);

//            const double MinTol = 1e-4; // ~0.03 mm in internal feet
//            return Math.Max(span * 0.01, MinTol);
//        }

//        /// <summary>Median of a sequence (copies & sorts ascending).</summary>
//        private static double MedianOf(IList<double> values)
//        {
//            if (values == null || values.Count == 0) return 0.0;
//            var arr = values.OrderBy(v => v).ToArray();
//            int n = arr.Length;
//            return (n % 2 == 1) ? arr[n / 2] : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
//        }

//        /// <summary>
//        /// Median with simple outlier rejection: discard values greater than
//        /// 3× the raw median (keeps evenly spaced grids robust to strays)
//        /// and recompute the median from the trimmed set.
//        /// </summary>
//        private static double RobustMedianWithoutOutliers(List<double> samples)
//        {
//            if (samples == null || samples.Count == 0) return 0.0;

//            double rawMedian = MedianOf(samples);
//            if (rawMedian <= 0) return 0.0;

//            double cutoff = rawMedian * 3.0;
//            var trimmed = samples.Where(x => x > 1e-9 && x <= cutoff).ToList();

//            return trimmed.Count == 0 ? rawMedian : MedianOf(trimmed);
//        }

//        /// <summary>
//        /// Snap a normalized value to the nearest integer with a soft tolerance band.
//        /// valueUnits is already scaled by the step (e.g., R/stepR). toleranceUnits is
//        /// the allowed deviation from the nearest integer (e.g., 0.5 for half-step bins).
//        /// </summary>
//        private static int TolerantRoundToInt(double valueUnits, double toleranceUnits)
//        {
//            double nearest = Math.Round(valueUnits);
//            return Math.Abs(valueUnits - nearest) <= toleranceUnits
//                 ? (int)nearest
//                 : (int)Math.Round(valueUnits);
//        }



//        // ---------- Helpers ----------

//        /// <summary>
//        /// Given a sorted array of coordinates along one axis, compute the primary spacing:
//        /// take consecutive differences, drop near-zeros, and use a robust median of the
//        /// smallest quartile to avoid multiples/outliers.
//        /// </summary>
//        private static double EstimatePrimaryStepFromSorted(double[] sortedVals)
//        {
//            if (sortedVals.Length < 2) return 0.0;

//            var diffs = new List<double>(sortedVals.Length - 1);
//            for (int i = 1; i < sortedVals.Length; ++i)
//            {
//                double d = sortedVals[i] - sortedVals[i - 1];
//                if (d > 1e-9) diffs.Add(d);
//            }
//            if (diffs.Count == 0) return 0.0;

//            diffs.Sort();

//            // Take the lowest quartile (Q1 bucket); median of that is very stable
//            int q1Count = Math.Max(1, diffs.Count / 4);
//            double step = Median(diffs.Take(q1Count).ToArray());

//            // Fallback if that collapses
//            if (step <= 1e-9) step = Median(diffs.ToArray());

//            return step;
//        }

//        private static int TolerantRoundToInt2(double valueUnits, double toleranceBandFraction)
//        {
//            // toleranceBandFraction is relative to 1 step (e.g., 0.35 = 35% of step)
//            double nearest = Math.Round(valueUnits);
//            return (Math.Abs(valueUnits - nearest) <= toleranceBandFraction)
//                ? (int)nearest
//                : (int)Math.Round(valueUnits);
//        }

//        private static double Median(IList<double> values)
//        {
//            var arr = values.OrderBy(x => x).ToArray();
//            int n = arr.Length;
//            if (n == 0) return 0.0;
//            return (n % 2 == 1) ? arr[n / 2] : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
//        }

//        private static bool IsPerfectRectangle(Dictionary<ElementId, GridIndex> indexById)
//        {
//            int maxRow = indexById.Values.Max(ix => ix.Row);
//            int maxCol = indexById.Values.Max(ix => ix.Col);

//            var present = new HashSet<(int, int)>(indexById.Values.Select(ix => (ix.Row, ix.Col)));
//            for (int r = 0; r <= maxRow; ++r)
//                for (int c = 0; c <= maxCol; ++c)
//                    if (!present.Contains((r, c))) return false;
//            return true;
//        }
//    }
//}

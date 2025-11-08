
#region Namespaces
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
#endregion

// Revit 2020-2026
// ORH – Standalone command: create matchline guides from an EXISTING grid of scope boxes.
// Robust grid detection using the seed scope box local axes (rotation-agnostic, overlap-tolerant).

namespace RevitAPI_Testing
{
    [Transaction(TransactionMode.Manual)]
    public class Cmd_MatchlineDetailLines : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument.Document;
            View activeView = document.ActiveView;

            if (activeView.ViewType != ViewType.FloorPlan && activeView.ViewType != ViewType.CeilingPlan)
            {
                TaskDialog.Show("Matchline Guides", "Please run this command in a Floor Plan or Ceiling Plan view.");
                return Result.Cancelled;
            }

            if (!TryEnsureSelectionOfScopeBoxes(uiDocument, out IList<ElementId> selectedScopeBoxIds, out bool wasCancelled, out string errorMessage))
            {
                if (wasCancelled) return Result.Cancelled;
                message = errorMessage ?? "No scope boxes selected.";
                return Result.Failed;
            }
            if (selectedScopeBoxIds.Count == 0)
            {
                TaskDialog.Show("Matchline Guides", "No scope boxes were selected.");
                return Result.Cancelled;
            }

            try
            {
                // Inspect selected scope boxes
                var inspected = new List<ScopeBoxProperties>(selectedScopeBoxIds.Count);
                foreach (ElementId id in selectedScopeBoxIds)
                {
                    Element e = document.GetElement(id);
                    if (!IsScopeBox(e)) continue;
                    inspected.Add(ScopeBoxInspector.Inspect(e, activeView));
                }
                if (inspected.Count == 0)
                {
                    TaskDialog.Show("Matchline Guides", "Selection does not contain valid Scope Boxes.");
                    return Result.Cancelled;
                }

                // Orientation from the first box
                ScopeBoxProperties seed = inspected[0];
                XYZ axisRight = seed.DirRight2D.Normalize();
                XYZ axisDown = seed.DirDown2D.Normalize();

                // Project centers onto the seed axes
                var projected = inspected.Select(p =>
                {
                    double uAlongRight = (p.CenterWorld - seed.CenterWorld).DotProduct(axisRight);
                    double vAlongDown = (p.CenterWorld - seed.CenterWorld).DotProduct(axisDown);
                    return new ProjectedRecord { Id = p.Id, Props = p, UAlongRight = uAlongRight, VAlongDown = vAlongDown };
                }).ToList();

                // NEW: estimate step using DISTINCT coordinates only (no intra-column/row zeros)
                double stepAlongRight = EstimateStepFromDistinct(projected.Select(x => x.UAlongRight).ToArray());
                double stepAlongDown = EstimateStepFromDistinct(projected.Select(x => x.VAlongDown).ToArray());

                if (stepAlongRight <= 1e-9 || stepAlongDown <= 1e-9)
                {
                    TaskDialog.Show("Matchline Guides", "Could not infer spacing from selection (degenerate step).");
                    return Result.Cancelled;
                }

                double minU = projected.Min(x => x.UAlongRight);
                double minV = projected.Min(x => x.VAlongDown);

                const double allowedRelativeDeviation = 0.25; // be forgiving with jitter

                foreach (var pr in projected)
                {
                    double normalizedCol = (pr.UAlongRight - minU) / stepAlongRight;
                    double normalizedRow = (pr.VAlongDown - minV) / stepAlongDown;

                    pr.ColumnIndex = (int)Math.Round(normalizedCol, MidpointRounding.AwayFromZero);
                    pr.RowIndex = (int)Math.Round(normalizedRow, MidpointRounding.AwayFromZero);

                    // Sanity reproject (optional)
                    double reU = minU + pr.ColumnIndex * stepAlongRight;
                    double reV = minV + pr.RowIndex * stepAlongDown;

                    double deltaU = Math.Abs(pr.UAlongRight - reU);
                    double deltaV = Math.Abs(pr.VAlongDown - reV);

                    bool withinU = deltaU <= allowedRelativeDeviation * stepAlongRight;
                    bool withinV = deltaV <= allowedRelativeDeviation * stepAlongDown;
                    if (!withinU || !withinV)
                    {
                        // keep going; grids made by our tool typically still cluster correctly
                    }
                }

                // Derive counts
                int columnCount = projected.Max(x => x.ColumnIndex) + 1;
                int rowCount = projected.Max(x => x.RowIndex) + 1;

                if (rowCount * columnCount != projected.Count)
                {
                    NormalizeSparseIndices(projected, 'C');
                    NormalizeSparseIndices(projected, 'R');
                    columnCount = projected.Max(x => x.ColumnIndex) + 1;
                    rowCount = projected.Max(x => x.RowIndex) + 1;
                }

                if (rowCount * columnCount != projected.Count)
                {
                    TaskDialog.Show("Matchline Guides",
                        $"Could not infer a clean rectangular grid from the selection.\n" +
                        $"Detected rows × columns = {rowCount} × {columnCount} = {rowCount * columnCount}, " +
                        $"but there are {projected.Count} boxes.");
                    return Result.Cancelled;
                }

                // Build row-major id list
                var rowMajorIds = new List<ElementId>(rowCount * columnCount);
                for (int r = 0; r < rowCount; ++r)
                {
                    for (int c = 0; c < columnCount; ++c)
                    {
                        var match = projected.FirstOrDefault(x => x.RowIndex == r && x.ColumnIndex == c);
                        if (match == null)
                        {
                            TaskDialog.Show("Matchline Guides", $"Missing grid cell R{r + 1}C{c + 1}.");
                            return Result.Cancelled;
                        }
                        rowMajorIds.Add(match.Id);
                    }
                }

                // Build the guides
                var guideOptions = new GuideLineOptions
                {
                    ManageTransactions = false,
                    DeleteExisting = true,
                    LineStyleName = "Matchline Reference",
                    LineColor = new Autodesk.Revit.DB.Color(0, 255, 255), // cyan
                    LinePatternName = "Dash",
                    LineWeight = 2,
                    OffsetPastOuterEdgesFeet = 0.5,
                    TagIntersections = false
                };

                using (TransactionGroup transactionGroup = new TransactionGroup(document, "Create Matchline Guides"))
                {
                    transactionGroup.Start();
                    using (Transaction transaction = new Transaction(document, "Draw Matchline Reference Lines"))
                    {
                        transaction.Start();

                        IList<ElementId> createdGuideIds = ScopeBoxGuideLineBuilder.BuildGuides(
                            document, activeView, rowMajorIds, rowCount, columnCount, guideOptions);

                        if (createdGuideIds != null && createdGuideIds.Count > 0)
                        {
                            TaskDialog.Show("Matchline Guides",
                                $"Created {createdGuideIds.Count} guide line(s) for a {rowCount} × {columnCount} grid.");
                        }

                        transaction.Commit();
                    }
                    transactionGroup.Assimilate();
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }

        // ----------------- Helpers -----------------

        private static bool IsScopeBox(Element element)
        {
            return element?.Category != null
                && element.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
        }

        private static bool TryEnsureSelectionOfScopeBoxes(
            UIDocument uiDocument,
            out IList<ElementId> scopeBoxIds,
            out bool wasCancelled,
            out string errorMessage)
        {
            wasCancelled = false;
            errorMessage = null;

            ICollection<ElementId> selected = uiDocument.Selection.GetElementIds();
            if (selected != null && selected.Count > 0)
            {
                List<ElementId> onlyScope = new List<ElementId>();
                foreach (ElementId id in selected)
                {
                    Element e = uiDocument.Document.GetElement(id);
                    if (IsScopeBox(e)) onlyScope.Add(id);
                }

                if (onlyScope.Count > 0)
                {
                    scopeBoxIds = onlyScope;
                    return true;
                }

                errorMessage = "Please select one or more Scope Boxes and run the command again.";
                scopeBoxIds = new List<ElementId>();
                return false;
            }

            errorMessage = "No elements selected. Please select one or more Scope Boxes.";
            scopeBoxIds = new List<ElementId>();
            return false;
        }

        private sealed class ProjectedRecord
        {
            public ElementId Id;
            public ScopeBoxProperties Props;
            public double UAlongRight;  // coordinate along seed DirRight2D
            public double VAlongDown;   // coordinate along seed DirDown2D
            public int ColumnIndex;     // 0..C-1
            public int RowIndex;        // 0..R-1
        }

        /// <summary>
        /// Estimate step size from an array of projected coordinates by:
        /// 1) Sorting
        /// 2) Collapsing near-equal values into DISTINCT coordinates
        /// 3) Taking the median gap between consecutive DISTINCT values
        /// </summary>
        private static double EstimateStepFromDistinct(double[] coordinates)
        {
            if (coordinates == null || coordinates.Length < 2)
                return 0.0;

            Array.Sort(coordinates);

            // Collapse near-equal coordinates (tolerance ~ 1e-6 ft)
            const double clusterTol = 1e-6;
            List<double> distinct = new List<double>(coordinates.Length);
            double last = coordinates[0];
            distinct.Add(last);
            for (int i = 1; i < coordinates.Length; ++i)
            {
                if (Math.Abs(coordinates[i] - last) > clusterTol)
                {
                    distinct.Add(coordinates[i]);
                    last = coordinates[i];
                }
            }

            if (distinct.Count < 2)
                return 0.0; // all were effectively identical (e.g., a single column/row selected)

            // Compute gaps between DISTINCT positions
            List<double> gaps = new List<double>(distinct.Count - 1);
            for (int i = 1; i < distinct.Count; ++i)
                gaps.Add(Math.Abs(distinct[i] - distinct[i - 1]));

            gaps.Sort();
            int mid = gaps.Count / 2;
            return gaps.Count % 2 == 1 ? gaps[mid] : 0.5 * (gaps[mid - 1] + gaps[mid]);
        }

        /// <summary>
        /// Compress sparse integer indices to contiguous 0..N-1 (e.g., 0,2,2,3 -> 0,1,1,2).
        /// axis = 'C' (columns by UAlongRight) or 'R' (rows by VAlongDown).
        /// </summary>
        private static void NormalizeSparseIndices(List<ProjectedRecord> projected, char axis)
        {
            if (axis == 'C')
            {
                var unique = projected.Select(x => x.ColumnIndex).Distinct().OrderBy(i => i).ToList();
                var map = new Dictionary<int, int>(unique.Count);
                for (int i = 0; i < unique.Count; ++i) map[unique[i]] = i;
                foreach (var p in projected) p.ColumnIndex = map[p.ColumnIndex];
            }
            else
            {
                var unique = projected.Select(x => x.RowIndex).Distinct().OrderBy(i => i).ToList();
                var map = new Dictionary<int, int>(unique.Count);
                for (int i = 0; i < unique.Count; ++i) map[unique[i]] = i;
                foreach (var p in projected) p.RowIndex = map[p.RowIndex];
            }
        }
    }
}







// ===================================================================================
//#region Namespaces
//using System;
//using System.Collections.Generic;
//using System.Linq;

//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.UI;
//#endregion

//// Revit 2020-2026
//// ORH – Standalone command: create matchline guides from an EXISTING grid of scope boxes.
//// Robust grid detection using the seed scope box local axes (rotation-agnostic, overlap-tolerant).

//namespace RevitAPI_Testing
//{
//    [Transaction(TransactionMode.Manual)]
//    public class Cmd_MatchlineDetailLines : IExternalCommand
//    {
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
//            Document document = uiDocument.Document;
//            View activeView = document.ActiveView;

//            // Plan / RCP only
//            if (activeView.ViewType != ViewType.FloorPlan && activeView.ViewType != ViewType.CeilingPlan)
//            {
//                TaskDialog.Show("Matchline Guides", "Please run this command in a Floor Plan or Ceiling Plan view.");
//                return Result.Cancelled;
//            }

//            // Collect selected scope boxes
//            if (!TryEnsureSelectionOfScopeBoxes(uiDocument, out IList<ElementId> selectedScopeBoxIds, out bool wasCancelled, out string errorMessage))
//            {
//                if (wasCancelled) return Result.Cancelled;
//                message = errorMessage ?? "No scope boxes selected.";
//                return Result.Failed;
//            }
//            if (selectedScopeBoxIds.Count == 0)
//            {
//                TaskDialog.Show("Matchline Guides", "No scope boxes were selected.");
//                return Result.Cancelled;
//            }

//            try
//            {
//                // Inspect all selected boxes and store center + local (Right2D/Down2D) axes from the FIRST box
//                var inspected = new List<ScopeBoxProperties>(selectedScopeBoxIds.Count);
//                foreach (ElementId id in selectedScopeBoxIds)
//                {
//                    Element e = document.GetElement(id);
//                    if (!IsScopeBox(e)) continue;
//                    inspected.Add(ScopeBoxInspector.Inspect(e, activeView));
//                }
//                if (inspected.Count == 0)
//                {
//                    TaskDialog.Show("Matchline Guides", "Selection does not contain valid Scope Boxes.");
//                    return Result.Cancelled;
//                }

//                // Use the first inspected box as the seed for orientation
//                ScopeBoxProperties seed = inspected[0];
//                XYZ axisRight = seed.DirRight2D.Normalize();
//                XYZ axisDown = seed.DirDown2D.Normalize();

//                // Project every center onto the seed axes (u along Right, v along Down)
//                var projected = inspected.Select(p =>
//                {
//                    double uRight = (p.CenterWorld - seed.CenterWorld).DotProduct(axisRight);
//                    double vDown = (p.CenterWorld - seed.CenterWorld).DotProduct(axisDown);
//                    return new ProjectedRecord { Id = p.Id, Props = p, UAlongRight = uRight, VAlongDown = vDown };
//                }).ToList();

//                // Determine grid step along Right/Down using median of nearest-neighbor gaps
//                double stepAlongRight = EstimateTypicalStepFromProjected(projected.Select(x => x.UAlongRight).ToArray());
//                double stepAlongDown = EstimateTypicalStepFromProjected(projected.Select(x => x.VAlongDown).ToArray());

//                if (stepAlongRight <= 1e-9 || stepAlongDown <= 1e-9)
//                {
//                    TaskDialog.Show("Matchline Guides", "Could not infer spacing from selection (degenerate step).");
//                    return Result.Cancelled;
//                }

//                // Compute min along each axis to normalize to zero-based grid
//                double minU = projected.Min(x => x.UAlongRight);
//                double minV = projected.Min(x => x.VAlongDown);

//                // Map each box to (rowIndex, colIndex) using rounded normalized coordinates.
//                // Tolerate ±20% deviation from the estimated step when checking reprojected positions.
//                const double allowedRelativeDeviation = 0.20;

//                foreach (var pr in projected)
//                {
//                    double normalizedCol = (pr.UAlongRight - minU) / stepAlongRight;
//                    double normalizedRow = (pr.VAlongDown - minV) / stepAlongDown;

//                    pr.ColumnIndex = (int)Math.Round(normalizedCol, MidpointRounding.AwayFromZero);
//                    pr.RowIndex = (int)Math.Round(normalizedRow, MidpointRounding.AwayFromZero);

//                    // Optional sanity reproject (not strictly required, but helps catch outliers)
//                    double reprojectedU = minU + pr.ColumnIndex * stepAlongRight;
//                    double reprojectedV = minV + pr.RowIndex * stepAlongDown;

//                    double deltaU = Math.Abs(pr.UAlongRight - reprojectedU);
//                    double deltaV = Math.Abs(pr.VAlongDown - reprojectedV);

//                    bool withinU = deltaU <= allowedRelativeDeviation * stepAlongRight;
//                    bool withinV = deltaV <= allowedRelativeDeviation * stepAlongDown;
//                    if (!withinU || !withinV)
//                    {
//                        // Let it pass; grids built by our tool will still cluster correctly.
//                        // If you want to be strict, uncomment the next 3 lines:
//                        // TaskDialog.Show("Matchline Guides", "Selection appears irregular (outlier found).");
//                        // return Result.Cancelled;
//                    }
//                }

//                // Derive counts
//                int columnCount = projected.Max(x => x.ColumnIndex) + 1;
//                int rowCount = projected.Max(x => x.RowIndex) + 1;

//                if (rowCount * columnCount != projected.Count)
//                {
//                    // Attempt a gentle renormalization by shifting indices to close small gaps
//                    NormalizeSparseIndices(projected, axis: 'C');
//                    NormalizeSparseIndices(projected, axis: 'R');
//                    columnCount = projected.Max(x => x.ColumnIndex) + 1;
//                    rowCount = projected.Max(x => x.RowIndex) + 1;
//                }

//                if (rowCount * columnCount != projected.Count)
//                {
//                    TaskDialog.Show("Matchline Guides",
//                        $"Could not infer a clean rectangular grid from the selection.\n" +
//                        $"Detected rows × columns = {rowCount} × {columnCount} = {rowCount * columnCount}, " +
//                        $"but there are {projected.Count} boxes.");
//                    return Result.Cancelled;
//                }

//                // Build row-major list (R1C1, R1C2, ..., R2C1, ...), ordering by ColumnIndex (0..C-1) within each RowIndex (0..R-1)
//                var rowMajorIds = new List<ElementId>(rowCount * columnCount);
//                for (int r = 0; r < rowCount; ++r)
//                {
//                    for (int c = 0; c < columnCount; ++c)
//                    {
//                        var match = projected.FirstOrDefault(x => x.RowIndex == r && x.ColumnIndex == c);
//                        if (match == null)
//                        {
//                            TaskDialog.Show("Matchline Guides", $"Missing grid cell R{r + 1}C{c + 1}.");
//                            return Result.Cancelled;
//                        }
//                        rowMajorIds.Add(match.Id);
//                    }
//                }

//                // Build the guides
//                var guideOptions = new GuideLineOptions
//                {
//                    ManageTransactions = false,           // we will own the transaction below
//                    DeleteExisting = true,
//                    LineStyleName = "Matchline Reference",
//                    LineColor = new Autodesk.Revit.DB.Color(0, 255, 255), // cyan
//                    LinePatternName = "Dash",
//                    LineWeight = 2,
//                    OffsetPastOuterEdgesFeet = 0.5,
//                    TagIntersections = false
//                };

//                using (TransactionGroup transactionGroup = new TransactionGroup(document, "Create Matchline Guides"))
//                {
//                    transactionGroup.Start();
//                    using (Transaction transaction = new Transaction(document, "Draw Matchline Reference Lines"))
//                    {
//                        transaction.Start();

//                        IList<ElementId> createdGuideIds = ScopeBoxGuideLineBuilder.BuildGuides(
//                            document, activeView, rowMajorIds, rowCount, columnCount, guideOptions);

//                        if (createdGuideIds != null && createdGuideIds.Count > 0)
//                        {
//                            TaskDialog.Show("Matchline Guides",
//                                $"Created {createdGuideIds.Count} guide line(s) for a {rowCount} × {columnCount} grid.");
//                        }

//                        transaction.Commit();
//                    }
//                    transactionGroup.Assimilate();
//                }

//                return Result.Succeeded;
//            }
//            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
//            {
//                return Result.Cancelled;
//            }
//            catch (Exception ex)
//            {
//                message = ex.ToString();
//                return Result.Failed;
//            }
//        }

//        // ---- Helpers --------------------------------------------------------

//        private static bool IsScopeBox(Element element)
//        {
//            return element?.Category != null
//                && element.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
//        }

//        private static bool TryEnsureSelectionOfScopeBoxes(
//            UIDocument uiDocument,
//            out IList<ElementId> scopeBoxIds,
//            out bool wasCancelled,
//            out string errorMessage)
//        {
//            wasCancelled = false;
//            errorMessage = null;

//            ICollection<ElementId> selected = uiDocument.Selection.GetElementIds();
//            if (selected != null && selected.Count > 0)
//            {
//                List<ElementId> onlyScope = new List<ElementId>();
//                foreach (ElementId id in selected)
//                {
//                    Element e = uiDocument.Document.GetElement(id);
//                    if (IsScopeBox(e)) onlyScope.Add(id);
//                }

//                if (onlyScope.Count > 0)
//                {
//                    scopeBoxIds = onlyScope;
//                    return true;
//                }

//                errorMessage = "Please select one or more Scope Boxes and run the command again.";
//                scopeBoxIds = new List<ElementId>();
//                return false;
//            }

//            errorMessage = "No elements selected. Please select one or more Scope Boxes.";
//            scopeBoxIds = new List<ElementId>();
//            return false;
//        }

//        private sealed class ProjectedRecord
//        {
//            public ElementId Id;
//            public ScopeBoxProperties Props;
//            public double UAlongRight;  // coordinate along seed DirRight2D
//            public double VAlongDown;   // coordinate along seed DirDown2D
//            public int ColumnIndex;     // 0..C-1
//            public int RowIndex;        // 0..R-1
//        }

//        private static double EstimateTypicalStepFromProjected(double[] coordinates)
//        {
//            if (coordinates == null || coordinates.Length < 2)
//                return 0.0;

//            Array.Sort(coordinates);
//            List<double> nearestNeighborGaps = new List<double>(coordinates.Length - 1);
//            for (int i = 1; i < coordinates.Length; ++i)
//                nearestNeighborGaps.Add(Math.Abs(coordinates[i] - coordinates[i - 1]));

//            nearestNeighborGaps.Sort();
//            int mid = nearestNeighborGaps.Count / 2;
//            return nearestNeighborGaps.Count % 2 == 1
//                ? nearestNeighborGaps[mid]
//                : 0.5 * (nearestNeighborGaps[mid - 1] + nearestNeighborGaps[mid]);
//        }

//        /// <summary>
//        /// If rounded indices left accidental gaps (e.g., 0,2,2,3), compress them to 0,1,1,2.
//        /// axis = 'C' (columns by UAlongRight) or 'R' (rows by VAlongDown).
//        /// </summary>
//        private static void NormalizeSparseIndices(List<ProjectedRecord> projected, char axis)
//        {
//            if (axis == 'C')
//            {
//                var unique = projected.Select(x => x.ColumnIndex).Distinct().OrderBy(i => i).ToList();
//                var map = new Dictionary<int, int>(unique.Count);
//                for (int i = 0; i < unique.Count; ++i) map[unique[i]] = i;
//                foreach (var p in projected) p.ColumnIndex = map[p.ColumnIndex];
//            }
//            else
//            {
//                var unique = projected.Select(x => x.RowIndex).Distinct().OrderBy(i => i).ToList();
//                var map = new Dictionary<int, int>(unique.Count);
//                for (int i = 0; i < unique.Count; ++i) map[unique[i]] = i;
//                foreach (var p in projected) p.RowIndex = map[p.RowIndex];
//            }
//        }
//    }
//}


//// ==========================================================================================
///// previous version (incomplete)
//#region Namespaces
//using System;
//using System.Collections.Generic;
//using System.Linq;

//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.UI;
//#endregion

//// Revit 2020-2026
//// ORH – Scope Box Grid (plan-plane tiling, rotation-agnostic)

//namespace RevitAPI_Testing
//{
//    [Transaction(TransactionMode.Manual)]
//    public class Cmd_MatchlineDetailLines : IExternalCommand
//    {
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIDocument uidoc = commandData.Application.ActiveUIDocument;
//            Document doc = uidoc.Document;
//            View view = doc.ActiveView;

//            // check if elements are selected
//            var ok = Utils.TryEnsureSelectionOrPrompt(uidoc, out bool canceled, out string errMessage, out IList<ElementId> ElementIds);
//            if (ok != true)
//                return canceled ? Result.Cancelled : (TaskDialog.Show("Warning", errMessage), Result.Failed).Item2;
//            // proceed with ids

//            // get only scope boxes from selection
//            var ScopeBoxIds = ElementIds.Select(e => doc.GetElement(e))
//                                        .Where(e => IsScopeBox(e))
//                                        .Select(e => e.Id)
//                                        .ToList();
//            if (ScopeBoxIds.Count == 0)
//            {
//                message = "No scope boxes selected.";
//                return Result.Cancelled;
//            }



//            try
//            {

//                using (TransactionGroup transGroup = new TransactionGroup(doc, "Create Matchline Guides"))
//                {
//                    transGroup.Start();

//                    using (Transaction trans_1 = new Transaction(doc, "Matchline Guide Transaction"))
//                    {
//                        trans_1.Start();

//                        var guideOptions = new GuideLineOptions
//                        {
//                            ManageTransactions = false,                     // you’re already inside a Transaction
//                            DeleteExisting = false,                      // clear previous guides of same style in this view
//                            LineStyleName = "Matchline Reference",
//                            LineColor = new Autodesk.Revit.DB.Color(0, 255, 255), // cyan
//                            LinePatternName = "Dash",
//                            LineWeight = 2,
//                            TagIntersections = false
//                        };

//                        //TaskDialog.Show("test", "This would have executed");


//                        IList<ElementId> createdGuideIds = ScopeBoxGuideLineBuilder.BuildGuides(
//                            doc,
//                            doc.ActiveView,
//                            ScopeBoxIds /* from your grid builder, row-major */,
//                            5, //rows,
//                            3, //cols,
//                            guideOptions);


//                        trans_1.Commit();
//                    }

//                    transGroup.Assimilate();
//                }

//                return Result.Succeeded;
//            }
//            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
//            {
//                return Result.Cancelled;
//            }
//            catch (Exception ex)
//            {
//                message = ex.ToString();
//                return Result.Failed;
//            }
//        }

//        private static bool IsScopeBox(Element e)
//           => e?.Category != null && e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));



//    }

//}

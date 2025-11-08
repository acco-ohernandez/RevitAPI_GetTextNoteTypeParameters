#region Namespaces
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;
#endregion


// ============================================================================
//  ScopeBoxGuideLineBuilder
//  - Creates cyan "Matchline Reference" detail lines centered in the overlaps
//    of a rectangular grid of scope boxes at ANY rotation.
//  - Extends each guide exactly to the outer scope-box edges (no overshoot),
//    using true edge intersections in the view’s Right/Up (RU) coordinate frame.
//  - All variable names are verbose and descriptive (no single-letter names).
// ============================================================================

namespace RevitAPI_Testing
{
    /// <summary>
    /// Options for the guide-line builder.
    /// </summary>
    public class GuideLineOptions
    {
        /// <summary>When true, the method wraps work in a TransactionGroup + Transaction.</summary>
        public bool ManageTransactions { get; set; } = true;

        /// <summary>Delete previously created guides (by style name) in this view before drawing new guides.</summary>
        public bool DeleteExisting { get; set; } = true;

        /// <summary>Detail line subcategory (graphics style) to use or create under OST_Lines.</summary>
        public string LineStyleName { get; set; } = "Matchline Reference";

        /// <summary>Projection color of the style (RGB 0-255). Default cyan.</summary>
        public Color LineColor { get; set; } = new Color(0, 255, 255);

        /// <summary>Projection line pattern name. If missing, it will be created (simple dashed).</summary>
        public string LinePatternName { get; set; } = "Dash";

        /// <summary>Projection line weight (view scale dependent). Typical 1–16.</summary>
        public int LineWeight { get; set; } = 2;

        /// <summary>
        /// Extra extension (feet, internal units) added past the outer edges at both ends
        /// of each guide. For example 0.50 extends each end by 6 inches.
        /// </summary>
        public double OffsetPastOuterEdgesFeet { get; set; } = 0.50;

        /// <summary>
        /// Optional per-intersection tagging (not implemented here; keep for future).
        /// </summary>
        public bool TagIntersections { get; set; } = false;
    }

    /// <summary>
    /// Builds cyan dashed “matchline reference” guides centered in scope-box overlaps,
    /// length-clamped to the true outer edges of the grid (works for rotated grids).
    /// </summary>
    public static class ScopeBoxGuideLineBuilder
    {
        // ---------------------------------------------------------------------
        // Public entry – create guides for a rectangular grid of scope boxes
        // ---------------------------------------------------------------------

        public static IList<ElementId> BuildGuides(
            Document document,
            View planView,
            IList<ElementId> scopeBoxIdsRowMajor,
            int rowCount,
            int columnCount,
            GuideLineOptions options = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (planView == null) throw new ArgumentNullException(nameof(planView));
            if (scopeBoxIdsRowMajor == null || scopeBoxIdsRowMajor.Count == 0)
                throw new ArgumentException("No scope box ids supplied.", nameof(scopeBoxIdsRowMajor));
            if (rowCount < 1 || columnCount < 1)
                throw new ArgumentOutOfRangeException("Rows/cols must be >= 1.");

            options = options ?? new GuideLineOptions();

            var createdDetailCurveIds = new List<ElementId>();

            if (options.ManageTransactions)
            {
                using (var transactionGroup = new TransactionGroup(document, "Build Scope Box Guides"))
                {
                    transactionGroup.Start();

                    using (var transaction = new Transaction(document, "Create Guide Lines"))
                    {
                        transaction.Start();

                        GraphicsStyle graphicsStyle = EnsureLineStyle(document,
                            options.LineStyleName, options.LineColor, options.LinePatternName, options.LineWeight);

                        if (options.DeleteExisting)
                            DeleteExistingGuidesInView(document, planView, graphicsStyle, options.LineStyleName);

                        createdDetailCurveIds.AddRange(
                            BuildGuidesInternal(document, planView, scopeBoxIdsRowMajor, rowCount, columnCount,
                                                graphicsStyle, options.OffsetPastOuterEdgesFeet));

                        transaction.Commit();
                    }

                    transactionGroup.Assimilate();
                }
            }
            else
            {
                // Assume caller has already started a transaction.
                GraphicsStyle graphicsStyle = EnsureLineStyle(document,
                    options.LineStyleName, options.LineColor, options.LinePatternName, options.LineWeight);

                if (options.DeleteExisting)
                    DeleteExistingGuidesInView(document, planView, graphicsStyle, options.LineStyleName);

                createdDetailCurveIds.AddRange(
                    BuildGuidesInternal(document, planView, scopeBoxIdsRowMajor, rowCount, columnCount,
                                        graphicsStyle, options.OffsetPastOuterEdgesFeet));
            }

            return createdDetailCurveIds;
        }

        // ---------------------------------------------------------------------
        // Core: load props, compute endpoints by edge intersections, draw curves
        // ---------------------------------------------------------------------

        private static IList<ElementId> BuildGuidesInternal(
            Document document,
            View planView,
            IList<ElementId> scopeBoxIdsRowMajor,
            int rowCount,
            int columnCount,
            GraphicsStyle graphicsStyle,
            double paddingFeet)
        {
            // 1) Rebuild a 2D array of properties [row, col] from row-major list
            var properties2D = new ScopeBoxProperties[rowCount, columnCount];
            int index = 0;
            for (int row = 0; row < rowCount; ++row)
            {
                for (int col = 0; col < columnCount; ++col)
                {
                    Element scopeBoxElement = document.GetElement(scopeBoxIdsRowMajor[index++]);
                    properties2D[row, col] = ScopeBoxInspector.Inspect(scopeBoxElement, planView);
                }
            }

            // View axes used for RU conversions
            XYZ viewRight = planView.RightDirection.Normalize();
            XYZ viewUp = planView.UpDirection.Normalize();

            // Reference world point for RU <-> World conversion (seed center is fine)
            XYZ referenceWorldPoint = properties2D[0, 0].CenterWorld;

            var createdDetailCurveIds = new List<ElementId>();

            // 2) COLUMN guides between columns (c and c+1), for each column gap
            for (int column = 0; column < columnCount - 1; ++column)
            {
                // Use row 0 to pick a representative midline between columns
                XYZ guideOriginWorld =
                    (properties2D[0, column].MidRight + properties2D[0, column + 1].MidLeft) * 0.5;

                // Guide direction is “vertical-ish” (down in view space)
                XYZ guideDirectionUnitWorld = properties2D[0, column].DirDown2D;

                // Outer edges for intersections: TOP from top row, BOTTOM from bottom row
                ScopeBoxProperties topRowBox = properties2D[0, column];
                ScopeBoxProperties bottomRowBox = properties2D[rowCount - 1, column];

                XYZ topEdgeStartWorld = topRowBox.CornerTopLeft;
                XYZ topEdgeEndWorld = topRowBox.CornerTopRight;

                XYZ bottomEdgeStartWorld = bottomRowBox.CornerBottomLeft;
                XYZ bottomEdgeEndWorld = bottomRowBox.CornerBottomRight;

                // Compute endpoints by true edge intersections in RU, padded along guide dir
                ComputeColumnGuideEndpointsByEdgeIntersections(
                    viewRight, viewUp, referenceWorldPoint,
                    guideOriginWorld, guideDirectionUnitWorld,
                    topEdgeStartWorld, topEdgeEndWorld,
                    bottomEdgeStartWorld, bottomEdgeEndWorld,
                    paddingFeet,
                    out XYZ guideStartWorld, out XYZ guideEndWorld);

                // Draw the guide as a DetailCurve and set the graphics style
                Line guideLine = Line.CreateBound(guideStartWorld, guideEndWorld);
                DetailCurve detailCurve = document.Create.NewDetailCurve(planView, guideLine);
                if (detailCurve != null)
                {
                    detailCurve.LineStyle = graphicsStyle;
                    createdDetailCurveIds.Add(detailCurve.Id);
                }
            }

            // 3) ROW guides between rows (r and r+1), for each row gap
            for (int row = 0; row < rowCount - 1; ++row)
            {
                // Use column 0 to pick a representative midline between rows
                XYZ guideOriginWorld =
                    (properties2D[row, 0].MidBottom + properties2D[row + 1, 0].MidTop) * 0.5;

                // Guide direction is “horizontal-ish” (right in view space)
                XYZ guideDirectionUnitWorld = properties2D[row, 0].DirRight2D;

                // Outer edges for intersections: LEFT from leftmost col, RIGHT from rightmost col
                ScopeBoxProperties leftmostBox = properties2D[row, 0];
                ScopeBoxProperties rightmostBox = properties2D[row, columnCount - 1];

                XYZ leftEdgeStartWorld = leftmostBox.CornerTopLeft;
                XYZ leftEdgeEndWorld = leftmostBox.CornerBottomLeft;

                XYZ rightEdgeStartWorld = rightmostBox.CornerTopRight;
                XYZ rightEdgeEndWorld = rightmostBox.CornerBottomRight;

                // Compute endpoints by true edge intersections in RU, padded along guide dir
                ComputeRowGuideEndpointsByEdgeIntersections(
                    viewRight, viewUp, referenceWorldPoint,
                    guideOriginWorld, guideDirectionUnitWorld,
                    leftEdgeStartWorld, leftEdgeEndWorld,
                    rightEdgeStartWorld, rightEdgeEndWorld,
                    paddingFeet,
                    out XYZ guideStartWorld, out XYZ guideEndWorld);

                // Draw the guide as a DetailCurve and set the graphics style
                Line guideLine = Line.CreateBound(guideStartWorld, guideEndWorld);
                DetailCurve detailCurve = document.Create.NewDetailCurve(planView, guideLine);
                if (detailCurve != null)
                {
                    detailCurve.LineStyle = graphicsStyle;
                    createdDetailCurveIds.Add(detailCurve.Id);
                }
            }

            return createdDetailCurveIds;
        }

        // ---------------------------------------------------------------------
        // RU (Right/Up) helpers and robust line/segment intersection
        // ---------------------------------------------------------------------

        private struct RightUpInfiniteLine
        {
            public double GuideOriginR, GuideOriginU;
            public double GuideDirectionR, GuideDirectionU;
            public RightUpInfiniteLine(double originR, double originU, double dirR, double dirU)
            {
                GuideOriginR = originR; GuideOriginU = originU;
                GuideDirectionR = dirR; GuideDirectionU = dirU;
            }
        }

        private static void WorldToRightUp(
            XYZ worldPoint, XYZ viewRight, XYZ viewUp,
            out double rightCoord, out double upCoord)
        {
            rightCoord = worldPoint.DotProduct(viewRight);
            upCoord = worldPoint.DotProduct(viewUp);
        }

        private static XYZ RightUpToWorld(
            double rightCoord, double upCoord,
            XYZ viewRight, XYZ viewUp,
            XYZ referenceWorldPoint)
        {
            double referenceR = referenceWorldPoint.DotProduct(viewRight);
            double referenceU = referenceWorldPoint.DotProduct(viewUp);
            return referenceWorldPoint
                 + viewRight.Multiply(rightCoord - referenceR)
                 + viewUp.Multiply(upCoord - referenceU);
        }

        private static bool TryIntersectInfiniteRUWithSegmentRU(
            RightUpInfiniteLine ruInfiniteLine,
            double segmentStartR, double segmentStartU,
            double segmentEndR, double segmentEndU,
            out double intersectionR, out double intersectionU,
            double parallelTolerance = 1e-12,
            double segmentTolerance = 1e-9)
        {
            // Solve the 2x2:
            // [ dirR  -(R2-R1) ] [ t ] = [ R1 - R0 ]
            // [ dirU  -(U2-U1) ] [ s ]   [ U1 - U0 ]
            double a11 = ruInfiniteLine.GuideDirectionR;
            double a12 = -(segmentEndR - segmentStartR);
            double a21 = ruInfiniteLine.GuideDirectionU;
            double a22 = -(segmentEndU - segmentStartU);

            double determinant = a11 * a22 - a12 * a21;
            if (Math.Abs(determinant) < parallelTolerance)
            {
                intersectionR = intersectionU = 0.0;
                return false; // parallel
            }

            double rhs1 = segmentStartR - ruInfiniteLine.GuideOriginR;
            double rhs2 = segmentStartU - ruInfiniteLine.GuideOriginU;

            double inverseDeterminant = 1.0 / determinant;
            double t = (rhs1 * a22 - a12 * rhs2) * inverseDeterminant;
            double s = (-rhs1 * a21 + a11 * rhs2) * inverseDeterminant;

            intersectionR = ruInfiniteLine.GuideOriginR + t * ruInfiniteLine.GuideDirectionR;
            intersectionU = ruInfiniteLine.GuideOriginU + t * ruInfiniteLine.GuideDirectionU;

            return (s >= -segmentTolerance && s <= 1.0 + segmentTolerance);
        }

        /// <summary>
        /// Column guide endpoints by intersecting the guide (vertical-ish) with
        /// the true TOP and BOTTOM edges of the grid; then add padding along guide.
        /// </summary>
        private static void ComputeColumnGuideEndpointsByEdgeIntersections(
            XYZ viewRight,
            XYZ viewUp,
            XYZ referenceWorldPoint,
            XYZ guideOriginWorld,
            XYZ guideDirectionUnitWorld,
            XYZ topEdgeStartWorld,
            XYZ topEdgeEndWorld,
            XYZ bottomEdgeStartWorld,
            XYZ bottomEdgeEndWorld,
            double paddingAlongGuideFeet,
            out XYZ guideStartWorld,
            out XYZ guideEndWorld)
        {
            // Build RU infinite line for the guide
            WorldToRightUp(guideOriginWorld, viewRight, viewUp,
                out double guideOriginR, out double guideOriginU);

            double guideDirectionR = guideDirectionUnitWorld.DotProduct(viewRight);
            double guideDirectionU = guideDirectionUnitWorld.DotProduct(viewUp);
            var ruGuide = new RightUpInfiniteLine(guideOriginR, guideOriginU, guideDirectionR, guideDirectionU);

            // Convert edges to RU
            WorldToRightUp(topEdgeStartWorld, viewRight, viewUp, out double topEdgeStartR, out double topEdgeStartU);
            WorldToRightUp(topEdgeEndWorld, viewRight, viewUp, out double topEdgeEndR, out double topEdgeEndU);
            WorldToRightUp(bottomEdgeStartWorld, viewRight, viewUp, out double bottomEdgeStartR, out double bottomEdgeStartU);
            WorldToRightUp(bottomEdgeEndWorld, viewRight, viewUp, out double bottomEdgeEndR, out double bottomEdgeEndU);

            // Intersections (RU)
            if (!TryIntersectInfiniteRUWithSegmentRU(ruGuide,
                topEdgeStartR, topEdgeStartU, topEdgeEndR, topEdgeEndU,
                out double topIntersectionR, out double topIntersectionU))
                throw new InvalidOperationException("Column guide parallel to TOP edge.");

            if (!TryIntersectInfiniteRUWithSegmentRU(ruGuide,
                bottomEdgeStartR, bottomEdgeStartU, bottomEdgeEndR, bottomEdgeEndU,
                out double bottomIntersectionR, out double bottomIntersectionU))
                throw new InvalidOperationException("Column guide parallel to BOTTOM edge.");

            // Back to world
            XYZ topIntersectionWorld =
                RightUpToWorld(topIntersectionR, topIntersectionU, viewRight, viewUp, referenceWorldPoint);
            XYZ bottomIntersectionWorld =
                RightUpToWorld(bottomIntersectionR, bottomIntersectionU, viewRight, viewUp, referenceWorldPoint);

            // Padding strictly along guide direction (both ends)
            XYZ paddingVector = guideDirectionUnitWorld.Multiply(paddingAlongGuideFeet);
            guideStartWorld = topIntersectionWorld - paddingVector;
            guideEndWorld = bottomIntersectionWorld + paddingVector;
        }

        /// <summary>
        /// Row guide endpoints by intersecting the guide (horizontal-ish) with
        /// the true LEFT and RIGHT edges of the grid; then add padding along guide.
        /// </summary>
        private static void ComputeRowGuideEndpointsByEdgeIntersections(
            XYZ viewRight,
            XYZ viewUp,
            XYZ referenceWorldPoint,
            XYZ guideOriginWorld,
            XYZ guideDirectionUnitWorld,
            XYZ leftEdgeStartWorld,
            XYZ leftEdgeEndWorld,
            XYZ rightEdgeStartWorld,
            XYZ rightEdgeEndWorld,
            double paddingAlongGuideFeet,
            out XYZ guideStartWorld,
            out XYZ guideEndWorld)
        {
            // Build RU infinite line for the guide
            WorldToRightUp(guideOriginWorld, viewRight, viewUp,
                out double guideOriginR, out double guideOriginU);

            double guideDirectionR = guideDirectionUnitWorld.DotProduct(viewRight);
            double guideDirectionU = guideDirectionUnitWorld.DotProduct(viewUp);
            var ruGuide = new RightUpInfiniteLine(guideOriginR, guideOriginU, guideDirectionR, guideDirectionU);

            // Convert edges to RU
            WorldToRightUp(leftEdgeStartWorld, viewRight, viewUp, out double leftEdgeStartR, out double leftEdgeStartU);
            WorldToRightUp(leftEdgeEndWorld, viewRight, viewUp, out double leftEdgeEndR, out double leftEdgeEndU);
            WorldToRightUp(rightEdgeStartWorld, viewRight, viewUp, out double rightEdgeStartR, out double rightEdgeStartU);
            WorldToRightUp(rightEdgeEndWorld, viewRight, viewUp, out double rightEdgeEndR, out double rightEdgeEndU);

            // Intersections (RU)
            if (!TryIntersectInfiniteRUWithSegmentRU(ruGuide,
                leftEdgeStartR, leftEdgeStartU, leftEdgeEndR, leftEdgeEndU,
                out double leftIntersectionR, out double leftIntersectionU))
                throw new InvalidOperationException("Row guide parallel to LEFT edge.");

            if (!TryIntersectInfiniteRUWithSegmentRU(ruGuide,
                rightEdgeStartR, rightEdgeStartU, rightEdgeEndR, rightEdgeEndU,
                out double rightIntersectionR, out double rightIntersectionU))
                throw new InvalidOperationException("Row guide parallel to RIGHT edge.");

            // Back to world
            XYZ leftIntersectionWorld =
                RightUpToWorld(leftIntersectionR, leftIntersectionU, viewRight, viewUp, referenceWorldPoint);
            XYZ rightIntersectionWorld =
                RightUpToWorld(rightIntersectionR, rightIntersectionU, viewRight, viewUp, referenceWorldPoint);

            // Padding strictly along guide direction (both ends)
            XYZ paddingVector = guideDirectionUnitWorld.Multiply(paddingAlongGuideFeet);
            guideStartWorld = leftIntersectionWorld - paddingVector;
            guideEndWorld = rightIntersectionWorld + paddingVector;
        }

        // ---------------------------------------------------------------------
        // Style utilities (must be called inside an open Transaction)
        // ---------------------------------------------------------------------

        private static GraphicsStyle EnsureLineStyle(
            Document document,
            string styleName,
            Color color,
            string patternName,
            int weight)
        {
            // Parent category = Lines
            Category linesCategory = document.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

            // Try to find existing subcategory
            Category subCategory = linesCategory.SubCategories.Cast<Category>()
                .FirstOrDefault(c => c.Name.Equals(styleName, StringComparison.OrdinalIgnoreCase));

            if (subCategory == null)
            {
                subCategory = document.Settings.Categories.NewSubcategory(linesCategory, styleName);
            }

            // Color
            subCategory.LineColor = color;

            // Pattern
            LinePatternElement pattern = new FilteredElementCollector(document)
                .OfClass(typeof(LinePatternElement))
                .Cast<LinePatternElement>()
                .FirstOrDefault(p => p.Name.Equals(patternName, StringComparison.OrdinalIgnoreCase));

            if (pattern == null)
            {
                // Create a simple dashed pattern
                var simplePattern = new LinePattern(patternName);
                simplePattern.SetSegments(new List<LinePatternSegment>
                {
                    new LinePatternSegment(LinePatternSegmentType.Dash, 1/12.0), // 1" dash
                    new LinePatternSegment(LinePatternSegmentType.Space, 1/24.0) // 1/2" space
                });
                pattern = LinePatternElement.Create(document, simplePattern);
            }

            // Assign projection weight/pattern via GraphicsStyle
            GraphicsStyle graphicsStyle = subCategory.GetGraphicsStyle(GraphicsStyleType.Projection);
            subCategory.SetLineWeight(weight, GraphicsStyleType.Projection);
            subCategory.SetLinePatternId(pattern.Id, GraphicsStyleType.Projection);

            return graphicsStyle;
        }

        private static void DeleteExistingGuidesInView(
            Document document,
            View view,
            GraphicsStyle styleToRemove,
            string styleNameMarker)
        {
            // Remove any CurveElement (includes DetailCurve) in THIS VIEW whose style matches the target
            var toDelete = new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(CurveElement))
                .Cast<CurveElement>()
                .Where(c =>
                {
                    GraphicsStyle s = c.LineStyle as GraphicsStyle;
                    return s != null && s.Name.Equals(styleNameMarker, StringComparison.OrdinalIgnoreCase);
                })
                .Select(e => e.Id)
                .ToList();

            if (toDelete.Count > 0)
                document.Delete(toDelete);
        }
    }
}


// ============================================
// Archive of previous test code 


// ORH – Revit API Testing – ScopeBoxGuideLineBuilder

//#region Namespaces
//using System;
//using System.Collections.Generic;
//using System.Linq;

//using Autodesk.Revit.DB;
//#endregion

//// ============================================================================
//// Revit 2020–2026
//// ORH – ScopeBoxGuideLineBuilder
//// - Given a rectangular grid of scope boxes (ids + rows/cols), draws Detail Lines
////   down the centers of the overlap bands (vertical & horizontal) in a plan/RCP view.
//// - Rotation-proof: uses real geometry midpoints from ScopeBoxInspector (curve-first).
//// - Safe styling: resolves/creates a dedicated line style "Matchline Reference"
////   and can optionally set color, weight, and pattern.
//// - Reusable: independent of the grid-creation code. Call from any command.
//// ============================================================================

//namespace RevitAPI_Testing
//{
//    /// <summary>
//    /// Options controlling how guide (reference) lines are created and styled.
//    /// </summary>
//    public class GuideLineOptions
//    {
//        /// <summary>When true, this builder will wrap its work in a TransactionGroup + single Transaction.</summary>
//        public bool ManageTransactions { get; set; } = true;

//        /// <summary>When true, delete existing guide lines in the same view that use our marker/style before creating new ones.</summary>
//        public bool DeleteExisting { get; set; } = false;

//        /// <summary>Line style (subcategory under “Lines”) used for the guides.</summary>
//        public string LineStyleName { get; set; } = "Matchline Reference";

//        /// <summary>Optional color to apply to the style (null = leave as-is).</summary>
//        public Color LineColor { get; set; } = null;

//        /// <summary>Optional projection line weight to apply to the style (null = leave as-is).</summary>
//        public int? LineWeight { get; set; } = null;

//        /// <summary>Optional line pattern name to apply to the style (null = leave as-is).</summary>
//        public string LinePatternName { get; set; } = null;

//        /// <summary>If true, create/update the style automatically. If false and style not found, falls back to default Lines style.</summary>
//        public bool EnsureOrUpdateLineStyle { get; set; } = true;

//        /// <summary>Create tiny QA text at each vertical×horizontal guide intersection (e.g., R2C3).</summary>
//        public bool TagIntersections { get; set; } = false;

//        /// <summary>Label size in internal feet when TagIntersections is true (default ~ 1/8").</summary>
//        public double TagTextHeight { get; set; } = 1.0 / 96.0;

//        /// <summary>Numerical tolerance for bucketing rows/cols by RU coordinates (internal feet).</summary>
//        public double Tolerance { get; set; } = 1e-6;

//        /// <summary>Marker written to the ALL_MODEL_INSTANCE_COMMENTS of created curves to enable safe cleanup.</summary>
//        public string Marker { get; set; } = "[ScopeBoxGridGuides]";
//    }

//    /// <summary>
//    /// Builds vertical and horizontal detail lines centered on the overlap bands of a scope-box grid.
//    /// </summary>
//    public static class ScopeBoxGuideLineBuilder
//    {
//        /// <summary>
//        /// Create guide (reference) Detail Lines for a rectangular grid of scope boxes.
//        /// The method infers row/column ordering by sorting boxes in the active view (Up/Right).
//        /// </summary>
//        /// <param name="doc">Owner document.</param>
//        /// <param name="planView">Plan or RCP view in which the guides will be created.</param>
//        /// <param name="scopeBoxIds">All scope box ids forming the grid.</param>
//        /// <param name="rows">Row count (>=1).</param>
//        /// <param name="cols">Column count (>=1).</param>
//        /// <param name="options">Styling and behavior options.</param>
//        /// <returns>Ids of created DetailCurves (guides). Text notes, if any, are not included.</returns>
//        public static IList<ElementId> BuildGuides(
//            Document doc,
//            View planView,
//            IList<ElementId> scopeBoxIds,
//            int rows,
//            int cols,
//            GuideLineOptions options = null)
//        {
//            if (doc == null) throw new ArgumentNullException(nameof(doc));
//            if (planView == null) throw new ArgumentNullException(nameof(planView));
//            if (planView.ViewType != ViewType.FloorPlan && planView.ViewType != ViewType.CeilingPlan)
//                throw new InvalidOperationException("Guide lines can only be created in a plan/ceiling plan view.");
//            if (scopeBoxIds == null || scopeBoxIds.Count == 0)
//                throw new ArgumentException("No scope boxes were provided.", nameof(scopeBoxIds));
//            if (rows < 1 || cols < 1)
//                throw new ArgumentOutOfRangeException("rows/cols must be >= 1.");
//            if (scopeBoxIds.Count < rows * cols)
//                throw new ArgumentException("The number of scope boxes is less than rows * cols.", nameof(scopeBoxIds));

//            if (options == null) options = new GuideLineOptions();

//            // Inspect all boxes and prepare an RU-sorted grid (r,c) → props
//            var propsList = scopeBoxIds
//                .Select(id => doc.GetElement(id))
//                .Where(e => e != null)
//                .Select(e => ScopeBoxInspector.Inspect(e, planView))
//                .ToList();

//            if (propsList.Count < rows * cols)
//                throw new InvalidOperationException("Failed to inspect all scope boxes for the grid.");

//            // Use view frame for ordering (Right/Up unit vectors)
//            XYZ viewRight = propsList[0].ViewRight;
//            XYZ viewUp = propsList[0].ViewUp;
//            double tol = Math.Max(1e-9, options.Tolerance);

//            // Bucket scope boxes into rows by their Up scalar, then into columns by Right scalar
//            var grid = BucketToGrid(propsList, rows, cols, viewRight, viewUp, tol);

//            // Resolve or create the line style (and optionally update color/weight/pattern)
//            GraphicsStyle guideStyle = ResolveOrCreateGuideStyle(doc, options);

//            var created = new List<ElementId>();

//            Action work = () =>
//            {
//                if (options.DeleteExisting)
//                    DeleteExistingGuidesInView(doc, planView, guideStyle, options.Marker);

//                // ---------------- Vertical guides (between columns 0..cols-2) ----------------
//                for (int c = 0; c < cols - 1; ++c)
//                {
//                    // For a vertical boundary at column index c, build a line through the
//                    // centers of the overlaps in the topmost and bottommost rows.
//                    ScopeBoxProperties topLeft = grid[0, c];
//                    ScopeBoxProperties topRight = grid[0, c + 1];
//                    ScopeBoxProperties bottomLeft = grid[rows - 1, c];
//                    ScopeBoxProperties bottomRight = grid[rows - 1, c + 1];

//                    // Center of overlap in the top row and bottom row (average adjacent edge midpoints)
//                    XYZ topCenter = (topLeft.MidRight + topRight.MidLeft) * 0.5;
//                    XYZ bottomCenter = (bottomLeft.MidRight + bottomRight.MidLeft) * 0.5;

//                    Line vertical = Line.CreateBound(topCenter, bottomCenter);
//                    DetailCurve dc = doc.Create.NewDetailCurve(planView, vertical);
//                    TrySetLineStyleAndMark(dc, guideStyle, options.Marker);
//                    created.Add(dc.Id);
//                }

//                // ---------------- Horizontal guides (between rows 0..rows-2) ----------------
//                for (int r = 0; r < rows - 1; ++r)
//                {
//                    ScopeBoxProperties leftTop = grid[r, 0];
//                    ScopeBoxProperties leftBottom = grid[r + 1, 0];
//                    ScopeBoxProperties rightTop = grid[r, cols - 1];
//                    ScopeBoxProperties rightBottom = grid[r + 1, cols - 1];

//                    // Center of overlap in the leftmost and rightmost columns
//                    XYZ leftCenter = (leftTop.MidBottom + leftBottom.MidTop) * 0.5;
//                    XYZ rightCenter = (rightTop.MidBottom + rightBottom.MidTop) * 0.5;

//                    Line horizontal = Line.CreateBound(leftCenter, rightCenter);
//                    DetailCurve dc = doc.Create.NewDetailCurve(planView, horizontal);
//                    TrySetLineStyleAndMark(dc, guideStyle, options.Marker);
//                    created.Add(dc.Id);
//                }

//                // ---------------- Optional intersection tags ----------------
//                if (options.TagIntersections)
//                {
//                    for (int r = 0; r < rows - 1; ++r)
//                    {
//                        for (int c = 0; c < cols - 1; ++c)
//                        {
//                            XYZ topCenter = (grid[r, c].MidRight + grid[r, c + 1].MidLeft) * 0.5;
//                            XYZ bottomCenter = (grid[r + 1, c].MidRight + grid[r + 1, c + 1].MidLeft) * 0.5;
//                            XYZ leftCenter = (grid[r, c].MidBottom + grid[r + 1, c].MidTop) * 0.5;
//                            XYZ rightCenter = (grid[r, c + 1].MidBottom + grid[r + 1, c + 1].MidTop) * 0.5;

//                            // Average the four overlap-centers to get the visual crossing
//                            XYZ inter = new XYZ(
//                                0.25 * (topCenter.X + bottomCenter.X + leftCenter.X + rightCenter.X),
//                                0.25 * (topCenter.Y + bottomCenter.Y + leftCenter.Y + rightCenter.Y),
//                                0.25 * (topCenter.Z + bottomCenter.Z + leftCenter.Z + rightCenter.Z));

//                            // Pick a base TextNoteType
//                            TextNoteType baseType = new FilteredElementCollector(doc)
//                                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();
//                            if (baseType == null) continue;

//                            // If a size is requested, ensure a type at that size (in POINTS, not feet)
//                            ElementId typeId = baseType.Id;
//                            if (options.TagTextHeight > 0)
//                            {
//                                TextNoteType sizedType = EnsureTextNoteTypeWithSize(doc, baseType, options.TagTextHeight /* points */);
//                                if (sizedType != null) typeId = sizedType.Id;
//                            }

//#if REVIT2020
//            TextNote tn = TextNote.Create(doc, planView.Id, inter, $"R{r + 2}C{c + 2}", typeId);
//#else
//                            var tno = new TextNoteOptions(typeId) { HorizontalAlignment = HorizontalTextAlignment.Center };
//                            TextNote tn = TextNote.Create(doc, planView.Id, inter, $"R{r + 2}C{c + 2}", tno);
//#endif
//                            // Mark for future cleanup
//                            Parameter p = tn.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
//                            if (p != null && !p.IsReadOnly && !string.IsNullOrEmpty(options.Marker))
//                                p.Set(options.Marker);
//                        }
//                    }
//                }


//            };

//            if (options.ManageTransactions)
//            {
//                using (var tg = new TransactionGroup(doc, "Create Matchline Reference Guides"))
//                {
//                    tg.Start();
//                    using (var tx = new Transaction(doc, "Draw Guide Lines"))
//                    {
//                        tx.Start();
//                        work();
//                        tx.Commit();
//                    }
//                    tg.Assimilate();
//                }
//            }
//            else
//            {
//                work();
//            }

//            return created;
//        }

//        // ============================== helpers ==============================

//        /// <summary>
//        /// Map the inspected scope boxes into a 2D array [row, col] ordered by Up (top→down) and Right (left→right).
//        /// </summary>
//        private static ScopeBoxProperties[,] BucketToGrid(
//            List<ScopeBoxProperties> propsList,
//            int rows,
//            int cols,
//            XYZ viewRight,
//            XYZ viewUp,
//            double tol)
//        {
//            // Build RU scalars for ordering
//            var items = propsList.Select(p => new
//            {
//                Props = p,
//                R = p.CenterWorld.DotProduct(viewRight),
//                U = p.CenterWorld.DotProduct(viewUp)
//            }).ToList();

//            // Group into rows by U (top to bottom)
//            var rowBands = ClusterBy(items, x => x.U, descending: true, desiredBands: rows, tol: tol);
//            if (rowBands.Count != rows)
//                throw new InvalidOperationException("Failed to cluster scope boxes into the requested number of rows.");

//            var grid = new ScopeBoxProperties[rows, cols];

//            for (int r = 0; r < rows; ++r)
//            {
//                // Within each row, sort by R (left to right) and cluster into 'cols' groups
//                var rowItems = rowBands[r].OrderBy(x => x.R).ToList();
//                var colBands = ClusterBy(rowItems, x => x.R, descending: false, desiredBands: cols, tol: tol);
//                if (colBands.Count != cols)
//                    throw new InvalidOperationException($"Failed to cluster row {r + 1} into the requested number of columns.");

//                for (int c = 0; c < cols; ++c)
//                {
//                    // Expect exactly one per cell; take the first
//                    var cell = colBands[c].FirstOrDefault();
//                    if (cell == null) throw new InvalidOperationException($"Missing scope box at R{r + 1}C{c + 1}.");
//                    grid[r, c] = cell.Props;
//                }
//            }

//            return grid;
//        }

//        /// <summary>
//        /// Cluster a list into N bands along a scalar selector with tolerance.
//        /// </summary>
//        private static List<List<T>> ClusterBy<T>(
//            List<T> items,
//            Func<T, double> scalar,
//            bool descending,
//            int desiredBands,
//            double tol)
//        {
//            var ordered = descending
//                ? items.OrderByDescending(scalar).ToList()
//                : items.OrderBy(scalar).ToList();

//            var bands = new List<List<T>>();
//            List<T> current = null;
//            double? last = null;

//            foreach (var it in ordered)
//            {
//                double s = scalar(it);
//                if (current == null || (last.HasValue && Math.Abs(s - last.Value) > tol))
//                {
//                    current = new List<T>();
//                    bands.Add(current);
//                }
//                current.Add(it);
//                last = s;
//            }

//            // If clustering produced too many/too few bands, try to merge/split roughly to desired count.
//            // (In well-formed grids from our builder this is rarely needed.)
//            if (bands.Count != desiredBands)
//            {
//                // Simple fallback: repartition by count
//                var flat = ordered.ToList();
//                bands.Clear();
//                for (int i = 0; i < desiredBands; ++i)
//                {
//                    int start = (int)Math.Round(i * flat.Count / (double)desiredBands);
//                    int end = (int)Math.Round((i + 1) * flat.Count / (double)desiredBands);
//                    bands.Add(flat.GetRange(start, Math.Max(0, end - start)));
//                }
//            }

//            return bands;
//        }

//        /// <summary>
//        /// Resolve (and optionally create/update) the "Matchline Reference" line style and return its GraphicsStyle.
//        /// </summary>
//        private static GraphicsStyle ResolveOrCreateGuideStyle(Document doc, GuideLineOptions options)
//        {
//            // Base "Lines" category
//            Category linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
//            if (linesCat == null)
//                throw new InvalidOperationException("Cannot find Lines category.");

//            // Try to find existing subcategory by name
//            Category sub = null;
//            foreach (Category c in linesCat.SubCategories)
//            {
//                if (c != null && string.Equals(c.Name, options.LineStyleName ?? "", StringComparison.OrdinalIgnoreCase))
//                {
//                    sub = c;
//                    break;
//                }
//            }

//            // Create if missing and allowed
//            if (sub == null && options.EnsureOrUpdateLineStyle)
//            {
//                sub = doc.Settings.Categories.NewSubcategory(linesCat, options.LineStyleName ?? "Matchline Reference");
//            }

//            // If still null, fall back to the main Lines category style
//            Category styleCat = sub ?? linesCat;

//            // Optionally update style properties (affects all elements using the style)
//            if (options.EnsureOrUpdateLineStyle)
//            {
//                if (options.LineColor != null)
//                    styleCat.LineColor = options.LineColor;

//                if (options.LineWeight.HasValue)
//                    styleCat.SetLineWeight(options.LineWeight.Value, GraphicsStyleType.Projection);

//                if (!string.IsNullOrWhiteSpace(options.LinePatternName))
//                {
//                    LinePatternElement lpe = new FilteredElementCollector(doc)
//                        .OfClass(typeof(LinePatternElement))
//                        .Cast<LinePatternElement>()
//                        .FirstOrDefault(p => p.Name.Equals(options.LinePatternName, StringComparison.OrdinalIgnoreCase));
//                    if (lpe != null)
//                        styleCat.SetLinePatternId(lpe.Id, GraphicsStyleType.Projection);
//                }
//            }

//            GraphicsStyle gs = styleCat.GetGraphicsStyle(GraphicsStyleType.Projection);
//            if (gs == null)
//                throw new InvalidOperationException("Failed to resolve a GraphicsStyle for the guide line style.");

//            return gs;
//        }


//        /// <summary>
//        /// Delete existing guide lines in the view that either use our style or carry our marker
//        /// in Instance Comments. Must query as CurveElement (DetailCurve is not valid for OfClass).
//        /// </summary>
//        private static void DeleteExistingGuidesInView(Document doc, View view, GraphicsStyle guideStyle, string marker)
//        {
//            var toDelete = new List<ElementId>();

//            // Collect curve elements in this view (detail lines are CurveElement + ViewSpecific)
//            var curvesInView = new FilteredElementCollector(doc, view.Id)
//                .OfClass(typeof(CurveElement))
//                .Cast<CurveElement>()
//                .Where(ce => ce.ViewSpecific && ce.OwnerViewId == view.Id);

//            // Match by style
//            if (guideStyle != null)
//            {
//                var byStyle = curvesInView
//                    .Where(ce => ce.LineStyle != null && ce.LineStyle.Id == guideStyle.Id)
//                    .Select(ce => ce.Id);
//                toDelete.AddRange(byStyle);
//            }

//            // Match by marker in Instance Comments
//            if (!string.IsNullOrEmpty(marker))
//            {
//                foreach (var ce in curvesInView)
//                {
//                    Parameter p = ce.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
//                    if (p != null)
//                    {
//                        string s = p.AsString();
//                        if (!string.IsNullOrEmpty(s) && s.Contains(marker))
//                            toDelete.Add(ce.Id);
//                    }
//                }
//            }

//            if (toDelete.Count > 0)
//                doc.Delete(toDelete);
//        }


//        private static void TrySetLineStyleAndMark(DetailCurve dc, GraphicsStyle style, string marker)
//        {
//            if (dc == null) return;
//            try { dc.LineStyle = style; } catch { /* some styles may be read-only */ }

//            if (!string.IsNullOrEmpty(marker))
//            {
//                Parameter p = dc.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
//                if (p != null && !p.IsReadOnly) p.Set(marker);
//            }
//        }


//        /// <summary>
//        /// Returns a TextNoteType whose TEXT_SIZE equals <paramref name="sizePoints"/> (paper points).
//        /// If none exists, duplicates the <paramref name="baseType"/> and sets its TEXT_SIZE.
//        /// </summary>
//        private static TextNoteType EnsureTextNoteTypeWithSize(Document doc, TextNoteType baseType, double sizePoints)
//        {
//            // 1) Try to find an existing type with the same size (within tolerance)
//            const double tolPts = 1e-6;
//            foreach (TextNoteType t in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)))
//            {
//                Parameter sz = t.get_Parameter(BuiltInParameter.TEXT_SIZE);
//                if (sz != null && Math.Abs(sz.AsDouble() - sizePoints) < tolPts)
//                    return t;
//            }

//            // 2) Duplicate base type and set TEXT_SIZE (points)
//            TextNoteType dup = baseType.Duplicate($"{baseType.Name} {sizePoints:0.##}pt") as TextNoteType;
//            if (dup != null)
//            {
//                Parameter sz = dup.get_Parameter(BuiltInParameter.TEXT_SIZE);
//                if (sz != null && !sz.IsReadOnly)
//                    sz.Set(sizePoints); // POINTS (paper), not model units
//            }
//            return dup;
//        }

//    }

//}

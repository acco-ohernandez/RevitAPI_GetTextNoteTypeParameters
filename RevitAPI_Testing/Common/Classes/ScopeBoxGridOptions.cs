using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;

namespace RevitAPI_Testing
{
    /// <summary>
    /// Builds an N×M grid of scope boxes from a selected “seed” scope box in a plan/RCP view.
    /// The builder is rotation-proof and overlap-aware: horizontal/vertical steps are computed
    /// from the seed’s actual edge-midpoint spans (MidLeft→MidRight, MidTop→MidBottom), projected
    /// to the view plane. This guarantees that copies “just touch” at overlap=0 and interpenetrate
    /// by the requested amount for positive overlaps, regardless of the seed’s rotation.
    /// <para>
    /// Usage: call <see cref="BuildGrid(Autodesk.Revit.DB.Document, Autodesk.Revit.DB.View, Autodesk.Revit.DB.Element, int, int, double, double, ScopeBoxGridOptions)"/>
    /// with rows/cols and overlaps (internal feet). Steps are applied as a single translation
    /// from the original for each cell (no chained copies), eliminating cumulative drift.
    /// </para>
    /// <para>
    /// Options: <see cref="ScopeBoxGridOptions"/> controls transaction management (builder-owned or caller-owned),
    /// optional naming (BaseName/NameFormatter written to Instance Comments), inclusion of the original id in results,
    /// and a tiny zero-gap nudge to defeat floating-point hairlines when overlap≈0.
    /// </para>
    /// <para>
    /// Notes:
    /// • The original scope box remains at (row=1, col=1) and is not moved.  
    /// • Overlaps are measured along the corresponding axis: +value = interpenetrate, 0 = touch, −value = gap.  
    /// • Requires a plan/ceiling plan context; no rotations are performed—only translations.  
    /// • Pin state is not altered (add unpin/re-pin externally if needed).
    /// </para>
    /// </summary>

    public class ScopeBoxGridOptions
    {
        /// <summary>Include the original scope box id as the first item in the returned list.</summary>
        public bool IncludeOriginalInResult { get; set; } = true;

        /// <summary>Optional base name used for renaming copies (e.g., "Scope Box"). Null/empty = no renaming.</summary>
        public string BaseName { get; set; }

        /// <summary>Optional custom formatter: given (row, col) -> name. If null, uses "BaseName R{r}C{c}".</summary>
        public Func<int, int, string> NameFormatter { get; set; }

        /// <summary>Set to true to write the name into Instance Comments (if writable).</summary>
        public bool WriteNameToComments { get; set; } = true;

        /// <summary>When true, wraps work in a TransactionGroup + single Transaction.</summary>
        public bool ManageTransactions { get; set; } = true;

        /// <summary>Apply a tiny nudge when overlap≈0 to avoid hairline gaps due to floating precision.</summary>
        public bool ApplyZeroGapNudge { get; set; } = true;

        /// <summary>Size of the nudge (internal feet). Default ~0.0003 mm.</summary>
        public double ZeroGapNudge { get; set; } = 1e-6;
    }

    /// <summary>
    /// Builds an N×M (rows×columns) grid of scope boxes from a picked seed. Rotation-proof, overlap-aware.
    /// </summary>
    public static class ScopeBoxGridBuilder
    {
        /// <summary>
        /// Creates a grid of copies using the seed scope box as the top-left cell (R1C1).
        /// Uses midpoint-to-midpoint span vectors for exact spacing at any rotation.
        /// </summary>
        /// <param name="doc">Owner document.</param>
        /// <param name="view">Active plan/rcp view that defines the 2D frame.</param>
        /// <param name="seedScopeBox">The seed scope box element (original stays put).</param>
        /// <param name="rows">Row count (>=1).</param>
        /// <param name="cols">Column count (>=1).</param>
        /// <param name="overlapX">Horizontal overlap along the width axis (internal feet). Positive = interpenetrate.</param>
        /// <param name="overlapY">Vertical overlap along the height axis (internal feet). Positive = interpenetrate.</param>
        /// <param name="options">Grid build options (transactions, naming, etc.).</param>
        /// <returns>List of created element ids; original first if IncludeOriginalInResult=true.</returns>
        public static IList<ElementId> BuildGrid(
            Document doc,
            View view,
            Element seedScopeBox,
            int rows,
            int cols,
            double overlapX,
            double overlapY,
            ScopeBoxGridOptions options = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc)); // if doc is null, view/element are invalid too
            if (view == null) throw new ArgumentNullException(nameof(view)); // if view is null, element is invalid too
            if (seedScopeBox == null) throw new ArgumentNullException(nameof(seedScopeBox)); // if element is null, doc/view are invalid too
            if (rows < 1 || cols < 1) throw new ArgumentOutOfRangeException("rows/cols must be >= 1."); // if rows or cols < 1, no grid to build

            if (options == null) // if no options provided, use defaults
            {
                options = new ScopeBoxGridOptions();
            }


            // Inspect seed (geometry-first inspector you already have)
            ScopeBoxProperties props = ScopeBoxInspector.Inspect(seedScopeBox, view);

            // Precompute column and row step vectors using the same midpoint-span method used by movers
            XYZ colStep = ComputeStepVector(
                fromMidpoint: props.MidLeft, // this is the "from" side of the step
                toMidpoint: props.MidRight, // this is the "to" side of the step
                viewNormal: props.ViewNormal, // this is the view normal for projection into the view plane 
                overlap: overlapX,
                applyNudge: options.ApplyZeroGapNudge,
                nudge: options.ZeroGapNudge);

            // Rows go visually downward; step vector points "down-ish"
            XYZ rowStep = ComputeStepVector(
                fromMidpoint: props.MidTop,
                toMidpoint: props.MidBottom,
                viewNormal: props.ViewNormal,
                overlap: overlapY,
                applyNudge: options.ApplyZeroGapNudge,
                nudge: options.ZeroGapNudge);

            var result = new List<ElementId>(rows * cols);

            // Transaction management
            if (options.ManageTransactions)
            {
                using (var tg = new TransactionGroup(doc, "Create Scope Box Grid"))
                {
                    tg.Start();
                    using (var tx = new Transaction(doc, "Place Scope Box Grid"))
                    {
                        tx.Start();
                        PlaceGrid(doc, props, rows, cols, colStep, rowStep, options, result);
                        tx.Commit();
                    }
                    tg.Assimilate();
                }
            }
            else
            {
                // Assume caller manages transactions
                PlaceGrid(doc, props, rows, cols, colStep, rowStep, options, result);
            }

            return result;
        }

        // ------------------------- Core placement -------------------------

        private static void PlaceGrid(
            Document doc,
            ScopeBoxProperties seedProps,
            int rows,
            int cols,
            XYZ colStep,
            XYZ rowStep,
            ScopeBoxGridOptions options,
            List<ElementId> outIds)
        {
            // Optionally add the original as R1C1
            if (options.IncludeOriginalInResult)
                outIds.Add(seedProps.Id);

            // Fill in row-major order, skipping R1C1 (original) at (0,0)
            for (int r = 0; r < rows; ++r)
            {
                for (int c = 0; c < cols; ++c)
                {
                    bool isOriginalCell = (r == 0 && c == 0);
                    if (isOriginalCell) continue;

                    // Single translation from the original = no accumulated drift
                    XYZ delta = colStep.Multiply(c).Add(rowStep.Multiply(r));

                    ICollection<ElementId> copyIds = ElementTransformUtils.CopyElement(doc, seedProps.Id, delta);
                    if (copyIds == null || copyIds.Count == 0) continue;

                    ElementId newId = copyIds.First();
                    outIds.Add(newId);

                    // Optional naming
                    if (!string.IsNullOrWhiteSpace(options.BaseName) && options.WriteNameToComments)
                    {
                        string name = options.NameFormatter != null
                            ? options.NameFormatter(r + 1, c + 1)
                            : $"{options.BaseName} R{r + 1}C{c + 1}";

                        TryWriteInstanceComments(doc.GetElement(newId), name);
                    }
                }
            }
        }

        // ----------------------- Helpers / Math ---------------------------
        /// <summary>
        /// Computes the precise translation vector between two edge midpoints, constrained to the plan view plane,
        /// then shortens that distance by the requested <paramref name="overlap"/> along the same axis.
        /// A tiny nudge can be applied when overlap≈0 to avoid hairline gaps due to floating precision.
        /// </summary>
        /// <param name="fromMidpoint">
        /// The midpoint of the source edge (world coordinates).
        /// For columns this is typically MidLeft; for rows this is typically MidTop.
        /// </param>
        /// <param name="toMidpoint">
        /// The midpoint of the target opposite edge (world coordinates).
        /// For columns this is typically MidRight; for rows this is typically MidBottom.
        /// </param>
        /// <param name="viewNormal">
        /// The view’s forward direction (unit or non-unit). The move will be projected to the plane orthogonal to this vector
        /// (i.e., the plan/RCP plane), ensuring a strictly 2D translation in the current view.
        /// </param>
        /// <param name="overlap">
        /// Requested overlap distance in internal feet. Positive = interpenetrate; 0 = just touch; negative = gap.
        /// The overlap is applied along the same axis defined by <paramref name="fromMidpoint"/>→<paramref name="toMidpoint"/>.
        /// </param>
        /// <param name="applyNudge">
        /// If true, apply a tiny extra move when |overlap| is effectively zero to defeat floating-point hairlines.
        /// </param>
        /// <param name="nudge">
        /// The tiny distance (internal feet) added only when overlap≈0. Default is ~1e-6 ft (~0.0003 mm).
        /// </param>
        /// <returns>
        /// A world-space translation vector that, when applied to a copy of the seed scope box, places it so that
        /// the two relevant edges “touch” (or overlap/gap by <paramref name="overlap"/>) exactly in the plan plane,
        /// regardless of rotation.
        /// </returns>
        private static XYZ ComputeStepVector(
            XYZ fromMidpoint,
            XYZ toMidpoint,
            XYZ viewNormal,
            double overlap,
            bool applyNudge,
            double nudge)
        {
            // 1) Normalize the view normal. This defines the view (plan) plane as the plane orthogonal to this vector.
            //    We’ll remove any out-of-plane component to guarantee a pure 2D translation in the current view.
            XYZ normalizedViewNormal = viewNormal.Normalize();

            // 2) Build the raw world-space vector from the “from” edge midpoint to the “to” edge midpoint.
            //    This captures both the direction (axis) and the base edge-to-edge span between the two sides.
            XYZ rawSpanVectorWorld = toMidpoint - fromMidpoint;

            // 3) Project that span into the plan plane by removing any component along the view normal.
            //    This guarantees our movement is planar (no stray Z due to numerical noise or tilted geometry).
            XYZ inPlaneSpanVector =
                rawSpanVectorWorld - normalizedViewNormal.Multiply(rawSpanVectorWorld.DotProduct(normalizedViewNormal));

            // 4) Measure the in-plane span length (this is the exact “touching” distance at overlap = 0).
            double inPlaneSpanLength = inPlaneSpanVector.GetLength();

            //    Safety: if the length is effectively zero, the two midpoints coincide (degenerate or bad geometry).
            if (inPlaneSpanLength <= 1e-12)
                throw new InvalidOperationException("Scope box midpoint span is degenerate (zero in-plane length).");

            // 5) Convert the in-plane span into a unit axis (pure direction of travel).
            XYZ unitMoveAxis = inPlaneSpanVector / inPlaneSpanLength;

            // 6) Compute the final move distance:
            //    - Start from the “touching” distance (= inPlaneSpanLength)
            //    - Subtract the requested overlap (positive overlap shortens the distance → interpenetrate)
            //    - Optionally add a tiny nudge only when overlap is effectively zero to defeat hairline gaps
            //      caused by floating-point rounding in graphics.
            bool isOverlapEffectivelyZero = Math.Abs(overlap) < 1e-9;
            double moveDistance =
                inPlaneSpanLength
                - overlap
                + ((applyNudge && isOverlapEffectivelyZero) ? nudge : 0.0);

            // 7) Return the final translation vector: axis * distance (still world-space, confined to the plan plane).
            return unitMoveAxis.Multiply(moveDistance);
        }

        /// <summary>
        /// Computes the precise step vector between two edge midpoints, projected to the view plane,
        /// then subtracts the requested overlap along the same axis. Applies a tiny nudge when overlap≈0.
        /// </summary>
        private static XYZ ComputeStepVector1(
            XYZ fromMidpoint,
            XYZ toMidpoint,
            XYZ viewNormal,
            double overlap,
            bool applyNudge,
            double nudge)
        {
            // n contains the normalized view normal which means it is perpendicular to the plan plane and has a length of 1.
            XYZ n = viewNormal.Normalize();

            // Exact span vector in world → project into the plan plane
            XYZ span = toMidpoint - fromMidpoint;
            span = span - n.Multiply(span.DotProduct(n));

            double spanLen = span.GetLength();
            if (spanLen <= 1e-12)
                throw new InvalidOperationException("Scope box midpoint span is degenerate.");

            XYZ axis = span / spanLen;

            bool zeroish = Math.Abs(overlap) < 1e-9;
            double move = spanLen - overlap + (applyNudge && zeroish ? nudge : 0.0);

            return axis.Multiply(move);
        }

        private static void TryWriteInstanceComments(Element e, string text)
        {
            if (e == null) return;
            Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p != null && !p.IsReadOnly)
                p.Set(text);
        }
    }
}

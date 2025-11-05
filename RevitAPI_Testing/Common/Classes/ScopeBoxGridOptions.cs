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
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (seedScopeBox == null) throw new ArgumentNullException(nameof(seedScopeBox));
            if (rows < 1 || cols < 1) throw new ArgumentOutOfRangeException("rows/cols must be >= 1.");

            if (options == null)
            {
                options = new ScopeBoxGridOptions();
            }


            // Inspect seed (geometry-first inspector you already have)
            ScopeBoxProperties props = ScopeBoxInspector.Inspect(seedScopeBox, view);

            // Precompute column and row step vectors using the same midpoint-span method used by movers
            XYZ colStep = ComputeStepVector(
                fromMidpoint: props.MidLeft,
                toMidpoint: props.MidRight,
                viewNormal: props.ViewNormal,
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
        /// Computes the precise step vector between two edge midpoints, projected to the view plane,
        /// then subtracts the requested overlap along the same axis. Applies a tiny nudge when overlap≈0.
        /// </summary>
        private static XYZ ComputeStepVector(
            XYZ fromMidpoint,
            XYZ toMidpoint,
            XYZ viewNormal,
            double overlap,
            bool applyNudge,
            double nudge)
        {
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

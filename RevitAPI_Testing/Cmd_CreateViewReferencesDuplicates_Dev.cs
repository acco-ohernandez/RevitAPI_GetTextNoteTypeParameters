// ============================================================================
// Revit 2020–2026
// ORH – View Reference Duplicator (rotation-agnostic, grid-aware)
//
// What this command does (short):
// 1) You pick ONE existing View Reference in the active plan/RCP (the “seed”).
// 2) You select a set of Scope Boxes that form a rectangular grid (any rotation).
// 3) The command infers the grid layout and, for every internal shared edge,
//    places a NEW copy of the seed View Reference:
//       • on each box’s RIGHT edge where a right neighbor exists, and
//       • on each box’s BOTTOM edge where a bottom neighbor exists.
//    That guarantees exactly one tag per shared edge (no doubles).
// 4) Each new tag is moved so the seed’s visual head (its BB center in this view)
//    lands at a computed anchor point on that edge, then rotated so the tag’s
//    “right” axis runs along the shared edge direction. Offsets are view-scale
//    aware so spacing looks consistent at different view scales.
//
// Requirements already in this project:
//   - ScopeBoxInspector.Inspect(Element, View)  → ScopeBoxProperties (DirRight2D, DirDown2D,
//     MidRight, MidBottom, ViewNormal, etc.)
//   - ScopeBoxGridInference.InferGridAndNeighbors(Document, View, IList<ElementId>,
//     out Dictionary<ElementId, GridIndex>, out Dictionary<ElementId, BoxNeighbors>,
//     out int rowCount, out int colCount)
//
// Notes:
//   - If your View Reference family’s insertion point is already the visual head,
//     switch GetElementViewCenter(seed) to GetElementInsertionPoint(seed).
//   - This command is transaction-safe and will not create duplicates on the same edge.
// ============================================================================

#region Namespaces
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
#endregion

namespace RevitAPI_Testing
{
    [Transaction(TransactionMode.Manual)]
    public class Cmd_CreateViewReferencesDuplicates_Dev : IExternalCommand
    {
        // View-scale offsets (internal feet)
        private const double DefaultInsetFeet = 0.75;        // inset into the owning box along the edge normal
        private const double DefaultNormalOffsetFeet = 0.25; // small bump perpendicular to the edge (keeps head off guide)
        private const double AngleTolerance = 1e-6;
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument.Document;
            View planView = document.ActiveView;

            try
            {
                // Guardrails
                if (planView.ViewType != ViewType.FloorPlan && planView.ViewType != ViewType.CeilingPlan)
                {
                    TaskDialog.Show("View References", "Please run this in a plan or ceiling plan view.");
                    return Result.Cancelled;
                }

                // 1) Pick or reuse a seed View Reference in this view
                Element seedViewReference = PickSeedViewReference(uiDocument);
                if (seedViewReference == null)
                {
                    message = "No view reference selected.";
                    return Result.Cancelled;
                }

                // 2) Collect scope boxes: current selection first, otherwise prompt
                IList<Element> scopeBoxElements = GetScopeBoxesFromSelection(uiDocument);
                if (scopeBoxElements.Count == 0)
                    scopeBoxElements = PromptForScopeBoxes(uiDocument);
                if (scopeBoxElements.Count == 0)
                {
                    message = "No scope boxes selected.";
                    return Result.Cancelled;
                }

                // 3) Infer the grid (rotation-agnostic)
                IList<ElementId> selectedScopeBoxIds = scopeBoxElements.Select(e => e.Id).ToList();

                ScopeBoxGridInference.InferGridAndNeighbors(
                    document,
                    planView,
                    selectedScopeBoxIds,
                    out Dictionary<ElementId, ScopeBoxGridInference.GridIndex> indexByElementId,
                    out _ /* neighborsByElementId – not needed here */,
                    out int rowCount,
                    out int colCount);

                if (rowCount <= 0 || colCount <= 0)
                {
                    TaskDialog.Show("View References", "Could not infer a rectangular grid from the selected scope boxes.");
                    return Result.Cancelled;
                }

                // 4) Inspect each selected scope box once (RU axes, midpoints, sizes, etc.)
                List<ScopeBoxProperties> inspectors = scopeBoxElements
                    .Select(sb => ScopeBoxInspector.Inspect(sb, planView))
                    .ToList();

                // Comparer so GridIndex works in dictionaries/sets
                var gridIndexComparer = ScopeBoxGridInference.GridIndexEqualityComparer.Instance;

                // (Row,Col) -> Properties using EXACT indices from inference
                var propsByIndex = inspectors.ToDictionary(
                    p => indexByElementId[p.Id],
                    p => p,
                    gridIndexComparer);

                // Fast membership checks
                var presentIndices = new HashSet<ScopeBoxGridInference.GridIndex>(propsByIndex.Keys, gridIndexComparer);

                // 5) View-scale aware offsets
                double insetIntoBox = ScaleByView(planView, DefaultInsetFeet);
                double perpendicularBump = ScaleByView(planView, DefaultNormalOffsetFeet);

                // 6) Seed “visual head” anchor to move from (center of seed BB in THIS view)
                XYZ seedVisualAnchor = GetElementViewCenter(seedViewReference, planView);

                int placedCount = 0;

                using (var transactionGroup = new TransactionGroup(document, "Duplicate View References"))
                {
                    transactionGroup.Start();

                    using (var transaction = new Transaction(document, "Place View Reference Copies"))
                    {
                        transaction.Start();

                        // ------------------------------------------------------------------
                        // A) Shared edges – place on BOTH sides (same as before)
                        // ------------------------------------------------------------------
                        for (int r = 0; r < rowCount; ++r)
                        {
                            for (int c = 0; c < colCount; ++c)
                            {
                                var here = new ScopeBoxGridInference.GridIndex { Row = r, Col = c };
                                if (!propsByIndex.TryGetValue(here, out ScopeBoxProperties box))
                                    continue; // skip gaps (should not happen for perfect rectangle)

                                bool hasRightNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r, Col = c + 1 });
                                bool hasLeftNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r, Col = c - 1 });
                                bool hasTopNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r - 1, Col = c });
                                bool hasBottomNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r + 1, Col = c });

                                XYZ viewNormal = box.ViewNormal;
                                XYZ edgeRight = box.DirRight2D;                              // along the “horizontal” axis in the box frame
                                XYZ perpInPlane = edgeRight.CrossProduct(viewNormal).Normalize();

                                // RIGHT edge (this box's right side), aligned with +Right
                                if (hasRightNeighbor)
                                {
                                    XYZ inward = edgeRight.Negate();
                                    XYZ anchor = ProjectToPlane(
                                        box.MidRight + inward.Multiply(insetIntoBox) + perpInPlane.Multiply(perpendicularBump),
                                        viewNormal);

                                    placedCount += CopyMoveRotate(document, seedViewReference, seedVisualAnchor, anchor, planView, edgeRight);
                                }

                                // LEFT edge (this box's left side), aligned with −Right
                                if (hasLeftNeighbor)
                                {
                                    XYZ edgeLeft = edgeRight.Negate();
                                    XYZ inward = edgeRight; // into this box from its left edge
                                    XYZ perpLeft = edgeLeft.CrossProduct(viewNormal).Normalize();

                                    XYZ anchor = ProjectToPlane(
                                        box.MidLeft + inward.Multiply(insetIntoBox) + perpLeft.Multiply(perpendicularBump),
                                        viewNormal);

                                    placedCount += CopyMoveRotate(document, seedViewReference, seedVisualAnchor, anchor, planView, edgeLeft);
                                }

                                // TOP edge (this box's top side), align along +Right for a consistent look
                                if (hasTopNeighbor)
                                {
                                    XYZ inward = box.DirDown2D.Negate(); // inward from top = up
                                    XYZ anchor = ProjectToPlane(
                                        box.MidTop + inward.Multiply(insetIntoBox) + perpInPlane.Multiply(perpendicularBump),
                                        viewNormal);

                                    placedCount += CopyMoveRotate(document, seedViewReference, seedVisualAnchor, anchor, planView, edgeRight);
                                }

                                // BOTTOM edge (this box's bottom side), align along +Right
                                if (hasBottomNeighbor)
                                {
                                    XYZ inward = box.DirDown2D; // inward from bottom = down
                                    XYZ anchor = ProjectToPlane(
                                        box.MidBottom + inward.Multiply(insetIntoBox) + perpInPlane.Multiply(perpendicularBump),
                                        viewNormal);

                                    placedCount += CopyMoveRotate(document, seedViewReference, seedVisualAnchor, anchor, planView, edgeRight);
                                }
                            }
                        }

                        // ------------------------------------------------------------------
                        // B) EXTRA: +2 tags per interior crossing
                        //    Crossing at (r,c) exists for r=0..rows-2, c=0..cols-2 and requires
                        //    all four boxes (r,c), (r,c+1), (r+1,c), (r+1,c+1).
                        //    We place BOTH tags inside the TOP-LEFT box (r,c):
                        //      • one HORIZONTAL just above the crossing (aligned with DirRight2D)
                        //      • one VERTICAL   just left  of the crossing (aligned with DirDown2D)
                        //    Offsets keep them off the guides and inside the box.
                        // ------------------------------------------------------------------
                        for (int r = 0; r < rowCount - 1; ++r)
                        {
                            for (int c = 0; c < colCount - 1; ++c)
                            {
                                var tl = new ScopeBoxGridInference.GridIndex { Row = r, Col = c };
                                var tr = new ScopeBoxGridInference.GridIndex { Row = r, Col = c + 1 };
                                var bl = new ScopeBoxGridInference.GridIndex { Row = r + 1, Col = c };
                                var br = new ScopeBoxGridInference.GridIndex { Row = r + 1, Col = c + 1 };

                                if (!presentIndices.Contains(tl) ||
                                    !presentIndices.Contains(tr) ||
                                    !presentIndices.Contains(bl) ||
                                    !presentIndices.Contains(br))
                                {
                                    continue; // not a valid interior crossing
                                }

                                // Use the TOP-LEFT box frame for orientation
                                ScopeBoxProperties boxTL = propsByIndex[tl];
                                XYZ viewNormal = boxTL.ViewNormal;

                                // Crossing point = TL center + 0.5*W*Right + 0.5*H*Down (projected to view plane)
                                XYZ crossingWorld =
                                    boxTL.CenterWorld
                                    + boxTL.DirRight2D.Multiply(boxTL.Width2D * 0.5)
                                    + boxTL.DirDown2D.Multiply(boxTL.Height2D * 0.5);
                                crossingWorld = ProjectToPlane(crossingWorld, viewNormal);

                                // (1) HORIZONTAL extra: just ABOVE the crossing, aligned with +Right
                                {
                                    XYZ targetRight = boxTL.DirRight2D;
                                    XYZ perpInPlane = targetRight.CrossProduct(viewNormal).Normalize(); // (≈ up-ish)
                                    XYZ anchor = crossingWorld
                                                 + boxTL.DirDown2D.Negate().Multiply(insetIntoBox)   // nudge up (into TL box)
                                                 + perpInPlane.Multiply(perpendicularBump);          // tiny side bump

                                    placedCount += CopyMoveRotate(document, seedViewReference, seedVisualAnchor, anchor, planView, targetRight);
                                }

                                // (2) VERTICAL extra: just LEFT of the crossing, align by setting “right” to DirDown2D
                                {
                                    XYZ targetRight = boxTL.DirDown2D;                                 // makes instance look vertical
                                    XYZ perpInPlane = targetRight.CrossProduct(viewNormal).Normalize(); // (≈ -Right)
                                    XYZ anchor = crossingWorld
                                                 + boxTL.DirRight2D.Negate().Multiply(insetIntoBox)   // nudge left (into TL box)
                                                 + perpInPlane.Multiply(perpendicularBump);           // tiny side bump

                                    placedCount += CopyMoveRotate(document, seedViewReference, seedVisualAnchor, anchor, planView, targetRight);
                                }
                            }
                        }

                        // Optional: remove the seed after duplicating
                        document.Delete(seedViewReference.Id);

                        transaction.Commit();
                    }

                    transactionGroup.Assimilate();
                }

                TaskDialog.Show("View References", $"Placed {placedCount} view references.");
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



        //public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        //{
        //    UIDocument uiDocument = commandData.Application.ActiveUIDocument;
        //    Document doc = uiDocument.Document;
        //    View planView = doc.ActiveView;

        //    try
        //    {
        //        // Guardrails
        //        if (planView.ViewType != ViewType.FloorPlan && planView.ViewType != ViewType.CeilingPlan)
        //        {
        //            TaskDialog.Show("View References", "Please run this in a plan or ceiling plan view.");
        //            return Result.Cancelled;
        //        }

        //        // 1) Pick or reuse a seed View Reference in this view
        //        Element seedViewReference = PickSeedViewReference(uiDocument);
        //        if (seedViewReference == null)
        //        {
        //            message = "No view reference selected.";
        //            return Result.Cancelled;
        //        }

        //        // 2) Collect scope boxes: current selection first, otherwise prompt
        //        IList<Element> scopeBoxElements = GetScopeBoxesFromSelection(uiDocument);
        //        if (scopeBoxElements.Count == 0)
        //            scopeBoxElements = PromptForScopeBoxes(uiDocument);
        //        if (scopeBoxElements.Count == 0)
        //        {
        //            message = "No scope boxes selected.";
        //            return Result.Cancelled;
        //        }

        //        // 3) Infer the grid (rotation-agnostic)
        //        IList<ElementId> selectedScopeBoxIds = scopeBoxElements.Select(e => e.Id).ToList();

        //        ScopeBoxGridInference.InferGridAndNeighbors(
        //            doc,
        //            planView,
        //            selectedScopeBoxIds,
        //            out Dictionary<ElementId, ScopeBoxGridInference.GridIndex> indexByElementId,
        //            out _ /* neighborsByElementId – not needed here */,
        //            out int rowCount,
        //            out int colCount);

        //        if (rowCount <= 0 || colCount <= 0)
        //        {
        //            TaskDialog.Show("View References", "Could not infer a rectangular grid from the selected scope boxes.");
        //            return Result.Cancelled;
        //        }

        //        // 4) Inspect each selected scope box once (gives us RU axes, midpoints, etc.)
        //        List<ScopeBoxProperties> inspectors = scopeBoxElements
        //            .Select(sb => ScopeBoxInspector.Inspect(sb, planView))
        //            .ToList();

        //        // Comparer for GridIndex dictionary / hash set lookups
        //        var gridIndexComparer = new ScopeBoxGridInference.GridIndexEqualityComparer();

        //        // (Row,Col) -> Properties, using the EXACT indices returned by inference
        //        var propsByIndex = inspectors.ToDictionary(
        //            p => indexByElementId[p.Id],
        //            p => p,
        //            gridIndexComparer);

        //        // The set of indices that exist (for neighbor probing)
        //        var presentIndices = new HashSet<ScopeBoxGridInference.GridIndex>(propsByIndex.Keys, gridIndexComparer);

        //        // 5) View-scale aware offsets (so spacing looks consistent)
        //        double insetIntoBox = ScaleByView(planView, DefaultInsetFeet);            // inward along the edge normal
        //        double perpendicularBump = ScaleByView(planView, DefaultNormalOffsetFeet); // small shift along in-plane perpendicular

        //        // 6) Seed “visual head” anchor we’ll move from (center of its BB in THIS view)
        //        //    If your family’s origin is already that point, use GetElementInsertionPoint(seedViewReference) instead.
        //        XYZ seedVisualAnchor = GetElementViewCenter(seedViewReference, planView);

        //        int placedCount = 0;

        //        using (var transactionGroup = new TransactionGroup(doc, "Duplicate View References"))
        //        {
        //            transactionGroup.Start();

        //            using (var transaction = new Transaction(doc, "Place View Reference Copies"))
        //            {
        //                transaction.Start();

        //                // Iterate row-major and place on ALL shared edges (Left/Right/Top/Bottom)
        //                for (int r = 0; r < rowCount; ++r)
        //                {
        //                    for (int c = 0; c < colCount; ++c)
        //                    {
        //                        var here = new ScopeBoxGridInference.GridIndex { Row = r, Col = c };
        //                        if (!propsByIndex.TryGetValue(here, out ScopeBoxProperties box))
        //                            continue; // skip gaps if user selected a non-full rectangle

        //                        bool hasRightNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r, Col = c + 1 });
        //                        bool hasLeftNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r, Col = c - 1 });
        //                        bool hasTopNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r - 1, Col = c });
        //                        bool hasBottomNeighbor = presentIndices.Contains(new ScopeBoxGridInference.GridIndex { Row = r + 1, Col = c });

        //                        // Common helpers for this box
        //                        XYZ viewNormal = box.ViewNormal;
        //                        XYZ edgeRight = box.DirRight2D;                                        // +Right along the “top” edges
        //                        XYZ perpRight = edgeRight.CrossProduct(viewNormal).Normalize();        // in-plane perpendicular

        //                        // RIGHT edge (this box's right side), orient along +Right
        //                        if (hasRightNeighbor)
        //                        {
        //                            XYZ inward = edgeRight.Negate(); // into this box from its right edge
        //                            XYZ anchor = ProjectToPlane(
        //                                box.MidRight + inward.Multiply(insetIntoBox) + perpRight.Multiply(perpendicularBump),
        //                                viewNormal);

        //                            placedCount += CopyMoveRotate(doc, seedViewReference, seedVisualAnchor, anchor, planView, edgeRight);
        //                        }

        //                        // LEFT edge (this box's left side), orient along −Right
        //                        if (hasLeftNeighbor)
        //                        {
        //                            XYZ edgeLeft = edgeRight.Negate();
        //                            XYZ perpLeft = edgeLeft.CrossProduct(viewNormal).Normalize();  // perpendicular for the left-edge axis
        //                            XYZ inward = edgeRight;                                      // into this box from its left edge

        //                            XYZ anchor = ProjectToPlane(
        //                                box.MidLeft + inward.Multiply(insetIntoBox) + perpLeft.Multiply(perpendicularBump),
        //                                viewNormal);

        //                            placedCount += CopyMoveRotate(doc, seedViewReference, seedVisualAnchor, anchor, planView, edgeLeft);
        //                        }

        //                        // TOP edge (this box's top side), orient along +Right (consistent visual)
        //                        if (hasTopNeighbor)
        //                        {
        //                            XYZ inward = box.DirDown2D.Negate(); // inward from TOP is “up”
        //                            XYZ anchor = ProjectToPlane(
        //                                box.MidTop + inward.Multiply(insetIntoBox) + perpRight.Multiply(perpendicularBump),
        //                                viewNormal);

        //                            placedCount += CopyMoveRotate(doc, seedViewReference, seedVisualAnchor, anchor, planView, edgeRight);
        //                        }

        //                        // BOTTOM edge (this box's bottom side), orient along +Right (consistent visual)
        //                        if (hasBottomNeighbor)
        //                        {
        //                            XYZ inward = box.DirDown2D; // inward from BOTTOM is “down”
        //                            XYZ anchor = ProjectToPlane(
        //                                box.MidBottom + inward.Multiply(insetIntoBox) + perpRight.Multiply(perpendicularBump),
        //                                viewNormal);

        //                            placedCount += CopyMoveRotate(doc, seedViewReference, seedVisualAnchor, anchor, planView, edgeRight);
        //                        }
        //                    }
        //                }

        //                // Toggle seed deletion as desired:
        //                doc.Delete(seedViewReference.Id);

        //                transaction.Commit();
        //            }

        //            transactionGroup.Assimilate();
        //        }

        //        TaskDialog.Show("View References", $"Placed {placedCount} view references.");
        //        return Result.Succeeded;
        //    }
        //    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        //    {
        //        return Result.Cancelled;
        //    }
        //    catch (Exception ex)
        //    {
        //        message = ex.ToString();
        //        return Result.Failed;
        //    }
        //}

        private static Element PickSeedViewReference(UIDocument uiDoc)
        {
            Document doc = uiDoc.Document;

            // Use current selection if it already contains one
            Element fromSelection = uiDoc.Selection
                .GetElementIds()
                .Select(id => doc.GetElement(id))
                .FirstOrDefault(IsViewReference);
            if (fromSelection != null) return fromSelection;

            // Otherwise prompt for a single element
            Reference picked = uiDoc.Selection.PickObject(
                ObjectType.Element,
                new ViewReferenceSelectionFilter(),
                "Pick a View Reference to duplicate");
            if (picked == null) return null;

            Element e = doc.GetElement(picked);
            return IsViewReference(e) ? e : null;
        }

        private static IList<Element> GetScopeBoxesFromSelection(UIDocument uiDoc)
        {
            Document doc = uiDoc.Document;
            var list = new List<Element>();
            foreach (ElementId id in uiDoc.Selection.GetElementIds())
            {
                Element e = doc.GetElement(id);
                if (IsScopeBox(e)) list.Add(e);
            }
            return list;
        }

        private static IList<Element> PromptForScopeBoxes(UIDocument uiDoc)
        {
            var picked = new List<Element>();
            try
            {
                while (true)
                {
                    Reference r = uiDoc.Selection.PickObject(
                        ObjectType.Element,
                        new ScopeBoxSelectionFilter(),
                        "Pick scope boxes (ESC to finish)");
                    if (r == null) break;
                    Element e = uiDoc.Document.GetElement(r);
                    if (IsScopeBox(e)) picked.Add(e);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // user finished
            }
            return picked;
        }

        private static bool IsScopeBox(Element e)
            => e?.Category != null &&
               e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));

        private static bool IsViewReference(Element e)
        {
            if (e == null || e.Category == null) return false;

            // Primary check: official category
            if (e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_ReferenceViewer)))
                return true;

            // Defensive fallback by family name (covers custom content)
            if (e is FamilyInstance fi)
            {
                string fam = (fi.Symbol?.Family?.Name ?? "").ToLowerInvariant();
                string sym = (fi.Symbol?.Name ?? "").ToLowerInvariant();
                if (fam.Contains("view ref") || sym.Contains("view ref"))
                    return true;
            }
            return false;
        }

        private sealed class ScopeBoxSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => IsScopeBox(elem);
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private sealed class ViewReferenceSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => IsViewReference(elem);
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        // ---------------------------- Math / placement helpers -------------------

        /// <summary>Project a world point into the active plan plane.</summary>
        private static XYZ ProjectToPlane(XYZ worldPoint, XYZ planeNormal)
        {
            XYZ n = planeNormal.Normalize();
            return worldPoint - n.Multiply(worldPoint.DotProduct(n));
        }

        /// <summary>Center of element bounding box in THIS view (visual head anchor).</summary>
        private static XYZ GetElementViewCenter(Element e, View v)
        {
            BoundingBoxXYZ bb = e.get_BoundingBox(v) ?? e.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) * 0.5 : GetElementInsertionPoint(e);
        }

        /// <summary>Family insertion point if available, otherwise BB center.</summary>
        private static XYZ GetElementInsertionPoint(Element e)
        {
            if (e is FamilyInstance fi && fi.Location is LocationPoint lp) return lp.Point;
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) * 0.5 : XYZ.Zero;
        }

        /// <summary>Returns the instance “right” axis (BasisX) or falls back to the view’s Right.</summary>
        private static XYZ GetInstanceRightDirection(Element e)
        {
            if (e is FamilyInstance fi)
            {
                try
                {
                    Transform t = fi.GetTransform();
                    if (t != null) return t.BasisX;
                }
                catch { }
            }
            View v = e.Document?.ActiveView;
            return v != null ? v.RightDirection : new XYZ(1, 0, 0);
        }

        /// <summary>Scale model feet by view scale (100 = 1.0x).</summary>
        private static double ScaleByView(View v, double modelFeet)
        {
            int scale = v.Scale > 0 ? v.Scale : 100;
            return modelFeet * (scale / 100.0);
        }

        /// <summary>
        /// Copy, move and rotate one tag:
        /// - Copies the seed element
        /// - Moves the copy so the seed’s visual anchor lands at the desired anchor
        /// - Rotates around that anchor so instance “right” aligns with targetRightInPlane
        /// </summary>
        private int CopyMoveRotate(
            Document doc,
            Element seed,
            XYZ seedVisualAnchor,
            XYZ desiredAnchorWorld,
            View planView,
            XYZ targetRightInPlane)
        {
            ICollection<ElementId> newIds = ElementTransformUtils.CopyElement(doc, seed.Id, XYZ.Zero);
            if (newIds == null || newIds.Count == 0) return 0;

            ElementId newId = newIds.First();
            Element newElem = doc.GetElement(newId);

            // Move purely by (desired − seedAnchor). We do NOT read the new element location,
            // because different families report different insertion points.
            XYZ move = desiredAnchorWorld - seedVisualAnchor;
            if (move.GetLength() > 1e-9)
                ElementTransformUtils.MoveElement(doc, newId, move);

            // Align “right” axis
            XYZ viewNormal = planView.ViewDirection.Normalize();

            XYZ currentRight = GetInstanceRightDirection(newElem);
            currentRight = ProjectToPlane(currentRight, viewNormal);
            targetRightInPlane = ProjectToPlane(targetRightInPlane, viewNormal);

            if (currentRight.GetLength() > 1e-9 && targetRightInPlane.GetLength() > 1e-9)
            {
                currentRight = currentRight.Normalize();
                targetRightInPlane = targetRightInPlane.Normalize();

                double dot = Math.Max(-1.0, Math.Min(1.0, currentRight.DotProduct(targetRightInPlane)));
                double ang = Math.Acos(dot);
                double sign = viewNormal.DotProduct(currentRight.CrossProduct(targetRightInPlane));
                if (sign < 0) ang = -ang;

                if (Math.Abs(ang) > AngleTolerance)
                {
                    Line axis = Line.CreateBound(desiredAnchorWorld, desiredAnchorWorld + viewNormal);
                    ElementTransformUtils.RotateElement(doc, newId, axis, ang);
                }
            }
            return 1;
        }
    }
}

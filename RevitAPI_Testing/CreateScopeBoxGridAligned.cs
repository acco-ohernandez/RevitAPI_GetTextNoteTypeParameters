// ===========================================================
#region Namespaces
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
#endregion

// Revit 2020-2026
// ORH – Scope Box Grid (plan-plane tiling, rotation-agnostic)

namespace RevitAPI_Testing
{
    [Transaction(TransactionMode.Manual)]
    public class CreateScopeBoxGridAligned : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;

            try
            {
                // Plan / RCP only
                if (view.ViewType != ViewType.FloorPlan && view.ViewType != ViewType.CeilingPlan)
                {
                    TaskDialog.Show("Scope Box Grid", "Please run this in a plan/ceiling plan view.");
                    return Result.Cancelled;
                }

                // Pick or use selected scope box
                Element scope = GetScopeBoxFromSelectionOrPick(uidoc);
                if (scope == null)
                {
                    message = "No scope box selected.";
                    return Result.Cancelled;
                }

                // Inspect source box
                ScopeBoxProperties props = ScopeBoxInspector.Inspect(scope, view);
                if (props == null)
                {
                    message = "Failed to extract scope box properties.";
                    return Result.Cancelled;
                }

                // TEST: move copy so it touches on the right (no overlap)
                double overlap = 1.0;

                using (TransactionGroup tg = new TransactionGroup(doc, "Create scope box right copy"))
                {
                    tg.Start();

                    using (Transaction tx = new Transaction(doc, "Duplicate to right by left edge"))
                    {
                        tx.Start();

                        //// Create the right-side copy using the inspector helper
                        //ScopeBoxProperties rightCopy =
                        //    ScopeBoxInspector.DuplicateToRightByLeftEdge(doc, view, props, overlap);

                        //// Create the left-side copy using the inspector helper
                        //ScopeBoxProperties leftCopy =
                        //    ScopeBoxInspector.DuplicateToLeftByRightEdge(doc, view, props, overlap);

                        //// Create the bottom-side copy using the inspector helper
                        //ScopeBoxProperties bottomCopy =
                        //    ScopeBoxInspector.DuplicateDownByTopEdge(doc, view, props, overlap);

                        //// Create the top-side copy using the inspector helper
                        //ScopeBoxProperties topCopy =
                        //    ScopeBoxInspector.DuplicateUpByBottomEdge(doc, view, props, overlap);

                        //// Optional: force graphics update
                        //if (rightCopy != null && leftCopy != null && bottomCopy != null && topCopy != null)
                        //    doc.Regenerate();


                        // Example call inside Execute(...)
                        var opts = new ScopeBoxGridOptions
                        {
                            IncludeOriginalInResult = true,
                            BaseName = "Scope Box",
                            WriteNameToComments = true,
                            ManageTransactions = false
                        };

                        int rows = 10;
                        int cols = 5;

                        // overlapX / overlapY in INTERNAL FEET (convert from UI if needed)
                        double overlapX = overlap;
                        double overlapY = overlap;

                        IList<ElementId> created = ScopeBoxGridBuilder.BuildGrid(
                            doc, view, scope, rows, cols, overlapX, overlapY, opts);

                        if (created != null && created.Count > 0)
                        {
                            doc.Regenerate();
                            TaskDialog.Show("Scope Box Grid", $"Created {created.Count} scope boxes.");
                        }

                        tx.Commit();
                    }

                    tg.Assimilate();
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

        private static Element GetScopeBoxFromSelectionOrPick(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            // Use selection if any
            Element pre = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .FirstOrDefault(IsScopeBox);
            if (pre != null) return pre;

            // Or prompt
            Reference r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element,
                new ScopeBoxSelectionFilter(), "Pick a Scope Box");
            if (r == null) return null;
            Element e = doc.GetElement(r);
            return IsScopeBox(e) ? e : null;
        }

        private static bool IsScopeBox(Element e)
            => e?.Category != null && e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));

        internal class ScopeBoxSelectionFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            public bool AllowElement(Element elem)
                => elem?.Category != null && elem.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
            public bool AllowReference(Reference refer, XYZ pos) => true;
        }

    }

}




// ===========================================================
// This version: direct plan-plane approach

//#region Namespaces
//using System;
//using System.Collections.Generic;
//using System.Globalization;
//using System.Linq;
//using System.Reflection;

//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.UI;
//#endregion

//// Revit 2020-2026
//// ORH – Scope Box Grid (plan-plane tiling, rotation-agnostic)

//namespace RevitAPI_Testing
//{
//    [Transaction(TransactionMode.Manual)]
//    public class CreateScopeBoxGridAligned : IExternalCommand
//    {
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIDocument uidoc = commandData.Application.ActiveUIDocument;
//            Document doc = uidoc.Document;
//            View view = doc.ActiveView;

//            try
//            {
//                // Plan / RCP only
//                if (view.ViewType != ViewType.FloorPlan && view.ViewType != ViewType.CeilingPlan)
//                {
//                    TaskDialog.Show("Scope Box Grid", "Please run this in a plan/ceiling plan view.");
//                    return Result.Cancelled;
//                }

//                // Pick or use selected scope box
//                Element scope = GetScopeBoxFromSelectionOrPick(uidoc);
//                if (scope == null)
//                {
//                    message = "No scope box selected.";
//                    return Result.Cancelled;
//                }

//                // TODO hook to your UI
//                int rows = 4;
//                int cols = 3;
//#if REVIT2020
//                double overlapX = UnitUtils.ConvertToInternalUnits(10.416, DisplayUnitType.DUT_FEET);
//                double overlapY = UnitUtils.ConvertToInternalUnits(10.416, DisplayUnitType.DUT_FEET);
//#else
//                double overlapX = UnitUtils.ConvertToInternalUnits(10.416, UnitTypeId.Feet);
//                double overlapY = UnitUtils.ConvertToInternalUnits(10.416, UnitTypeId.Feet);
//#endif
//                string baseName = "Scope Box";

//                // View 2D frame
//                XYZ R = view.RightDirection.Normalize();
//                XYZ U = view.UpDirection.Normalize();
//                XYZ N = view.ViewDirection.Normalize();

//                // Read the box face corners in world, then work *in the view plane*
//                BoundingBoxXYZ bb = scope.get_BoundingBox(null);
//                if (bb == null) throw new InvalidOperationException("Scope box has no bounding box.");
//                Transform t = bb.Transform ?? Transform.Identity;

//                List<XYZ> corners = new List<XYZ>
//                {
//                    t.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)), // p00
//                    t.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z)), // p10
//                    t.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)), // p01
//                    t.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z))  // p11
//                };

//                // Pick the Top-Left corner in *view* space (max Up, then min Right)
//                XYZ topLeft = corners
//                    .OrderByDescending(p => p.DotProduct(U))
//                    .ThenBy(p => p.DotProduct(R))
//                    .First();

//                // Find the two *adjacent* corners to topLeft (shortest two distances)
//                List<(XYZ pt, double d)> neigh = new List<(XYZ, double)>();
//                foreach (var c in corners)
//                {
//                    if (c.IsAlmostEqualTo(topLeft)) continue;
//                    double d2 = (c - topLeft).GetLength();
//                    neigh.Add((c, d2));
//                }
//                neigh = neigh.OrderBy(n => n.d).Take(2).ToList(); // the two edges

//                // Edge vectors in world, then projected to the view plane
//                XYZ e1 = ProjectToPlane(neigh[0].pt - topLeft, N);
//                XYZ e2 = ProjectToPlane(neigh[1].pt - topLeft, N);

//                // Assign Right/Down edges by dot with R/U
//                XYZ eRight = (e1.DotProduct(R) >= e2.DotProduct(R)) ? e1 : e2; // more to the right
//                XYZ eDown = (e1 == eRight) ? e2 : e1;                         // the other adjacent one
//                // Ensure "down" points downward (negative Up)
//                if (eDown.DotProduct(U) > 0) eDown = eDown.Negate();

//                // Unit directions and tile sizes taken *from the visible edges*
//                double tileX = eRight.GetLength();
//                double tileY = eDown.GetLength();
//                if (tileX <= 1e-9 || tileY <= 1e-9)
//                    throw new InvalidOperationException("Scope box edge length is zero.");

//                XYZ dirX = eRight.Divide(tileX); // to the right in the view
//                XYZ dirY = eDown.Divide(tileY);  // downward in the view

//                // Steps from what the eye sees (visible edge length) minus overlap
//                double stepX = Math.Max(0.0, tileX - overlapX);
//                double stepY = Math.Max(0.0, tileY - overlapY);

//                const bool DEBUG_DRAW = true;

//                using (TransactionGroup tg = new TransactionGroup(doc, "Scope Box Grid (plan-plane)"))
//                {
//                    tg.Start();

//                    using (Transaction tx = new Transaction(doc, "Copy Scope Boxes"))
//                    {
//                        tx.Start();

//                        // Keep original – it is the top-left cell
//                        List<ElementId> created = new List<ElementId> { scope.Id };

//                        if (DEBUG_DRAW)
//                            DebugDraw.DrawCrosshairAtCorners(doc, view, topLeft, dirX, tileX, -dirY, tileY, "R1C1", 2.0, true, false);

//                        for (int r = 0; r < rows; ++r)
//                        {
//                            for (int c = 0; c < cols; ++c)
//                            {
//                                if (r == 0 && c == 0) continue; // original

//                                XYZ delta = dirX.Multiply(stepX * c).Add(dirY.Multiply(stepY * r));
//                                if (DEBUG_DRAW)
//                                    DebugDraw.DrawCrosshairAtCorners(doc, view, topLeft + delta, dirX, tileX, -dirY, tileY, $"R{r + 1}C{c + 1}", 2.0, true, false);

//                                var ids = ElementTransformUtils.CopyElement(doc, scope.Id, delta);
//                                if (ids != null && ids.Count > 0)
//                                {
//                                    ElementId nid = ids.First();
//                                    created.Add(nid);
//                                    TryRename(doc.GetElement(nid), baseName, r, c);
//                                }
//                            }
//                        }

//                        tx.Commit();
//                    }

//                    tg.Assimilate();
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

//        // ----------------- Helpers -----------------

//        private static Element GetScopeBoxFromSelectionOrPick(UIDocument uidoc)
//        {
//            Document doc = uidoc.Document;

//            // Use selection if any
//            Element pre = uidoc.Selection.GetElementIds()
//                .Select(id => doc.GetElement(id))
//                .FirstOrDefault(IsScopeBox);
//            if (pre != null) return pre;

//            // Or prompt
//            Reference r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element,
//                new ScopeBoxSelectionFilter(), "Pick a Scope Box");
//            if (r == null) return null;
//            Element e = doc.GetElement(r);
//            return IsScopeBox(e) ? e : null;
//        }

//        private static bool IsScopeBox(Element e)
//            => e?.Category != null && e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));

//        private static XYZ ProjectToPlane(XYZ v, XYZ planeNormal)
//        {
//            XYZ n = planeNormal.Normalize();
//            return v - n.Multiply(v.DotProduct(n));
//        }

//        private static void TryRename(Element e, string baseName, int row, int col)
//        {
//            if (e == null) return;
//            Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
//            if (p != null && !p.IsReadOnly)
//                p.Set($"{baseName} R{row + 1}C{col + 1}");
//        }
//    }

//    // -------- Reusable Debug Drawing Helpers (XYZ labels, cross-version) --------
//    internal static class DebugDraw
//    {
//        public static void DrawCrosshair(
//            Document doc, View view, XYZ center, double size,
//            string note = null, bool labelModelXYZ = true, bool labelViewRU = false, int precision = 3)
//        {
//            if (doc == null || view == null || center == null) return;
//            if (size <= 0) size = 1.0;

//            XYZ right = view.RightDirection.Normalize();
//            XYZ up = view.UpDirection.Normalize();
//            XYZ nrm = view.ViewDirection.Normalize();

//            double half = size * 0.5;
//            XYZ pR1 = center - right.Multiply(half);
//            XYZ pR2 = center + right.Multiply(half);
//            XYZ pU1 = center - up.Multiply(half);
//            XYZ pU2 = center + up.Multiply(half);

//            Plane plane = Plane.CreateByNormalAndOrigin(nrm, center);
//            using (SketchPlane sp = SketchPlane.Create(doc, plane))
//            {
//                Line h = Line.CreateBound(pR1, pR2);
//                Line v = Line.CreateBound(pU1, pU2);
//                doc.Create.NewDetailCurve(view, h);
//                doc.Create.NewDetailCurve(view, v);
//            }

//            string label = note;

//            if (labelModelXYZ)
//            {
//#if REVIT2020
//                double x = UnitUtils.ConvertFromInternalUnits(center.X, DisplayUnitType.DUT_FEET);
//                double y = UnitUtils.ConvertFromInternalUnits(center.Y, DisplayUnitType.DUT_FEET);
//                double z = UnitUtils.ConvertFromInternalUnits(center.Z, DisplayUnitType.DUT_FEET);
//#else
//                double x = UnitUtils.ConvertFromInternalUnits(center.X, UnitTypeId.Feet);
//                double y = UnitUtils.ConvertFromInternalUnits(center.Y, UnitTypeId.Feet);
//                double z = UnitUtils.ConvertFromInternalUnits(center.Z, UnitTypeId.Feet);
//#endif
//                string m = $"({x.ToString("F" + precision, CultureInfo.InvariantCulture)}, " +
//                           $"{y.ToString("F" + precision, CultureInfo.InvariantCulture)}, " +
//                           $"{z.ToString("F" + precision, CultureInfo.InvariantCulture)})";
//                label = string.IsNullOrEmpty(label) ? m : $"{label}  {m}";
//            }

//            if (labelViewRU)
//            {
//                double xr = center.DotProduct(right);
//                double yu = center.DotProduct(up);
//#if REVIT2020
//                xr = UnitUtils.ConvertFromInternalUnits(xr, DisplayUnitType.DUT_FEET);
//                yu = UnitUtils.ConvertFromInternalUnits(yu, DisplayUnitType.DUT_FEET);
//#else
//                xr = UnitUtils.ConvertFromInternalUnits(xr, UnitTypeId.Feet);
//                yu = UnitUtils.ConvertFromInternalUnits(yu, UnitTypeId.Feet);
//#endif
//                string ru = $"[R={xr.ToString("F" + precision, CultureInfo.InvariantCulture)}, " +
//                            $"U={yu.ToString("F" + precision, CultureInfo.InvariantCulture)}]";
//                label = string.IsNullOrEmpty(label) ? ru : $"{label}  {ru}";
//            }

//            if (!string.IsNullOrWhiteSpace(label))
//            {
//                XYZ notePt = center + up.Multiply(size * 0.6);
//#if REVIT2020
//                var fec = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType));
//                TextNoteType tnt = fec.FirstElement() as TextNoteType;
//                TextNote.Create(doc, view.Id, notePt, label, tnt.Id);
//#else
//                var fec = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType));
//                TextNoteType tnt = fec.FirstElement() as TextNoteType;
//                TextNoteOptions opt = new TextNoteOptions(tnt?.Id ?? ElementId.InvalidElementId);
//                TextNote.Create(doc, view.Id, notePt, label, opt);
//#endif
//            }
//        }

//        public static void DrawCrosshairAtCorners(
//            Document doc, View view,
//            XYZ origin, XYZ ux, double lenX, XYZ uy, double lenY,
//            string notePrefix = null, double size = 1.0,
//            bool labelModelXYZ = true, bool labelViewRU = false, int precision = 3)
//        {
//            XYZ p00 = origin;
//            XYZ p10 = origin + ux.Multiply(lenX);
//            XYZ p01 = origin + uy.Multiply(lenY);
//            XYZ p11 = origin + ux.Multiply(lenX) + uy.Multiply(lenY);

//            DrawCrosshair(doc, view, p00, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p10, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p01, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p11, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//        }
//    }

//    internal class ScopeBoxSelectionFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
//    {
//        public bool AllowElement(Element elem)
//            => elem?.Category != null && elem.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
//        public bool AllowReference(Reference refer, XYZ pos) => true;
//    }

//    // Optional ribbon hook helper
//    internal static class RibbonHelper
//    {
//        internal static PushButtonData GetButtonData()
//        {
//            string buttonInternalName = "btnScopeBoxGrid";
//            string buttonTitle = "Scope Box Grid";
//            ButtonDataClass myButtonData1 = new ButtonDataClass(
//                buttonInternalName, buttonTitle,
//                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
//                Properties.Resources.Blue_32, Properties.Resources.Blue_16,
//                "Create an overlapping Scope Box grid in any rotation");
//            return myButtonData1.Data;
//        }
//    }
//}






// ===========================================================
// This version: rotate-to-orthogonal approach

//#region Namespaces
//using System;
//using System.Collections.Generic;
//using System.Globalization;
//using System.Linq;
//using System.Reflection;

//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.UI;
//#endregion

//// Revit 2020-2026
//// ORH – Scope Box Grid (rotation-safe via rotate-to-orthogonal, grid, rotate-back)

//namespace RevitAPI_Testing
//{
//    [Transaction(TransactionMode.Manual)]
//    public class CreateScopeBoxGridAligned : IExternalCommand
//    {
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIDocument uidoc = commandData.Application.ActiveUIDocument;
//            Document doc = uidoc.Document;
//            View view = doc.ActiveView;

//            try
//            {
//                // Guard: plan/ceiling plan only
//                if (view.ViewType != ViewType.FloorPlan && view.ViewType != ViewType.CeilingPlan)
//                {
//                    TaskDialog.Show("Scope Box Grid", "Please run this in a plan/ceiling plan view.");
//                    return Result.Cancelled;
//                }

//                // 1) Get a scope box
//                Element scope = GetScopeBoxFromSelectionOrPick(uidoc);
//                if (scope == null)
//                {
//                    message = "No scope box selected.";
//                    return Result.Cancelled;
//                }

//                // Inputs (plug in your UI here)
//                int rows = 4;
//                int cols = 3;
//#if REVIT2020
//                double overlapX = UnitUtils.ConvertToInternalUnits(10.416, DisplayUnitType.DUT_FEET);
//                double overlapY = UnitUtils.ConvertToInternalUnits(10.416, DisplayUnitType.DUT_FEET);
//#else
//                double overlapX = UnitUtils.ConvertToInternalUnits(10.416, UnitTypeId.Feet);
//                double overlapY = UnitUtils.ConvertToInternalUnits(10.416, UnitTypeId.Feet);
//#endif
//                string baseName = "Scope Box";

//                // Debug crosshairs
//                const bool DEBUG_DRAW = true;

//                using (TransactionGroup tg = new TransactionGroup(doc, "Scope Box Grid (rotate-safe)"))
//                {
//                    tg.Start();

//                    // --- Measure original pose ---
//                    BoundingBoxXYZ bb0 = scope.get_BoundingBox(null);
//                    if (bb0 == null) throw new InvalidOperationException("Scope box has no bounding box.");
//                    Transform t0 = bb0.Transform ?? Transform.Identity;

//                    XYZ vRight = view.RightDirection.Normalize();
//                    XYZ vUp = view.UpDirection.Normalize();
//                    XYZ vNorm = view.ViewDirection.Normalize();

//                    // Local extents (scale-free)
//                    double widthLocal = Math.Abs(bb0.Max.X - bb0.Min.X);
//                    double heightLocal = Math.Abs(bb0.Max.Y - bb0.Min.Y);

//                    // World center (pivot)
//                    XYZ center0 = t0.OfPoint((bb0.Min + bb0.Max) * 0.5);

//                    // Angle in plane from local +X to view Right (signed)
//                    XYZ uxW = t0.BasisX; // local X in world
//                    XYZ ux2D = ProjectToPlane(uxW, vNorm).Normalize();
//                    double angle = SignedAngleInPlane(ux2D, vRight, vNorm); // +ccw from Right; we’ll rotate by -angle to orthogonalize

//                    // Track pinned state
//                    bool wasPinned = scope.Pinned;

//                    // Unpin original if needed
//                    using (Transaction tUnpin = new Transaction(doc, "Unpin"))
//                    {
//                        tUnpin.Start();
//                        if (wasPinned) scope.Pinned = false;
//                        tUnpin.Commit();
//                    }

//                    // 2) Rotate original to orthogonal
//                    using (Transaction tRot1 = new Transaction(doc, "Rotate to orthogonal"))
//                    {
//                        tRot1.Start();
//                        ElementTransformUtils.RotateElement(doc, scope.Id,
//                            Line.CreateBound(center0, center0 + vNorm), -angle);
//                        tRot1.Commit();
//                    }

//                    // 3) Create the grid in orthogonal space (original as top-left along view axes)
//                    IList<ElementId> created = new List<ElementId>();
//                    using (Transaction tGrid = new Transaction(doc, "Create grid (orthogonal)"))
//                    {
//                        tGrid.Start();

//                        // Recompute AABB after rotation
//                        BoundingBoxXYZ bbOrtho = scope.get_BoundingBox(null);
//                        Transform tOrtho = bbOrtho.Transform ?? Transform.Identity;

//                        // Pick TOP-LEFT corner in view space as anchor
//                        var corners = GetCornersWorld(tOrtho, bbOrtho);
//                        XYZ topLeft = corners
//                            .OrderByDescending(p => p.DotProduct(vUp))   // highest Up
//                            .ThenBy(p => p.DotProduct(vRight))           // then most left
//                            .First();

//                        // Tile directions in orthogonal space: Right (+), Down (-Up)
//                        XYZ colDir = vRight;  // horizontal
//                        XYZ rowDir = vUp.Negate(); // downward

//                        double stepX = Math.Max(0.0, widthLocal - overlapX);
//                        double stepY = Math.Max(0.0, heightLocal - overlapY);

//                        // Optional debug crosshair at the first cell
//                        if (DEBUG_DRAW)
//                            DebugDraw.DrawCrosshairAtCorners(doc, view, topLeft, vRight, widthLocal, vUp, heightLocal, "R1C1", 2.0, true, false);

//                        // Do copies (skip original at R1C1)
//                        for (int r = 0; r < rows; ++r)
//                        {
//                            for (int c = 0; c < cols; ++c)
//                            {
//                                if (r == 0 && c == 0) continue;

//                                XYZ delta = colDir.Multiply(stepX * c).Add(rowDir.Multiply(stepY * r));
//                                if (DEBUG_DRAW)
//                                    DebugDraw.DrawCrosshairAtCorners(doc, view, topLeft + delta, vRight, widthLocal, vUp, heightLocal, $"R{r + 1}C{c + 1}", 2.0, true, false);

//                                var ids = ElementTransformUtils.CopyElement(doc, scope.Id, delta);
//                                if (ids != null && ids.Count > 0)
//                                {
//                                    ElementId nid = ids.First();
//                                    created.Add(nid);
//                                    TryRename(doc.GetElement(nid), baseName, r, c);
//                                }
//                            }
//                        }

//                        tGrid.Commit();
//                    }

//                    // 4) Rotate the whole set back to original angle – emulate group rotation
//                    using (Transaction tRot2 = new Transaction(doc, "Rotate back to original"))
//                    {
//                        tRot2.Start();

//                        // All boxes to process: original + newly created
//                        var toProcess = new List<ElementId>(created) { scope.Id };

//                        // Precompute rotation transform around the view normal (through origin)
//                        Transform rot = Transform.CreateRotation(vNorm, angle);

//                        foreach (ElementId eid in toProcess)
//                        {
//                            Element e = doc.GetElement(eid);
//                            if (e == null) continue;

//                            // Current center in world
//                            BoundingBoxXYZ bb = e.get_BoundingBox(null);
//                            Transform te = bb.Transform ?? Transform.Identity;
//                            XYZ c = te.OfPoint((bb.Min + bb.Max) * 0.5);

//                            // Target center after rotation about common pivot center0
//                            XYZ vec = c - center0;                // vector from pivot to current center
//                            XYZ vecR = rot.OfVector(vec);         // rotate that vector
//                            XYZ cTarget = center0 + vecR;         // new center position

//                            // 1) Translate to the target orbit position
//                            XYZ delta = cTarget - c;
//                            if (!delta.IsZeroLength())
//                                ElementTransformUtils.MoveElement(doc, eid, delta);

//                            // 2) Rotate in place about its own (new) center by +angle
//                            Line localAxis = Line.CreateBound(cTarget, cTarget + vNorm);
//                            ElementTransformUtils.RotateElement(doc, eid, localAxis, angle);
//                        }

//                        tRot2.Commit();
//                    }


//                    // Restore pinned state
//                    using (Transaction tRePin = new Transaction(doc, "Restore pin"))
//                    {
//                        tRePin.Start();
//                        if (wasPinned) scope.Pinned = true;
//                        tRePin.Commit();
//                    }

//                    tg.Assimilate();
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

//        // ---------- Helpers ----------

//        private static Element GetScopeBoxFromSelectionOrPick(UIDocument uidoc)
//        {
//            Document doc = uidoc.Document;

//            // Try current selection
//            Element pre = uidoc.Selection.GetElementIds()
//                .Select(id => doc.GetElement(id))
//                .FirstOrDefault(IsScopeBox);
//            if (pre != null) return pre;

//            // Prompt pick
//            Reference r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element,
//                new ScopeBoxSelectionFilter(), "Pick a Scope Box");
//            if (r == null) return null;
//            Element e = doc.GetElement(r);
//            return IsScopeBox(e) ? e : null;
//        }

//        private static bool IsScopeBox(Element e)
//        {
//            return e?.Category != null
//                && e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
//        }

//        private static XYZ ProjectToPlane(XYZ v, XYZ planeNormal)
//        {
//            XYZ n = planeNormal.Normalize();
//            return v - n.Multiply(v.DotProduct(n));
//        }

//        private static double SignedAngleInPlane(XYZ a, XYZ b, XYZ normal)
//        {
//            a = a.Normalize(); b = b.Normalize();
//            double dot = Math.Min(1.0, Math.Max(-1.0, a.DotProduct(b)));
//            double ang = Math.Acos(dot); // 0..pi
//            double sign = normal.DotProduct(a.CrossProduct(b)) >= 0 ? 1.0 : -1.0;
//            return ang * sign;
//        }

//        private static List<XYZ> GetCornersWorld(Transform t, BoundingBoxXYZ bb)
//        {
//            var pts = new List<XYZ>
//            {
//                t.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)), // p00
//                t.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z)), // p10
//                t.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)), // p01
//                t.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z))  // p11
//            };
//            return pts;
//        }

//        private static void TryRename(Element e, string baseName, int row, int col)
//        {
//            if (e == null) return;
//            Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
//            if (p != null && !p.IsReadOnly)
//                p.Set($"{baseName} R{row + 1}C{col + 1}");
//        }

//#if REVIT2020
//        private static DisplayUnitType GetFeetUnit() => DisplayUnitType.DUT_FEET;
//#else
//        private static ForgeTypeId GetFeetUnit() => UnitTypeId.Feet;
//#endif
//    }

//    // -------- Reusable Debug Drawing Helpers --------
//    internal static class DebugDraw
//    {
//        public static void DrawCrosshair(
//            Document doc, View view, XYZ center, double size,
//            string note = null, bool labelModelXYZ = true, bool labelViewRU = false, int precision = 3)
//        {
//            if (doc == null || view == null || center == null) return;
//            if (size <= 0) size = 1.0;

//            XYZ right = view.RightDirection.Normalize();
//            XYZ up = view.UpDirection.Normalize();
//            XYZ nrm = view.ViewDirection.Normalize();

//            double half = size * 0.5;
//            XYZ pR1 = center - right.Multiply(half);
//            XYZ pR2 = center + right.Multiply(half);
//            XYZ pU1 = center - up.Multiply(half);
//            XYZ pU2 = center + up.Multiply(half);

//            Plane plane = Plane.CreateByNormalAndOrigin(nrm, center);
//            using (SketchPlane sp = SketchPlane.Create(doc, plane))
//            {
//                Line h = Line.CreateBound(pR1, pR2);
//                Line v = Line.CreateBound(pU1, pU2);
//                doc.Create.NewDetailCurve(view, h);
//                doc.Create.NewDetailCurve(view, v);
//            }
//            string label = note;

//            if (labelModelXYZ)
//            {
//#if REVIT2020
//                double x = UnitUtils.ConvertFromInternalUnits(center.X, DisplayUnitType.DUT_FEET);
//                double y = UnitUtils.ConvertFromInternalUnits(center.Y, DisplayUnitType.DUT_FEET);
//                double z = UnitUtils.ConvertFromInternalUnits(center.Z, DisplayUnitType.DUT_FEET);
//#else
//                double x = UnitUtils.ConvertFromInternalUnits(center.X, UnitTypeId.Feet);
//                double y = UnitUtils.ConvertFromInternalUnits(center.Y, UnitTypeId.Feet);
//                double z = UnitUtils.ConvertFromInternalUnits(center.Z, UnitTypeId.Feet);
//#endif

//                string m = $"({x.ToString("F" + precision, CultureInfo.InvariantCulture)}, " +
//                           $"{y.ToString("F" + precision, CultureInfo.InvariantCulture)}, " +
//                           $"{z.ToString("F" + precision, CultureInfo.InvariantCulture)})";

//                label = string.IsNullOrEmpty(label) ? m : $"{label}  {m}";
//            }

//            if (labelViewRU)
//            {
//                double xr = center.DotProduct(right);
//                double yu = center.DotProduct(up);

//#if REVIT2020
//                xr = UnitUtils.ConvertFromInternalUnits(xr, DisplayUnitType.DUT_FEET);
//                yu = UnitUtils.ConvertFromInternalUnits(yu, DisplayUnitType.DUT_FEET);
//#else
//                xr = UnitUtils.ConvertFromInternalUnits(xr, UnitTypeId.Feet);
//                yu = UnitUtils.ConvertFromInternalUnits(yu, UnitTypeId.Feet);
//#endif

//                string ru = $"[R={xr.ToString("F" + precision, CultureInfo.InvariantCulture)}, " +
//                            $"U={yu.ToString("F" + precision, CultureInfo.InvariantCulture)}]";

//                label = string.IsNullOrEmpty(label) ? ru : $"{label}  {ru}";
//            }


//            if (!string.IsNullOrWhiteSpace(label))
//            {
//                XYZ notePt = center + up.Multiply(size * 0.6);
//#if REVIT2020
//                var fec = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType));
//                TextNoteType tnt = fec.FirstElement() as TextNoteType;
//                TextNote.Create(doc, view.Id, notePt, label, tnt.Id);
//#else
//                var fec = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType));
//                TextNoteType tnt = fec.FirstElement() as TextNoteType;
//                TextNoteOptions opt = new TextNoteOptions(tnt?.Id ?? ElementId.InvalidElementId);
//                TextNote.Create(doc, view.Id, notePt, label, opt);
//#endif
//            }
//        }

//        public static void DrawCrosshairAtCorners(
//            Document doc, View view,
//            XYZ origin, XYZ ux, double lenX, XYZ uy, double lenY,
//            string notePrefix = null, double size = 1.0,
//            bool labelModelXYZ = true, bool labelViewRU = false, int precision = 3)
//        {
//            XYZ p00 = origin;
//            XYZ p10 = origin + ux.Multiply(lenX);
//            XYZ p01 = origin + uy.Multiply(lenY);
//            XYZ p11 = origin + ux.Multiply(lenX) + uy.Multiply(lenY);

//            DrawCrosshair(doc, view, p00, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p10, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p01, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p11, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//        }
//    }

//    internal class ScopeBoxSelectionFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
//    {
//        public bool AllowElement(Element elem)
//            => elem?.Category != null && elem.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
//        public bool AllowReference(Reference refer, XYZ pos) => true;
//    }

//    // Optional: ribbon helper (unchanged)
//    internal static class RibbonHelper
//    {
//        internal static PushButtonData GetButtonData()
//        {
//            string buttonInternalName = "btnScopeBoxGrid";
//            string buttonTitle = "Scope Box Grid";
//            ButtonDataClass myButtonData1 = new ButtonDataClass(
//                buttonInternalName, buttonTitle,
//                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
//                Properties.Resources.Blue_32, Properties.Resources.Blue_16,
//                "Create a rotated, overlapping Scope Box grid");
//            return myButtonData1.Data;
//        }
//    }
//}








// =============================================================
//Archive version without rotate-to-orthogonal approach

//#region Namespaces
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Reflection;

//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.UI;
//#endregion

//// Revit 2020-2026
//// ORH – Scope Box Grid that honors rotation (no view rotation hacks)
//// Keep namespace/class names per user code.

//namespace RevitAPI_Testing
//{
//    [Transaction(TransactionMode.Manual)]
//    public class CreateScopeBoxGridAligned : IExternalCommand
//    {
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIDocument uidoc = commandData.Application.ActiveUIDocument;
//            Document doc = uidoc.Document;

//            try
//            {
//                // 1) Ask the user to pick one scope box (or take the first selected)
//                Element scope = GetScopeBoxFromSelectionOrPick(uidoc);
//                if (scope == null)
//                {
//                    message = "No scope box selected.";
//                    return Result.Cancelled;
//                }

//                // TODO: plug these values from your existing UI
//                int rows = 4;      // example
//                int cols = 3;      // example
//                double overlapX = UnitUtils.ConvertToInternalUnits(10.416, GetFeetUnit());
//                double overlapY = UnitUtils.ConvertToInternalUnits(10.416, GetFeetUnit());
//                string baseName = "Scope Box 1"; // example

//                using (TransactionGroup tg = new TransactionGroup(doc, "Create Scope Box Grid (aligned)"))
//                {
//                    tg.Start();

//                    IList<ElementId> created = ScopeBoxGridService.CreateGrid(doc, scope, rows, cols, overlapX, overlapY, baseName);

//                    tg.Assimilate();
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

//        private static Element GetScopeBoxFromSelectionOrPick(UIDocument uidoc)
//        {
//            Document doc = uidoc.Document;

//            // 1) Try current selection
//            Element pre = uidoc.Selection.GetElementIds()
//                .Select(id => doc.GetElement(id))
//                .FirstOrDefault(IsScopeBox);
//            if (pre != null) return pre;

//            // 2) Prompt pick
//            Reference r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element,
//                new ScopeBoxSelectionFilter(),
//                "Pick a Scope Box");
//            if (r == null) return null;
//            Element e = doc.GetElement(r);
//            return IsScopeBox(e) ? e : null;
//        }

//        private static bool IsScopeBox(Element e)
//        {
//            // robust across versions/locales; avoid IntegerValue/Value
//            return e?.Category != null && e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
//        }

//        // ---- Units helper (older first to be future-proof) ----
//#if REVIT2020
//        private static DisplayUnitType GetFeetUnit()
//        {
//            return DisplayUnitType.DUT_FEET;
//        }
//#else
//        private static ForgeTypeId GetFeetUnit()
//        {
//            return UnitTypeId.Feet;
//        }
//#endif

//        internal static PushButtonData GetButtonData()
//        {
//            // use this method to define the properties for this command in the Revit ribbon
//            string buttonInternalName = "btnCommand2";
//            string buttonTitle = "Button 2";

//            ButtonDataClass myButtonData1 = new ButtonDataClass(
//                buttonInternalName,
//                buttonTitle,
//                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
//                Properties.Resources.Blue_32,
//                Properties.Resources.Blue_16,
//                "This is a tooltip for Button 2");

//            return myButtonData1.Data;
//        }
//    }

//    /// <summary>
//    /// Encapsulates rotation-safe creation of a rectangular grid of scope boxes based on an exemplar.
//    /// Works by reading the exemplar BoundingBoxXYZ.Transform to get its local X/Y axes and sizes.
//    /// Avoids rotating the view; only translates copies along local axes.
//    /// </summary>
//    internal static class ScopeBoxGridService
//    {
//        public static IList<ElementId> CreateGrid(
//      Document doc,
//      Element exemplarScopeBox,
//      int rows,
//      int cols,
//      double overlapX,
//      double overlapY,
//      string baseName)
//        {
//            if (rows < 1 || cols < 1)
//                throw new ArgumentOutOfRangeException("rows/cols must be >= 1");

//            View v = doc.ActiveView;
//            XYZ vRight = v.RightDirection.Normalize();
//            XYZ vUp = v.UpDirection.Normalize();
//            XYZ vNormal = v.ViewDirection.Normalize();

//            BoundingBoxXYZ bb = exemplarScopeBox.get_BoundingBox(null);
//            if (bb == null)
//                throw new InvalidOperationException("Scope box has no bounding box.");

//            // Scale-free local extents
//            double widthLocal = Math.Abs(bb.Max.X - bb.Min.X);
//            double heightLocal = Math.Abs(bb.Max.Y - bb.Min.Y);
//            if (widthLocal <= 1e-9 || heightLocal <= 1e-9)
//                throw new InvalidOperationException("Scope box has zero width or height.");

//            // Helper: project vector to view plane (remove normal component)
//            XYZ ProjToView(XYZ w) => w - vNormal.Multiply(w.DotProduct(vNormal));

//            // Build world-space corners for this face
//            Transform t = bb.Transform ?? Transform.Identity;
//            XYZ p00 = t.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z));
//            XYZ p10 = t.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z));
//            XYZ p01 = t.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z));
//            XYZ p11 = t.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z));

//            // Edge directions taken from the actual edges, then projected to the view plane
//            XYZ exW = p10 - p00;
//            XYZ eyW = p01 - p00;

//            XYZ ux = ProjToView(exW);
//            XYZ uy = ProjToView(eyW);

//            // Degenerate fallback
//            if (ux.IsZeroLength()) ux = vRight;
//            if (uy.IsZeroLength()) uy = vUp;

//            // Orthonormalize in the view plane (force perfect perpendicularity)
//            ux = ux.Normalize();
//            XYZ uz = vNormal;                  // exact view normal
//            uy = uz.CrossProduct(ux).Normalize();

//            // Align to view's right/up so 'top-left' is consistent for any rotation
//            if (ux.DotProduct(vRight) < 0) ux = ux.Negate();
//            if (uy.DotProduct(vUp) < 0) uy = uy.Negate();

//            // Pick TOP-LEFT corner in view space (highest Up, then lowest Right)
//            (XYZ pt, double xr, double yu)[] corners = new[]
//            {
//        (p00, p00.DotProduct(vRight), p00.DotProduct(vUp)),
//        (p10, p10.DotProduct(vRight), p10.DotProduct(vUp)),
//        (p01, p01.DotProduct(vRight), p01.DotProduct(vUp)),
//        (p11, p11.DotProduct(vRight), p11.DotProduct(vUp))
//    };
//            XYZ topLeft = corners.OrderByDescending(c => c.yu).ThenBy(c => c.xr).First().pt;

//            // Grid marching: columns to the right (+ux), rows downward (-uy)
//            XYZ colDir = ux;
//            XYZ rowDir = uy.Negate();

//            // Steps from local extents (rotation/scale agnostic) minus overlap
//            double stepX = Math.Max(0.0, widthLocal - overlapX);
//            double stepY = Math.Max(0.0, heightLocal - overlapY);

//            const double EPS = 1e-9;
//            if (stepX < EPS) stepX = 0.0;
//            if (stepY < EPS) stepY = 0.0;

//            IList<ElementId> result = new List<ElementId>();

//            using (Transaction tCreate = new Transaction(doc, "Copy Scope Boxes"))
//            {
//                tCreate.Start();

//                // Original is the top-left cell
//                result.Add(exemplarScopeBox.Id);

//                const bool DEBUG_DRAW = true;
//                if (DEBUG_DRAW)
//                    DebugDraw.DrawCrosshairAtCorners(doc, v, topLeft, ux, widthLocal, uy, heightLocal, "R1C1");

//                for (int r = 0; r < rows; ++r)
//                {
//                    for (int c = 0; c < cols; ++c)
//                    {
//                        if (r == 0 && c == 0) continue;

//                        XYZ delta = colDir.Multiply(stepX * c).Add(rowDir.Multiply(stepY * r));

//                        if (DEBUG_DRAW)
//                            DebugDraw.DrawCrosshairAtCorners(doc, v, topLeft + delta, ux, widthLocal, uy, heightLocal, $"R{r + 1}C{c + 1}");

//                        ICollection<ElementId> ids = ElementTransformUtils.CopyElement(doc, exemplarScopeBox.Id, delta);
//                        if (ids != null && ids.Count > 0)
//                        {
//                            ElementId newId = ids.First();
//                            result.Add(newId);
//                            TryRename(doc.GetElement(newId), baseName, r, c);
//                        }
//                    }
//                }

//                tCreate.Commit();
//            }

//            return result;
//        }



//        private static void TryRename(Element e, string baseName, int row, int col)
//        {
//            if (e == null) return;
//            Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
//            if (p != null && !p.IsReadOnly)
//            {
//                p.Set($"{baseName} R{row + 1}C{col + 1}");
//            }
//        }
//    }

//    // -------- Reusable Debug Drawing Helpers --------
//    internal static class DebugDraw
//    {
//        // === Public entry points ==================================================

//        /// <summary>
//        /// Draw a crosshair at 'center' with an optional XYZ label.
//        /// Assumes an open transaction. Works in any view that supports detail curves.
//        /// </summary>
//        public static void DrawCrosshair(
//            Document doc,
//            View view,
//            XYZ center,
//            double size,
//            string note = null,
//            bool labelModelXYZ = true,
//            bool labelViewRU = false,
//            int precision = 3)
//        {
//            if (doc == null || view == null || center == null) return;
//            if (size <= 0) size = 1.0;

//            // View axes (2D)
//            XYZ right = view.RightDirection.Normalize();
//            XYZ up = view.UpDirection.Normalize();
//            XYZ nrm = view.ViewDirection.Normalize();

//            // Crosshair endpoints
//            double half = size * 0.5;
//            XYZ pR1 = center - right.Multiply(half);
//            XYZ pR2 = center + right.Multiply(half);
//            XYZ pU1 = center - up.Multiply(half);
//            XYZ pU2 = center + up.Multiply(half);

//            // Sketch plane through the point, normal to the view
//            Plane plane = Plane.CreateByNormalAndOrigin(nrm, center);
//            using (SketchPlane sp = SketchPlane.Create(doc, plane))
//            {
//                Line h = Line.CreateBound(pR1, pR2);
//                Line v = Line.CreateBound(pU1, pU2);

//                doc.Create.NewDetailCurve(view, h);
//                doc.Create.NewDetailCurve(view, v);
//            }

//            // Compose label text
//            string label = note;

//            if (labelModelXYZ)
//            {
//                string m = FormatModelXYZ(doc, center, precision);
//                label = string.IsNullOrEmpty(label) ? m : $"{label}  {m}";
//            }
//            if (labelViewRU)
//            {
//                string ru = FormatViewRU(center, view, precision);
//                label = string.IsNullOrEmpty(label) ? ru : $"{label}  {ru}";
//            }

//            if (!string.IsNullOrWhiteSpace(label))
//            {
//                // Tiny offset so note does not sit on top of the crosshair
//                XYZ notePt = center + up.Multiply(size * 0.6);

//#if REVIT2020
//            // Any available text type
//            var fec = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType));
//            TextNoteType tnt = fec.FirstElement() as TextNoteType;
//            TextNote.Create(doc, view.Id, notePt, label, tnt.Id);
//#else
//                var fec = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType));
//                TextNoteType tnt = fec.FirstElement() as TextNoteType;
//                TextNoteOptions opt = new TextNoteOptions(tnt?.Id ?? ElementId.InvalidElementId);
//                TextNote.Create(doc, view.Id, notePt, label, opt);
//#endif
//            }
//        }

//        /// <summary>
//        /// Draw crosshairs at the four corners of a rectangle defined by origin + ux/uy axes and lengths.
//        /// </summary>
//        public static void DrawCrosshairAtCorners(
//            Document doc,
//            View view,
//            XYZ origin,
//            XYZ ux, double lenX,
//            XYZ uy, double lenY,
//            string notePrefix = null,
//            double size = 1.0,
//            bool labelModelXYZ = true,
//            bool labelViewRU = false,
//            int precision = 3)
//        {
//            XYZ p00 = origin;
//            XYZ p10 = origin + ux.Multiply(lenX);
//            XYZ p01 = origin + uy.Multiply(lenY);
//            XYZ p11 = origin + ux.Multiply(lenX) + uy.Multiply(lenY);

//            DrawCrosshair(doc, view, p00, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p10, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p01, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//            DrawCrosshair(doc, view, p11, size, notePrefix, labelModelXYZ, labelViewRU, precision);
//        }

//        /// <summary>
//        /// Draw crosshairs for an arbitrary set of points.
//        /// </summary>
//        public static void DrawCrosshairs(
//            Document doc,
//            View view,
//            IEnumerable<XYZ> points,
//            double size,
//            string notePrefix = null,
//            bool labelModelXYZ = true,
//            bool labelViewRU = false,
//            int precision = 3)
//        {
//            if (points == null) return;
//            int i = 0;
//            foreach (var p in points)
//            {
//                string tag = string.IsNullOrEmpty(notePrefix) ? null : $"{notePrefix}-{++i}";
//                DrawCrosshair(doc, view, p, size, tag, labelModelXYZ, labelViewRU, precision);
//            }
//        }

//        // === Formatting helpers ===================================================

//        private static string FormatModelXYZ(Document doc, XYZ p, int precision)
//        {
//            // Display in feet (project typical), cross-version safe
//#if REVIT2020
//        double xf = UnitUtils.ConvertFromInternalUnits(p.X, DisplayUnitType.DUT_FEET);
//        double yf = UnitUtils.ConvertFromInternalUnits(p.Y, DisplayUnitType.DUT_FEET);
//        double zf = UnitUtils.ConvertFromInternalUnits(p.Z, DisplayUnitType.DUT_FEET);
//#else
//            double xf = UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Feet);
//            double yf = UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Feet);
//            double zf = UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Feet);
//#endif
//            string fmt = "F" + Math.Max(0, precision).ToString();
//            return $"({xf.ToString(fmt)}, {yf.ToString(fmt)}, {zf.ToString(fmt)})";
//        }

//        private static string FormatViewRU(XYZ p, View v, int precision)
//        {
//            XYZ r = v.RightDirection.Normalize();
//            XYZ u = v.UpDirection.Normalize();
//            double xr = p.DotProduct(r);
//            double yu = p.DotProduct(u);

//            // Convert to feet for readability
//#if REVIT2020
//        xr = UnitUtils.ConvertFromInternalUnits(xr, DisplayUnitType.DUT_FEET);
//        yu = UnitUtils.ConvertFromInternalUnits(yu, DisplayUnitType.DUT_FEET);
//#else
//            xr = UnitUtils.ConvertFromInternalUnits(xr, UnitTypeId.Feet);
//            yu = UnitUtils.ConvertFromInternalUnits(yu, UnitTypeId.Feet);
//#endif
//            string fmt = "F" + Math.Max(0, precision).ToString();
//            return $"[R={xr.ToString(fmt)}, U={yu.ToString(fmt)}]";
//        }
//    }

//    internal class ScopeBoxSelectionFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
//    {
//        public bool AllowElement(Element elem)
//            => elem?.Category != null && elem.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest));
//        public bool AllowReference(Reference refer, XYZ pos) => true;
//    }
//}




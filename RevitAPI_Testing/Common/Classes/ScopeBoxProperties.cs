#region Namespaces
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;
#endregion

// ============================================================================
//  Revit 2020–2026
//  ORH – ScopeBoxInspector (curve-geometry first)
//  - Extracts the four real scope-box corners from CURVE endpoints (not faces).
//  - Handles nested GeometryInstance with accumulated transforms.
//  - Width/Height 2D are measured by edge-midpoint distances (as requested).
// ============================================================================
/// <summary>
/// Snapshot of a scope box’ geometry and orientation as seen in a plan/ceiling plan view.
/// <para>
/// Instances are produced by <c>ScopeBoxInspector.Inspect</c>, which reads the scope box
/// **curve endpoints** (not face/BoundingBox corners), applies any instance transforms,
/// projects to the view plane, and orders the four true corners relative to the view’s
/// Right/Up axes. This makes all values rotation-proof.
/// </para>
/// <para>
/// Key guarantees:
/// </para>
/// <list type="bullet">
///   <item>
///     <description><b>Width2D/Height2D</b> are measured between **edge midpoints**
///     (MidLeft↔MidRight and MidTop↔MidBottom), not from the oriented bounding box,
///     so they remain correct at any angle.</description>
///   </item>
///   <item>
///     <description><b>DirRight2D/DirDown2D</b> are unit vectors in the view plane aligned with
///     the box’s visible top and left edges (TL→TR and TL→BL), suitable for tiling and offsets.</description>
///   </item>
///   <item>
///     <description><b>Corners/Midpoints</b> are world points ordered for the view:
///     TopLeft, TopRight, BottomLeft, BottomRight and MidTop/Right/Bottom/Left.</description>
///   </item>
/// </list>
/// <para>
/// Typical use: build rotation-aware grids or adjacent copies. For example,
/// <c>stepX = Width2D − overlap</c> along <c>DirRight2D</c>, and
/// <c>stepY = Height2D − overlap</c> along <c>−DirDown2D</c>. Use
/// <see cref="StepX(double)"/> / <see cref="StepY(double)"/> helpers to compute these.
/// </para>
/// <para>
/// This is a read-only data container (no transactions). Identity and diagnostic
/// fields (e.g., <see cref="BoundingBox"/>/<see cref="LocalToWorld"/>) are included for
/// completeness but are not used for 2D sizing.
/// </para>
/// </summary>

namespace RevitAPI_Testing
{
    public class ScopeBoxProperties
    {
        // Identity
        public ElementId Id { get; internal set; }
        public string Name { get; internal set; }
        public ElementId ViewId { get; internal set; }
        public bool WasPinned { get; internal set; }

        // Raw (diagnostic only)
        public BoundingBoxXYZ BoundingBox { get; internal set; }
        public Transform LocalToWorld { get; internal set; }

        // View frame
        public XYZ ViewRight { get; internal set; }
        public XYZ ViewUp { get; internal set; }
        public XYZ ViewNormal { get; internal set; }
        public Plane ViewPlane { get; internal set; }

        // Centers & extents
        public XYZ CenterWorld { get; internal set; }
        public double WidthLocal { get; internal set; }   // from oriented BB (scale-free only)
        public double HeightLocal { get; internal set; }  // from oriented BB (scale-free only)
        public double Width2D { get; internal set; }      // measured MidLeft↔MidRight
        public double Height2D { get; internal set; }     // measured MidTop↔MidBottom

        // Ordered corners (view-ordered)
        public XYZ CornerTopLeft { get; internal set; }
        public XYZ CornerTopRight { get; internal set; }
        public XYZ CornerBottomLeft { get; internal set; }
        public XYZ CornerBottomRight { get; internal set; }

        // Side midpoints
        public XYZ MidTop { get; internal set; }
        public XYZ MidRight { get; internal set; }
        public XYZ MidBottom { get; internal set; }
        public XYZ MidLeft { get; internal set; }

        // 2D edge vectors (view plane)
        public XYZ EdgeRight2D { get; internal set; }
        public XYZ EdgeDown2D { get; internal set; }
        public XYZ DirRight2D { get; internal set; }
        public XYZ DirDown2D { get; internal set; }

        // Orientation
        public double AngleToViewRight { get; internal set; }
        public double AngleDegrees => AngleToViewRight * 180.0 / Math.PI;

        // Helpers
        public double StepX(double overlap) => Math.Max(0.0, Width2D - overlap);
        public double StepY(double overlap) => Math.Max(0.0, Height2D - overlap);

        // Diagnostics
        public IReadOnlyList<XYZ> CornersWorldRaw { get; internal set; }
        public IReadOnlyList<XYZ> CornersViewOrdered { get; internal set; }
        public IReadOnlyList<XYZ> SideMidpointsOrdered { get; internal set; }
        public double Tolerance { get; internal set; }
        public bool IsDegenerate { get; internal set; }
        public bool IsAxisAligned { get; internal set; }
    }

    public static class ScopeBoxInspector
    {
        /// <summary>
        /// Inspect a scope box using its CURVE endpoints to derive true corners and 2D axes.
        /// </summary>
        public static ScopeBoxProperties Inspect(Element scopeBox, View planView)
        {
            if (scopeBox == null) throw new ArgumentNullException(nameof(scopeBox));
            if (planView == null) throw new ArgumentNullException(nameof(planView));
            if (scopeBox.Category == null || !scopeBox.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest)))
                throw new ArgumentException("Element is not a Scope Box (OST_VolumeOfInterest).", nameof(scopeBox));

            // ---- View basis (Right / Up / Normal), normalized for stable math
            XYZ viewRight = planView.RightDirection.Normalize();
            XYZ viewUp = planView.UpDirection.Normalize();
            XYZ viewNorm = planView.ViewDirection.Normalize();

            // ---- Oriented bounding box (diagnostics / local sizes only)
            BoundingBoxXYZ bb = scopeBox.get_BoundingBox(null)
                ?? throw new InvalidOperationException("Scope box has no BoundingBoxXYZ.");
            Transform localToWorld = bb.Transform ?? Transform.Identity;
            double widthLocal = Math.Abs(bb.Max.X - bb.Min.X);
            double heightLocal = Math.Abs(bb.Max.Y - bb.Min.Y);

            // ---- CURVE endpoints: collect all unique points in the plan plane
            // We traverse the element geometry recursively and gather endpoints of curves
            // that live in (or parallel to) the plan view plane.
            var curvePoints = CollectPlanCurveEndpoints(scopeBox, viewNorm);

            // In some models, extra points can be present. Use RU-space extrema to find the four corners.
            if (curvePoints.Count < 4)
                throw new InvalidOperationException("Failed to read scope box planar curve geometry (need ≥ 4 endpoints).");

            // Convert to RU coordinates for robust “left/right/top/bottom” detection
            var ruPts = curvePoints
                .Select(p => new RUPoint(p, p.DotProduct(viewRight), p.DotProduct(viewUp)))
                .ToList();

            // Unique points by RU tolerance
            ruPts = RUPoint.MakeUnique(ruPts, 1e-9);

            if (ruPts.Count < 4)
                throw new InvalidOperationException("Planar curve geometry did not yield 4 unique corner candidates.");

            // Find the extreme RU values
            double minR = ruPts.Min(q => q.R);
            double maxR = ruPts.Max(q => q.R);
            double minU = ruPts.Min(q => q.U);
            double maxU = ruPts.Max(q => q.U);

            // Pick the closest RU point to each extreme combination:
            // (minR,maxU)=TopLeft, (maxR,maxU)=TopRight, (minR,minU)=BottomLeft, (maxR,minU)=BottomRight
            XYZ cornerTopLeft = RUPoint.ClosestTo(ruPts, minR, maxU).P;
            XYZ cornerTopRight = RUPoint.ClosestTo(ruPts, maxR, maxU).P;
            XYZ cornerBottomLeft = RUPoint.ClosestTo(ruPts, minR, minU).P;
            XYZ cornerBottomRight = RUPoint.ClosestTo(ruPts, maxR, minU).P;

            // ---- Build in-plane edge vectors strictly in the view plane
            XYZ edgeRight2D = ProjectToPlane(cornerTopRight - cornerTopLeft, viewNorm);
            XYZ edgeDown2D = ProjectToPlane(cornerBottomLeft - cornerTopLeft, viewNorm);
            if (edgeDown2D.DotProduct(viewUp) > 0.0) edgeDown2D = edgeDown2D.Negate(); // enforce “down”

            // Unit directions
            double rightLen = edgeRight2D.GetLength();
            double downLen = edgeDown2D.GetLength();
            if (rightLen <= 1e-12 || downLen <= 1e-12)
                throw new InvalidOperationException("Scope box produced degenerate 2D edge length.");
            XYZ dirRight2D = edgeRight2D / rightLen;
            XYZ dirDown2D = edgeDown2D / downLen;

            // ---- Midpoints and center from true corners
            XYZ midTop = (cornerTopLeft + cornerTopRight) * 0.5;
            XYZ midRight = (cornerTopRight + cornerBottomRight) * 0.5;
            XYZ midBottom = (cornerBottomLeft + cornerBottomRight) * 0.5;
            XYZ midLeft = (cornerTopLeft + cornerBottomLeft) * 0.5;

            XYZ centerWorld = (cornerTopLeft + cornerTopRight + cornerBottomLeft + cornerBottomRight) * 0.25;

            // ---- Width/Height 2D measured the way you requested (midpoint to midpoint)
            double width2D = midLeft.DistanceTo(midRight);    // not from BB; from geometry
            double height2D = midTop.DistanceTo(midBottom);    // not from BB; from geometry

            // ---- Orientation (signed angle to view Right)
            double angleToRight = SignedAngleInPlane(dirRight2D, viewRight, viewNorm);

            // ---- Pack up properties
            var props = new ScopeBoxProperties
            {
                Id = scopeBox.Id,
                Name = scopeBox.Name,
                ViewId = planView.Id,
                WasPinned = scopeBox.Pinned,

                BoundingBox = bb,
                LocalToWorld = localToWorld,

                ViewRight = viewRight,
                ViewUp = viewUp,
                ViewNormal = viewNorm,
                ViewPlane = Plane.CreateByNormalAndOrigin(viewNorm, centerWorld),

                CenterWorld = centerWorld,
                WidthLocal = widthLocal,
                HeightLocal = heightLocal,
                Width2D = width2D,
                Height2D = height2D,

                CornerTopLeft = cornerTopLeft,
                CornerTopRight = cornerTopRight,
                CornerBottomLeft = cornerBottomLeft,
                CornerBottomRight = cornerBottomRight,

                MidTop = midTop,
                MidRight = midRight,
                MidBottom = midBottom,
                MidLeft = midLeft,

                EdgeRight2D = edgeRight2D,
                EdgeDown2D = edgeDown2D,
                DirRight2D = dirRight2D,
                DirDown2D = dirDown2D,

                AngleToViewRight = angleToRight,
                Tolerance = 1e-9,
                IsDegenerate = (width2D <= 1e-9 || height2D <= 1e-9),
                IsAxisAligned = (Math.Abs(angleToRight) <= (Math.PI / 180.0 * 0.01))
            };

            props.CornersWorldRaw = new List<XYZ> { cornerTopLeft, cornerTopRight, cornerBottomLeft, cornerBottomRight }.AsReadOnly();
            props.CornersViewOrdered = new List<XYZ> { cornerTopLeft, cornerTopRight, cornerBottomLeft, cornerBottomRight }.AsReadOnly();
            props.SideMidpointsOrdered = new List<XYZ> { midTop, midRight, midBottom, midLeft }.AsReadOnly();

            return props;
        }



        /// <summary>
        /// Duplicate to the RIGHT of the original so the COPY's LEFT-edge midpoint
        /// lands on the ORIGINAL's RIGHT-edge midpoint (then apply overlap).
        /// Positive overlap = interpenetrate; negative = gap.
        /// </summary>
        public static ScopeBoxProperties DuplicateToRightByLeftEdge(
            Document doc, View planView, ScopeBoxProperties original, double overlap)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (planView == null) throw new ArgumentNullException(nameof(planView));
            if (original == null) throw new ArgumentNullException(nameof(original));

            // Span from original LEFT edge → RIGHT edge (right-ish axis).
            // Using the exact midpoint-to-midpoint vector guarantees precise touching at overlap=0.
            return DuplicateByMidpointSpan(
                doc, planView, original,
                fromMidpoint: original.MidLeft,     // start at left
                toMidpoint: original.MidRight,    // direction toward right
                overlap: overlap);
        }

        /// <summary>
        /// Convenience overload that inspects the element first, then duplicates.
        /// </summary>
        public static ScopeBoxProperties DuplicateToRightByLeftEdge(
            Document doc, View planView, Element scopeBoxElement, double overlap)
        {
            if (scopeBoxElement == null) throw new ArgumentNullException(nameof(scopeBoxElement));
            var props = Inspect(scopeBoxElement, planView);
            return DuplicateToRightByLeftEdge(doc, planView, props, overlap);
        }


        // ======================================================================
        //  ScopeBox movers – midpoint-span method (rotation-proof)
        //  Positive overlap = interpenetrate; Negative overlap = gap
        // ======================================================================

        /// <summary>
        /// Duplicate to the LEFT of the original so the COPY's RIGHT-edge midpoint
        /// lands on the ORIGINAL's LEFT-edge midpoint (then apply overlap).
        /// </summary>
        public static ScopeBoxProperties DuplicateToLeftByRightEdge(
            Document doc, View planView, ScopeBoxProperties original, double overlap)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (planView == null) throw new ArgumentNullException(nameof(planView));
            if (original == null) throw new ArgumentNullException(nameof(original));

            // Span from original RIGHT edge → LEFT edge (left-ish axis)
            return DuplicateByMidpointSpan(
                doc, planView, original,
                fromMidpoint: original.MidRight,    // start at original right
                toMidpoint: original.MidLeft,     // direction toward left
                overlap: overlap);
        }

        /// <summary>Overload: element version.</summary>
        public static ScopeBoxProperties DuplicateToLeftByRightEdge(
            Document doc, View planView, Element scopeBoxElement, double overlap)
        {
            if (scopeBoxElement == null) throw new ArgumentNullException(nameof(scopeBoxElement));
            var props = Inspect(scopeBoxElement, planView);
            return DuplicateToLeftByRightEdge(doc, planView, props, overlap);
        }

        /// <summary>
        /// Duplicate DOWN so the COPY's TOP-edge midpoint lands on the ORIGINAL's
        /// BOTTOM-edge midpoint (then apply overlap).
        /// </summary>
        public static ScopeBoxProperties DuplicateDownByTopEdge(
            Document doc, View planView, ScopeBoxProperties original, double overlap)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (planView == null) throw new ArgumentNullException(nameof(planView));
            if (original == null) throw new ArgumentNullException(nameof(original));

            // Span from original TOP edge → BOTTOM edge (down-ish axis)
            return DuplicateByMidpointSpan(
                doc, planView, original,
                fromMidpoint: original.MidTop,       // start at top
                toMidpoint: original.MidBottom,    // direction toward bottom
                overlap: overlap);
        }

        /// <summary>Overload: element version.</summary>
        public static ScopeBoxProperties DuplicateDownByTopEdge(
            Document doc, View planView, Element scopeBoxElement, double overlap)
        {
            if (scopeBoxElement == null) throw new ArgumentNullException(nameof(scopeBoxElement));
            var props = Inspect(scopeBoxElement, planView);
            return DuplicateDownByTopEdge(doc, planView, props, overlap);
        }

        /// <summary>
        /// Duplicate UP so the COPY's BOTTOM-edge midpoint lands on the ORIGINAL's
        /// TOP-edge midpoint (then apply overlap).
        /// </summary>
        public static ScopeBoxProperties DuplicateUpByBottomEdge(
            Document doc, View planView, ScopeBoxProperties original, double overlap)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (planView == null) throw new ArgumentNullException(nameof(planView));
            if (original == null) throw new ArgumentNullException(nameof(original));

            // Span from original BOTTOM edge → TOP edge (up-ish axis)
            return DuplicateByMidpointSpan(
                doc, planView, original,
                fromMidpoint: original.MidBottom,  // start at bottom
                toMidpoint: original.MidTop,     // direction toward top
                overlap: overlap);
        }

        /// <summary>Overload: element version.</summary>
        public static ScopeBoxProperties DuplicateUpByBottomEdge(
            Document doc, View planView, Element scopeBoxElement, double overlap)
        {
            if (scopeBoxElement == null) throw new ArgumentNullException(nameof(scopeBoxElement));
            var props = Inspect(scopeBoxElement, planView);
            return DuplicateUpByBottomEdge(doc, planView, props, overlap);
        }

        // ----------------------------------------------------------------------
        // Shared mover core – uses the true vector between edge midpoints.
        // This guarantees “just touching” at overlap=0 with no rounding gap.
        // ----------------------------------------------------------------------
        private static ScopeBoxProperties DuplicateByMidpointSpan(
            Document doc,
            View planView,
            ScopeBoxProperties original,
            XYZ fromMidpoint,
            XYZ toMidpoint,
            double overlap)
        {
            // 1) Exact span vector between the two relevant edge midpoints (world)
            XYZ spanVectorWorld = toMidpoint - fromMidpoint;

            // 2) Project to the plan view plane (pure 2D move in the view)
            XYZ n = original.ViewNormal.Normalize();
            spanVectorWorld = spanVectorWorld - n.Multiply(spanVectorWorld.DotProduct(n));

            double spanLength = spanVectorWorld.GetLength();
            if (spanLength <= 1e-12)
                throw new InvalidOperationException("Scope box midpoint span is degenerate.");

            // 3) Unit axis along the span
            XYZ axis = spanVectorWorld / spanLength;

            // 4) Final move = exact span minus overlap (positive overlap = interpenetrate)
            //    Add a microscopic nudge only when overlap is effectively zero to defeat hairline gaps.
            const double NUDGE = 1e-6; // feet (~0.0003 mm)
            bool zeroish = Math.Abs(overlap) < 1e-9;
            double moveDistance = spanLength - overlap + (zeroish ? NUDGE : 0.0);

            // Optional safety clamp if users type huge overlaps (comment out if you prefer no clamp)
            // double maxSafe = spanLength - 1e-9;
            // if (moveDistance < 0) moveDistance = 0;            // don't cross back through the original
            // if (moveDistance > maxSafe) moveDistance = maxSafe; // don't fully pass beyond opposite edge

            XYZ translation = axis.Multiply(moveDistance);

            // 5) Copy (initially coincident) and move
            ICollection<ElementId> newIds = ElementTransformUtils.CopyElement(doc, original.Id, XYZ.Zero);
            if (newIds == null || newIds.Count == 0)
                throw new InvalidOperationException("CopyElement failed to create a new scope box.");

            ElementId newId = newIds.First();
            if (translation.GetLength() > 1e-12)
                ElementTransformUtils.MoveElement(doc, newId, translation);

            // 6) Return fresh properties for the new copy (geometry-first Inspect)
            return Inspect(doc.GetElement(newId), planView);
        }





        // =========================== Geometry (curves) ===========================

        /// <summary>
        /// Collects unique endpoints of curves that lie in / parallel to the plan view plane.
        /// Applies instance transforms recursively. Returns world points.
        /// </summary>
        private static List<XYZ> CollectPlanCurveEndpoints(Element e, XYZ viewNormal)
        {
            var results = new List<XYZ>();
            var opts = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement root = e.get_Geometry(opts);
            if (root != null)
                CollectCurveEndpointsRecursive(root, Transform.Identity, viewNormal.Normalize(), results);

            // Deduplicate by small model-space tolerance
            return results.Distinct(new XyzEqualityComparer(1e-9)).ToList();
        }

        /// <summary>
        /// Recursively traverse geometry, accumulate instance transforms,
        /// collect endpoints of curves that are (near) coplanar with the plan view plane.
        /// </summary>
        private static void CollectCurveEndpointsRecursive(
            GeometryElement ge, Transform acc, XYZ viewNormal, List<XYZ> outPts)
        {
            foreach (GeometryObject go in ge)
            {
                switch (go)
                {
                    case GeometryInstance gi:
                        Transform tNext = acc.Multiply(gi.Transform);
                        GeometryElement inst = gi.GetInstanceGeometry();
                        if (inst != null)
                            CollectCurveEndpointsRecursive(inst, tNext, viewNormal, outPts);
                        break;

                    case Curve c:
                        {
                            // Filter to curves that live in / parallel to the plan plane:
                            // i.e., their tangent is perpendicular to the view normal.
                            // For lines this is exact; for arcs/splines, use endpoints only.
                            XYZ dir = TryGetCurveTangent(c);
                            if (dir != null && Math.Abs(dir.Normalize().DotProduct(viewNormal)) < 1e-6)
                            {
                                outPts.Add(acc.OfPoint(c.GetEndPoint(0)));
                                outPts.Add(acc.OfPoint(c.GetEndPoint(1)));
                            }
                            break;
                        }

                    case Solid s:
                        // Ignore solids here; many scope boxes present as wireframe
                        break;

                        // Other types ignored.
                }
            }
        }

        /// <summary>
        /// Returns a representative tangent direction for a curve if available (e.g., for Line).
        /// If unavailable, returns null and the caller will still use endpoints.
        /// </summary>
        private static XYZ TryGetCurveTangent(Curve c)
        {
            if (c is Line ln)
                return (ln.GetEndPoint(1) - ln.GetEndPoint(0));
            // For arcs/splines we can’t reliably use a single tangent; endpoints suffice.
            return new XYZ(1, 0, 0); // dummy non-parallel vector to avoid filtering them out entirely
        }

        // ============================== Math helpers ============================

        private static XYZ ProjectToPlane(XYZ v, XYZ planeNormal)
        {
            XYZ n = planeNormal.Normalize();
            return v - n.Multiply(v.DotProduct(n));
        }

        private static double SignedAngleInPlane(XYZ a, XYZ b, XYZ n)
        {
            XYZ aN = a.Normalize();
            XYZ bN = b.Normalize();
            double dot = Math.Min(1.0, Math.Max(-1.0, aN.DotProduct(bN)));
            double ang = Math.Acos(dot);
            double sign = n.Normalize().DotProduct(aN.CrossProduct(bN));
            return sign >= 0 ? ang : -ang;
        }

        private static bool Almost(XYZ a, XYZ b, double tol = 1e-9)
            => a != null && b != null && a.DistanceTo(b) <= tol;

        private sealed class XyzEqualityComparer : IEqualityComparer<XYZ>
        {
            private readonly double _tol;
            public XyzEqualityComparer(double tol) { _tol = tol; }
            public bool Equals(XYZ x, XYZ y) => x.DistanceTo(y) <= _tol;
            public int GetHashCode(XYZ v)
            {
                double s = 1.0 / _tol;
                unchecked
                {
                    int hx = (int)Math.Round(v.X * s);
                    int hy = (int)Math.Round(v.Y * s);
                    int hz = (int)Math.Round(v.Z * s);
                    return hx * 73856093 ^ hy * 19349663 ^ hz * 83492791;
                }
            }
        }

        // ======================== RU helper types ==============================
        private sealed class RUPoint
        {
            public XYZ P { get; }
            public double R { get; }
            public double U { get; }
            public RUPoint(XYZ p, double r, double u) { P = p; R = r; U = u; }

            public static List<RUPoint> MakeUnique(IEnumerable<RUPoint> pts, double tol)
            {
                var list = new List<RUPoint>();
                foreach (var p in pts)
                {
                    if (!list.Any(q => Math.Abs(p.R - q.R) <= tol && Math.Abs(p.U - q.U) <= tol))
                        list.Add(p);
                }
                return list;
            }

            public static RUPoint ClosestTo(IEnumerable<RUPoint> pts, double r, double u)
            {
                RUPoint best = null;
                double bestD2 = double.MaxValue;
                foreach (var p in pts)
                {
                    double dr = p.R - r;
                    double du = p.U - u;
                    double d2 = dr * dr + du * du;
                    if (d2 < bestD2) { bestD2 = d2; best = p; }
                }
                return best;
            }
        }
    }
}



// ============================================================================
// this is the previous version of the code before the key change mentioned above

//#region Namespaces
//using System;
//using System.Collections.Generic;
//using System.Linq;

//using Autodesk.Revit.DB;
//#endregion

//// ============================================================================
////  Revit 2020–2026
////  ORH – ScopeBoxInspector : extracts robust 2D (plan) and 3D properties
////  from a Scope Box element (BuiltInCategory.OST_VolumeOfInterest).
////
////  Usage:
////      var props = ScopeBoxInspector.Inspect(scopeBoxElement, planView);
////      // props.CenterWorld, props.CornerTopLeft, props.Width2D, props.AngleDegrees, ...
////
////  Notes:
////  - This class assumes the call happens in a Floor Plan or Ceiling Plan.
////  - All “2D” vectors/lengths live in the active view plane (Right/Up).
////  - Width/Height “Local” come from the box’s own local extents (scale-free).
////  - Width/Height “2D” come from the projected visible edges in the view plane.
////  - The “Top/Right/Bottom/Left” naming is relative to the active view.
////  - No transactions are required; this is a read-only inspector.
//// ============================================================================

//namespace RevitAPI_Testing
//{
//    /// <summary>
//    /// Immutable data container holding everything commonly needed
//    /// to work with a Scope Box in a plan view.
//    /// </summary>
//    public class ScopeBoxProperties
//    {
//        // ---------------------- Identity ----------------------

//        /// <summary>Element id of the scope box.</summary>
//        public ElementId Id { get; internal set; }

//        /// <summary>Display name (from element.Name where available).</summary>
//        public string Name { get; internal set; }

//        /// <summary>Id of the view used for the extraction (plan/rcp).</summary>
//        public ElementId ViewId { get; internal set; }

//        /// <summary>Was the element pinned when inspected (useful for ops planning).</summary>
//        public bool WasPinned { get; internal set; }

//        // ------------------- Raw geometry ---------------------

//        /// <summary>BoundingBox as returned by Revit (may be oriented via Transform).</summary>
//        public BoundingBoxXYZ BoundingBox { get; internal set; }

//        /// <summary>Local-to-World transform for the scope box’ oriented bounding frame.</summary>
//        public Transform LocalToWorld { get; internal set; }

//        // -------------------- View frame ----------------------

//        /// <summary>View right direction (unit).</summary>
//        public XYZ ViewRight { get; internal set; }

//        /// <summary>View up direction (unit).</summary>
//        public XYZ ViewUp { get; internal set; }

//        /// <summary>View normal (unit) – pointing out of the view (screen-toward user).</summary>
//        public XYZ ViewNormal { get; internal set; }

//        /// <summary>Convenience plane through the center with normal = ViewNormal.</summary>
//        public Plane ViewPlane { get; internal set; }

//        // ---------------- Centers & Extents -------------------

//        /// <summary>World-space center of the visible face (averaged from bb min/max, then transformed).</summary>
//        public XYZ CenterWorld { get; internal set; }

//        /// <summary>Scale-free width measured in the scope box local X: bb.Max.X − bb.Min.X.</summary>
//        public double WidthLocal { get; internal set; }

//        /// <summary>Scale-free height measured in the scope box local Y: bb.Max.Y − bb.Min.Y.</summary>
//        public double HeightLocal { get; internal set; }

//        /// <summary>Visible width in the view plane – length of the rightward edge.</summary>
//        public double Width2D { get; internal set; }

//        /// <summary>Visible height in the view plane – length of the downward edge.</summary>
//        public double Height2D { get; internal set; }

//        // --------------- Ordered corners (view) ----------------

//        /// <summary>Top-Left corner (max Up, then min Right) in world coords.</summary>
//        public XYZ CornerTopLeft { get; internal set; }

//        /// <summary>Top-Right corner (same row as TL, farther to the right).</summary>
//        public XYZ CornerTopRight { get; internal set; }

//        /// <summary>Bottom-Left corner (below TL, same column).</summary>
//        public XYZ CornerBottomLeft { get; internal set; }

//        /// <summary>Bottom-Right corner (diagonal from TL).</summary>
//        public XYZ CornerBottomRight { get; internal set; }

//        // --------------- Side midpoints (view) -----------------

//        /// <summary>Midpoint of the top edge (between TL and TR).</summary>
//        public XYZ MidTop { get; internal set; }

//        /// <summary>Midpoint of the right edge (between TR and BR).</summary>
//        public XYZ MidRight { get; internal set; }

//        /// <summary>Midpoint of the bottom edge (between BL and BR).</summary>
//        public XYZ MidBottom { get; internal set; }

//        /// <summary>Midpoint of the left edge (between TL and BL).</summary>
//        public XYZ MidLeft { get; internal set; }

//        // ---------------- Edge vectors (2D) --------------------

//        /// <summary>Projected (2D) edge from TL to TR in the view plane (not unit).</summary>
//        public XYZ EdgeRight2D { get; internal set; }

//        /// <summary>Projected (2D) edge from TL to BL in the view plane (points downward; not unit).</summary>
//        public XYZ EdgeDown2D { get; internal set; }

//        /// <summary>Unit direction of EdgeRight2D.</summary>
//        public XYZ DirRight2D { get; internal set; }

//        /// <summary>Unit direction of EdgeDown2D.</summary>
//        public XYZ DirDown2D { get; internal set; }

//        // ------------------- Orientation ----------------------

//        /// <summary>Signed angle (radians) from the visible “X” edge to the view’s Right; CCW about ViewNormal.</summary>
//        public double AngleToViewRight { get; internal set; }

//        /// <summary>AngleToViewRight converted to degrees.</summary>
//        public double AngleDegrees => AngleToViewRight * 180.0 / Math.PI;

//        // -------------------- Tiling helpers ------------------

//        /// <summary>Returns Width2D − overlap (clamped ≥ 0).</summary>
//        public double StepX(double overlap) => Math.Max(0.0, Width2D - overlap);

//        /// <summary>Returns Height2D − overlap (clamped ≥ 0).</summary>
//        public double StepY(double overlap) => Math.Max(0.0, Height2D - overlap);

//        // ------------------ Diagnostics (optional) ------------

//        /// <summary>Unordered world corners as read from BoundingBox/Transform.</summary>
//        public IReadOnlyList<XYZ> CornersWorldRaw { get; internal set; }

//        /// <summary>Ordered world corners in [TL, TR, BL, BR].</summary>
//        public IReadOnlyList<XYZ> CornersViewOrdered { get; internal set; }

//        /// <summary>Ordered side midpoints in [Top, Right, Bottom, Left].</summary>
//        public IReadOnlyList<XYZ> SideMidpointsOrdered { get; internal set; }

//        /// <summary>Generic tolerance used during extraction.</summary>
//        public double Tolerance { get; internal set; }

//        /// <summary>True if width/height are effectively zero in 2D.</summary>
//        public bool IsDegenerate { get; internal set; }

//        /// <summary>True if edges are essentially axis-aligned to the view (0°/90° within tol).</summary>
//        public bool IsAxisAligned { get; internal set; }
//    }

//    /// <summary>
//    /// Static inspector that extracts <see cref="ScopeBoxProperties"/> from a scope box and a plan/rcp view.
//    /// All computations are read-only and side-effect free.
//    /// </summary>
//    public static class ScopeBoxInspector
//    {
//        /// <summary>
//        /// Extracts robust plan-plane properties for a given Scope Box element.
//        /// </summary>
//        /// <param name="scopeBox">Element whose Category must be OST_VolumeOfInterest.</param>
//        /// <param name="planView">Active plan or ceiling plan view that defines the 2D frame.</param>
//        public static ScopeBoxProperties Inspect(Element scopeBox, View planView)
//        {
//            if (scopeBox == null) throw new ArgumentNullException(nameof(scopeBox));
//            if (planView == null) throw new ArgumentNullException(nameof(planView));
//            if (scopeBox.Category == null || !scopeBox.Category.Id.Equals(new ElementId(BuiltInCategory.OST_VolumeOfInterest)))
//                throw new ArgumentException("Element is not a Scope Box (OST_VolumeOfInterest).", nameof(scopeBox));

//            // ------------------ View frame (Right/Up/Normal) ------------------

//            // Normalize all three axes so dot/cross products behave predictably.
//            XYZ viewRight = planView.RightDirection.Normalize();
//            XYZ viewUp = planView.UpDirection.Normalize();
//            XYZ viewNorm = planView.ViewDirection.Normalize();

//            // ------------------ Bounding Box & Transform ----------------------

//            // get_BoundingBox(null) gives the oriented box in model space via a Transform.
//            BoundingBoxXYZ bb = scopeBox.get_BoundingBox(null);
//            if (bb == null) throw new InvalidOperationException("Scope box has no BoundingBoxXYZ.");

//            Transform localToWorld = bb.Transform ?? Transform.Identity;

//            // Local extents in the box’ coordinate system (scale-free).
//            double widthLocal = Math.Abs(bb.Max.X - bb.Min.X);
//            double heightLocal = Math.Abs(bb.Max.Y - bb.Min.Y);

//            // World center of the face we see in plan (Z from bb.Min is fine; it is projected anyway).
//            XYZ centerWorld = localToWorld.OfPoint((bb.Min + bb.Max) * 0.5);

//            // ------------------ Build the four world corners -------------------

//            // These are the four local XY corners, then mapped to world space.
//            var cornersWorldRaw = new List<XYZ>
//            {
//                localToWorld.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)),
//                localToWorld.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z)),
//                localToWorld.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)),
//                localToWorld.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z))
//            };

//            // ------------------ Order corners relative to the view -------------

//            // We choose Top-Left = highest along Up, then leftmost along Right.
//            XYZ cornerTopLeft = cornersWorldRaw
//                .OrderByDescending(p => p.DotProduct(viewUp))
//                .ThenBy(p => p.DotProduct(viewRight))
//                .First();

//            // Fetch the two corners adjacent to Top-Left (the closest two by distance).
//            // Those two define the top edge (to the right) and the left edge (downward).
//            var adjacent = cornersWorldRaw
//                .Where(p => !p.IsAlmostEqualTo(cornerTopLeft))
//                .Select(p => new { P = p, D = (p - cornerTopLeft).GetLength() })
//                .OrderBy(x => x.D)
//                .Take(2)
//                .ToList();

//            XYZ a1 = adjacent[0].P;
//            XYZ a2 = adjacent[1].P;

//            // The “right” neighbor is whichever has larger dot with viewRight.
//            XYZ cornerTopRight = (a1 - cornerTopLeft).DotProduct(viewRight) >= (a2 - cornerTopLeft).DotProduct(viewRight) ? a1 : a2;
//            // The remaining adjacent must be the one below (along down).
//            XYZ cornerBottomLeft = cornerTopRight.IsAlmostEqualTo(a1) ? a2 : a1;

//            // The fourth corner is the one we did not pick yet.
//            XYZ cornerBottomRight = cornersWorldRaw
//                .First(p => !p.IsAlmostEqualTo(cornerTopLeft)
//                         && !p.IsAlmostEqualTo(cornerTopRight)
//                         && !p.IsAlmostEqualTo(cornerBottomLeft));

//            // ------------------ 2D edge vectors in the view plane --------------

//            // Helper to remove the normal component (pure in-plane vector).
//            XYZ ProjectToPlane(XYZ v) => v - viewNorm.Multiply(v.DotProduct(viewNorm));

//            // Edge along the top (rightward) and edge along the left (downward).
//            XYZ edgeRight2D = ProjectToPlane(cornerTopRight - cornerTopLeft);
//            XYZ edgeDown2D = ProjectToPlane(cornerBottomLeft - cornerTopLeft);

//            // Ensure “down” truly points downward (negative Up), flip if needed.
//            if (edgeDown2D.DotProduct(viewUp) > 0) edgeDown2D = edgeDown2D.Negate();

//            // Compute visible 2D sizes and unit directions.
//            double width2D = edgeRight2D.GetLength();
//            double height2D = edgeDown2D.GetLength();
//            if (width2D <= 1e-12 || height2D <= 1e-12)
//                throw new InvalidOperationException("Scope box has degenerate 2D edge length.");

//            XYZ dirRight2D = edgeRight2D.Divide(width2D);
//            XYZ dirDown2D = edgeDown2D.Divide(height2D);

//            // ------------------ Side midpoints (view-ordered) ------------------

//            XYZ midTop = (cornerTopLeft + cornerTopRight) * 0.5;
//            XYZ midRight = (cornerTopRight + cornerBottomRight) * 0.5;
//            XYZ midBottom = (cornerBottomLeft + cornerBottomRight) * 0.5;
//            XYZ midLeft = (cornerTopLeft + cornerBottomLeft) * 0.5;

//            // ------------------ Orientation (angle to view Right) --------------

//            // Compute signed angle between the visible “X edge” (top edge) and the view’s Right axis.
//            double angle = SignedAngleInPlane(dirRight2D, viewRight, viewNorm);

//            // ------------------ Pack up the result -----------------------------

//            var props = new ScopeBoxProperties
//            {
//                // Identity
//                Id = scopeBox.Id,
//                Name = scopeBox.Name,
//                ViewId = planView.Id,
//                WasPinned = scopeBox.Pinned,

//                // Raw
//                BoundingBox = bb,
//                LocalToWorld = localToWorld,

//                // View frame
//                ViewRight = viewRight,
//                ViewUp = viewUp,
//                ViewNormal = viewNorm,
//                ViewPlane = Plane.CreateByNormalAndOrigin(viewNorm, centerWorld),

//                // Centers & extents
//                CenterWorld = centerWorld,
//                WidthLocal = widthLocal,
//                HeightLocal = heightLocal,
//                Width2D = width2D,
//                Height2D = height2D,

//                // Corners (ordered for the view)
//                CornerTopLeft = cornerTopLeft,
//                CornerTopRight = cornerTopRight,
//                CornerBottomLeft = cornerBottomLeft,
//                CornerBottomRight = cornerBottomRight,

//                // Midpoints
//                MidTop = midTop,
//                MidRight = midRight,
//                MidBottom = midBottom,
//                MidLeft = midLeft,

//                // Edge vectors & unit directions in the view plane
//                EdgeRight2D = edgeRight2D,
//                EdgeDown2D = edgeDown2D,
//                DirRight2D = dirRight2D, // 
//                DirDown2D = dirDown2D,

//                // Orientation
//                AngleToViewRight = angle,

//                // Diagnostics
//                CornersWorldRaw = cornersWorldRaw.AsReadOnly(),
//                CornersViewOrdered = new List<XYZ> { cornerTopLeft, cornerTopRight, cornerBottomLeft, cornerBottomRight }.AsReadOnly(),
//                SideMidpointsOrdered = new List<XYZ> { midTop, midRight, midBottom, midLeft }.AsReadOnly(),
//                Tolerance = 1e-9,
//                IsDegenerate = (width2D <= 1e-9 || height2D <= 1e-9),
//                IsAxisAligned = (Math.Abs(angle) <= (Math.PI / 180.0 * 0.01)) // within ~0.01°
//            };

//            return props;
//        }

//        // ========================== Helpers ===============================

//        /// <summary>
//        /// Signed angle from vector a to vector b, measured in the plane with normal n.
//        /// Positive is CCW when looking from the tip of n toward the origin.
//        /// </summary>
//        private static double SignedAngleInPlane(XYZ a, XYZ b, XYZ n)
//        {
//            XYZ aN = a.Normalize();
//            XYZ bN = b.Normalize();
//            double dot = Math.Min(1.0, Math.Max(-1.0, aN.DotProduct(bN))); // clamp for numeric safety
//            double ang = Math.Acos(dot);                                   // 0..pi
//            double s = n.Normalize().DotProduct(aN.CrossProduct(bN));    // sign via right-hand rule
//            return s >= 0 ? ang : -ang;
//        }

//        /// <summary>
//        /// Convenience checker for nearly equal XYZs (model units).
//        /// </summary>
//        private static bool IsAlmostEqualTo(this XYZ a, XYZ b, double tol = 1e-9)
//        {
//            if (a == null || b == null) return false;
//            return a.DistanceTo(b) <= tol;
//        }


//        /// <summary>
//        /// Duplicate the scope box and translate the copy so its LEFT-edge midpoint
//        /// coincides with the ORIGINAL’s RIGHT-edge midpoint, with optional overlap.
//        /// Pure plan-plane translation; no rotation. Works at any box rotation.
//        /// </summary>
//        /// <param name="doc">Owner document.</param>
//        /// <param name="planView">Plan/RCP view (defines Right/Up/Normal).</param>
//        /// <param name="original">ScopeBoxProperties produced by Inspect.</param>
//        /// <param name="overlap">
//        /// Overlap in internal units (feet). 0 = just touch; +value = overlap into original;
//        /// −value = gap. Overlap is measured along the box width axis.
//        /// </param>
//        /// <returns>ScopeBoxProperties for the new copy.</returns>
//        public static ScopeBoxProperties DuplicateToRightByLeftEdge(
//            Document doc,
//            View planView,
//            ScopeBoxProperties original,
//            double overlap)
//        {
//            if (doc == null) throw new ArgumentNullException(nameof(doc));
//            if (planView == null) throw new ArgumentNullException(nameof(planView));
//            if (original == null) throw new ArgumentNullException(nameof(original));

//            // Move along the scope box's WIDTH axis (TL→TR), by visible width minus overlap.
//            // This aligns the copy's left-edge midpoint to the original's right-edge midpoint.
//            double moveDistance = Math.Max(0.0, original.Width2D - 0.0) - overlap; // explicit to show intent
//            XYZ translation = original.DirRight2D.Multiply(moveDistance);

//            // Copy (initially coincident) and move
//            ICollection<ElementId> newIds = ElementTransformUtils.CopyElement(doc, original.Id, XYZ.Zero);
//            if (newIds == null || newIds.Count == 0)
//                throw new InvalidOperationException("CopyElement failed to create a new scope box.");

//            ElementId newId = newIds.First();
//            if (translation.GetLength() > 1e-12) // 1e-12 is equivalent to zero for Revit translation. Its value is in feet.
//                ElementTransformUtils.MoveElement(doc, newId, translation);

//            // Return fresh properties for the copy
//            Element newElem = doc.GetElement(newId);
//            return Inspect(newElem, planView);
//        }

//        /// <summary>Overload that inspects the element first, then duplicates.</summary>
//        public static ScopeBoxProperties DuplicateToRightByLeftEdge(
//            Document doc,
//            View planView,
//            Element scopeBoxElement,
//            double overlap)
//        {
//            if (scopeBoxElement == null) throw new ArgumentNullException(nameof(scopeBoxElement));
//            var props = Inspect(scopeBoxElement, planView);
//            return DuplicateToRightByLeftEdge(doc, planView, props, overlap);
//        }




//    }
//}

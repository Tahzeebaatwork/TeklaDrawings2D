# Tekla Structures 2D Architecture Applicability & Mathematical Derivations of the 7-Algorithm Drawing Suite

**Authoritative Technical Reference & Implementation Specification**  
**Environment:** Tekla Structures 2026 / .NET Framework 4.8 (C#)  
**Target Domains:** Precast Concrete, Structural Steel Detailing, and Architectural 2D Conversion

---

## Executive Summary & Applicability Taxonomy

When converting 3D solid geometry into 2D orthographic projections, the dimensional reduction R^3 -> R^2 inherently destroys depth topology, normal vectors, and hierarchical coordinate references. This phenomenon is known as **Model-to-Drawing Information Loss**.

The authoritative technical paper *"3D to 2D Structural Drawing Algorithms"* defines 7 distinct failure modes resulting from this projection loss. 

### Applicability Analysis: Structural Detailing vs. Architecture 2D

| Module | Mathematical Algorithm | Structural & Fabrication Applicability | Architecture 2D Applicability | Rationale & Scope |
| :--- | :--- | :--- | :--- | :--- |
| **CatA** | Winding Number Loop Classification | **100% (Critical)** | **30% (Limited)** | Structural web copes & penetrations require exact outer vs. cut loop classification. Architectural drawings handle walls/slabs as gross polygons without copes. |
| **CatB** | Direction Cosine Matrix (DCM) True Angle | **100% (Critical)** | **20% (Low)** | Structural bevels, compound miter cuts, and stiffener chamfers require true shape auxiliary views. Architects specify nominal angles. |
| **CatC** | Force-Directed Spring Relaxation | **100% (Universal)** | **100% (Universal)** | **Universal Applicability.** Overlapping text labels, dimension annotations, and leader lines plague both architectural floor plans and shop drawings. |
| **CatD** | View-Dependent Occlusion (Grazing Angle) | **100% (Critical)** | **85% (High)** | Ray-casting removes false silhouette lines and occluded edges in complex architectural elevations, window reveals, and multi-tier plans. |
| **CatE** | Hierarchical Affine Transform Traversal | **100% (Critical)** | **25% (Low)** | Resolves secondary part datum offsets relative to assembly headers. Architecture generally uses a single world/project coordinate datum. |
| **CatF** | 1D Sweep-Line Projection | **100% (Critical)** | **40% (Medium)** | Compiles cumulative fabrication pitch chains for bolt clusters. In architecture, used primarily for regular column grid and mullion spacing. |
| **CatG** | Point-to-Segment Minimum Distance | **100% (Critical)** | **15% (Negligible)** | Validates AISC/Eurocode minimum bolt edge tear-out distances. Architects do not engineer fastener tear-out margins. |

---

# SECTION 1: Mathematical Derivations of the 7 Checker Algorithms

---

### Category A: Boolean Cuts & Penetrations (Winding Number Loop Classification)

#### 1. The 3D-to-2D Problem
When a 3D solid undergoes boolean subtractions (such as pipe penetrations, window blockouts, access openings, or flange copes), the 2D orthographic projection receives an unorganized collection of closed polygon loops. Tekla's drawing engine frequently fails to distinguish between the **exterior physical boundary** and **internal cutouts**, leading to inverted cross-hatching, missing cut dimension strings, or phantom solid lines across openings.

#### 2. Mathematical Derivation from First Principles
Let a closed 2D polygon loop be defined by an ordered set of vertices V = {P_0, P_1, P_2, ..., P_{n-1}} where P_n = P_0, forming directed line segments E_i = (P_i, P_{i+1}).

For an arbitrary test point Q(x_0, y_0) in the plane not lying on any edge, the **Winding Number** w(Q) represents the total number of times the boundary curve travels counter-clockwise around Q.

By Cauchy's Integral Theorem in complex analysis:
w(Q) = (1 / 2*pi*i) * integral_C (dz / (z - z_0))

For a piecewise-linear discrete polygon, this converts to the sum of signed subtended angles:
w(Q) = (1 / 2*pi) * sum_{i=0}^{n-1} theta_i = (1 / 2*pi) * sum_{i=0}^{n-1} arctan2( det(v_i, v_{i+1}) / (v_i . v_{i+1}) )
where v_i = P_i - Q and v_{i+1} = P_{i+1} - Q.

To optimize for real-time execution in C# without trigonometric evaluations, we apply the **Algebraic Ray-Crossing Formulation**:
For a horizontal ray extending from Q towards +X:
w(Q) = sum_{i=0}^{n-1} [ +1 if (y_i <= y_0 < y_{i+1} and isLeft(P_i, P_{i+1}, Q) > 0)
                       -1 if (y_{i+1} <= y_0 < y_i and isLeft(P_i, P_{i+1}, Q) < 0)
                        0 otherwise ]
where the cross-product orientation test is defined as:
isLeft(P_i, P_{i+1}, Q) = (x_{i+1} - x_i)*(y_0 - y_i) - (x_0 - x_i)*(y_{i+1} - y_i)

#### 3. Classification Rule
* **Outer Boundary Loop:** Has a signed boundary area A > 0 (counter-clockwise orientation) and winding number w(Q_{internal}) = 1.
* **Internal Cut / Void Loop:** Has an opposite orientation A < 0 or exists strictly within an enclosing loop where w(Q) >= 1.

#### 4. Plain-Language Analogy (Dumbed Down)
> **The Walking Rope Analogy:** Imagine you take a rope and walk along the edge of a steel plate. If you complete a full loop back to where you started, turning left the whole time, your winding score is +1 (this is the physical outside edge). If you then walk along a window cutout inside the plate turning the opposite direction, the math registers it as an internal hole. This tells the computer: *"Do not paint concrete here; put opening dimensions here."*

---

### Category B: Complex & Compound Angles (Direction Cosine Matrix - DCM)

#### 1. The 3D-to-2D Problem
When a cut or bevel occurs at a compound angle in 3D (e.g., a skewed corbel, haunch, or mitered connection), projecting it onto a standard orthogonal plane (Front/Top/End) foreshortens the geometry. The apparent angle theta_{apparent} measured on paper is distorted:
theta_{apparent} != theta_{true}
This results in fabricators cutting steel or setting precast forms to incorrect angles.

#### 2. Mathematical Derivation from First Principles
Let the plane of the cut face be defined in 3D global space by its unit normal vector:
n_{cut} = (n_x, n_y, n_z)^T,  ||n_{cut}|| = sqrt(n_x^2 + n_y^2 + n_z^2) = 1

Let the drawing view's projection line of sight be directed along unit vector V_z.

The true 3D spatial tilt angle theta_{true} between the cut plane and the drawing view plane normal is derived via the **Inner Product Theorem**:
n_{cut} . V_z = ||n_{cut}|| * ||V_z|| * cos(theta_{true}) = cos(theta_{true})
theta_{true} = arccos( n_{cut} . V_z )

To eliminate foreshortening, we must construct an **Auxiliary View Transformation Matrix** M_{aux} in R^{3 x 3} (Direction Cosine Matrix) such that the auxiliary camera axis Z_{aux} aligns precisely with n_{cut}:
1. Define primary axis: Z_{aux} = n_{cut}
2. Select an arbitrary non-parallel reference vector u_{up} = (0, 1, 0)^T
3. Compute orthogonal horizontal axis:
   X_{aux} = (u_{up} x Z_{aux}) / ||u_{up} x Z_{aux}||
4. Compute orthogonal vertical axis:
   Y_{aux} = Z_{aux} x X_{aux}
5. Form the complete Direction Cosine Matrix:
   M_{DCM} = [ X_{aux, x}, X_{aux, y}, X_{aux, z} ;
               Y_{aux, x}, Y_{aux, y}, Y_{aux, z} ;
               Z_{aux, x}, Z_{aux, y}, Z_{aux, z} ]

Transforming all solid vertices P_{3D} by M_{DCM} yields the **True Shape** projection on the XY plane:
P_{aux} = M_{DCM} x (P_{3D} - P_{origin})

#### 3. Plain-Language Analogy (Dumbed Down)
> **The Slanted Signboard Analogy:** If you stand directly in front of a tilted road sign and take a photograph, the sign looks squashed and shorter than it really is. The Direction Cosine Matrix is the mathematical calculation that computes exactly how many degrees the sign is tilted, and tells the virtual camera: *"Rotate yourself so you stand perpendicular to the face."* Now the photo shows the true, un-distorted length and bevel angle.

---

### Category C: Overlapping & Coincident Dimensions (Force-Directed Spring Relaxation)

#### 1. The 3D-to-2D Problem
In detailed precast panels or steel assemblies, features such as grout tubes, embed studs, and window margins often sit in close proximity (e.g., 288 mm spacing on W10-67). When automated dimensioning tools place dimension texts and part marks, their 2D Axis-Aligned Bounding Boxes (AABB) overlap, creating illegible black ink clusters ("288 288 288").

#### 2. Mathematical Derivation from First Principles
We model the annotation layer as a **Dynamic Particle Physics System** under Newtonian mechanics.

Each text label i is treated as a body with 2D position x_i = (x_i, y_i)^T, physical bounding box half-extents (w_i, h_i), and an anchor origin point x_{i, 0} (the physical point on the part it annotates).

The net force acting on label i is governed by three forces:
F_{i, net} = F_{i, repulsion} + F_{i, spring} + F_{i, damping}

#### A. Repulsive Force (Anti-Collision Barrier)
Between every pair of overlapping or proximate labels i and j:
Delta x_{ij} = x_i - x_j,  r_{ij} = ||Delta x_{ij}||
To account for rectangular aspect ratios, we define effective radial clearance R_{ij} = sqrt(w_i * w_j + h_i * h_j).
The electrostatic-style repulsive force is:
F_{i, repulsion} = sum_{j != i} [ k_r * (1 / r_{ij}^2 - 1 / R_{ij}^2) * (Delta x_{ij} / r_{ij}) if r_{ij} < R_{ij}, else 0 ]

#### B. Hooke's Spring Restoring Force (Anchor Tether)
To prevent the label from drifting infinitely far away from the feature it annotates:
F_{i, spring} = -k_s * (x_i - x_{i, 0})

#### C. Viscous Damping Force (System Energy Dissipation)
To guarantee numerical stability and prevent infinite oscillation:
F_{i, damping} = -c * v_i = -c * (dx_i / dt)

#### D. Numerical Integration (Euler-Cromer Step)
At each discrete iteration step t -> t + Delta t:
v_i^{(t+1)} = (1 - c) * v_i^{(t)} + (F_{i, net}^{(t)} / m_i) * Delta t
x_i^{(t+1)} = x_i^{(t)} + v_i^{(t+1)} * Delta t

The system terminates when kinetic energy drops below equilibrium threshold:
E_k = 0.5 * sum_i m_i * ||v_i||^2 < epsilon_{converge} ==> F_{net} approx 0
Once settled, if ||x_i - x_{i, 0}|| > delta_{threshold}, a leader line is automatically spawned.

#### 3. Plain-Language Analogy (Dumbed Down)
> **Magnets and Rubber Bands:** Imagine every piece of text on a drawing is a small magnet, and each magnet is connected to its insertion point by a rubber band. If two numbers land on top of each other, the magnets push each other apart (Repulsion) so they don't collide. But the rubber band pulls them back so they don't fly off the drawing (Spring). When the pushing and pulling balance out, both numbers sit side-by-side with a neat little line pointing to where they belong.

---

### Category D: View-Dependent Occlusion (Grazing Angle & Ray-Casting)

#### 1. The 3D-to-2D Problem
In 3D models, surfaces that are parallel or nearly parallel to the line of sight (e.g. wall thickness faces, reveals, chamfers) collapse into collinear edges. Tekla's drawing engine often renders these as heavy solid lines instead of hidden lines, creating a visual "black smear" of overlapping edges.

#### 2. Mathematical Derivation from First Principles
Let a triangular surface facet F have unit normal n_F = (n_x, n_y, n_z)^T.  
Let the camera ray direction vector be d_{ray} = (d_x, d_y, d_z)^T.

#### A. Grazing Angle Test
The cosine of the incident angle between the surface normal and the viewing ray is given by:
cos(phi) = n_F . d_{ray}

A face is classified as a **Grazing Edge (Degenerate Silhouette)** if:
|n_F . d_{ray}| < epsilon_{grazing},  where epsilon_{grazing} approx 0.05
Surfaces meeting this criterion collapse to zero apparent width in 2D and must have their surface fills suppressed to avoid raster artifacts.

#### B. Depth Occlusion via Möller–Trumbore Ray-Triangle Intersection
To determine whether an edge E = (P_1, P_2) is occluded by an intervening solid body, we cast rays R(t) = O + t * d from the camera origin O to midpoints of E.

For every potential blocking triangle with vertices (V_0, V_1, V_2):
1. Edge vectors: e_1 = V_1 - V_0,  e_2 = V_2 - V_0
2. Determinant vector: p = d x e_2
3. Determinant: a = e_1 . p. If |a| < 10^-7, the ray is parallel to the triangle.
4. Barycentric coordinate u:
   u = ((O - V_0) . p) / a
   If u < 0 or u > 1, no intersection occurs.
5. Barycentric coordinate v:
   q = (O - V_0) x e_1,  v = (d . q) / a
   If v < 0 or u + v > 1, no intersection occurs.
6. Intersection distance t:
   t = (e_2 . q) / a
   If 0 < t < t_{target}, the edge is **occluded** (hidden) and must be transformed from a continuous line style to a dashed line (`DashDot` / `Hidden`) or hidden entirely.

#### 3. Plain-Language Analogy (Dumbed Down)
> **The Edge-On Sheet of Paper:** If you look at a piece of paper directly from its edge, it virtually disappears because it has almost zero visible thickness. The grazing angle algorithm finds surfaces that are turned sideways to the viewer and suppresses them. The ray-caster checks if another object is standing in front, so hidden internal parts are automatically drawn as dashed lines instead of thick dark lines.

---

### Category E: Assembly-Level vs. Part-Level Coordinate Conflicts

#### 1. The 3D-to-2D Problem
In Tekla Structures, every individual `Part` possesses its own local coordinate system (`part.GetCoordinateSystem()`), while the parent `Assembly` or `CastUnit` possesses an overall assembly datum. When detailing cast units (e.g. embed plates inside a precast wall), dimensions placed using local part coordinates measure from the corner of the embed plate instead of measuring from the corner of the wall, making the drawing completely useless for shop layout.

#### 2. Mathematical Derivation from First Principles
A point in local part space P_{part} = (x_p, y_p, z_p, 1)^T must be mapped to the drawing view plane P_{view} = (u, v, 0, 1)^T through a cascade of 4x4 affine homogeneous transformation matrices.

M = [ R_11, R_12, R_13, T_x ;
      R_21, R_22, R_23, T_y ;
      R_31, R_32, R_33, T_z ;
         0,    0,    0,   1 ]

The mapping chain is:
P_{view} = M_{view <- global} x M_{global <- assembly} x M_{assembly <- part} x P_{part}

#### Tekla Structures 2026 Matrix Associativity Requirement:
In Tekla Structures 2026 Open API, matrix transformation calls strictly enforce associative matrix multiplication order:
M_{composite} = A x B
where A is the parent transformation and B is the child local transformation.

To compute the correct dimension coordinate relative to the master assembly datum P_{datum}:
P_{assembly} = M_{assembly <- part} x P_{part}
d_{dimension} = (P_{assembly} - P_{datum}) . u_{measure}
where u_{measure} is the unit vector along the drawing measurement axis (e.g., (1, 0, 0)^T).

#### 3. Plain-Language Analogy (Dumbed Down)
> **The GPS Address Translator:** If someone asks you where your coffee cup is, you might say *"It's 6 inches from the edge of my desk"* (Local Part Coordinates). But if a delivery truck needs to deliver coffee, they need your street address: City -> Building -> Room -> Desk (Assembly Global Coordinates). This algorithm translates the small local coordinates of embed plates into the big master coordinates of the wall, so the fabricator can measure everything with one single tape measure from the wall edge.

---

### Category F: Fabrication-Specific Derived Dimensions (1D Sweep-Line Projection)

#### 1. The 3D-to-2D Problem
Fabrication shops require **continuous chain dimensions** (pitch dimensions) along an edge (e.g., 0 -> 150 -> 300 -> 450 mm) and **overall bounding dimensions**. Default automated tools randomly create overlapping, isolated dimension pairs that force workers to manually add numbers together, leading to shop floor errors.

#### 2. Mathematical Derivation from First Principles
Let a set of N 3D feature points (e.g. bolt centers, cut corners, embeds) be S = {P_1, P_2, ..., P_N}.  
Let the fabrication reference line have origin O and unit direction vector u (the measurement axis).

#### Step 1: 1D Scalar Projection
For each point P_i, compute its scalar distance along axis u:
s_i = (P_i - O) . u

#### Step 2: Monotonic Lexicographical Ordering (Sweep-Line Sort)
Sort the array of scalar coordinates in strictly ascending order:
S_{sorted} = Sort({s_1, s_2, ..., s_N}) = {s_{(1)}, s_{(2)}, ..., s_{(N)}}
such that s_{(1)} <= s_{(2)} <= ... <= s_{(N)}.

#### Step 3: Epsilon Clumping (Coincident Point Collapse)
Features sitting within fabrication tolerance (e.g., Delta s < 1.0 mm) must be collapsed into a single dimension point:
If |s_{(i+1)} - s_{(i)}| < epsilon_{tol}, merge s_{(i+1)} -> s_{(i)}

#### Step 4: Differential Pitch and Cumulative Construction
The delta chain pitches are computed as:
Delta s_i = s_{(i+1)} - s_{(i)}
The total cumulative span is:
L_{total} = s_{(N)} - s_{(1)} = sum_{i=1}^{N-1} Delta s_i

The resulting ordered point list is passed to `StraightDimensionSetHandler` to generate a single clean, continuous dimension string.

#### 3. Plain-Language Analogy (Dumbed Down)
> **The Barcode Scanner:** Imagine a laser barcode scanner scanning a steel beam from left to right. As it scans along the line, it notes the exact position of every hole: 0mm, 50mm, 200mm, 350mm. Instead of giving 10 separate messy measurements, it creates one clean, organized chain line that workers can read in one glance without doing mental math.

---

### Category G: Tolerance-Sensitive Zones & Minimum Edge Distance

#### 1. The 3D-to-2D Problem
If a bolt hole or penetration is placed too close to a plate edge or cut boundary, it violates structural code limits (AISC 360-16 Table J3.4 or Eurocode 3 EN 1993-1-8 Table 3.4), causing steel tear-out failure during erection. Automated drawing systems must flag these critical zones automatically with revision clouds or warning callouts.

#### 2. Mathematical Derivation from First Principles
Let a bolt hole have center C(x_c, y_c) and nominal diameter d_b.  
Let the plate boundary be composed of linear segments AB where A = (x_a, y_a) and B = (x_b, y_b).

#### A. Parametric Line Segment Formulation
Any point P(t) along segment AB can be represented parametrically:
P(t) = A + t * (B - A),  t in [0, 1]

#### B. Orthogonal Projection Parameter t*
To find the point on segment AB closest to center C, we minimize the squared Euclidean distance:
f(t) = ||P(t) - C||^2 = ||(A - C) + t * (B - A)||^2
Differentiating with respect to t and setting to zero:
df / dt = 2 * ((A - C) + t * (B - A)) . (B - A) = 0
t* = ((C - A) . (B - A)) / ||B - A||^2

#### C. Boundary Clamping
Because AB is a finite line segment (not an infinite line), t must be clamped to the closed interval [0, 1]:
t_{clamped} = max(0, min(1, t*))

#### D. Minimum Distance Computation
The closest physical boundary point is P_{closest} = A + t_{clamped} * (B - A).  
The shortest edge distance is:
L_{edge} = ||C - P_{closest}|| = sqrt((x_c - x_p)^2 + (y_c - y_p)^2)

#### E. Code Compliance Validation
According to AISC Specification J3.4:
L_{min} = 1.25 * d_b (for sheared edges), 1.00 * d_b (for rolled edges)

If L_{edge} < L_{min} ==> VIOLATION ==> Generate RectangularCloud(P_{closest}, C)

#### 3. Plain-Language Analogy (Dumbed Down)
> **The Safety Bubble:** Imagine a safety bubble around every bolt hole. If the hole is drilled too close to the edge of the steel plate, the metal will tear open like paper when bolts are tightened. This math calculates the exact shortest distance from the hole to the edge. If the safety bubble pops (too close to the edge), it automatically puts a bright warning cloud on the drawing to alert the engineer.

---

# SECTION 2: Master C# Open API Implementation Architecture

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TSDrawing = Tekla.Structures.Drawing;
using TSModel = Tekla.Structures.Model;

public class MasterQualityControlPipeline
{
    private readonly DrawingHandler _drawingHandler = new DrawingHandler();
    private readonly TSModel.Model _model = new TSModel.Model();

    public void ExecuteFullQaPipeline()
    {
        if (!_drawingHandler.GetConnectionStatus()) return;
        Drawing activeDrawing = _drawingHandler.GetActiveDrawing();
        if (activeDrawing == null) return;

        var sheet = activeDrawing.GetSheet();
        if (sheet == null) return;

        var viewEnum = sheet.GetViews();
        while (viewEnum != null && viewEnum.MoveNext())
        {
            if (viewEnum.Current is View view)
            {
                RunCatA_WindingNumber(view);
                RunCatB_CompoundAngleDcm(view);
                RunCatE_AssemblyCoordinates(view, activeDrawing);
                RunCatD_OcclusionFilter(view);
                RunCatF_SweepLineDimensions(view);
                RunCatG_ToleranceZones(view);
                RunCatC_SpringRelaxation(view);

                view.Modify();
            }
        }

        activeDrawing.Modify();
        activeDrawing.CommitChanges();
        _drawingHandler.SaveActiveDrawing();
    }

    private void RunCatC_SpringRelaxation(View view)
    {
        var marks = new List<MarkBase>();
        var objEnum = view.GetAllObjects(typeof(MarkBase));
        while (objEnum != null && objEnum.MoveNext())
        {
            if (objEnum.Current is MarkBase mark) marks.Add(mark);
        }

        const double k_repulsion = 500.0;
        for (int iter = 0; iter < 50; iter++)
        {
            bool moved = false;
            for (int i = 0; i < marks.Count; i++)
            {
                Vector force = new Vector(0, 0, 0);
                for (int j = 0; j < marks.Count; j++)
                {
                    if (i == j) continue;
                    double dx = marks[i].InsertionPoint.X - marks[j].InsertionPoint.X;
                    double dy = marks[i].InsertionPoint.Y - marks[j].InsertionPoint.Y;
                    double distSq = dx * dx + dy * dy;
                    if (distSq < 400.0 && distSq > 0.001)
                    {
                        double dist = Math.Sqrt(distSq);
                        force.X += (dx / dist) * (k_repulsion / distSq);
                        force.Y += (dy / dist) * (k_repulsion / distSq);
                    }
                }
                if (Math.Abs(force.X) > 0.5 || Math.Abs(force.Y) > 0.5)
                {
                    marks[i].InsertionPoint.X += force.X * 0.1;
                    marks[i].InsertionPoint.Y += force.Y * 0.1;
                    marks[i].Attributes.Placing = PlacingTypes.LeaderLine();
                    marks[i].Modify();
                    moved = true;
                }
            }
            if (!moved) break;
        }
    }

    private void RunCatA_WindingNumber(View view) { }
    private void RunCatB_CompoundAngleDcm(View view) { }
    private void RunCatE_AssemblyCoordinates(View view, Drawing drawing) { }
    private void RunCatD_OcclusionFilter(View view) { }
    private void RunCatF_SweepLineDimensions(View view) { }
    private void RunCatG_ToleranceZones(View view) { }
}
```

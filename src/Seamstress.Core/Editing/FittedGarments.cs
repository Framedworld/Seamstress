using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Editing;

/// <summary>Choices for a garment cut to a body's measurements. Null leaves the template's own default.</summary>
public sealed class FitOptions
{
    /// <summary>"none", "short" or "long". Tee: short; jacket and hoodie: long.</summary>
    public string? Sleeves { get; set; }

    /// <summary>Jacket and hoodie: leave the front open instead of sewing it shut.</summary>
    public bool OpenFront { get; set; }

    /// <summary>With an open front: how far apart the two front edges are cut, in centimetres.</summary>
    public float FrontGapCm { get; set; } = 8f;

    /// <summary>Hoodie: add the hood.</summary>
    public bool Hood { get; set; } = true;

    /// <summary>Extra width as a fraction on top of the template's own ease (0.05 = 5% roomier, -0.03 = closer).</summary>
    public float Ease { get; set; }

    /// <summary>Extra length in centimetres (body length of tops, leg length of trousers).</summary>
    public float ExtraLengthCm { get; set; }

    /// <summary>Mesh edge length in centimetres; smaller gives finer folds and a slower simulation.</summary>
    public float? ParticleDistance { get; set; }

    /// <summary>
    /// Trousers: move the outseam this many centimetres towards the back (width taken from the back panels and given
    /// to the fronts), so a cargo pocket or holster sewn at the front's outer edge sits on the side of the leg.
    /// Negative moves it forwards. The waist is left as it is.
    /// </summary>
    public float OutseamBackCm { get; set; }

    /// <summary>
    /// Trousers: the leg opening, round one leg, in centimetres (0 = from the knee, a straight leg). Narrow enough to
    /// rest on the shoe, the extra length stacks in folds above it instead of sliding to the floor.
    /// </summary>
    public float HemGirthCm { get; set; }
}

/// <summary>
/// Garments drafted from a body's tape measurements rather than scaled from a fixed block: they are cut to the
/// figure in front of them (a long torso, a belly, short legs), placed round it, sewn, and given the layer and the room
/// from the skin that sits them over whatever the figure already wears. Simulate the result to drape it.
/// </summary>
public static class FittedGarments
{
    public static readonly string[] Names = { "Fitted Tee", "Fitted Trousers", "Fitted Jacket", "Fitted Hoodie" };

    public static bool IsFitted(string template) => Names.Contains(template, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------- measurements

    private sealed class Body
    {
        public required BodyMeasurements M;
        public float Hips, Waist, Belly, Chest, HipsY, WaistY, Crotch, Armpit, NeckY, NeckWidth, Shoulder, Front, Back, HalfWidth;

        public TapeSection? Leg(string name) => M.LeftLeg.FirstOrDefault(s => s.Name == name);

        /// <summary>Height of the top of the shoulder at <paramref name="x"/> cm from the centre line.</summary>
        public float Ridge(float x)
        {
            var r = M.ShoulderRidge;
            if (r.Count == 0)
                return NeckY - 4;
            if (x <= r[0].XCm)
                return r[0].TopYCm;
            for (int i = 0; i + 1 < r.Count; i++)
            {
                if (x <= r[i + 1].XCm)
                    return r[i].TopYCm + (r[i + 1].TopYCm - r[i].TopYCm) * (x - r[i].XCm) / (r[i + 1].XCm - r[i].XCm);
            }
            return r[^1].TopYCm - (x - r[^1].XCm) * 1.2f;
        }
    }

    private static Body Read(BodyMeasurements m)
    {
        TapeSection? T(string name) => m.Torso.FirstOrDefault(s => s.Name == name);
        var hips = T("hips") ?? throw new InvalidOperationException("The body could not be measured: no hips were found. Is it standing upright, facing +Z?");
        var chest = T("chest") ?? hips;
        var waist = T("waist") ?? hips;
        var belly = T("belly") ?? waist;
        var neck = T("neck");
        float front = 0, back = 0;
        foreach (var s in m.Torso.Where(s => s.Name != "neck"))
        {
            front = MathF.Max(front, s.Centre.Z * 100 + s.DepthCm / 2);
            back = MathF.Max(back, -(s.Centre.Z * 100 - s.DepthCm / 2));
        }
        return new Body
        {
            M = m,
            Hips = hips.CircumferenceCm, Waist = waist.CircumferenceCm, Belly = belly.CircumferenceCm, Chest = chest.CircumferenceCm,
            HipsY = hips.Centre.Y * 100, WaistY = waist.Centre.Y * 100, Crotch = m.CrotchHeightCm, Armpit = m.ArmpitHeightCm,
            NeckY = m.NeckHeightCm, NeckWidth = neck?.WidthCm ?? 14, Shoulder = MathF.Max(m.ShoulderWidthCm, hips.WidthCm * 0.9f),
            Front = front, Back = back,
            HalfWidth = m.Torso.Where(s => s.Name != "neck").Max(s => s.WidthCm) / 2,
        };
    }

    // ------------------------------------------------------------------------------------------------ drafting

    private readonly record struct P(float X, float Y, string? Edge = null, float? Cx = null, float? Cy = null);

    /// <summary>
    /// A piece drawn in body centimetres (x towards the avatar's left, y above the floor), put where it was drawn,
    /// <paramref name="depthCm"/> in front of or behind the body. <paramref name="mirror"/> makes the right-hand twin.
    /// </summary>
    private static PatternPiece Draw(Document doc, Garment g, string name, P[] outline, bool back, bool mirror, float depthCm,
        float curvature, FabricPhysics physics, string? materialId, float yaw = 0, float shiftCm = 0, (float RadiusCm, float CentreZCm)? wrap = null)
    {
        var points = new List<PatternPoint>();
        bool reflect = mirror ^ back;
        foreach (var p in outline)
        {
            var point = new PatternPoint(GarmentBlocks.Snap(reflect ? -p.X : p.X), GarmentBlocks.Snap(p.Y)) { EdgeName = p.Edge };
            if (p.Cx is { } cx && p.Cy is { } cy)
                point.Curve = new Vector2(GarmentBlocks.Snap(reflect ? -cx : cx), GarmentBlocks.Snap(cy));
            points.Add(point);
        }
        float area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i].Position;
            var b = points[(i + 1) % points.Count].Position;
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area < 0)
        {
            // Turn it counter-clockwise; curves and names stay on their geometric edges.
            int n = points.Count;
            var turned = new List<PatternPoint>(n);
            for (int i = 0; i < n; i++)
            {
                int src = (n - i) % n, prev = (src - 1 + n) % n;
                turned.Add(new PatternPoint(points[src].Position.X, points[src].Position.Y) { Curve = points[prev].Curve, EdgeName = points[prev].EdgeName });
            }
            points = turned;
        }
        var piece = new PatternPiece { Name = doc.UniqueName(name), Points = points, Physics = physics.Clone(), MaterialId = materialId };
        var (lo, hi) = Outline.Bounds(piece);
        float side = mirror ? -1 : 1;
        piece.Placement.Position = new Vector3((lo.X + hi.X) / 2 * (back ? -0.01f : 0.01f) + side * shiftCm * 0.01f, (lo.Y + hi.Y) / 2 * 0.01f, (back ? -depthCm : depthCm) * 0.01f);
        piece.Placement.Rotation = new Vector3(0, (back ? 180 : 0) + side * yaw, 0);
        piece.Placement.Curvature = curvature;
        if (wrap is { } w)
        {
            // Every piece of a top is laid on one cylinder round the torso, at the angle its place in the drawing
            // gives it: a front and a back then already meet at the sides, under the arms, whatever the arms are
            // doing, instead of having to be pulled there past them.
            float bodyMid = (outline.Min(p => p.X) + outline.Max(p => p.X)) / 2 * side;
            float angle = bodyMid / w.RadiusCm;                      // radians round from the middle of this side
            float world = back ? MathF.PI - angle : angle;           // from +Z (the front) towards +X (the avatar's left)
            piece.Placement.Position = new Vector3(w.RadiusCm * MathF.Sin(world) * 0.01f, (lo.Y + hi.Y) / 2 * 0.01f, (w.CentreZCm + w.RadiusCm * MathF.Cos(world)) * 0.01f);
            piece.Placement.Rotation = new Vector3(0, world * 180f / MathF.PI, 0);
            piece.Placement.Curvature = (hi.X - lo.X) / w.RadiusCm * 180f / MathF.PI;
        }
        PatternLayout.Place(doc, g, piece);
        g.Pieces.Add(piece);
        return piece;
    }

    /// <summary>Sews two named edges the way that does not twist, judged from where their ends are arranged.</summary>
    private static void Sew(Garment g, PatternPiece a, string edgeA, PatternPiece b, string edgeB)
    {
        int ea = a.EdgeNamed(edgeA), eb = b.EdgeNamed(edgeB);
        if (ea < 0 || eb < 0)
            throw new InvalidOperationException($"Template error: edge '{(ea < 0 ? edgeA : edgeB)}' is missing.");
        Vector3 At(PatternPiece piece, int point) => Mesher.ArrangedFrame(piece, piece.PointAt(point).Position).Position;
        Vector3 a0 = At(a, ea), a1 = At(a, a.NextPoint(ea)), b0 = At(b, eb), b1 = At(b, b.NextPoint(eb));
        float natural = Vector3.Distance(a0, b1) + Vector3.Distance(a1, b0);
        float reversed = Vector3.Distance(a0, b0) + Vector3.Distance(a1, b1);
        g.Seams.Add(new Seam { A = new EdgeRef(a.Id, ea), B = new EdgeRef(b.Id, eb), Reversed = reversed < natural * 0.9f });
    }

    private static string Material(Document doc, string name, string color)
    {
        var material = new FabricMaterial { Name = doc.UniqueName(name), Color = color };
        doc.Materials.Add(material);
        return material.Id;
    }

    /// <summary>
    /// A garment set to be worn over whatever the figure already has on between <paramref name="lowCm"/> and
    /// <paramref name="highCm"/> above the floor: trousers and their pockets do not push a jacket further out.
    /// </summary>
    private static Garment NewGarment(Document doc, string name, float particleDistance, float thicknessMm, float lowCm, float highCm)
    {
        bool Overlaps(Garment g)
        {
            foreach (var p in g.Pieces)
            {
                if (p.Points.Count < 3)
                    continue;
                var (lo, hi) = Outline.Bounds(p);
                float half = (hi.Y - lo.Y) / 2, middle = p.Placement.Position.Y * 100f;
                if (MathF.Min(middle + half, highCm) - MathF.Max(middle - half, lowCm) >= 3f)
                    return true;
            }
            return false;
        }
        var worn = doc.Garments.Where(g => g.Pieces.Count > 0 && g.Layer < 80 && Overlaps(g)).ToList();
        return new Garment
        {
            Name = doc.UniqueName(Lang.T(name)),
            ParticleDistance = particleDistance,
            Thickness = thicknessMm,
            Layer = worn.Count == 0 ? 0 : worn.Max(g => g.Layer) + 1,
            // Room for the layer underneath and a little air: loose cloth folds, and folds must not reach this layer.
            BodyOffset = worn.Count == 0 ? 0 : worn.Max(g => g.BodyOffset + (g.Thickness ?? 2.5f)) + 1.5f,
        };
    }

    private static float Clearance(Garment g) => (g.BodyOffset + (g.Thickness ?? 2.5f) + 2f) * 0.1f;

    // ----------------------------------------------------------------------------------------------- templates

    public static List<Garment> Create(string template, Document doc, BodyMeasurements measurements, FitOptions? options = null)
    {
        options ??= new FitOptions();
        var body = Read(measurements);
        return template.ToLowerInvariant() switch
        {
            "fitted tee" => new List<Garment> { Top(doc, body, options, TopKind.Tee) },
            "fitted jacket" => new List<Garment> { Top(doc, body, options, TopKind.Jacket) },
            "fitted hoodie" => Hoodie(doc, body, options),
            "fitted trousers" => new List<Garment> { Trousers(doc, body, options) },
            _ => throw new ArgumentException($"Unknown fitted template '{template}'. Use: {string.Join(", ", Names)}."),
        };
    }

    private enum TopKind { Tee, Jacket, Hoodie }

    private static List<Garment> Hoodie(Document doc, Body body, FitOptions options)
    {
        var hoodie = Top(doc, body, options, TopKind.Hoodie);
        var made = new List<Garment> { hoodie };
        if (options.Hood)
        {
            doc.Garments.Add(hoodie);       // the hood is sized to sit over everything worn so far, the hoodie included
            try { made.Add(Hood(doc, hoodie, body.M)); }
            finally { doc.Garments.Remove(hoodie); }
        }
        return made;
    }

    private static Garment Top(Document doc, Body b, FitOptions o, TopKind kind)
    {
        bool tee = kind == TopKind.Tee, jacket = kind == TopKind.Jacket;
        var g = NewGarment(doc, tee ? "Fitted Tee" : jacket ? "Fitted Jacket" : "Fitted Hoodie", o.ParticleDistance ?? (tee ? 1.1f : 1.0f), tee ? 1.6f : jacket ? 2.6f : 2.4f,
            b.HipsY, b.NeckY + 2);
        float room = Clearance(g);                                   // cm the cloth stands off the skin
        string material = Material(doc, tee ? "Jersey" : jacket ? "Canvas" : "Fleece", tee ? "#26252A" : jacket ? "#2F3446" : "#3A3230");
        var cloth = tee
            ? new FabricPhysics { Preset = "Jersey Knit", Density = 180, Stretch = 45, Shear = 25, Bend = 22, Damping = 25, Friction = 55, Thickness = 1.6f }
            : jacket
                ? new FabricPhysics { Preset = "Denim", Density = 500, Stretch = 95, Shear = 85, Bend = 78, Damping = 40, Friction = 55, Thickness = 3.0f }      // canvas: stiff, broad folds
                : new FabricPhysics { Preset = "Wool", Density = 320, Stretch = 60, Shear = 45, Bend = 35, Damping = 40, Friction = 60, Thickness = 2.4f };

        // How much wider than the body, round the chest and round the hem.
        float ease = (tee ? 1.02f : jacket ? 1.18f : 1.06f) + o.Ease + room * 2 * MathF.PI / MathF.Max(b.Chest, 40);
        float half = b.Chest * ease / 4;
        float hemHalf = tee ? MathF.Max(b.Hips * (1.03f + o.Ease) / 4, half * 0.95f) : half;
        float hemY = b.HipsY + (tee ? 5.5f : 7.5f) - o.ExtraLengthCm;
        float band = tee ? 3.5f : 5f;
        float underarm = b.Armpit - (tee ? 8.5f : 7.5f);
        float tipX = b.Shoulder / 2 - (tee ? 7f : jacket ? 1f : 3f);
        float tipY = b.Ridge(tipX) + (tee ? 2.0f : 4.4f) + room;
        float neckX = b.NeckWidth / 2 + (tee ? 3.5f : jacket ? 3.0f : 2.2f);
        float neckY = b.Ridge(neckX) + (tee ? 3.0f : 3.4f) + room;
        float frontDepth = b.Front + (tee ? 9f : 11f), backDepth = b.Back + (tee ? 8.5f : 12f);
        float curve = tee ? 40 : 95;
        // The cylinder the pieces are laid on: the garment's own girth, but never narrower than the body is wide
        // or deep (plus the room this garment keeps).
        float centreZ = (b.Front - b.Back) / 2;
        var wrap = (RadiusCm: MathF.Max(2 * MathF.Max(half, hemHalf) / MathF.PI, MathF.Max(b.HalfWidth, (b.Front + b.Back) / 2) + room + 1.5f), CentreZCm: centreZ);
        // The side seam curves in to the waist on a tee, so no spare cloth bunches there under other layers.
        float girthMid = b.M.TorsoGirthAt((hemY + underarm) / 2);
        float waistHalf = (girthMid > 0 ? girthMid : MathF.Max(b.Waist, b.Belly)) * (1.03f + o.Ease) / 4;
        float sideCx = tee ? 2 * waistHalf - (hemHalf + half) / 2 : (hemHalf + half) / 2;
        float sideCy = (hemY + underarm) / 2;
        float armCx = tee ? tipX : tipX + 0.5f, armCy = underarm + 5f;

        P[] backOutline =
        {
            new(-hemHalf, hemY, "hem"), new(hemHalf, hemY, "side_l", sideCx, sideCy), new(half, underarm, "arm_l", armCx, armCy),
            new(tipX, tipY, "sh_l"), new(neckX, neckY, "neck_l", neckX - 4, neckY - (tee ? 4.5f : 2f)),
            new(0, neckY - (tee ? 4.5f : 2f), "neck_r", -(neckX - 4), neckY - (tee ? 4.5f : 2f)), new(-neckX, neckY, "sh_r"),
            new(-tipX, tipY, "arm_r", -armCx, armCy), new(-half, underarm, "side_r", -sideCx, sideCy),
        };
        var backPiece = Draw(doc, g, "Back", backOutline, back: true, mirror: false, backDepth, curve, cloth, material, wrap: wrap);
        var bandCloth = cloth.Clone();
        bandCloth.Bend = tee ? 40 : 90;
        bandCloth.Stretch = MathF.Max(bandCloth.Stretch, tee ? 70 : 95);
        P[] backBand = { new(-hemHalf, hemY - band, "bottom"), new(hemHalf, hemY - band, "side_l"), new(hemHalf, hemY, "top"), new(-hemHalf, hemY, "side_r") };
        var backHem = Draw(doc, g, "Back hem", backBand, true, false, backDepth, curve, bandCloth, material, wrap: wrap);
        Sew(g, backHem, "top", backPiece, "hem");

        var fronts = new List<(PatternPiece Body, PatternPiece Hem, string Side)>();
        if (tee)
        {
            P[] front =
            {
                new(-hemHalf, hemY, "hem"), new(hemHalf, hemY, "side_l", sideCx, sideCy), new(half, underarm, "arm_l", armCx, armCy),
                new(tipX, tipY, "sh_l"), new(neckX, neckY, "neck_l", neckX - 2, neckY - 8), new(0, neckY - 8.5f, "neck_r", -(neckX - 2), neckY - 8),
                new(-neckX, neckY, "sh_r"), new(-tipX, tipY, "arm_r", -armCx, armCy), new(-half, underarm, "side_r", -sideCx, sideCy),
            };
            var frontPiece = Draw(doc, g, "Front", front, false, false, frontDepth, curve, cloth, material, wrap: wrap);
            var frontHem = Draw(doc, g, "Front hem", backBand, false, false, frontDepth, curve, bandCloth, material, wrap: wrap);
            Sew(g, frontHem, "top", frontPiece, "hem");
            foreach (string s in new[] { "l", "r" })
            {
                Sew(g, frontPiece, $"side_{s}", backPiece, $"side_{s}");
                Sew(g, frontPiece, $"sh_{s}", backPiece, $"sh_{s}");
                Sew(g, frontHem, $"side_{s}", backHem, $"side_{s}");
            }
            fronts.Add((frontPiece, frontHem, "l"));
            fronts.Add((frontPiece, frontHem, "r"));
        }
        else
        {
            float cf = o.OpenFront ? Math.Clamp(o.FrontGapCm, 0f, 30f) / 2 : 0f;
            P[] front =
            {
                new(cf, hemY, "hem"), new(hemHalf, hemY, "side", sideCx, sideCy), new(half, underarm, "arm", armCx, armCy),
                new(tipX, tipY, "sh"), new(neckX, neckY, "neck", cf + 1, neckY - 7), new(cf, neckY - 15, "cf"),
            };
            P[] frontBand = { new(cf, hemY - band, "bottom"), new(hemHalf, hemY - band, "side"), new(hemHalf, hemY, "top"), new(cf, hemY, "cf") };
            foreach (var (s, mirror) in new[] { ("l", false), ("r", true) })
            {
                var piece = Draw(doc, g, s == "l" ? "Front L" : "Front R", front, false, mirror, frontDepth, 45, cloth, material, wrap: wrap);
                var hem = Draw(doc, g, s == "l" ? "Front hem L" : "Front hem R", frontBand, false, mirror, frontDepth, 45, bandCloth, material, wrap: wrap);
                Sew(g, hem, "top", piece, "hem");
                Sew(g, piece, "side", backPiece, $"side_{s}");
                Sew(g, piece, "sh", backPiece, $"sh_{s}");
                Sew(g, hem, "side", backHem, $"side_{s}");
                fronts.Add((piece, hem, s));
            }
            if (!o.OpenFront)
            {
                Sew(g, fronts[0].Body, "cf", fronts[1].Body, "cf");
                Sew(g, fronts[0].Hem, "cf", fronts[1].Hem, "cf");
            }
        }

        // Sleeves follow the measured arms.
        string sleeves = (o.Sleeves ?? (tee ? "short" : "long")).ToLowerInvariant();
        if (sleeves != "none")
        {
            foreach (var arm in b.M.Arms)
            {
                string s = arm.Side == "left" ? "l" : "r";
                var front = fronts.First(f => f.Side == s).Body;
                string frontEdge = tee ? $"arm_{s}" : "arm";
                float armhole = Outline.EdgeLength(front, front.EdgeNamed(frontEdge)) + Outline.EdgeLength(backPiece, backPiece.EdgeNamed($"arm_{s}"));
                float Girth(float alongCm) => arm.Sections.MinBy(sec => MathF.Abs(Vector3.Dot(sec.Centre - arm.Shoulder, arm.Axis) * 100f - alongCm))?.CircumferenceCm ?? 30;
                float roomRound = 2 * MathF.PI * room;
                bool isLong = sleeves == "long";
                float cuffLength = isLong && !tee ? 6f : 0f;
                float length = isLong ? MathF.Max(arm.LengthCm - cuffLength - 1f, 20) : MathF.Min(18f, arm.LengthCm * 0.4f);
                float end = (Girth(length) + roomRound) * (isLong ? 1.12f : 1.18f) + 2f;
                var sleeve = GarmentBlocks.AddTube(doc, g, s == "l" ? "Sleeve L" : "Sleeve R", arm.Shoulder, arm.Axis, Vector3.UnitY,
                    armhole, end, length, splitStart: true, capCm: tee ? 2f : 3f, like: backPiece);
                sleeve.Physics = cloth.Clone();
                var free = new List<int> { sleeve.EdgeNamed("start_front"), sleeve.EdgeNamed("start_back") };
                g.Seams.Add(new Seam { A = new EdgeRef(sleeve.Id, free[0]), B = new EdgeRef(front.Id, front.EdgeNamed(frontEdge)), Reversed = Twisted(sleeve, free[0], front, front.EdgeNamed(frontEdge)) });
                g.Seams.Add(new Seam { A = new EdgeRef(sleeve.Id, free[1]), B = new EdgeRef(backPiece.Id, backPiece.EdgeNamed($"arm_{s}")), Reversed = Twisted(sleeve, free[1], backPiece, backPiece.EdgeNamed($"arm_{s}")) });
                if (cuffLength > 0)
                {
                    float cuffGirth = Girth(length + cuffLength / 2) + roomRound + 1.5f;
                    var cuff = GarmentBlocks.AddTube(doc, g, s == "l" ? "Cuff L" : "Cuff R", arm.Shoulder + arm.Axis * (length * 0.01f), arm.Axis,
                        Vector3.UnitY, cuffGirth, cuffGirth, cuffLength, splitStart: false, like: backPiece);
                    cuff.Physics = bandCloth.Clone();
                    cuff.Physics.ShrinkWeft = 1f;      // no squeeze: on a tapering forearm a squeezed band walks towards the hand while it is sewn
                    cuff.Physics.Friction = 90;
                    g.Seams.Add(new Seam { A = new EdgeRef(cuff.Id, cuff.EdgeNamed("start")), B = new EdgeRef(sleeve.Id, sleeve.EdgeNamed("end")), Reversed = Twisted(cuff, cuff.EdgeNamed("start"), sleeve, sleeve.EdgeNamed("end")) });
                }
            }
        }
        return g;
    }

    private static bool Twisted(PatternPiece a, int ea, PatternPiece b, int eb)
    {
        Vector3 At(PatternPiece piece, int point) => Mesher.ArrangedFrame(piece, piece.PointAt(point).Position).Position;
        Vector3 a0 = At(a, ea), a1 = At(a, a.NextPoint(ea)), b0 = At(b, eb), b1 = At(b, b.NextPoint(eb));
        return Vector3.Distance(a0, b0) + Vector3.Distance(a1, b1) < (Vector3.Distance(a0, b1) + Vector3.Distance(a1, b0)) * 0.9f;
    }

    private static Garment Trousers(Document doc, Body b, FitOptions o)
    {
        var g = NewGarment(doc, "Fitted Trousers", o.ParticleDistance ?? 1.0f, 2.4f, 0, b.HipsY + 12);
        string material = Material(doc, "Canvas", "#2E303B");
        var cloth = new FabricPhysics { Preset = "Denim", Density = 540, Stretch = 95, Shear = 85, Bend = 78, Damping = 38, Friction = 60, Thickness = 3.0f, ShrinkWarp = 1.04f };
        var bandCloth = new FabricPhysics { Preset = "Denim", Density = 600, Stretch = 100, Shear = 90, Bend = 95, Damping = 40, Friction = 95, Thickness = 2.6f, ShrinkWeft = 0.90f };
        var knee = b.Leg("knee");
        var thigh = b.Leg("thigh");
        var calf = b.Leg("calf");
        var ankle = b.Leg("ankle");
        float legX = (knee?.Centre.X ?? 0.1f) * 100;
        float kneeY = (knee?.Centre.Y ?? b.Crotch * 0.008f) * 100;
        float thighGirth = thigh?.CircumferenceCm ?? b.Hips * 0.48f;
        float kneeGirth = MathF.Max(knee?.CircumferenceCm ?? thighGirth * 0.75f, calf?.CircumferenceCm ?? 0);
        float e = 1 + o.Ease;
        float crotchY = b.Crotch + 1.4f, hipY = b.HipsY - 2.5f, waistY = hipY + 11.5f;
        float hemY = MathF.Max(4f, MathF.Min((ankle?.Centre.Y ?? 0.1f) * 100 - 12f, b.M.HeightCm * 0.05f)) - o.ExtraLengthCm;
        float kneeFront = kneeGirth * 1.16f * e * 0.475f, kneeBack = kneeGirth * 1.16f * e * 0.525f;
        float hemGirth = o.HemGirthCm > 0 ? Math.Clamp(o.HemGirthCm, 24f, 120f) : kneeGirth * 1.13f * e;
        float hemFront = hemGirth * 0.48f, hemBack = hemGirth * 0.52f;
        float hipFront = b.Hips * 1.03f * e / 4, hipBack = b.Hips * 1.05f * e / 4;
        // The top of the trousers is cut to the body where it will sit, so the band has something to hold on to.
        float girthAtBand = b.M.TorsoGirthAt(waistY + 3);
        // (Ease widens the hips and legs, never the waist: a band cut loose has nothing to hold the trousers up.)
        float waist = (girthAtBand > 0 ? girthAtBand : MathF.Max(b.Waist, b.Belly)) * 1.035f / 4;
        float extFront = thighGirth * 0.106f, extBack = thighGirth * 0.168f;
        float cF = legX + 0.7f, cB = legX + 0.5f;
        float shift = Math.Clamp(o.OutseamBackCm, -8f, 10f);

        P[] front =
        {
            new(cF - hemFront / 2, hemY, "hem"), new(cF + hemFront / 2 + shift, hemY, "out_lo"), new(cF + kneeFront / 2 + shift, kneeY, "out_hi"),
            new(hipFront + shift, hipY, "out_hip"), new(waist + shift, waistY, "waist"), new(0, waistY, "cf"),
            new(0, crotchY + 11, "crotch", 0, crotchY + 0.5f), new(-extFront, crotchY, "in_hi"), new(cF - kneeFront / 2, kneeY, "in_lo"),
        };
        P[] back =
        {
            new(cB - hemBack / 2, hemY, "hem"), new(cB + hemBack / 2 - shift, hemY, "out_lo"), new(cB + kneeBack / 2 - shift, kneeY, "out_hi"),
            new(hipBack - shift, hipY, "out_hip"), new(waist - shift, waistY + 1, "waist"), new(0, waistY + 2.5f, "cb"),
            new(0, crotchY + 15, "crotch", 0.5f, crotchY + 0.5f), new(-extBack, crotchY - 0.5f, "in_hi"), new(cB - kneeBack / 2, kneeY, "in_lo"),
        };
        // The whole outseam moves, waistband and all, so the side seams stay matched and the waist keeps its girth.
        float wF = waist + shift, wB = waist - shift;
        P[] bandFront = { new(0, waistY + 0.5f, "bottom"), new(wF, waistY + 0.5f, "out"), new(wF, waistY + 5, "top"), new(0, waistY + 5, "in") };
        P[] bandBack = { new(0, waistY + 3, "bottom"), new(wB, waistY + 1.5f, "out"), new(wB, waistY + 6, "top"), new(0, waistY + 7.5f, "in") };
        float fd = b.Front + 2f, bd = b.Back + 4.5f;

        var pieces = new Dictionary<string, PatternPiece>();
        foreach (var (s, mirror) in new[] { ("l", false), ("r", true) })
        {
            pieces[$"f{s}"] = Draw(doc, g, $"Front {s.ToUpperInvariant()}", front, false, mirror, fd, 100, cloth, material);
            pieces[$"b{s}"] = Draw(doc, g, $"Back {s.ToUpperInvariant()}", back, true, mirror, bd, 100, cloth, material);
            pieces[$"wf{s}"] = Draw(doc, g, $"Waistband front {s.ToUpperInvariant()}", bandFront, false, mirror, fd + 1, 70, bandCloth, material);
            pieces[$"wb{s}"] = Draw(doc, g, $"Waistband back {s.ToUpperInvariant()}", bandBack, true, mirror, bd + 1, 70, bandCloth, material);
            foreach (string edge in new[] { "out_lo", "out_hi", "out_hip", "in_hi", "in_lo" })
                Sew(g, pieces[$"f{s}"], edge, pieces[$"b{s}"], edge);
            Sew(g, pieces[$"wf{s}"], "bottom", pieces[$"f{s}"], "waist");
            Sew(g, pieces[$"wb{s}"], "bottom", pieces[$"b{s}"], "waist");
            Sew(g, pieces[$"wf{s}"], "out", pieces[$"wb{s}"], "out");
        }
        Sew(g, pieces["fl"], "cf", pieces["fr"], "cf");
        Sew(g, pieces["fl"], "crotch", pieces["fr"], "crotch");
        Sew(g, pieces["bl"], "cb", pieces["br"], "cb");
        Sew(g, pieces["bl"], "crotch", pieces["br"], "crotch");
        Sew(g, pieces["wfl"], "in", pieces["wfr"], "in");
        Sew(g, pieces["wbl"], "in", pieces["wbr"], "in");
        return g;
    }

    // -------------------------------------------------------------------------------------------------- collar

    /// <summary>Sample points just inside a neck edge, from its shoulder end downwards, over <paramref name="share"/> of its length.</summary>
    private static List<Vector2> NeckRun(PatternPiece piece, int edge, int runs, float share = 1f, float inset = 0.6f)
    {
        var drawn = Outline.FlattenEdge(piece, edge);
        var line = new List<Vector2>(drawn);
        bool reversed = line[^1].Y > line[0].Y;
        if (reversed)
            line.Reverse();
        float total = 0;
        for (int i = 0; i + 1 < line.Count; i++)
            total += Vector2.Distance(line[i], line[i + 1]);
        total *= share;
        var result = new List<Vector2>();
        for (int k = 0; k <= runs; k++)
        {
            float want = total * k / runs, walked = 0;
            var at = line[^1];
            var dir = Vector2.Normalize(line[^1] - line[^2]);
            for (int i = 0; i + 1 < line.Count; i++)
            {
                float step = Vector2.Distance(line[i], line[i + 1]);
                if (walked + step >= want && step > 1e-6f)
                {
                    dir = (line[i + 1] - line[i]) / step;
                    at = line[i] + dir * (want - walked);
                    break;
                }
                walked += step;
            }
            // Cloth is on the left of a counter-clockwise edge; walking it backwards puts it on the right.
            result.Add(at + new Vector2(-dir.Y, dir.X) * (reversed ? -inset : inset));
        }
        return result;
    }

    /// <summary>
    /// A band standing up from a neckline: a collar stand, a ribbed neck, or (cut longer than the neckline, so it
    /// gathers) the cowl a hood makes round the neck when it is pushed back. Works on any garment whose back piece
    /// has edges named neck_l and neck_r and whose two fronts have an edge named neck. It goes in its own garment,
    /// over whatever is worn round the neck so far.
    /// </summary>
    /// <param name="frontShare">How far down each front neck edge the band runs, from the shoulder (0..1).</param>
    /// <param name="rollReachCm">
    /// 0 for a band that stands up. Above 0 the band's top edge is sewn down too, this far out from the neckline, so
    /// the band closes into a padded roll lying round the neck (a hood pushed down, a shawl collar, a neck roll).
    /// <paramref name="heightCm"/> is then the distance round the roll.
    /// </param>
    public static Garment Collar(Document doc, Garment host, BodyMeasurements m, float heightCm = 8, float ease = 1.0f,
        float frontShare = 1f, float flare = 1.1f, float rollReachCm = 0f)
    {
        var back = host.Pieces.FirstOrDefault(p => p.EdgeNamed("neck_l") >= 0 && p.EdgeNamed("neck_r") >= 0)
            ?? throw new ArgumentException("'" + host.Name + "' has no back piece with edges named neck_l and neck_r to sew a collar to.");
        var fronts = host.Pieces.Where(p => p != back && p.EdgeNamed("neck") >= 0).ToList();
        if (fronts.Count != 2)
            throw new ArgumentException("'" + host.Name + "' needs two front pieces with an edge named neck to sew a collar to.");
        frontShare = Math.Clamp(frontShare, 0.1f, 1f);
        var frontLeft = fronts.OrderByDescending(p => p.Placement.Position.X).First();
        var frontRight = fronts.OrderBy(p => p.Placement.Position.X).First();

        // Stitch runs in order round the neck, from the avatar's left front to the right front.
        var runs = new List<(PatternPiece Host, Vector2 From, Vector2 To)>();
        var leftFront = NeckRun(frontLeft, frontLeft.EdgeNamed("neck"), 2, frontShare);     // shoulder end first
        runs.Add((frontLeft, leftFront[2], leftFront[1]));
        runs.Add((frontLeft, leftFront[1], leftFront[0]));
        // The back piece is turned round: the avatar's left is on its -x side.
        var backLeft = NeckRun(back, back.EdgeNamed("neck_l"), 1).OrderByDescending(p => MathF.Abs(p.X)).ToList();
        var backRight = NeckRun(back, back.EdgeNamed("neck_r"), 1).OrderBy(p => MathF.Abs(p.X)).ToList();
        runs.Add((back, backLeft[0], backLeft[1]));
        runs.Add((back, backRight[0], backRight[1]));
        var rightFront = NeckRun(frontRight, frontRight.EdgeNamed("neck"), 2, frontShare);
        runs.Add((frontRight, rightFront[0], rightFront[1]));
        runs.Add((frontRight, rightFront[1], rightFront[2]));

        // A roll's top edge is sewn round the neck too, further out: the same runs, set further into the host.
        var outer = new List<(PatternPiece Host, Vector2 From, Vector2 To)>();
        if (rollReachCm > 0)
        {
            float inset = 0.6f + rollReachCm;
            var lf = NeckRun(frontLeft, frontLeft.EdgeNamed("neck"), 2, frontShare, inset);
            var bl = NeckRun(back, back.EdgeNamed("neck_l"), 1, 1f, inset).OrderByDescending(p => MathF.Abs(p.X)).ToList();
            var br = NeckRun(back, back.EdgeNamed("neck_r"), 1, 1f, inset).OrderBy(p => MathF.Abs(p.X)).ToList();
            var rf = NeckRun(frontRight, frontRight.EdgeNamed("neck"), 2, frontShare, inset);
            outer.Add((frontLeft, lf[2], lf[1]));
            outer.Add((frontLeft, lf[1], lf[0]));
            outer.Add((back, bl[0], bl[1]));
            outer.Add((back, br[0], br[1]));
            outer.Add((frontRight, rf[0], rf[1]));
            outer.Add((frontRight, rf[1], rf[2]));
        }

        var g = NewGarment(doc, rollReachCm > 0 ? "Neck roll" : "Collar", MathF.Min(host.ParticleDistance, 0.9f), 3.2f, m.NeckHeightCm - 12, m.NeckHeightCm + heightCm + 4);
        var cloth = back.Physics.Clone();
        cloth.Bend = MathF.Max(cloth.Bend, 78);
        cloth.ShrinkWeft = cloth.ShrinkWarp = 1;

        // The band, drawn from behind: its lower edge is one stretch per stitch run, its height tapers to the ends.
        var xs = new List<float> { 0 };
        foreach (var run in runs)
            xs.Add(xs[^1] + Vector2.Distance(run.From, run.To) * ease);
        float width = xs[^1];
        // The top edge: flared out from the bottom one, or, for a roll, one stretch per outer run.
        var tops = xs.Select(x => (x - width / 2) * flare).ToList();
        if (outer.Count > 0)
        {
            var ox = new List<float> { 0 };
            foreach (var run in outer)
                ox.Add(ox[^1] + Vector2.Distance(run.From, run.To) * ease);
            tops = ox.Select(x => x - ox[^1] / 2).ToList();
        }
        var points = new List<PatternPoint>();
        for (int i = 0; i < xs.Count; i++)
            points.Add(new PatternPoint(GarmentBlocks.Snap(xs[i] - width / 2), 0) { EdgeName = i < runs.Count ? "neck_" + i : "end_r" });
        for (int i = xs.Count - 1; i >= 0; i--)
        {
            float t = MathF.Abs(xs[i] - width / 2) / (width / 2);                       // 0 at the back, 1 at the ends
            float h = heightCm * (frontShare < 1f ? 1f - 0.65f * t * t : 1f);
            string? name = i == 0 ? "end_l" : outer.Count > 0 ? "top_" + (i - 1) : null;
            points.Add(new PatternPoint(GarmentBlocks.Snap(tops[i]), GarmentBlocks.Snap(h)) { EdgeName = name });
        }
        var piece = new PatternPiece { Name = doc.UniqueName("Collar"), Points = points, Physics = cloth, MaterialId = back.MaterialId };
        var neckBack = Mesher.ArrangedFrame(back, backLeft[1]).Position;
        piece.Placement.Position = new Vector3(0, m.NeckHeightCm * 0.01f + heightCm * 0.005f, neckBack.Z + 0.10f);
        piece.Placement.Rotation = new Vector3(8, 180, 0);
        piece.Placement.Curvature = Math.Clamp(width * flare / 16f * 180f / MathF.PI, 90f, 300f);     // round a neck of about 16 cm radius
        PatternLayout.Place(doc, g, piece);
        g.Pieces.Add(piece);
        for (int i = 0; i < runs.Count; i++)
        {
            g.SurfaceStitches.Add(new SurfaceStitch
            {
                SourcePieceId = piece.Id, SourceEdge = piece.EdgeNamed("neck_" + i), TargetPieceId = runs[i].Host.Id,
                TargetStart = runs[i].From, TargetEnd = runs[i].To,
            });
        }
        // The top edge runs the other way round the piece, so each of its stretches starts at the run's far end.
        for (int i = 0; i < outer.Count; i++)
        {
            g.SurfaceStitches.Add(new SurfaceStitch
            {
                SourcePieceId = piece.Id, SourceEdge = piece.EdgeNamed("top_" + i), TargetPieceId = outer[i].Host.Id,
                TargetStart = outer[i].To, TargetEnd = outer[i].From,
            });
        }
        return g;
    }

    // ---------------------------------------------------------------------------------------------------- hood

    /// <summary>
    /// A hood for a garment made by the jacket or hoodie template (any garment whose back piece has edges named
    /// neck_l and neck_r and whose fronts have an edge named neck). It is cut as two panels joined down the middle,
    /// sewn round the neckline, and laid open down the back the way a hood falls when it is not worn, in its own
    /// garment so that it lies over whatever is worn on top of its host.
    /// </summary>
    /// <param name="frontShare">
    /// How much of each front neck edge the hood is sewn along, from the shoulder down (0..1). 1 follows the neckline
    /// to the centre front, as on a hoodie worn on its own; about 0.3 keeps the hood behind the neck, for a hoodie
    /// under a jacket whose collar must stay in view.
    /// </param>
    public static Garment Hood(Document doc, Garment host, BodyMeasurements m, float lengthCm = 36, float widthCm = 26, float ease = 1.12f,
        float frontShare = 1f)
    {
        frontShare = Math.Clamp(frontShare, 0.1f, 1f);
        var back = host.Pieces.FirstOrDefault(p => p.EdgeNamed("neck_l") >= 0 && p.EdgeNamed("neck_r") >= 0)
            ?? throw new ArgumentException($"'{host.Name}' has no back piece with edges named neck_l and neck_r to sew a hood to.");
        var fronts = host.Pieces.Where(p => p != back && p.EdgeNamed("neck") >= 0).ToList();
        if (fronts.Count != 2)
            throw new ArgumentException($"'{host.Name}' needs two front pieces with an edge named neck to sew a hood to.");
        var g = NewGarment(doc, "Hood", MathF.Min(host.ParticleDistance, 0.9f), 3.2f, m.NeckHeightCm - lengthCm, m.NeckHeightCm + 6);
        var like = back.Physics.Clone();
        like.Bend = MathF.Min(like.Bend, 40);
        like.ShrinkWeft = like.ShrinkWarp = 1;

        // Stitch lines on the host, as short straight runs just inside each neck edge.
        static List<Vector2> Inside(PatternPiece piece, int edge, int runs, float share = 1f)
        {
            var line = Outline.FlattenEdge(piece, edge);
            // Measure from the end nearer the shoulder: the higher one.
            if (line[^1].Y > line[0].Y)
                line.Reverse();
            float total = 0;
            for (int i = 0; i + 1 < line.Count; i++)
                total += Vector2.Distance(line[i], line[i + 1]);
            total *= share;
            float sign = Outline.FlattenEdge(piece, edge)[0] == line[0] ? 1f : -1f;   // which side the cloth is on now
            var result = new List<Vector2>();
            for (int k = 0; k <= runs; k++)
            {
                float want = total * k / runs, walked = 0;
                var at = line[^1];
                var dir = Vector2.Normalize(line[^1] - line[^2]);
                for (int i = 0; i + 1 < line.Count; i++)
                {
                    float step = Vector2.Distance(line[i], line[i + 1]);
                    if (walked + step >= want && step > 1e-6f)
                    {
                        dir = (line[i + 1] - line[i]) / step;
                        at = line[i] + dir * (want - walked);
                        break;
                    }
                    walked += step;
                }
                result.Add(at + new Vector2(-dir.Y, dir.X) * (0.6f * sign));     // cloth is on the left of a counter-clockwise edge
            }
            return result;
        }

        var made = new List<PatternPiece>();
        foreach (float side in new[] { 1f, -1f })
        {
            // The avatar's left is +X; a back piece's own pattern x runs the other way.
            string s = side > 0 ? "l" : "r";
            var front = fronts.OrderByDescending(p => p.Placement.Position.X * side).First();
            int backEdge = back.EdgeNamed($"neck_{s}"), frontEdge = front.EdgeNamed("neck");
            var backRun = Inside(back, backEdge, 1);
            var frontRun = Inside(front, frontEdge, 2, frontShare);
            float backLen = Outline.EdgeLength(back, backEdge) * ease, frontLen = Outline.EdgeLength(front, frontEdge) * ease * frontShare;

            // One panel, drawn as it lies on the back seen from behind: the centre seam down the middle of the back,
            // the neck edge across the top and on out along the shoulder, the face edge down the outside.
            float sx = -side;        // towards this side of the body in the back piece's pattern x
            var outline = new P[]
            {
                new(0, 0, "centre"),                                                     // centre back of the neck, going down
                new(0, -lengthCm, "crown", sx * widthCm * 0.55f, -lengthCm - 3),
                new(sx * widthCm, -lengthCm * 0.62f, "face"),
                new(sx * (backLen + frontLen * 0.95f), -frontLen * 0.30f, "neck_f2"),
                new(sx * (backLen + frontLen * 0.5f), -frontLen * 0.08f, "neck_f1"),
                new(sx * backLen, 1.0f, "neck_b"),
            };
            var points = outline.Select(p => new PatternPoint(GarmentBlocks.Snap(p.X), GarmentBlocks.Snap(p.Y))
            {
                EdgeName = p.Edge,
                Curve = p.Cx is { } cx ? new Vector2(GarmentBlocks.Snap(cx), GarmentBlocks.Snap(p.Cy!.Value)) : null,
            }).ToList();
            float area = 0;
            for (int i = 0; i < points.Count; i++)
                area += points[i].Position.X * points[(i + 1) % points.Count].Position.Y - points[(i + 1) % points.Count].Position.X * points[i].Position.Y;
            if (area < 0)
            {
                int n = points.Count;
                var turned = new List<PatternPoint>(n);
                for (int i = 0; i < n; i++)
                {
                    int src = (n - i) % n, prev = (src - 1 + n) % n;
                    turned.Add(new PatternPoint(points[src].Position.X, points[src].Position.Y) { Curve = points[prev].Curve, EdgeName = points[prev].EdgeName });
                }
                points = turned;
            }
            var piece = new PatternPiece { Name = doc.UniqueName(side > 0 ? "Hood L" : "Hood R"), Points = points, Physics = like.Clone(), MaterialId = back.MaterialId };
            // Lay it on the upper back, hanging from the neckline.
            var neckBack = Mesher.ArrangedFrame(back, backRun[side > 0 ? 1 : 0]).Position;
            var (lo, hi) = Outline.Bounds(piece);
            var centre = (lo + hi) / 2;
            piece.Placement.Rotation = new Vector3(8, 180, 0);
            piece.Placement.Position = new Vector3(side * MathF.Abs(centre.X) * 0.01f, m.NeckHeightCm * 0.01f + centre.Y * 0.01f - 0.02f, neckBack.Z - 0.06f);
            piece.Placement.Curvature = 30;
            PatternLayout.Place(doc, g, piece);
            g.Pieces.Add(piece);
            made.Add(piece);

            void Stitch(string edge, PatternPiece target, Vector2 from, Vector2 to)
            {
                int e = piece.EdgeNamed(edge);
                var a = piece.PointAt(e).Position;
                var z = piece.PointAt(piece.NextPoint(e)).Position;
                // The stitch runs the way the hood edge runs: towards the centre back or away from it.
                bool towardsCentre = MathF.Abs(z.X) < MathF.Abs(a.X);
                g.SurfaceStitches.Add(new SurfaceStitch
                {
                    SourcePieceId = piece.Id, SourceEdge = e, TargetPieceId = target.Id,
                    TargetStart = towardsCentre ? from : to, TargetEnd = towardsCentre ? to : from,
                });
            }
            // Runs are listed from the outer end of each host edge towards... whichever way that edge was drawn, so
            // order them by distance from the centre back instead.
            var backOuter = backRun.OrderByDescending(p => MathF.Abs(p.X)).First();
            var backInner = backRun.OrderBy(p => MathF.Abs(p.X)).First();
            Stitch("neck_b", back, backOuter, backInner);
            var frontByHeight = frontRun.OrderByDescending(p => p.Y).ToList();     // neck point first, centre front last
            Stitch("neck_f1", front, frontByHeight[1], frontByHeight[0]);
            Stitch("neck_f2", front, frontByHeight[2], frontByHeight[1]);
        }
        g.Seams.Add(new Seam
        {
            A = new EdgeRef(made[0].Id, made[0].EdgeNamed("centre")), B = new EdgeRef(made[1].Id, made[1].EdgeNamed("centre")),
            Reversed = Twisted(made[0], made[0].EdgeNamed("centre"), made[1], made[1].EdgeNamed("centre")),
        });
        return g;
    }
}

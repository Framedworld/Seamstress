using System.Numerics;

namespace Seamstress.Core.Simulation;

/// <summary>One tape measurement round a limb or the torso: where it was taken and how far round it is.</summary>
public sealed record TapeSection(string Name, Vector3 Centre, float CircumferenceCm, float WidthCm, float DepthCm);

/// <summary>Where an arm is and how thick: its axis from shoulder to wrist and sections along it.</summary>
public sealed record ArmMeasure(string Side, Vector3 Shoulder, Vector3 Wrist, Vector3 Axis, float DroopDegrees,
    float LengthCm, List<TapeSection> Sections);

/// <summary>Top of the shoulder at a distance from the centre line: what a shoulder seam has to clear.</summary>
public sealed record RidgePoint(float XCm, float TopYCm, float ZCm);

/// <summary>
/// Tape measurements of a standing avatar (Y up, facing +Z, arms out to the sides or lowered): what a pattern
/// drafter needs before cutting anything. Circumferences are taken as a tape would lie, round the convex outline.
/// </summary>
public sealed class BodyMeasurements
{
    public float HeightCm { get; private set; }
    public float CrotchHeightCm { get; private set; }
    public float ArmpitHeightCm { get; private set; }
    public float NeckHeightCm { get; private set; }
    public float ShoulderWidthCm { get; private set; }
    public List<TapeSection> Torso { get; } = new();

    /// <summary>The torso's girth every centimetre from the crotch to the armpits, lowest first.</summary>
    public List<TapeSection> TorsoProfile { get; } = new();

    /// <summary>Girth of the torso at a height above the floor, from <see cref="TorsoProfile"/>; 0 outside it.</summary>
    public float TorsoGirthAt(float heightCm)
    {
        if (TorsoProfile.Count == 0)
            return 0;
        var nearest = TorsoProfile.MinBy(s => MathF.Abs(s.Centre.Y * 100f - heightCm))!;
        return MathF.Abs(nearest.Centre.Y * 100f - heightCm) > 3f ? 0 : nearest.CircumferenceCm;
    }
    public List<TapeSection> LeftLeg { get; } = new();
    public List<TapeSection> RightLeg { get; } = new();
    public List<ArmMeasure> Arms { get; } = new();
    public List<RidgePoint> ShoulderRidge { get; } = new();

    private sealed record Loop(float MinX, float MaxX, float MinZ, float MaxZ, float Circumference, int Count)
    {
        public Vector3 Centre(float y) => new((MinX + MaxX) / 2, y, (MinZ + MaxZ) / 2);
    }

    public static BodyMeasurements Measure(TriMesh mesh)
    {
        var m = new BodyMeasurements();
        var (min, max) = mesh.Bounds();
        float height = max.Y - min.Y;
        m.HeightCm = height * 100f;
        if (height < 0.2f || mesh.Triangles.Length < 12)
            return m;

        List<Loop> Slice(float y) => Loops(CutPoints(mesh, new Vector3(0, y, 0), Vector3.UnitY), 0.02f);
        Loop? Central(List<Loop> loops) => loops.Where(l => l.MinX < 0 && l.MaxX > 0).OrderByDescending(l => l.Count).FirstOrDefault();

        // Crotch: going up from the knees, the last height at which each leg is still its own loop.
        float crotch = min.Y + height * 0.45f;
        for (float y = min.Y + height * 0.20f; y < min.Y + height * 0.60f; y += 0.005f)
        {
            var loops = Slice(y);
            if (loops.Count(l => l.Count > 8 && (l.MaxX < 0.005f || l.MinX > -0.005f)) >= 2 && Central(loops) == null)
                crotch = y;
            else if (Central(loops) != null && y > min.Y + height * 0.28f)
                break;
        }
        m.CrotchHeightCm = (crotch - min.Y) * 100f;

        // Armpit: going up the torso, the last height at which the arms are still separate from it (or absent).
        float armpit = min.Y + height * 0.72f;
        float torsoHalf = 0;
        for (float y = crotch + 0.05f; y < min.Y + height * 0.88f; y += 0.005f)
        {
            var c = Central(Slice(y));
            if (c == null)
                continue;
            float half = MathF.Max(-c.MinX, c.MaxX);
            if (torsoHalf > 0 && half > torsoHalf * 1.35f && y > min.Y + height * 0.6f)
                break;
            if (y < min.Y + height * 0.7f)
                torsoHalf = MathF.Max(torsoHalf, half);
            armpit = y;
        }
        m.ArmpitHeightCm = (armpit - min.Y) * 100f;

        // Neck: the narrowest slice between the shoulders and the head.
        float neck = armpit + 0.2f, neckWidth = float.MaxValue;
        for (float y = armpit + 0.08f; y < max.Y - height * 0.06f; y += 0.005f)
        {
            var c = Central(Slice(y));
            if (c != null && c.MaxX - c.MinX < neckWidth)
            {
                neckWidth = c.MaxX - c.MinX;
                neck = y;
            }
        }
        m.NeckHeightCm = (neck - min.Y) * 100f;

        void Tape(List<TapeSection> list, string name, float y, Func<List<Loop>, Loop?> pick)
        {
            if (pick(Slice(y)) is { } l)
                list.Add(new TapeSection(name, l.Centre(y), l.Circumference * 100f, (l.MaxX - l.MinX) * 100f, (l.MaxZ - l.MinZ) * 100f));
        }

        // Torso from crotch to armpit, plus the named girths a drafter asks for.
        float span = armpit - crotch;
        var girths = new List<(float Y, Loop L)>();
        for (float y = crotch + 0.02f; y <= armpit + 0.001f; y += 0.01f)
        {
            if (Central(Slice(y)) is { } l)
                girths.Add((y, l));
        }
        foreach (var (y, l) in girths)
            m.TorsoProfile.Add(new TapeSection("", l.Centre(y), l.Circumference * 100f, (l.MaxX - l.MinX) * 100f, (l.MaxZ - l.MinZ) * 100f));
        if (girths.Count > 4)
        {
            var hips = girths.Where(g => g.Y < crotch + span * 0.45f).MaxBy(g => g.L.Circumference);
            var waist = girths.Where(g => g.Y > hips.Y && g.Y < armpit - span * 0.2f).DefaultIfEmpty(hips).MinBy(g => g.L.Circumference);
            var belly = girths.Where(g => g.Y > hips.Y).DefaultIfEmpty(hips).MaxBy(g => g.L.MaxZ);
            var chest = girths[^1];
            foreach (var (name, g) in new[] { ("hips", hips), ("belly", belly), ("waist", waist), ("chest", chest) })
                m.Torso.Add(new TapeSection(name, g.L.Centre(g.Y), g.L.Circumference * 100f, (g.L.MaxX - g.L.MinX) * 100f, (g.L.MaxZ - g.L.MinZ) * 100f));
        }
        Tape(m.Torso, "neck", neck, Central);

        // Legs, each on its own.
        foreach (var (list, side) in new[] { (m.LeftLeg, 1f), (m.RightLeg, -1f) })
        {
            Loop? Leg(List<Loop> loops) => loops.Where(l => l.Count > 8 && (l.MinX + l.MaxX) * side > 0 && !(l.MinX < 0 && l.MaxX > 0))
                .OrderByDescending(l => l.Count).FirstOrDefault();
            // Knee height follows stature rather than leg length, so a long torso does not move it.
            float knee = min.Y + MathF.Min(height * 0.275f, (crotch - min.Y) * 0.8f);
            float calf = knee - 0.08f, calfGirth = 0, ankle = min.Y + height * 0.05f, ankleGirth = float.MaxValue;
            for (float y = min.Y + height * 0.10f; y < knee - 0.04f; y += 0.01f)
            {
                if (Leg(Slice(y)) is { } l && l.Circumference > calfGirth)
                {
                    calfGirth = l.Circumference;
                    calf = y;
                }
            }
            for (float y = min.Y + height * 0.04f; y < calf; y += 0.01f)
            {
                if (Leg(Slice(y)) is { } l && l.Circumference < ankleGirth)
                {
                    ankleGirth = l.Circumference;
                    ankle = y;
                }
            }
            Tape(list, "thigh", crotch - 0.03f, Leg);
            Tape(list, "midThigh", (crotch + knee) / 2, Leg);
            Tape(list, "knee", knee, Leg);
            Tape(list, "calf", calf, Leg);
            Tape(list, "ankle", ankle, Leg);
        }

        // Shoulder ridge: the highest point of the body at each distance from the centre line, below the neck.
        float shoulderHalf = 0;
        for (float x = 0.08f; x < 0.45f; x += 0.02f)
        {
            float top = float.MinValue, z = 0;
            foreach (var p in mesh.Positions)
            {
                if (MathF.Abs(p.X - x) < 0.012f && p.Y <= neck + 0.005f && p.Y > armpit - 0.05f && p.Y > top)
                {
                    top = p.Y;
                    z = p.Z;
                }
            }
            if (top <= float.MinValue / 2 || top < armpit + 0.06f)
                break;
            m.ShoulderRidge.Add(new RidgePoint(x * 100f, (top - min.Y) * 100f, z * 100f));
            if (top > armpit + 0.10f)
                shoulderHalf = x;
        }
        m.ShoulderWidthCm = shoulderHalf * 200f;

        // Arms: the line through everything outside the torso, and the girth along it.
        float armStart = MathF.Max(torsoHalf + 0.04f, 0.18f);
        foreach (float side in new[] { 1f, -1f })
        {
            var pts = mesh.Positions.Where(p => p.X * side > armStart && p.Y > crotch).ToList();
            if (pts.Count < 30)
                continue;
            var centre = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Count;
            var axis = PrincipalAxis(pts, centre);
            if (axis.X * side < 0)
                axis = -axis;
            float t0 = pts.Min(p => Vector3.Dot(p - centre, axis)), t1 = pts.Max(p => Vector3.Dot(p - centre, axis));
            var u = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ));
            var w = Vector3.Cross(axis, u);
            var sections = new List<(float T, TapeSection S)>();
            const int stations = 12;
            for (int i = 0; i < stations; i++)
            {
                float t = t0 + (t1 - t0) * (i + 0.5f) / stations;
                var origin = centre + axis * t;
                var cut = CutPoints(mesh, origin, axis).Where(p => Vector3.Distance(p, origin) < 0.16f).ToList();
                if (cut.Count < 6)
                    continue;
                var flat = cut.Select(p => new Vector2(Vector3.Dot(p - origin, u), Vector3.Dot(p - origin, w))).ToList();
                var mid = cut.Aggregate(Vector3.Zero, (a, b) => a + b) / cut.Count;
                sections.Add((t, new TapeSection($"{i + 1}/{stations}", mid, HullPerimeter(flat) * 100f,
                    (flat.Max(p => p.X) - flat.Min(p => p.X)) * 100f, (flat.Max(p => p.Y) - flat.Min(p => p.Y)) * 100f)));
            }
            if (sections.Count < 4)
                continue;
            // The wrist is where the forearm stops getting thinner; beyond it the hand widens again.
            // A mitten hand that never widens has no such point: the wrist is then taken four fifths of the way out.
            var wrist = sections[Math.Min(sections.Count - 1, (int)(sections.Count * 0.8f))];
            for (int i = sections.Count / 2; i + 1 < sections.Count; i++)
            {
                if (sections[i].S.CircumferenceCm < sections[i + 1].S.CircumferenceCm)
                {
                    wrist = sections[i];
                    break;
                }
            }
            var shoulder = sections[0].S.Centre;
            float droop = MathF.Atan2(-axis.Y, MathF.Abs(axis.X)) * 180f / MathF.PI;
            m.Arms.Add(new ArmMeasure(side > 0 ? "left" : "right", shoulder, wrist.S.Centre, axis, droop,
                Vector3.Distance(shoulder, wrist.S.Centre) * 100f, sections.Where(s => s.T <= wrist.T).Select(s => s.S).ToList()));
        }
        // Bodies are symmetric, meshes not always: when the two arms disagree, both get the shorter reading, so a
        // garment's sleeves come out the same length.
        if (m.Arms.Count == 2 && MathF.Abs(m.Arms[0].LengthCm - m.Arms[1].LengthCm) > 0.04f * MathF.Max(m.Arms[0].LengthCm, m.Arms[1].LengthCm))
        {
            int shorter = m.Arms[0].LengthCm <= m.Arms[1].LengthCm ? 0 : 1;
            var keep = m.Arms[shorter];
            var fix = m.Arms[1 - shorter];
            Vector3 Flip(Vector3 v) => new(-v.X, v.Y, v.Z);
            m.Arms[1 - shorter] = fix with
            {
                Shoulder = Flip(keep.Shoulder), Wrist = Flip(keep.Wrist), Axis = Flip(keep.Axis), DroopDegrees = keep.DroopDegrees,
                LengthCm = keep.LengthCm, Sections = keep.Sections.Select(sec => sec with { Centre = Flip(sec.Centre) }).ToList(),
            };
        }
        return m;
    }

    /// <summary>Points where the mesh's edges cross the plane through <paramref name="origin"/>.</summary>
    private static List<Vector3> CutPoints(TriMesh mesh, Vector3 origin, Vector3 normal)
    {
        var result = new List<Vector3>();
        var pos = mesh.Positions;
        var tris = mesh.Triangles;
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                var a = pos[tris[t + k]];
                var b = pos[tris[t + (k + 1) % 3]];
                float da = Vector3.Dot(a - origin, normal), db = Vector3.Dot(b - origin, normal);
                if (da * db < 0)
                    result.Add(a + (b - a) * (da / (da - db)));
            }
        }
        return result;
    }

    /// <summary>Groups the points of a horizontal cut into separate loops wherever there is a gap along X.</summary>
    private static List<Loop> Loops(List<Vector3> points, float gap)
    {
        var loops = new List<Loop>();
        if (points.Count == 0)
            return loops;
        points.Sort((a, b) => a.X.CompareTo(b.X));
        int start = 0;
        for (int i = 1; i <= points.Count; i++)
        {
            if (i < points.Count && points[i].X - points[i - 1].X <= gap)
                continue;
            var part = points.GetRange(start, i - start);
            loops.Add(new Loop(part[0].X, part[^1].X, part.Min(p => p.Z), part.Max(p => p.Z),
                HullPerimeter(part.Select(p => new Vector2(p.X, p.Z)).ToList()), part.Count));
            start = i;
        }
        return loops;
    }

    private static float HullPerimeter(List<Vector2> points)
    {
        if (points.Count < 3)
            return 0;
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Vector2>();
        for (int pass = 0; pass < 2; pass++)
        {
            int floor = hull.Count + 1;
            foreach (var p in pass == 0 ? sorted : Enumerable.Reverse(sorted))
            {
                while (hull.Count >= floor + 1 && Cross(hull[^2], hull[^1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        float length = 0;
        for (int i = 0; i < hull.Count; i++)
            length += Vector2.Distance(hull[i], hull[(i + 1) % hull.Count]);
        return length;
    }

    /// <summary>Direction of greatest spread of the points (power iteration on their covariance).</summary>
    private static Vector3 PrincipalAxis(List<Vector3> points, Vector3 centre)
    {
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in points)
        {
            var d = p - centre;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
            yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        var v = new Vector3(1, -0.3f, 0);
        for (int i = 0; i < 40; i++)
        {
            v = new Vector3(xx * v.X + xy * v.Y + xz * v.Z, xy * v.X + yy * v.Y + yz * v.Z, xz * v.X + yz * v.Y + zz * v.Z);
            float len = v.Length();
            if (len < 1e-12f)
                return Vector3.UnitX;
            v /= len;
        }
        return v;
    }
}

using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Mcp;

public sealed class TuckOptions
{
    /// <summary>Only tuck this garment (under whatever is worn over it); null tucks every layer.</summary>
    public string? GarmentId { get; set; }

    /// <summary>Air left between the inner cloth and the cloth over it, on top of half of each one's thickness (mm).</summary>
    public float ClearanceMm { get; set; } = 1f;

    /// <summary>
    /// Most rounds over every pair of layers: tucking a layer can leave the one under it showing again, and the cloth
    /// following a tucked spot can dip under a neighbouring part of the outer garment. Stops early once nothing moves.
    /// </summary>
    public int Passes { get; set; } = 10;
}

/// <summary>One inner garment tucked under one outer garment.</summary>
public sealed record TuckedPair(string Inner, string Outer, int Moved, int Lifted, float MaxMoveMm);

/// <summary>
/// Puts inner layers back under the layers worn over them without simulating: every vertex of an inner garment that
/// lies outside a garment worn over it, where that garment covers it, is moved back under it (half of each cloth's
/// thickness and a clearance below it) along the body's normal, its neighbours following part of the way. Nothing is moved closer to the skin than
/// it was or than half its thickness; where there is no room left, the outer cloth is lifted instead.
/// For crowded outfits where simulating the inner layer again cannot settle under the outer ones.
/// </summary>
public static class LayerTuck
{
    private const float Reach = 0.03f;

    public static List<TuckedPair> Tuck(Document doc, Func<Garment, GarmentMesh> meshFor, Avatar? avatar, TuckOptions? options = null)
    {
        options ??= new TuckOptions();
        var focus = options.GarmentId == null ? null
            : doc.FindGarment(options.GarmentId) ?? throw new ArgumentException($"Garment '{options.GarmentId}' not found.");
        var totals = new Dictionary<(string, string), TuckedPair>();
        for (int pass = 0; pass < Math.Clamp(options.Passes, 1, 30); pass++)
        {
            int movedThisPass = 0;
            var scene = OutfitCritic.BuildScene(doc, meshFor, avatar);
            var garments = doc.Garments.Where(g => g.Visible && scene.Of(g).Any(p => p.Draped && !p.Hard)).ToList();
            var outers = garments.Where(g => OutfitCritic.IsShell(scene, g)).OrderByDescending(g => g.Layer).ThenByDescending(g => g.BodyOffset).ToList();
            // Outside in: the layer under the jacket first, then what is under that, so each is tucked under where its
            // cover now is.
            var pairs = (from outer in outers
                         from inner in garments.Where(g => g != outer && OutfitCritic.CompareLayer(g, outer) < 0)
                                               .OrderByDescending(g => g.Layer).ThenByDescending(g => g.BodyOffset)
                         where focus == null || inner == focus
                         select (inner, outer)).ToList();
            foreach (var (inner, outer) in pairs)
            {
                scene = OutfitCritic.BuildScene(doc, meshFor, avatar);
                var result = TuckPair(scene, meshFor(inner), inner, outer, options.ClearanceMm * 0.001f);
                if (result.Moved == 0 && result.Lifted == 0)
                    continue;
                movedThisPass += result.Moved + result.Lifted;
                var key = (inner.Id, outer.Id);
                totals[key] = totals.TryGetValue(key, out var was)
                    ? was with { Moved = was.Moved + result.Moved, Lifted = was.Lifted + result.Lifted, MaxMoveMm = MathF.Max(was.MaxMoveMm, result.MaxMoveMm) }
                    : result;
            }
            if (movedThisPass == 0)
                break;
        }
        return totals.Values.ToList();
    }

    private static TuckedPair TuckPair(OutfitCritic.Scene scene, GarmentMesh innerMesh, Garment inner, Garment outer, float clearance)
    {
        var outerPieces = scene.Of(outer).Where(p => p.Draped && !p.Hard).ToList();
        var hash = OutfitCritic.SpatialHash.Build(outerPieces, Reach);
        var lifts = new Dictionary<(OutfitCritic.PieceData Piece, int Vertex), float>();
        int moved = 0;
        float maxMove = 0;
        var work = new Dictionary<string, (OutfitCritic.PieceData Piece, Vector3[] Push, bool[] Pushed)>();
        foreach (var pd in scene.Of(inner).Where(p => p.Draped && !p.Hard))
        {
            float innerHalf = inner.EffectiveThicknessMm(pd.Piece) * 0.0005f;
            var push = new Vector3[pd.X.Length];
            var pushed = new bool[pd.X.Length];
            work[pd.Piece.Id] = (pd, push, pushed);
            for (int i = 0; i < pd.X.Length; i++)
            {
                var v = pd.X[i];
                if (hash.Closest(v, Reach) is not { } hit || OutfitCritic.Outside(scene, v, hit) is not { } outside)
                    continue;
                // Only cloth that shows (as critique_outfit judges it) is moved; it then goes a full gap under.
                if (outside <= 0.001f + outer.ShownThicknessMm * 0.0005f)
                    continue;
                float outerHalf = outer.EffectiveThicknessMm(hit.Piece.Piece) * 0.0005f;
                float gap = innerHalf + outerHalf + clearance;
                // Wanted: at least `gap` under the outer cloth.
                float by = outside + gap;
                if (by <= 0)
                    continue;
                Vector3 inward;
                if (scene.Avatar != null && scene.Avatar.Sdf.Sample(v, out var bodyNormal) is var dv && dv != float.MaxValue)
                {
                    inward = -bodyNormal;
                    // Never into the skin: what has no room lifts the outer cloth over it instead.
                    float room = dv - (innerHalf + 0.001f);
                    if (by > room)
                    {
                        var key = (hit.Piece, hit.Vertex);
                        lifts[key] = MathF.Max(lifts.GetValueOrDefault(key), by - MathF.Max(room, 0));
                        by = MathF.Max(room, 0);
                    }
                }
                else
                    inward = -scene.Outward(hit.Point, hit.Normal);
                if (by <= 0)
                    continue;
                push[i] = inward * by;
                pushed[i] = true;
                moved++;
                maxMove = MathF.Max(maxMove, by);
            }
        }
        if (moved > 0)
        {
            // Both sides of a seam move together, or tucking one panel would open the seam to the next.
            var stitches = innerMesh.Stitches
                .Select(st => (A: work.GetValueOrDefault(innerMesh.Pieces[st.PieceA].PieceId), VA: st.VertexA,
                               B: work.GetValueOrDefault(innerMesh.Pieces[st.PieceB].PieceId), VB: st.VertexB))
                .Where(st => st.A.Piece != null && st.B.Piece != null)
                .ToList();
            void Join(Func<string, Vector3[]> moves, bool markPushed)
            {
                for (int round = 0; round < 2; round++)
                {
                    foreach (var (a, va, b, vb) in stitches)
                    {
                        var ma = moves(a.Piece.Piece.Id);
                        var mb = moves(b.Piece.Piece.Id);
                        var m = ma[va].LengthSquared() >= mb[vb].LengthSquared() ? ma[va] : mb[vb];
                        if (m == Vector3.Zero)
                            continue;
                        ma[va] = mb[vb] = m;
                        if (markPushed)
                            a.Pushed[va] = b.Pushed[vb] = true;
                    }
                }
            }
            Join(id => work[id].Push, true);
            var totals = work.ToDictionary(w => w.Key, w => w.Value.Pushed.Any(x => x)
                ? Falloff(w.Value.Piece, w.Value.Push, w.Value.Pushed)
                : new Vector3[w.Value.Push.Length]);
            Join(id => totals[id], false);
            foreach (var (id, (pd, _, _)) in work)
                if (totals[id].Any(m => m != Vector3.Zero))
                    Apply(scene, pd, totals[id], inner.EffectiveThicknessMm(pd.Piece) * 0.0005f);
        }
        int lifted = Lift(scene, lifts);
        return new TuckedPair(inner.Name, outer.Name, moved, lifted, MathF.Round(maxMove * 1000f, 1));
    }

    /// <summary>The moves spread to two rings of neighbours (0.6 and 0.3 of the moves next to them), so no step is left.</summary>
    private static Vector3[] Falloff(OutfitCritic.PieceData pd, Vector3[] push, bool[] pushed)
    {
        var total = (Vector3[])push.Clone();
        var current = push;
        foreach (float weight in new[] { 0.6f, 0.3f })
        {
            var next = new Vector3[push.Length];
            for (int i = 0; i < push.Length; i++)
            {
                if (pushed[i])
                    continue;
                var sum = Vector3.Zero;
                int n = 0;
                foreach (int j in pd.Neighbours[i])
                {
                    if (current[j] == Vector3.Zero)
                        continue;
                    sum += current[j];
                    n++;
                }
                if (n > 0)
                    next[i] = sum / n * weight;
            }
            for (int i = 0; i < push.Length; i++)
                if (!pushed[i] && next[i].LengthSquared() > total[i].LengthSquared())
                    total[i] = next[i];
            current = next;
        }
        return total;
    }

    /// <summary>Writes the moved positions as the piece's drape, keeping each vertex at least as far from the skin as it can be.</summary>
    private static void Apply(OutfitCritic.Scene scene, OutfitCritic.PieceData pd, Vector3[] move, float half)
    {
        var drape = new float[pd.X.Length * 3];
        for (int i = 0; i < pd.X.Length; i++)
        {
            var p = pd.X[i] + move[i];
            if (move[i] != Vector3.Zero && scene.Avatar != null)
            {
                float before = scene.Avatar.Sdf.Sample(pd.X[i], out _);
                float now = scene.Avatar.Sdf.Sample(p, out var normal);
                float floor = MathF.Min(before, half + 0.001f);
                if (before != float.MaxValue && now != float.MaxValue && now < floor)
                    p += normal * (floor - now);
            }
            drape[3 * i] = p.X;
            drape[3 * i + 1] = p.Y;
            drape[3 * i + 2] = p.Z;
        }
        pd.Piece.Drape = drape;
        pd.Piece.DrapeKey = pd.Mesh.Key;
    }

    /// <summary>Lifts outer cloth over inner cloth that had no room to go under it, fading out over 2 cm.</summary>
    private static int Lift(OutfitCritic.Scene scene, Dictionary<(OutfitCritic.PieceData Piece, int Vertex), float> lifts)
    {
        const float radius = 0.02f;
        int lifted = 0;
        foreach (var group in lifts.GroupBy(l => l.Key.Piece))
        {
            var pd = group.Key;
            var sources = group.Select(l => (At: pd.X[l.Key.Vertex], By: l.Value)).ToList();
            var move = new Vector3[pd.X.Length];
            for (int i = 0; i < pd.X.Length; i++)
            {
                float by = 0;
                foreach (var (at, amount) in sources)
                {
                    float d = Vector3.Distance(pd.X[i], at);
                    if (d < radius)
                        by = MathF.Max(by, amount * (1 - d / radius));
                }
                if (by <= 0)
                    continue;
                var outward = scene.Avatar != null && scene.Avatar.Sdf.Sample(pd.X[i], out var normal) != float.MaxValue
                    ? normal
                    : scene.Outward(pd.X[i], pd.Normals[i]);
                move[i] = outward * by;
                lifted++;
            }
            Apply(scene, pd, move, 0f);
        }
        return lifted;
    }
}

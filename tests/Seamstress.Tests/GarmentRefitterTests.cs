using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Seamstress.Tests;

public class GarmentRefitterTests
{
    private readonly ITestOutputHelper _output;

    public GarmentRefitterTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RefitTShirtFromStandardToLargerSameTopologyMannequin()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "seamstress-refit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new BodyLibrary(tempDir);
            var standardProfile = new BodyProfile
            {
                Id = "mannequin-std",
                Name = "Standard Mannequin",
                SourceType = "Mannequin",
                WidthScale = 1.0f,
                HeightScale = 1.0f,
                DepthScale = 1.0f,
            };
            var largerProfile = new BodyProfile
            {
                Id = "mannequin-large",
                Name = "Larger Mannequin",
                SourceType = "Mannequin",
                WidthScale = 1.18f,
                HeightScale = 1.06f,
                DepthScale = 1.12f,
            };

            var sourceAvatar = library.BuildAvatar(standardProfile);
            var targetAvatar = library.BuildAvatar(largerProfile);

            Assert.Equal(sourceAvatar.Mesh.Positions.Length, targetAvatar.Mesh.Positions.Length);
            Assert.Equal(sourceAvatar.Mesh.Triangles.Length, targetAvatar.Mesh.Triangles.Length);

            var doc = new Document();
            var garment = Templates.Create("T-Shirt", doc);
            doc.Garments.Add(garment);
            var ws = new Workspace(doc);

            // Capture original 2D piece widths before refitting.
            var initialWidths = garment.Pieces.ToDictionary(
                p => p.Id,
                p =>
                {
                    var (min, max) = Outline.Bounds(p);
                    return max.X - min.X;
                });

            // Drape the T-shirt onto the standard mannequin first.
            using (var sim = new SimulationController(ws, () => sourceAvatar))
            {
                Assert.Null(sim.Start(false));
                sim.RunFrames(240);
                sim.Stop();
            }

            // Refit onto the wider/taller/deeper same-topology mannequin.
            var result = GarmentRefitter.RefitGarment(doc, ws, garment, sourceAvatar, targetAvatar);

            _output.WriteLine(
                $"Refit T-Shirt: transferred={result.TransferredVertices}, graded={result.GradedPieces}, "
                + $"frames={result.SimulatedFrames}, backend={result.ComputeBackend}, "
                + $"scale=({result.AverageScaleX:0.00}, {result.AverageScaleY:0.00}, {result.AverageScaleZ:0.00}), "
                + $"penetrated={result.PenetratedVerticesAfter}, maxPen={result.MaxPenetrationMmAfter:0.00} mm");

            Assert.True(result.TransferredVertices > 0);
            Assert.Equal(garment.Pieces.Count, result.GradedPieces);
            Assert.True(result.SimulatedFrames > 0);
            Assert.True(result.AverageScaleX > 1.05f, $"Expected AverageScaleX > 1.05, got {result.AverageScaleX:0.000}");
            Assert.True(result.MaxPenetrationMmAfter < 5f, $"Expected MaxPenetrationMmAfter < 5 mm, got {result.MaxPenetrationMmAfter:0.00} mm");

            // Confirm 2D pattern pieces actually grew in width.
            foreach (var piece in garment.Pieces)
            {
                var (min, max) = Outline.Bounds(piece);
                float newWidth = max.X - min.X;
                Assert.True(newWidth > initialWidths[piece.Id] * 1.04f,
                    $"Expected piece '{piece.Name}' width to grow from {initialWidths[piece.Id]:0.0} cm, got {newWidth:0.0} cm");
            }

            // Confirm final draped garment has valid Drape/DrapeKey and zero NaNs.
            var finalMesh = ws.MeshFor(garment);
            foreach (var pm in finalMesh.Pieces)
            {
                var piece = garment.FindPiece(pm.PieceId)!;
                Assert.NotNull(piece.Drape);
                Assert.Equal(pm.Key, piece.DrapeKey);
                var positions = Mesher.CurrentPositions(piece, pm);
                Assert.Equal(pm.VertexCount, positions.Length);
                foreach (var p in positions)
                {
                    Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z), "Refitted drape contains NaN");
                    Assert.False(float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z), "Refitted drape contains Infinity");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BodyLibraryRoundTripSaveLoadListBuildDelete()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "seamstress-bodylib-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new BodyLibrary(tempDir);
            Assert.Empty(library.ListProfiles());

            var profile = new BodyProfile
            {
                Id = "athletic-01",
                Name = "Athletic Frame",
                Description = "Broader shoulders and slightly taller mannequin",
                SourceType = "Mannequin",
                WidthScale = 1.12f,
                HeightScale = 1.05f,
                DepthScale = 1.04f,
                MorphWeights = new Dictionary<string, float> { ["broad"] = 0.5f },
            };

            library.SaveProfile(profile);

            var listed = library.ListProfiles();
            Assert.Single(listed);
            Assert.Equal("athletic-01", listed[0].Id);
            Assert.Equal("Athletic Frame", listed[0].Name);

            var byId = library.LoadProfile("athletic-01");
            Assert.NotNull(byId);
            Assert.Equal(1.12f, byId.WidthScale, precision: 4);
            Assert.Equal(1.05f, byId.HeightScale, precision: 4);
            Assert.Equal(1.04f, byId.DepthScale, precision: 4);
            Assert.Equal(0.5f, byId.MorphWeights["broad"], precision: 4);

            var byName = library.LoadProfile("Athletic Frame");
            Assert.NotNull(byName);
            Assert.Equal("athletic-01", byName.Id);

            var avatar = library.BuildAvatar(byId);
            Assert.Equal("Athletic Frame", avatar.Name);
            Assert.True(avatar.Mesh.Positions.Length > 1000);
            Assert.True(avatar.Mesh.Triangles.Length > 3000);
            var (min, max) = avatar.Mesh.Bounds();
            Assert.True(max.Y - min.Y > 1.65f, $"Expected taller mannequin, height was {max.Y - min.Y:0.00} m");
            Assert.True(avatar.Sdf.Sample(new Vector3(0, 1.25f, 0.045f), out _) < 0f, "Chest interior should be inside SDF");

            Assert.True(library.DeleteProfile("Athletic Frame"));
            Assert.Empty(library.ListProfiles());
            Assert.Null(library.LoadProfile("athletic-01"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BodyLibraryMorphTargetObjAndUnrealLoader()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "seamstress-morphobj-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Minimal closed cube OBJ in centimetres (Z-up) and a same-topology wider morph target.
            string baseObjPath = Path.Combine(tempDir, "base_cm.obj");
            string wideObjPath = Path.Combine(tempDir, "wide_cm.obj");
            WriteBoxObj(baseObjPath, halfX: 20f, halfY: 10f, minZ: 0f, maxZ: 100f);
            WriteBoxObj(wideObjPath, halfX: 30f, halfY: 10f, minZ: 0f, maxZ: 100f);

            var unrealAvatar = MetaHumanDnaLoader.LoadUnrealBodyObj(baseObjPath, scaleToMetres: 0.01f, zUpToYUp: true);
            var (uMin, uMax) = unrealAvatar.Mesh.Bounds();
            Assert.InRange(uMin.Y, -1e-4f, 1e-4f);
            Assert.InRange(uMax.Y, 0.99f, 1.01f);
            Assert.InRange(uMax.X - uMin.X, 0.39f, 0.41f);

            var library = new BodyLibrary(tempDir);
            var morphProfile = new BodyProfile
            {
                Id = "morphed-box",
                Name = "Morphed Box",
                SourceType = "MorphTargetObj",
                BaseMeshPath = baseObjPath,
                Scale = 0.01f,
                ZUpToYUp = true,
                MorphTargetPaths = new Dictionary<string, string> { ["wide"] = wideObjPath },
                MorphWeights = new Dictionary<string, float> { ["wide"] = 0.5f },
            };

            var morphed = library.BuildAvatar(morphProfile);
            var (mMin, mMax) = morphed.Mesh.Bounds();
            // Half-width blended halfway between 0.20 m and 0.30 m => full width 0.50 m.
            Assert.InRange(mMax.X - mMin.X, 0.49f, 0.51f);
            Assert.InRange(mMin.Y, -1e-4f, 1e-4f);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MetaHumanDnaLoaderInspectsValidAndInvalidHeaders()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "seamstress-dna-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Synthetic valid MetaHuman DNA header: "DNA" + big-endian gen=2, ver=4 + printable descriptor bytes.
            string validPath = Path.Combine(tempDir, "hero.dna");
            var header = new byte[32];
            header[0] = (byte)'D';
            header[1] = (byte)'N';
            header[2] = (byte)'A';
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3, 2), 2);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5, 2), 4);
            Encoding.ASCII.GetBytes("MetaHumanBody", header.AsSpan(8));
            File.WriteAllBytes(validPath, header);

            var valid = MetaHumanDnaLoader.Inspect(validPath);
            Assert.True(valid.IsValidDna);
            Assert.Equal((ushort)2, valid.FileFormatGeneration);
            Assert.Equal((ushort)4, valid.FileFormatVersion);
            Assert.Equal(32, valid.FileSizeBytes);
            Assert.Contains("2.4", valid.Summary);
            Assert.Contains("MetaHumanBody", valid.Summary);

            // Invalid file (wrong magic bytes).
            string invalidPath = Path.Combine(tempDir, "not_a_dna.bin");
            File.WriteAllBytes(invalidPath, Encoding.ASCII.GetBytes("NOTDNA123456"));

            var invalid = MetaHumanDnaLoader.Inspect(invalidPath);
            Assert.False(invalid.IsValidDna);
            Assert.Equal(12, invalid.FileSizeBytes);

            // Missing file does not throw.
            var missing = MetaHumanDnaLoader.Inspect(Path.Combine(tempDir, "missing.dna"));
            Assert.False(missing.IsValidDna);

            // Extracting an invalid DNA file fails cleanly with an error message.
            Assert.False(MetaHumanDnaLoader.TryExtractMeshViaPython(invalidPath, Path.Combine(tempDir, "out.obj"), out string? err));
            Assert.False(string.IsNullOrWhiteSpace(err));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BindAndTransferValidatesTopologyAndTracksDeformation()
    {
        var source = new TriMesh
        {
            Positions = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
            },
            Triangles = new[] { 0, 1, 2 },
        };
        var target = new TriMesh
        {
            Positions = new[]
            {
                new Vector3(0f, 0f, 0.5f),
                new Vector3(2f, 0f, 0.5f),
                new Vector3(0f, 2f, 0.5f),
            },
            Triangles = new[] { 0, 1, 2 },
        };

        // Point directly above the centroid at +0.02 m normal offset.
        var input = new[] { new Vector3(1f / 3f, 1f / 3f, 0.02f) };
        var transferred = GarmentRefitter.BindAndTransfer(input, source, target);
        Assert.Single(transferred);
        Assert.InRange(transferred[0].X, 2f / 3f - 1e-4f, 2f / 3f + 1e-4f);
        Assert.InRange(transferred[0].Y, 2f / 3f - 1e-4f, 2f / 3f + 1e-4f);
        Assert.InRange(transferred[0].Z, 0.52f - 1e-4f, 0.52f + 1e-4f);

        // Mismatched topology must throw ArgumentException.
        var mismatched = new TriMesh
        {
            Positions = new[] { Vector3.Zero, Vector3.UnitX },
            Triangles = Array.Empty<int>(),
        };
        Assert.Throws<ArgumentException>(() => GarmentRefitter.BindAndTransfer(input, source, mismatched));
    }

    private static void WriteBoxObj(string path, float halfX, float halfY, float minZ, float maxZ)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"v {-halfX} {-halfY} {minZ}\n");
        sb.Append(inv, $"v {halfX} {-halfY} {minZ}\n");
        sb.Append(inv, $"v {halfX} {halfY} {minZ}\n");
        sb.Append(inv, $"v {-halfX} {halfY} {minZ}\n");
        sb.Append(inv, $"v {-halfX} {-halfY} {maxZ}\n");
        sb.Append(inv, $"v {halfX} {-halfY} {maxZ}\n");
        sb.Append(inv, $"v {halfX} {halfY} {maxZ}\n");
        sb.Append(inv, $"v {-halfX} {halfY} {maxZ}\n");
        sb.Append("f 1 4 3 2\n");
        sb.Append("f 5 6 7 8\n");
        sb.Append("f 1 2 6 5\n");
        sb.Append("f 2 3 7 6\n");
        sb.Append("f 3 4 8 7\n");
        sb.Append("f 4 1 5 8\n");
        File.WriteAllText(path, sb.ToString());
    }
}

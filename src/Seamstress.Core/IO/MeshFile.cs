using System.Numerics;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.IO;

/// <summary>One named mesh read from an FBX or glTF file, already in Seamstress space: metres, Y up, facing +Z.</summary>
public sealed record MeshPart(string Name, Vector3[] Positions, int[] Triangles);

/// <summary>The meshes of a body file that were kept, and the names of those that were left out.</summary>
public sealed class MeshFile
{
    public List<MeshPart> Parts { get; } = new();
    public List<string> Skipped { get; } = new();

    public TriMesh ToTriMesh(float scale = 1f)
    {
        if (Parts.Count == 0)
            throw new InvalidDataException("The file contains no mesh that can be used as a body.");
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var part in Parts)
        {
            int offset = positions.Count;
            foreach (var p in part.Positions)
                positions.Add(p * scale);
            foreach (int index in part.Triangles)
                triangles.Add(offset + index);
        }
        return new TriMesh { Positions = positions.ToArray(), Triangles = triangles.ToArray() };
    }
}

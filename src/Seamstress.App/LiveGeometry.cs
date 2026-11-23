using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Seamstress.Core;

namespace Seamstress.App;

/// <summary>
/// Hands vertex buffers to Qt Quick 3D from memory instead of through ".mesh" files.
/// <para>
/// The 3D view gives each model that shows changing geometry a <c>ProceduralMesh</c> (a <c>QQuick3DGeometry</c>) and
/// tells this class where it is: QML writes an object as <c>ProceduralMesh(0xâ€¦)</c>, and that address is passed to
/// <see cref="Attach"/>. The buffers are then set through <c>QQuick3DGeometry</c>'s own exported functions
/// (<c>setVertexData</c>, <c>addAttribute</c>, <c>setBounds</c>, <c>update</c>), so a moving garment costs one copy
/// into a <c>QByteArray</c> and an upload, where a file had to be written, read back and parsed for every frame.
/// </para>
/// <para>
/// The Qt Bridge for C# has no way to subclass a C++ type, and building a native plugin needs a C++ compiler the build
/// does not otherwise require, so the functions are called directly. That ties this to the MSVC build of Qt on
/// Windows (the names below are MSVC's); elsewhere, or if a function cannot be found, <see cref="Available"/> is false
/// and the viewport keeps writing files. Setting SEAMSTRESS_MESH_FILES=1 forces the files.
/// </para>
/// <para>Everything here runs on the GUI thread: QML calls Attach and Detach, and the viewport's update calls Set.</para>
/// </summary>
internal sealed unsafe class LiveGeometry : IDisposable
{
    // QQuick3DGeometry::Attribute::Semantic and ComponentType, and QQuick3DGeometry::PrimitiveType.
    private const int IndexSemantic = 0, PositionSemantic = 1, NormalSemantic = 2, TexCoordSemantic = 3,
        TangentSemantic = 4, BinormalSemantic = 5;
    private const int U32Type = 1, F32Type = 3;
    private const int PrimitiveLines = 2, PrimitiveTriangles = 5;

    /// <summary>A QByteArray as laid out in Qt 6: its shared header, the data and the size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct QByteArray
    {
        public IntPtr D;
        public byte* Ptr;
        public long Size;
    }

    private static class Native
    {
        public static bool Resolved, Ok;
        public static delegate* unmanaged<QByteArray*, long, int, QByteArray*> ByteArrayUninitialized;
        public static delegate* unmanaged<QByteArray*, void> ByteArrayDestroy;
        public static delegate* unmanaged<IntPtr, void> Clear;
        public static delegate* unmanaged<IntPtr, QByteArray*, void> SetVertexData;
        public static delegate* unmanaged<IntPtr, QByteArray*, void> SetIndexData;
        public static delegate* unmanaged<IntPtr, int, void> SetStride;
        public static delegate* unmanaged<IntPtr, int, void> SetPrimitiveType;
        public static delegate* unmanaged<IntPtr, int, int, int, void> AddAttribute;
        public static delegate* unmanaged<IntPtr, Vector3*, Vector3*, void> SetBounds;
        public static delegate* unmanaged<IntPtr, void> Update;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        /// <summary>Looks the functions up in the Qt libraries the program has already loaded.</summary>
        public static bool Resolve()
        {
            if (Resolved)
                return Ok;
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                return false;
            // Only the copies Qt itself uses will do: a second copy of a Qt library would be a different Qt.
            IntPtr core = GetModuleHandleW("Qt6Core.dll"), quick3d = GetModuleHandleW("Qt6Quick3D.dll");
            if (core == IntPtr.Zero || quick3d == IntPtr.Zero)
                return false;   // the 3D view has not been created yet; tried again on the next update
            Resolved = true;
            try
            {
                IntPtr Get(IntPtr module, string name) => NativeLibrary.GetExport(module, name);
                ByteArrayUninitialized = (delegate* unmanaged<QByteArray*, long, int, QByteArray*>)
                    Get(core, "??0QByteArray@@QEAA@_JW4Initialization@Qt@@@Z");
                ByteArrayDestroy = (delegate* unmanaged<QByteArray*, void>)Get(core, "??1QByteArray@@QEAA@XZ");
                Clear = (delegate* unmanaged<IntPtr, void>)Get(quick3d, "?clear@QQuick3DGeometry@@QEAAXXZ");
                SetVertexData = (delegate* unmanaged<IntPtr, QByteArray*, void>)
                    Get(quick3d, "?setVertexData@QQuick3DGeometry@@QEAAXAEBVQByteArray@@@Z");
                SetIndexData = (delegate* unmanaged<IntPtr, QByteArray*, void>)
                    Get(quick3d, "?setIndexData@QQuick3DGeometry@@QEAAXAEBVQByteArray@@@Z");
                SetStride = (delegate* unmanaged<IntPtr, int, void>)Get(quick3d, "?setStride@QQuick3DGeometry@@QEAAXH@Z");
                SetPrimitiveType = (delegate* unmanaged<IntPtr, int, void>)
                    Get(quick3d, "?setPrimitiveType@QQuick3DGeometry@@QEAAXW4PrimitiveType@1@@Z");
                AddAttribute = (delegate* unmanaged<IntPtr, int, int, int, void>)
                    Get(quick3d, "?addAttribute@QQuick3DGeometry@@QEAAXW4Semantic@Attribute@1@HW4ComponentType@31@@Z");
                SetBounds = (delegate* unmanaged<IntPtr, Vector3*, Vector3*, void>)
                    Get(quick3d, "?setBounds@QQuick3DGeometry@@QEAAXAEBVQVector3D@@0@Z");
                Update = (delegate* unmanaged<IntPtr, void>)Get(quick3d, "?update@QQuick3DObject@@QEAAXXZ");
                Ok = true;
                AppLog.Write("3D view: meshes are handed to Qt Quick 3D in memory");
            }
            catch (EntryPointNotFoundException e)
            {
                AppLog.Write("3D view: Qt's geometry functions were not found, so meshes go through files: " + e.Message);
                Ok = false;
            }
            return Ok;
        }
    }

    /// <summary>One named buffer (a piece, the seam threads, â€¦) and the geometry objects showing it.</summary>
    private sealed class Slot
    {
        public QByteArray* Vertices;
        public QByteArray* Indices;
        public int Stride;
        public int Primitive;
        public bool HasUv;
        public Vector3 Min, Max;
        public readonly List<IntPtr> Targets = new();
    }

    private readonly Dictionary<string, Slot> _slots = new();
    private bool _disposed;

    /// <summary>The prefix of the keys handed to QML in place of a file URL.</summary>
    public const string Scheme = "live:";

    public static bool DisabledByEnvironment => Environment.GetEnvironmentVariable("SEAMSTRESS_MESH_FILES") == "1";

    /// <summary>True when geometry can be handed over in memory; checked once the 3D view exists.</summary>
    public bool Available => !_disposed && !DisabledByEnvironment && Native.Resolve();

    /// <summary>Sets a triangle mesh with normals and, when given, UVs plus the tangents a normal map needs.</summary>
    public string SetTriangles(string name, Vector3[] positions, Vector3[] normals, Vector2[]? uvs, int[] indices)
    {
        var slot = Begin(name);
        bool hasUv = uvs != null;
        int stride = QtMesh.TriangleStride(hasUv);
        slot.Stride = stride;
        slot.HasUv = hasUv;
        slot.Primitive = PrimitiveTriangles;
        slot.Vertices = NewArray((long)positions.Length * stride);
        QtMesh.FillTriangleVertices(new Span<float>(slot.Vertices->Ptr, positions.Length * stride / 4), positions, normals, uvs, indices);
        slot.Indices = NewArray((long)indices.Length * 4);
        MemoryMarshal.AsBytes(indices.AsSpan()).CopyTo(new Span<byte>(slot.Indices->Ptr, indices.Length * 4));
        (slot.Min, slot.Max) = Bounds(positions);
        Publish(slot);
        return Scheme + name;
    }

    /// <summary>Sets a list of line segments, two points each.</summary>
    public string SetLines(string name, IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0)
        {
            Remove(name);
            return "";
        }
        var slot = Begin(name);
        slot.Stride = 12;
        slot.HasUv = false;
        slot.Primitive = PrimitiveLines;
        slot.Vertices = NewArray((long)points.Count * 12);
        var span = new Span<Vector3>(slot.Vertices->Ptr, points.Count);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < points.Count; i++)
        {
            span[i] = points[i];
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }
        (slot.Min, slot.Max) = (min, max);
        Publish(slot);
        return Scheme + name;
    }

    public void Remove(string name)
    {
        if (!_slots.TryGetValue(name, out var slot))
            return;
        Release(slot);
        // A model still showing it keeps the data it was given until QML lets go of it.
        if (slot.Targets.Count == 0)
            _slots.Remove(name);
    }

    /// <summary>QML: a geometry object (written as "ProceduralMesh(0xâ€¦)") is to show the buffer behind a key.</summary>
    public void Attach(string key, string geometryObject)
    {
        if (!Available || !key.StartsWith(Scheme, StringComparison.Ordinal))
            return;
        IntPtr target = ParseAddress(geometryObject);
        if (target == IntPtr.Zero)
        {
            AppLog.Write("3D view: could not read a geometry object's address from " + geometryObject);
            return;
        }
        string name = key[Scheme.Length..];
        if (!_slots.TryGetValue(name, out var slot))
            _slots[name] = slot = new Slot();
        if (!slot.Targets.Contains(target))
            slot.Targets.Add(target);
        if (slot.Vertices != null)
            Push(slot, target);
    }

    /// <summary>QML: the geometry object is going away, or shows something else from now on.</summary>
    public void Detach(string key, string geometryObject)
    {
        if (!key.StartsWith(Scheme, StringComparison.Ordinal))
            return;
        IntPtr target = ParseAddress(geometryObject);
        string name = key[Scheme.Length..];
        if (!_slots.TryGetValue(name, out var slot))
            return;
        slot.Targets.Remove(target);
        if (slot.Targets.Count == 0 && slot.Vertices == null)
            _slots.Remove(name);
    }

    /// <summary>"ProceduralMesh(0x1d2c3b4a5960)" or "ProceduralMesh(0x1d2c3b4a5960, "name")" â†’ the address.</summary>
    internal static IntPtr ParseAddress(string text)
    {
        int start = text.IndexOf("(0x", StringComparison.Ordinal);
        if (start < 0)
            return IntPtr.Zero;
        start += 3;
        int end = start;
        while (end < text.Length && Uri.IsHexDigit(text[end]))
            end++;
        return end > start && ulong.TryParse(text.AsSpan(start, end - start), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
            out ulong address) ? (IntPtr)(long)address : IntPtr.Zero;
    }

    private Slot Begin(string name)
    {
        if (!_slots.TryGetValue(name, out var slot))
            _slots[name] = slot = new Slot();
        Release(slot);
        return slot;
    }

    private void Publish(Slot slot)
    {
        foreach (var target in slot.Targets)
            Push(slot, target);
    }

    private static void Push(Slot slot, IntPtr g)
    {
        // The geometry takes its own reference to each QByteArray (they are shared, not copied), so the slot can
        // release its reference whenever it is given new data.
        Native.Clear(g);
        Native.SetStride(g, slot.Stride);
        Native.SetPrimitiveType(g, slot.Primitive);
        Native.AddAttribute(g, PositionSemantic, 0, F32Type);
        if (slot.Primitive == PrimitiveTriangles)
        {
            Native.AddAttribute(g, NormalSemantic, 12, F32Type);
            if (slot.HasUv)
            {
                Native.AddAttribute(g, TexCoordSemantic, 24, F32Type);
                Native.AddAttribute(g, TangentSemantic, 32, F32Type);
                Native.AddAttribute(g, BinormalSemantic, 44, F32Type);
            }
        }
        Native.SetVertexData(g, slot.Vertices);
        if (slot.Indices != null)
        {
            Native.AddAttribute(g, IndexSemantic, 0, U32Type);
            Native.SetIndexData(g, slot.Indices);
        }
        var min = slot.Min;
        var max = slot.Max;
        Native.SetBounds(g, &min, &max);
        Native.Update(g);
    }

    private static QByteArray* NewArray(long size)
    {
        var array = (QByteArray*)NativeMemory.AllocZeroed((nuint)sizeof(QByteArray));
        Native.ByteArrayUninitialized(array, size, 0 /* Qt::Uninitialized */);
        return array;
    }

    private static void FreeArray(QByteArray* array)
    {
        if (array == null)
            return;
        Native.ByteArrayDestroy(array);
        NativeMemory.Free(array);
    }

    private static void Release(Slot slot)
    {
        FreeArray(slot.Vertices);
        FreeArray(slot.Indices);
        slot.Vertices = null;
        slot.Indices = null;
    }

    private static (Vector3, Vector3) Bounds(Vector3[] positions)
    {
        if (positions.Length == 0)
            return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Native.Ok)
        {
            foreach (var slot in _slots.Values)
                Release(slot);
        }
        _slots.Clear();
    }
}

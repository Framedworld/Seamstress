# Seamstress MCP Server (`Seamstress.Mcp`)

A stdio Model Context Protocol (MCP) server built on `Seamstress.Agent` (the tools, shared with the desktop window), `Seamstress.Core` and `Seamstress.Cuda` (NVIDIA CUDA cloth simulation via ILGPU).

When the Seamstress window is open, the tools work on the project open there, live: see "Working in the open
window" in `docs/MCP.md`. Set `SEAMSTRESS_LIVE=0` for a headless project of the server's own.

## Features

- **24 MCP tools** covering the full garment pipeline:
  - Project & templates: `new_project`, `open_project`, `save_project`, `get_project_summary`, `add_template_garment`
  - 2D pattern drafting: `create_garment`, `add_piece` (polygons + quadratic Bezier curves), `add_rectangle_piece`, `mirror_piece`, `cut_piece`, `inset_band`, `delete_piece`
  - Sewing & fabric: `sew_edges` (with length-mismatch checks), `reverse_seam`, `delete_seam`, `set_fabric`, `set_material`
  - Avatar & 3D draping: `get_avatar_landmarks`, `arrange_piece_3d`, `simulate` (CUDA GPU-accelerated on NVIDIA GPUs with CPU fallback), `reset_drape`, `inspect_fit`
  - Preview & export: `render_preview` (3-view Front/Side/Back shaded PNG + 1:1 cm 2D SVG pattern), `export_garment` (`.glb`, `.gltf`, `.obj`, `.fbx`, `.svg`)

## MCP Client Configuration

Add this entry to your MCP client config (Antigravity, Claude Desktop, Cursor, etc.):

```json
{
  "mcpServers": {
    "seamstress": {
      "command": "C:\\Users\\mattk\\Documents\\seamstress\\src\\Seamstress.Mcp\\bin\\Release\\net8.0\\Seamstress.Mcp.exe",
      "args": []
    }
  }
}
```

## Rebuild

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
dotnet build src/Seamstress.Mcp/Seamstress.Mcp.csproj -c Release
```

# Seamstress plugin for Claude Code

Everything an agent needs to make clothes in Seamstress, installed in one step:

| Part | What it is |
|---|---|
| MCP server | `src/Seamstress.Mcp`: draft pattern pieces, sew, drape on an avatar (CUDA, with a CPU fallback), render and export. Started by [`server/seamstress-mcp`](server/seamstress-mcp.cmd). |
| Skill | `seamstress-garment-tailoring`: how to build layered, sewn and draped outfits that fit (templates, layering, sewing, stiffness and folds, trim, materials, checking). |
| `/seamstress:dress <description>` | Dresses the avatar from a description: fitted templates first, then fabric and trim, then a fit check and a render. |
| `/seamstress:critique [focus]` | Runs `critique_outfit` and proposes fixes as tool calls, applying the ones you pick. |
| `/seamstress:fit-check [garment]` | `inspect_fit` plus `critique_outfit`, as a six-line report and a render. Changes nothing. |
| `/seamstress:compare <image>` | Runs `compare_to_reference` against a reference picture, scores how close it is and lists what to change. |

> **`/critique`, `/fit-check` and `/compare` need the outfit-critic tools** (`critique_outfit` and
> `compare_to_reference`), which are being added to the server separately. On a server without them, the commands
> say so and fall back to `inspect_fit` and `render_preview`, judging the picture themselves.

## Install

You need a Seamstress checkout and the .NET 8 SDK. An NVIDIA GPU makes simulation much faster, but is not required.

1. **Build the server** in the checkout:

   ```
   dotnet build src/Seamstress.Mcp -c Release
   ```

2. **Add the plugin** in Claude Code, giving the path of your checkout:

   ```
   /plugin marketplace add C:\path\to\seamstress
   /plugin install seamstress@seamstress
   ```

   Or from a terminal: `claude plugin marketplace add C:\path\to\seamstress`, then
   `claude plugin install seamstress@seamstress`. To try it without installing:
   `claude --plugin-dir C:\path\to\seamstress\plugin`.

3. **Check it:** run `/mcp` in Claude Code, or `claude mcp list` in a terminal. `plugin:seamstress:seamstress` should
   say Connected. Ask "what's in my Seamstress project?" and Claude calls `get_project_summary`.

If you already registered Seamstress by hand (a `seamstress` entry in a `.mcp.json` or in your user MCP config),
remove that entry. Otherwise every tool shows up twice.

### Where the server is found

`server/seamstress-mcp` (`seamstress-mcp.cmd` on Windows) starts the server and uses the first of these that applies:

1. `SEAMSTRESS_MCP`: the path of a built server executable, such as a copy built elsewhere with `-o`.
2. `SEAMSTRESS_REPO`: a Seamstress checkout (the folder that holds `Seamstress.slnx`).
3. The folder Claude Code was started in, if it is a Seamstress checkout.
4. The checkout that holds this `plugin/` folder. This covers an install from a local checkout, as above.

If none of these is a checkout, or the server is not built yet, the server fails to start and `/mcp` shows it as
failed. Run `plugin/server/seamstress-mcp` (`plugin\server\seamstress-mcp.cmd`) in a terminal to see why: it prints which
of the two it was, and what to do. A plugin installed from a copy that is not inside a checkout (for example from a
Git URL) needs `SEAMSTRESS_REPO` or `SEAMSTRESS_MCP`.

Set these in the environment Claude Code starts from, or in the `env` block of your Claude Code settings:

```json
{ "env": { "SEAMSTRESS_REPO": "C:\\path\\to\\seamstress" } }
```

| Variable | Effect |
|---|---|
| `SEAMSTRESS_REPO` | The checkout to run the server from. |
| `SEAMSTRESS_MCP` | A server executable to run instead. |
| `SEAMSTRESS_LIVE` | `0` always works headless, on a project of the server's own, even when the window is open. Unset: live when the window is open (below). |
| `DOTNET_ROOT` | Where .NET is installed. If it is unset, the launcher uses a per-user install in `~/.dotnet` when one is there. |

### After pulling new code

Rebuild with the same `dotnet build` command, then reconnect the server (`/mcp`, or restart Claude Code). A running
server locks its own folder, so stop Claude Code first, or build a copy elsewhere
(`dotnet build src/Seamstress.Mcp -c Release -o <folder>`) and point `SEAMSTRESS_MCP` at the exe. When the source is
newer than the build, `get_project_summary` returns a `staleWarning` and every command stops and tells you to rebuild.

## Live mode: working in the open window

Open the Seamstress window, then ask Claude for clothes. The server works on the project you have open, so you
watch each piece appear in the 2D and 3D views and the cloth drape as `simulate` runs in your window.

- **Every tool call is one undo step**, named `Agent: <tool>`. Undo anything you don't like.
- **Your work is safe.** `new_project` and `open_project` are refused while the window has unsaved changes, and the
  commands ask before starting a clean project.
- **Which mode is used is decided at the first tool call:** live if the window is open, headless otherwise. The
  `live_link` tool reports which (`status`), connects to a window you opened later (`connect`), or switches to a
  headless project (`headless`).
- The window listens on 127.0.0.1 only, with a random key in `%LOCALAPPDATA%\Seamstress\live-link.json` that only your
  own processes can read.

Headless is better for building a whole outfit from scratch in a script: set `SEAMSTRESS_LIVE=0`, save the project,
then open it in the window. See "Working in the open window" in [`docs/MCP.md`](../docs/MCP.md).

## Examples

```
/seamstress:dress worn canvas work trousers stacking on the boots, a grey tee, an open olive canvas jacket with chest pockets and pushed-up sleeves
/seamstress:dress a loose linen summer dress, knee length, with a wide neckline
/seamstress:dress a fitted hoodie with the hood down under a denim jacket
/seamstress:fit-check
/seamstress:fit-check Fitted Jacket
/seamstress:critique the pockets
/seamstress:compare C:\refs\mechanic.png
```

You can also just ask in your own words ("make the jacket stiffer and give it a few broad folds at the elbows"). The
tailoring skill loads when the task is about clothing in Seamstress.

To let Claude use the tools without asking each time, allow them in your settings
(`"permissions": { "allow": ["mcp__plugin_seamstress_seamstress__*"] }`), or allow single tools such as
`mcp__plugin_seamstress_seamstress__render_preview`.

## For maintainers

- **One source for the skill.** The skill lives in
  [`docs/skills/seamstress-garment-tailoring/SKILL.md`](../docs/skills/seamstress-garment-tailoring/SKILL.md). The
  copy in `plugin/skills/` is generated, because an installed plugin cannot read files outside its folder. After
  editing the source, run `python plugin/scripts/sync_skill.py`. `python plugin/scripts/sync_skill.py --check` exits 1
  when the copy is out of date, for CI or a pre-commit hook.
- **Validate:** `claude plugin validate plugin --strict` (the plugin) and `claude plugin validate .` (the marketplace
  in `.claude-plugin/marketplace.json` at the repository root).
- **Layout:** `.claude-plugin/plugin.json` (manifest), `.mcp.json` (the server), `server/` (launchers),
  `commands/` (slash commands), `skills/` (generated), `scripts/` (the sync script).
- **Tool names** in Claude Code are `mcp__plugin_seamstress_seamstress__<tool>`. The commands refer to tools by their
  plain names (`simulate`, `inspect_fit`).

using System.Text.Json;
using Seamstress.Core;
using Seamstress.Core.Editing;

namespace Seamstress.Mcp;

/// <summary>
/// Runs agent tool calls on a project someone has open, so each call that changes it is one undo step named
/// "Agent: …" and a call that fails half way leaves nothing behind. Used by the window for the live link and for the
/// in-app assistant, and by tests that check the undo steps.
/// </summary>
public sealed class UndoableTools
{
    /// <summary>Tools that only look, or that open or start a project: they leave no undo step.</summary>
    public static readonly IReadOnlySet<string> LookingTools = new HashSet<string>
    {
        "get_project_summary", "get_avatar_landmarks", "locate_on_piece", "inspect_fit", "fit_map", "render_preview",
        "list_body_profiles", "inspect_metahuman_dna", "export_garment", "save_project", "save_body_profile",
        "critique_outfit", "compare_to_reference", "new_project", "open_project",
    };

    private readonly Workspace _workspace;
    private readonly McpToolRegistry _tools;

    public UndoableTools(Workspace workspace, McpToolRegistry tools)
    {
        _workspace = workspace;
        _tools = tools;
    }

    public McpToolRegistry Registry => _tools;

    /// <summary>The name of the undo step a tool's call makes, in the interface language.</summary>
    public static string UndoName(string tool) => Lang.F("Agent: {0}", tool.Replace('_', ' '));

    /// <summary>True when the call can change the project, and so makes an undo step.</summary>
    public static bool Changes(string tool) => !LookingTools.Contains(tool);

    public object Run(string tool, JsonElement args)
    {
        if (!Changes(tool))
            return _tools.Dispatch(tool, args);
        _workspace.History.BeginGroup(UndoName(tool));
        try
        {
            var result = _tools.Dispatch(tool, args);
            _workspace.History.EndGroup(keep: true);
            return result;
        }
        catch
        {
            _workspace.History.EndGroup(keep: false);
            throw;
        }
    }
}

using System.Text.Json.Nodes;

namespace Deskweave.AgentWorkspaces;

public sealed partial class WorkspaceBrowser
{
    // Protocol messages can echo URLs, local paths, script text and credentials. A receipt carries
    // only an allowlisted engine message, numeric protocol code or validated Chromium error name.
    internal static string ProtocolFailureSummary(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "The browser did not confirm the operation";
        if (error is "DevTools connection closed before the command completed."
            or "The DevTools command timed out."
            or "The navigation became a download."
            or "The page did not finish loading within the wait budget.") return error.TrimEnd('.');
        const string prefix = "net::ERR_";
        if (error.StartsWith(prefix, StringComparison.Ordinal) && error.Length > prefix.Length
            && error.Length <= 96 && error[prefix.Length..].All(c => c is >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '_')) return error;
        if (error.Length <= 65536)
        {
            try
            {
                if (JsonNode.Parse(error) is JsonObject detail
                    && detail["code"] is JsonValue code && code.TryGetValue<int>(out int number))
                    return "DevTools rejected the command (code " + number + ")";
            }
            catch (System.Text.Json.JsonException) { }
        }
        return "The browser reported a protocol or page error; details omitted to protect page data";
    }

    internal static string NavigationFailureReceipt(string? error) =>
        "Browser navigation was not confirmed [navigation]: " + ProtocolFailureSummary(error)
        + ". Inspect the current page before continuing; do not blindly replay the action.";

    internal static string StartupFailureReceipt(string desktopName) =>
        "Browser startup failed [startup]: "
        + (StartFailureReason(desktopName) ?? "the browser did not provide a startup receipt")
        + ". Inspect the workspace screen and browser diagnostics before retrying.";
}

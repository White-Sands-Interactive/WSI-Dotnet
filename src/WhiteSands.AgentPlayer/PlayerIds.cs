using System.Text.RegularExpressions;

namespace WhiteSands.AgentPlayer;

internal static class PlayerIds
{
    private static readonly Regex Pattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Trim and check the character id <c>start_session</c> must send.
    /// The pattern matches the platform's character id check.
    /// </summary>
    public static string Require(string? value)
    {
        var id = value?.Trim() ?? "";
        if (id.Length == 0)
        {
            throw new InvalidOperationException(
                "playerId is required. Copy the character ID from the web app");
        }

        if (!Pattern.IsMatch(id))
        {
            throw new InvalidOperationException(
                $"playerId \"{id}\" is not a character ID. Copy the ID from the character in the web app");
        }

        return id;
    }
}

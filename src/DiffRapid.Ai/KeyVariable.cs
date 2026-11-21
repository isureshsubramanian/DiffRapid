using System.Text.RegularExpressions;

namespace DiffRapid.Ai;

/// <summary>
/// Checks the setting that names the environment variable holding an API key, and words the advice shown when no
/// key was sent. The setting is never repeated in that advice: people paste the key itself into it by mistake, and
/// a message, screenshot or bug report would then carry the key.
/// </summary>
public static class KeyVariable
{
    // Letters, digits and '_', not starting with a digit: every name a setx or shell user types. Keys such as
    // "sk-proj-..." contain '-', so they are told apart; a key without one (a hex Azure key) still is never shown.
    private static readonly Regex Name = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="setting"/> reads as an environment variable name.</summary>
    public static bool IsName(string? setting) => setting is not null && Name.IsMatch(setting);

    /// <summary>
    /// Explains, without repeating <paramref name="setting"/>, why no API key was sent and what to change.
    /// </summary>
    public static string MissingKeyAdvice(string? setting)
    {
        if (string.IsNullOrEmpty(setting))
        {
            return "No key is sent: name the environment variable that holds it in Tools > Options > DiffRapid > AI.";
        }

        if (!IsName(setting))
        {
            return "No key was sent: the API key environment variable setting in Tools > Options > DiffRapid > AI "
                + "does not look like a variable name (letters, digits and _), and may contain the key itself. If so, "
                + "enter the variable's name there instead, such as OPENAI_API_KEY, and set your key in that variable.";
        }

        return "No key was sent because the environment variable named in Tools > Options > DiffRapid > AI is not set. "
            + "Check that the setting holds the variable's name, not the key, then set the variable to your API key "
            + "(setx <the name from the settings> <your key>) and ask again.";
    }
}

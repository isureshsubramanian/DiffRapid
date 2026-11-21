namespace DiffRapid.Core.IO;

/// <summary>Encodes one value as an RFC 4180 CSV field that spreadsheet applications read as text.</summary>
internal static class CsvCell
{
    /// <summary>
    /// Quotes <paramref name="value"/>, doubling inner quotes. A value starting with =, +, -, @, tab or carriage return
    /// gets a leading apostrophe first, so a spreadsheet shows it instead of running it as a formula (CSV injection).
    /// </summary>
    internal static string Quote(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiffRapid.Ai;

/// <summary>Masks common credential formats. This heuristic cannot identify every secret.</summary>
public static class SecretRedactor
{
    private static readonly Regex PrivateKey = Create(
        @"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z0-9]+ )*PRIVATE KEY-----|\z)");
    private static readonly Regex Bearer = Create(@"\bBearer[ \t]+[A-Za-z0-9._~+/\-]+=*");
    private static readonly Regex ConnectionUri = Create(@"\b[a-z][a-z0-9+.-]*://[^\s/:""']+:[^\s@""']+@[^\s""']+");
    // The run of "key=value;" pairs around a server-like key, up to a quote or the end of the line. What comes
    // before the run stays, so a diff keeps its "+" marker, indentation and "//", and code keeps its quotes.
    private static readonly Regex ConnectionString = Create(
        @"\b(?:[A-Za-z][A-Za-z0-9 _]*=[^;\r\n""']*;[ \t]*)*\b(?:Server|Data Source|Host|AccountName|DefaultEndpointsProtocol|Database)\s*=[^\r\n""']*");
    private static readonly Regex Assignment = Create(
        @"(?<prefix>[""']?\b(?:password|pwd|secret|api[_-]?key|token|access[_-]?token|client[_-]?secret|connection[_ ]?strings?)[""']?\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s;,&\r\n]+)");

    /// <summary>Redacts obvious secrets before text leaves the process.</summary>
    public static string Redact(string text, CancellationToken cancellationToken = default)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            text = PrivateKey.Replace(text, "[REDACTED PRIVATE KEY]");
            cancellationToken.ThrowIfCancellationRequested();
            text = Bearer.Replace(text, "Bearer [REDACTED]");
            cancellationToken.ThrowIfCancellationRequested();
            text = ConnectionUri.Replace(text, "[REDACTED CONNECTION STRING]");
            cancellationToken.ThrowIfCancellationRequested();
            text = Assignment.Replace(text, "${prefix}[REDACTED]");
            cancellationToken.ThrowIfCancellationRequested();
            return ConnectionString.Replace(text, "[REDACTED CONNECTION STRING]");
        }
        catch (RegexMatchTimeoutException)
        {
            throw new AiException("Secret redaction timed out; input was not sent.");
        }
    }

    private static Regex Create(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
}

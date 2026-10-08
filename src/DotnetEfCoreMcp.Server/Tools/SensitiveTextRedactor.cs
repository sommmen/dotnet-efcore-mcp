using System.Text.RegularExpressions;

namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>Removes credential material from text that is about to be returned to an MCP client.
/// <para>Exception messages were previously withheld wholesale because providers can embed
/// connection strings in them. That cost more than it bought: EF Core translation and provider SQL
/// diagnostics describe the <em>query</em>, and they are precisely what lets a calling agent correct
/// itself (issue #85). Redacting the narrow, well-known set of credential-bearing keywords lets the
/// diagnostic through while still keeping secrets out of the response.</para>
/// <para>This is a defence-in-depth measure, not the primary control: an MCP client can never supply
/// a connection string in the first place (they are resolved server-side by logical name), so this
/// guards only against a provider echoing its own configuration back inside an error message.</para></summary>
public static partial class SensitiveTextRedactor
{
    private const string Placeholder = "[REDACTED]";

    /// <summary>Replaces the value of any credential-bearing connection-string keyword with
    /// <see cref="Placeholder"/>, leaving all other text untouched.
    /// <para>Covers the keyword spellings used across the providers this server supports, including
    /// Npgsql's <c>Host</c>/<c>Username</c>, and handles quoted values - which may legally contain
    /// the <c>;</c> delimiter, so a naive "match up to the first semicolon" rule leaks the
    /// remainder of the secret.</para></summary>
    public static string? Redact(string? text) =>
        string.IsNullOrEmpty(text) ? text : CredentialKeyword().Replace(text, $"$1={Placeholder}");

    // Keywords are matched on a word boundary so ordinary prose mentioning e.g. "password" without
    // an assignment is left alone. `=(?!=|>)` skips `==` and `=>` so an EF expression such as
    // `u => u.Token == x` is not mistaken for an assignment and eaten.
    //
    // The value alternation is ordered deliberately:
    //   1. a properly closed double- or single-quoted run (allowing the doubled-quote escape form),
    //      consumed whole because a quoted value may legally contain the ';' delimiter;
    //   2. a value that opens a quote but never closes it - which happens in truncated diagnostics -
    //      redacted through to the end of the text, since there is no reliable terminator and
    //      falling through to rule 3 would stop at the first ';' and leak the rest;
    //   3. an ordinary unquoted run that stops at the delimiter.
    [GeneratedRegex(
        """
        \b(Password|Pwd|User\s*ID|Uid|UserName|Username|User|Server|Host|Data\s*Source|DataSource|Initial\s*Catalog|AccountKey|AccountName|SharedAccessSignature|Sig|Token|ApiKey|Api\s*Key|Secret)\s*=(?!=|>)\s*(?:"(?:[^"]|"")*"|'(?:[^']|'')*'|["'].*|[^;]*)
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex CredentialKeyword();
}

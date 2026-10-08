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

    /// <summary>Replaces the value of any credential-bearing connection-string keyword
    /// (<c>Password</c>, <c>Pwd</c>, <c>User ID</c>, <c>Uid</c>, <c>Server</c>, <c>Data Source</c>, ...)
    /// with <see cref="Placeholder"/>, leaving all other text untouched.</summary>
    public static string? Redact(string? text) =>
        string.IsNullOrEmpty(text) ? text : CredentialKeyword().Replace(text, $"$1={Placeholder}");

    // Matches `keyword=value` up to the next `;` or end of string. Keywords are matched on a word
    // boundary so ordinary prose mentioning e.g. "password" without an assignment is left alone.
    [GeneratedRegex(
        @"\b(Password|Pwd|User\s*ID|Uid|User|Server|Data\s*Source|Initial\s*Catalog|AccountKey|SharedAccessSignature)\s*=\s*[^;]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialKeyword();
}

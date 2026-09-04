namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>Thrown for invalid or failed migration inspection/script-generation requests.
/// Messages are sanitized and never include connection strings or provider exception detail.</summary>
public sealed class MigrationInspectionException : Exception
{
    public MigrationInspectionException(string message) : base(message) { }
    public MigrationInspectionException(string message, Exception innerException) : base(message, innerException) { }
}

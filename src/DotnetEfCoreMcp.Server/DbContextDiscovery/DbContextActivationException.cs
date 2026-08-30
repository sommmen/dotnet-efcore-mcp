namespace DotnetEfCoreMcp.Server.DbContextDiscovery;

/// <summary>Thrown when a discovered <see cref="Microsoft.EntityFrameworkCore.DbContext"/> type
/// could not be constructed (no supported construction path, or construction/connection-string
/// override failed). Messages never include the connection string itself.</summary>
public sealed class DbContextActivationException : Exception
{
    public DbContextActivationException(string message)
        : base(message)
    {
    }

    public DbContextActivationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

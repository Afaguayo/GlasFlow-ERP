namespace GlasFlow.Domain.Entities;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public const string Writers = Admin + "," + Operator;
}

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    /// <summary>APP for application-level audit, DB_TRIGGER for rows written by SQL Server triggers.</summary>
    public string Source { get; set; } = "APP";
}

public enum NotificationType { LowStock, OrderConfirmed, OrderShipped, ProductionCompleted, ImportCompleted }
public enum NotificationSeverity { Info, Warning, Critical }

public class Notification
{
    public int Id { get; set; }
    public NotificationType Type { get; set; }
    public NotificationSeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public int? ProductId { get; set; }
    public int? WarehouseId { get; set; }
    /// <summary>
    /// Identifies the condition the notification is about (e.g. LOWSTOCK:12:1). A filtered unique
    /// index guarantees at most one unresolved notification per key, so repeated checks never duplicate.
    /// </summary>
    public string? DedupKey { get; set; }
    public bool IsRead { get; set; }
    public bool IsResolved { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public Product? Product { get; set; }
    public Warehouse? Warehouse { get; set; }
}

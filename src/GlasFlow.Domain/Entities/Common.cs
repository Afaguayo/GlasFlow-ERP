namespace GlasFlow.Domain.Entities;

/// <summary>Base for tables that track creation and last-modification time.</summary>
public abstract class AuditableEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Thrown when an operation would break a business rule (maps to HTTP 409/422).</summary>
public class DomainException(string message) : Exception(message);

public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public enum TaxZone
{
    /// <summary>IVA general, 16%.</summary>
    General,
    /// <summary>Estímulo fiscal región fronteriza norte (e.g. Ciudad Juárez), IVA 8%.</summary>
    FronteraNorte
}

public static class TaxCalculator
{
    public const decimal GeneralRate = 0.16m;
    public const decimal BorderRate = 0.08m;

    public static decimal RateFor(TaxZone zone) => zone == TaxZone.FronteraNorte ? BorderRate : GeneralRate;

    public static decimal TaxFor(decimal subtotal, TaxZone zone)
    {
        if (subtotal < 0) throw new DomainException("Subtotal cannot be negative.");
        return Money.Round(subtotal * RateFor(zone));
    }
}

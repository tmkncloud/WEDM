namespace WEDM.Domain.Enums;

/// <summary>How WEDM selects Oracle inventory XML version metadata during bootstrap.</summary>
public enum BootstrapVersionStrategy
{
    VersionSpecific = 0,
    LatestSupported = 1,
    DerivedFromPayload = 2,
    CompatibilityMode = 3,
}

/// <summary>Scope of the inventory pointer (oraInst.loc) written during deployment.</summary>
public enum InventoryPointerScope
{
    DefaultCentral = 0,
    RetryIsolation = 1,
    Bootstrap = 2,
    Temporary = 3,
}

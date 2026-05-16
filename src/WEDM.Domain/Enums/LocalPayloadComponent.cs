namespace WEDM.Domain.Enums;

/// <summary>Logical payload slot in the versioned local repository (D:\WEDM\{version}\...).</summary>
public enum LocalPayloadComponent
{
    Jdk            = 1,
    Vc             = 2,
    Infrastructure = 3,
    WebLogic       = 4,
    Forms          = 5,
    WebTier        = 6,
    WebUtil        = 7
}

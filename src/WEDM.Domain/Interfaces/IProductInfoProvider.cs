using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Resolves build/version/channel metadata for the running WEDM host process.</summary>
public interface IProductInfoProvider
{
    ProductVersionSnapshot GetSnapshot();
}

using FxFixGateway.Domain.Entities;

namespace FxFixGateway.Domain.Interfaces
{
    public interface ITpicapInstrumentRepository
    {
        Task UpsertAsync(TpicapInstrument instrument);
        Task<TpicapInstrument?> GetBySecurityIdAsync(string sessionKey, string securityId);
    }
}
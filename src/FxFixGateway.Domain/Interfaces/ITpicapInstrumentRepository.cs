using FxFixGateway.Domain.Entities;

namespace FxFixGateway.Domain.Interfaces
{
    public interface ITpicapInstrumentRepository
    {
        Task UpsertAsync(TpicapInstrument instrument);

        /// <summary>
        /// Upsert från 35=X. Skriver identitet + kanoniska kolumner men rör INTE
        /// raw-kolumner (premium_type, delta_basis, maturity_month_year …) vid
        /// ON DUPLICATE — de ägs av 35=W (UpsertAsync).
        /// </summary>
        Task UpsertCanonicalAsync(TpicapInstrument instrument);

        Task<TpicapInstrument?> GetBySecurityIdAsync(string sessionKey, string securityId);
    }
}
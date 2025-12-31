using DataSvc.Models;

namespace DataSvc.Analyzer;

public interface IClubEloService
{
    Task<ClubEloResponse> GetCurrentAsync(CancellationToken ct = default);
}

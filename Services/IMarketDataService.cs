using FinanceAPP.Models;

namespace FinanceAPP.Services;

public interface IMarketDataService
{
    Task<MarketChartData> GetChartDataAsync(string symbol, string range = "6mo", string interval = "1d", Action<string>? onProgress = null);
}

using System.Net.Http;
using System.Security.Authentication;
using Newtonsoft.Json;
using FinanceAPP.Models;

namespace FinanceAPP.Services;

/// <summary>
/// Yahoo Finance 行情数据服务
/// 使用 Yahoo Finance v8 chart API 获取K线数据，支持本地SQLite缓存
/// </summary>
public class YahooFinanceService : IMarketDataService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DatabaseService _db;
    private const string BaseUrl = "https://query1.finance.yahoo.com/v8/finance/chart";

    public YahooFinanceService(DatabaseService db)
    {
        _db = db;
        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<MarketChartData> GetChartDataAsync(string symbol, string range = "6mo", string interval = "1d", Action<string>? onProgress = null)
    {
        if (interval == "1d")
        {
            var expectedCount = RangeToCount(range);
            if (_db.IsDataFresh(symbol) && _db.GetCandleCount(symbol) >= expectedCount)
            {
                var cached = _db.GetCandles(symbol, expectedCount);
                if (cached.Count > 0)
                {
                    onProgress?.Invoke("📦 使用本地缓存数据");
                    return BuildFromCache(symbol, cached);
                }
            }
        }

        try
        {
            var data = await FetchFromYahooAsync(symbol, range, interval, onProgress);
            if (interval == "1d" && data.Candles.Count > 0)
            {
                _db.SaveCandles(symbol, data.Candles);
                _db.SaveSymbolInfo(symbol, data.Symbol, data.ExchangeName, data.Currency);
                onProgress?.Invoke($"✅ 已保存{data.Candles.Count}条数据到本地");
            }
            return data;
        }
        catch (Exception ex)
        {
            if (interval == "1d")
            {
                var count = RangeToCount(range);
                var cached = _db.GetCandles(symbol, count);
                if (cached.Count > 0)
                {
                    onProgress?.Invoke($"⚠️ API请求失败({ex.Message})，使用本地缓存数据");
                    return BuildFromCache(symbol, cached);
                }
            }
            throw;
        }
    }

    private async Task<MarketChartData> FetchFromYahooAsync(string symbol, string range, string interval, Action<string>? onProgress)
    {
        var url = $"{BaseUrl}/{symbol}?range={range}&interval={interval}";
        const int maxRetries = 3;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                onProgress?.Invoke($"正在获取数据... (第{attempt}次尝试)");
                var response = await _httpClient.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API请求失败: HTTP {response.StatusCode}");
                return ParseYahooResponse(json, symbol);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < maxRetries)
                {
                    var delay = 1000 * attempt;
                    onProgress?.Invoke($"⚠️ 第{attempt}次失败({ex.Message})，{delay}ms后重试...");
                    await Task.Delay(delay);
                }
            }
        }
        throw lastException!;
    }

    private MarketChartData ParseYahooResponse(string json, string symbol)
    {
        var response = JsonConvert.DeserializeObject<YahooChartResponse>(json);
        var result = response?.Chart?.Result;
        if (result == null || result.Count == 0)
            throw new Exception($"未获取到数据，请检查代码 \"{symbol}\" 是否正确。");

        var data = result[0];
        var meta = data.Meta;
        var timestamps = data.Timestamp;
        var quoteList = data.Indicators?.Quote;

        if (timestamps == null || quoteList == null || quoteList.Count == 0)
            throw new Exception($"未获取到数据，请检查代码 \"{symbol}\" 是否正确。");

        var quote = quoteList[0];
        var candles = new List<CandleStickData>();

        for (int i = 0; i < timestamps.Count; i++)
        {
            if (i >= (quote.Open?.Count ?? 0)) continue;
            if (i >= (quote.High?.Count ?? 0)) continue;
            if (i >= (quote.Low?.Count ?? 0)) continue;
            if (i >= (quote.Close?.Count ?? 0)) continue;

            var open = quote.Open![i];
            var high = quote.High![i];
            var low = quote.Low![i];
            var close = quote.Close![i];
            var volume = i < (quote.Volume?.Count ?? 0) ? quote.Volume![i] : null;

            if (!open.HasValue || !high.HasValue || !low.HasValue || !close.HasValue)
                continue;

            var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime.Date;
            candles.Add(new CandleStickData
            {
                Date = date,
                Open = open.Value,
                High = high.Value,
                Low = low.Value,
                Close = close.Value,
                Volume = volume ?? 0,
                Amount = 0,
                Amplitude = 0
            });
        }

        candles.Sort((a, b) => a.Date.CompareTo(b.Date));

        return new MarketChartData
        {
            Symbol = meta?.Symbol ?? symbol,
            Currency = meta?.Currency ?? "",
            ExchangeName = meta?.ExchangeName ?? "",
            Candles = candles,
            FromCache = false
        };
    }

    private MarketChartData BuildFromCache(string symbol, List<CandleStickData> candles)
    {
        var info = _db.GetSymbolInfo(symbol);
        return new MarketChartData
        {
            Symbol = info?.name ?? symbol,
            ExchangeName = info?.exchange ?? "",
            Currency = info?.currency ?? "",
            Candles = candles,
            FromCache = true
        };
    }

    private static int RangeToCount(string range)
    {
        return range switch
        {
            "5d"   => 5,
            "1mo"  => 22,
            "3mo"  => 66,
            "6mo"  => 132,
            "1y"   => 250,
            "2y"   => 500,
            "5y"   => 1250,
            "10y"  => 2500,
            "max"  => 5000,
            _ when range.StartsWith("custom:") => int.Parse(range[7..]),
            _      => 132
        };
    }

    private class YahooChartResponse
    {
        [JsonProperty("chart")]
        public YahooChart? Chart { get; set; }
    }

    private class YahooChart
    {
        [JsonProperty("result")]
        public List<YahooResult>? Result { get; set; }
    }

    private class YahooResult
    {
        [JsonProperty("meta")]
        public YahooMeta? Meta { get; set; }
        [JsonProperty("timestamp")]
        public List<long>? Timestamp { get; set; }
        [JsonProperty("indicators")]
        public YahooIndicators? Indicators { get; set; }
    }

    private class YahooMeta
    {
        [JsonProperty("symbol")]
        public string? Symbol { get; set; }
        [JsonProperty("currency")]
        public string? Currency { get; set; }
        [JsonProperty("exchangeName")]
        public string? ExchangeName { get; set; }
    }

    private class YahooIndicators
    {
        [JsonProperty("quote")]
        public List<YahooQuote>? Quote { get; set; }
    }

    private class YahooQuote
    {
        [JsonProperty("open")]
        public List<double?>? Open { get; set; }
        [JsonProperty("high")]
        public List<double?>? High { get; set; }
        [JsonProperty("low")]
        public List<double?>? Low { get; set; }
        [JsonProperty("close")]
        public List<double?>? Close { get; set; }
        [JsonProperty("volume")]
        public List<double?>? Volume { get; set; }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

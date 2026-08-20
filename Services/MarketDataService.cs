using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using FinanceAPP.Models;

namespace FinanceAPP.Services;

/// <summary>
/// 东方财富行情数据服务
/// 支持本地SQLite数据库缓存，增量更新，离线访问
/// </summary>
public class MarketDataService : IMarketDataService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DatabaseService _db;
    private const string BaseUrl = "https://push2his.eastmoney.com/api/qt/stock/kline/get";
    private const string UserToken = "f057cbcbce2a86e2866ab8877db1d059";

    private static readonly HashSet<string> ChineseADRs = new()
    {
        "BABA", "DIDI", "NIO", "XPEV", "LI", "PDD", "JD", "BIDU",
        "NTES", "TME", "BILI", "WB", "VIPS", "HTHT", "ZTO", "YRD",
        "TCOM", "BEKE", "DADA", "LU", "FUTU", "TAL", "HUYA", "DOYU"
    };

    public MarketDataService()
    {
        var handler = new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        };
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _db = new DatabaseService();
    }

    /// <summary>
    /// 获取共享的数据库服务（供其他数据源使用同一缓存）
    /// </summary>
    public DatabaseService GetDatabase() => _db;

    /// <summary>
    /// 获取K线数据（增量更新：只抓取本地数据库中缺失的部分）
    /// </summary>
    /// <param name="onProgress">进度回调（可选，用于显示分批获取进度）</param>
    public async Task<MarketChartData> GetChartDataAsync(string symbol, string range = "6mo", string interval = "1d", Action<string>? onProgress = null)
    {
        var secid = GetSecId(symbol);
        var (klt, lmt) = GetRangeParams(range);

        // 非日K线直接从API获取
        if (klt != 101)
        {
            return await FetchFromApiAsync(symbol, secid, klt, lmt, onProgress);
        }

        // ===== 日K线：增量更新逻辑 =====
        var dbCount = _db.GetCandleCount(symbol);
        var dbEarliest = _db.GetEarliestDate(symbol);
        var dbLatest = _db.GetLatestDate(symbol);
        var isFresh = _db.IsDataFresh(symbol);

        // 情况1：数据库有足够数据且数据新鲜 -> 直接返回缓存
        if (dbCount >= lmt && isFresh)
        {
            var cached = _db.GetCandles(symbol, lmt);
            if (cached.Count >= lmt * 0.8)
            {
                onProgress?.Invoke("📦 使用本地缓存数据");
                return BuildFromCache(symbol, cached);
            }
        }

        // 计算需要获取多少条新数据
        int needFromApi = lmt;
        bool needRecentUpdate = !isFresh;

        // 情况2：数据库有部分数据 -> 只抓取缺失部分
        if (dbCount > 0 && dbEarliest.HasValue && dbLatest.HasValue)
        {
            int haveCount = dbCount;
            int needOlder = Math.Max(0, lmt - haveCount);

            // 补充历史数据（每批立即存库）
            if (needOlder > 0)
            {
                onProgress?.Invoke($"📦 本地已有{haveCount}条，需补充{needOlder}条历史数据");
                await FetchOlderDataAsync(symbol, secid, klt, needOlder, dbEarliest.Value, onProgress);
            }

            // 更新最新数据（立即存库）
            if (needRecentUpdate)
            {
                onProgress?.Invoke($"正在更新最近数据...");
                try
                {
                    // end设为远期，确保获取到今天的数据
                    var recentData = await FetchSingleBatchAsync(symbol, secid, klt, 250, "20500101");
                    if (recentData.Candles.Count > 0)
                    {
                        // 只保存比缓存更新的数据
                        var newCandles = recentData.Candles.Where(c => c.Date > dbLatest.Value).ToList();
                        if (newCandles.Count > 0)
                        {
                            _db.SaveCandles(symbol, newCandles);
                            _db.SaveSymbolInfo(symbol, recentData.Symbol, recentData.ExchangeName, recentData.Currency);
                            onProgress?.Invoke($"✅ 已更新{newCandles.Count}条最新数据");
                        }
                        else
                        {
                            onProgress?.Invoke("📦 本地数据已是最新");
                        }
                    }
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"⚠️ 更新最新数据失败({ex.Message})，使用已有数据");
                }
            }

            // 从数据库返回合并后的数据
            var allCached = _db.GetCandles(symbol, lmt);
            if (allCached.Count > 0)
            {
                var result = BuildFromCache(symbol, allCached);
                onProgress?.Invoke($"✅ 数据就绪，共{allCached.Count}条");
                return result;
            }
        }

        // 情况3：数据库为空 -> 从API全量获取（每批立即存库）
        try
        {
            return await FetchFromApiAsync(symbol, secid, klt, lmt, onProgress);
        }
        catch (Exception ex)
        {
            // API失败，检查数据库是否有部分保存的数据
            var savedCount = _db.GetCandleCount(symbol);
            if (savedCount > 0)
            {
                onProgress?.Invoke($"⚠️ 获取中断({ex.Message})，本地已保存{savedCount}条数据");
                var cached = _db.GetCandles(symbol, lmt);
                if (cached.Count > 0)
                    return BuildFromCache(symbol, cached);
            }
            throw;
        }
    }

    /// <summary>
    /// 从指定日期往前抓取历史数据（增量补充，每批立即存库）
    /// </summary>
    private async Task<MarketChartData> FetchOlderDataAsync(string symbol, string secid, int klt, int totalNeeded, DateTime startDate, Action<string>? onProgress)
    {
        const int batchSize = 250;
        const int delayMs = 800;

        var allCandles = new List<CandleStickData>();
        var seenDates = new HashSet<string>();
        MarketChartData? result = null;

        int remaining = totalNeeded;
        int batchNum = 0;
        string endDate = startDate.AddDays(-1).ToString("yyyyMMdd");

        while (remaining > 0)
        {
            batchNum++;
            int batchLmt = Math.Min(batchSize, remaining);

            onProgress?.Invoke($"正在补充历史数据... 第{batchNum}批 (剩余{remaining}条)");

            MarketChartData batch;
            try
            {
                batch = await FetchSingleBatchAsync(symbol, secid, klt, batchLmt, endDate);
            }
            catch (Exception ex)
            {
                // 某批失败，已保存的数据不受影响
                if (allCandles.Count > 0)
                {
                    onProgress?.Invoke($"⚠️ 第{batchNum}批获取失败({ex.Message})，已保存{allCandles.Count}条");
                    break;
                }
                throw;
            }

            if (batch.Candles.Count == 0) break;

            // 去重并收集
            int newCount = 0;
            foreach (var c in batch.Candles)
            {
                string key = c.Date.ToString("yyyy-MM-dd");
                if (seenDates.Add(key))
                {
                    allCandles.Add(c);
                    newCount++;
                }
            }

            // 立即存入数据库（断点保护）
            _db.SaveCandles(symbol, batch.Candles);
            onProgress?.Invoke($"✅ 第{batchNum}批已保存{newCount}条，累计{allCandles.Count}条");

            result ??= new MarketChartData
            {
                Symbol = batch.Symbol,
                ExchangeName = batch.ExchangeName,
                Currency = batch.Currency
            };

            var earliest = batch.Candles.First().Date;
            endDate = earliest.AddDays(-1).ToString("yyyyMMdd");

            remaining -= batch.Candles.Count;
            if (batch.Candles.Count < batchLmt) break;
            if (remaining > 0) await Task.Delay(delayMs);
        }

        allCandles.Sort((a, b) => a.Date.CompareTo(b.Date));

        result ??= new MarketChartData { Symbol = symbol, ExchangeName = "", Currency = "" };
        result.Candles = allCandles;
        return result;
    }

    /// <summary>
    /// 从API获取数据（含重试机制，超过1年自动分批抓取）
    /// </summary>
    private async Task<MarketChartData> FetchFromApiAsync(string symbol, string secid, int klt, int lmt, Action<string>? onProgress = null)
    {
        // 日K线且数据量超过250（约1年），分批抓取
        if (klt == 101 && lmt > 250)
        {
            return await FetchInBatchesAsync(symbol, secid, klt, lmt, onProgress);
        }

        return await FetchSingleBatchAsync(symbol, secid, klt, lmt, "20500101");
    }

    /// <summary>
    /// 分批抓取数据（每批250条≈1年，批次间延时避免请求过于频繁）
    /// </summary>
    private async Task<MarketChartData> FetchInBatchesAsync(string symbol, string secid, int klt, int totalNeeded, Action<string>? onProgress)
    {
        const int batchSize = 250;
        const int delayMs = 800;

        var allCandles = new List<CandleStickData>();
        var seenDates = new HashSet<string>();
        MarketChartData? result = null;

        int remaining = totalNeeded;
        int batchNum = 0;
        string endDate = "20500101"; // 从最新开始往前抓

        while (remaining > 0)
        {
            batchNum++;
            int batchLmt = Math.Min(batchSize, remaining);

            onProgress?.Invoke($"正在获取数据... 第{batchNum}批 (剩余{remaining}条)");

            MarketChartData batch;
            try
            {
                batch = await FetchSingleBatchAsync(symbol, secid, klt, batchLmt, endDate);
            }
            catch (Exception ex)
            {
                if (allCandles.Count > 0)
                {
                    onProgress?.Invoke($"⚠️ 第{batchNum}批获取失败({ex.Message})，已保存{allCandles.Count}条");
                    break;
                }
                throw;
            }

            if (batch.Candles.Count == 0)
                break;

            // 去重并收集
            int newCount = 0;
            foreach (var c in batch.Candles)
            {
                string key = c.Date.ToString("yyyy-MM-dd");
                if (seenDates.Add(key))
                {
                    allCandles.Add(c);
                    newCount++;
                }
            }

            // 立即存入数据库（断点保护）
            if (klt == 101)
            {
                _db.SaveCandles(symbol, batch.Candles);
                _db.SaveSymbolInfo(symbol, batch.Symbol, batch.ExchangeName, batch.Currency);
            }
            onProgress?.Invoke($"✅ 第{batchNum}批已保存{newCount}条，累计{allCandles.Count}条");

            // 保存元信息（第一批）
            result ??= new MarketChartData
            {
                Symbol = batch.Symbol,
                ExchangeName = batch.ExchangeName,
                Currency = batch.Currency
            };

            // 下一批的结束日期 = 本批最早日期的前一天
            var earliestDate = batch.Candles.First().Date;
            endDate = earliestDate.AddDays(-1).ToString("yyyyMMdd");

            remaining -= batch.Candles.Count;

            // 如果返回数据少于请求数量，说明没有更多历史数据了
            if (batch.Candles.Count < batchLmt)
                break;

            // 批次间延时
            if (remaining > 0)
                await Task.Delay(delayMs);
        }

        // 按日期升序排列
        allCandles.Sort((a, b) => a.Date.CompareTo(b.Date));

        onProgress?.Invoke($"数据获取完成，共{allCandles.Count}条");

        result ??= new MarketChartData { Symbol = symbol, ExchangeName = "", Currency = "" };
        result.Candles = allCandles;
        return result;
    }

    /// <summary>
    /// 单次API请求（含重试）
    /// </summary>
    private async Task<MarketChartData> FetchSingleBatchAsync(string symbol, string secid, int klt, int lmt, string endDate)
    {
        var fqt = NeedsAdjustment(symbol) ? 1 : 0;
        var url = $"{BaseUrl}?secid={secid}" +
                  $"&fields1=f1,f2,f3,f4,f5,f6" +
                  $"&fields2=f51,f52,f53,f54,f55,f56,f57,f58" +
                  $"&klt={klt}&fqt={fqt}&end={endDate}&lmt={lmt}" +
                  $"&ut={UserToken}";

        const int maxRetries = 3;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"API请求失败: HTTP {response.StatusCode}");

                var eastmoneyResponse = JsonConvert.DeserializeObject<EastmoneyResponse>(json);

                if (eastmoneyResponse?.Data?.Klines == null || eastmoneyResponse.Data.Klines.Count == 0)
                    throw new Exception($"未获取到数据，请检查代码 \"{symbol}\" 是否正确。");

                return ParseResponse(eastmoneyResponse, symbol);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < maxRetries)
                    await Task.Delay(1000 * attempt);
            }
        }

        throw lastException!;
    }

    /// <summary>
    /// 从缓存构建返回数据
    /// </summary>
    private MarketChartData BuildFromCache(string symbol, List<CandleStickData> candles)
    {
        var info = _db.GetSymbolInfo(symbol);
        return new MarketChartData
        {
            Symbol = info?.name ?? symbol,
            ExchangeName = info?.exchange ?? GetExchangeName(symbol),
            Currency = info?.currency ?? "",
            Candles = candles,
            FromCache = true
        };
    }

    private string GetSecId(string symbol)
    {
        symbol = symbol.ToUpper().Trim();

        // 港股: HK00700 -> 116.00700
        if (symbol.StartsWith("HK"))
        {
            string hkCode = symbol[2..].TrimStart('0').PadLeft(5, '0');
            return $"116.{hkCode}";
        }

        if (symbol.EndsWith(".SS"))
            return $"1.{symbol.Replace(".SS", "")}";
        if (symbol.EndsWith(".SZ"))
            return $"0.{symbol.Replace(".SZ", "")}";

        if (Regex.IsMatch(symbol, @"^\d{6}$"))
            return symbol.StartsWith("6") ? $"1.{symbol}" : $"0.{symbol}";

        if (symbol == "GC=F") return "101.GC00Y";
        if (symbol == "SI=F") return "101.SI00Y";
        if (symbol == "HG=F") return "101.HG00Y";
        if (symbol == "CL=F") return "102.CL00Y";
        if (symbol == "NG=F") return "102.NG00Y";

        if (symbol.StartsWith("101.") || symbol.StartsWith("102.") ||
            symbol.StartsWith("122.") || symbol.StartsWith("105.") ||
            symbol.StartsWith("106.") || symbol.StartsWith("1.") ||
            symbol.StartsWith("0."))
            return symbol;

        if (ChineseADRs.Contains(symbol))
            return $"106.{symbol}";

        return $"105.{symbol}";
    }

    /// <summary>
    /// 通过东方财富API查询股票名称
    /// </summary>
    public async Task<string?> SearchStockNameAsync(string code)
    {
        try
        {
            string secid;
            // 港股: HK00700 -> 116.00700
            if (code.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
            {
                string hkCode = code[2..].TrimStart('0').PadLeft(5, '0');
                secid = $"116.{hkCode}";
            }
            else
            {
                secid = GetSecId(code);
            }
            var url = $"https://push2.eastmoney.com/api/qt/stock/get?secid={secid}&fields=f57,f58";
            var json = await _httpClient.GetStringAsync(url);
            var data = JsonConvert.DeserializeObject<dynamic>(json);
            var name = data?["data"]?["f58"]?.ToString();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    private (int klt, int lmt) GetRangeParams(string range)
    {
        return range switch
        {
            "5d"   => (101, 5),
            "1mo"  => (101, 22),
            "3mo"  => (101, 66),
            "6mo"  => (101, 132),
            "1y"   => (101, 250),
            "2y"   => (101, 500),
            "5y"   => (101, 1250),   // 日K线分批抓取（5批×250）
            "10y"  => (101, 2500),   // 日K线分批抓取（10批×250）
            "max"  => (103, 1000),   // 月K线，避免过多请求
            _ when range.StartsWith("custom:") => (101, int.Parse(range[7..])),
            _      => (101, 66)
        };
    }

    private bool NeedsAdjustment(string symbol)
    {
        symbol = symbol.ToUpper().Trim();
        if (symbol == "GC=F" || symbol == "SI=F" || symbol == "CL=F" ||
            symbol == "NG=F" || symbol == "HG=F")
            return false;
        if (symbol.StartsWith("101.") || symbol.StartsWith("102.") || symbol.StartsWith("122."))
            return false;
        return true;
    }

    private MarketChartData ParseResponse(EastmoneyResponse response, string inputSymbol)
    {
        var data = response.Data!;
        var result = new MarketChartData
        {
            Symbol = data.Name ?? inputSymbol,
            ExchangeName = GetExchangeName(inputSymbol)
        };

        var secid = GetSecId(inputSymbol);
        result.Currency = (secid.StartsWith("1.") || secid.StartsWith("0.")) ? "CNY" : "USD";

        foreach (var line in data.Klines)
        {
            var parts = line.Split(',');
            if (parts.Length < 6) continue;

            if (!DateTime.TryParse(parts[0], out var date)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
            if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
            if (!double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
            if (!double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
            double.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var volume);
            double.TryParse(parts[6], NumberStyles.Any, CultureInfo.InvariantCulture, out var amount);        // 成交额
            double.TryParse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture, out var amplitude);     // 振幅

            result.Candles.Add(new CandleStickData
            {
                Date = date,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                Amount = amount,
                Amplitude = amplitude
            });
        }

        // API返回降序(最新在前)，反转为升序(最老在前)以匹配图表左到右渲染
        result.Candles.Reverse();
        return result;
    }

    private string GetExchangeName(string symbol)
    {
        symbol = symbol.ToUpper().Trim();
        if (symbol.EndsWith(".SS") || (Regex.IsMatch(symbol, @"^\d{6}$") && symbol.StartsWith("6")))
            return "上海证券交易所";
        if (symbol.EndsWith(".SZ") || (Regex.IsMatch(symbol, @"^\d{6}$") && (symbol.StartsWith("0") || symbol.StartsWith("3"))))
            return "深圳证券交易所";
        if (symbol == "GC=F" || symbol == "SI=F" || symbol == "HG=F") return "COMEX";
        if (symbol == "CL=F" || symbol == "NG=F") return "NYMEX";
        if (ChineseADRs.Contains(symbol)) return "NYSE (中概股)";
        if (symbol.Length <= 5 && Regex.IsMatch(symbol, @"^[A-Z]+$")) return "美股";
        return "";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _db.Dispose();
    }
}
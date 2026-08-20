using System.IO;
using System.Net.Sockets;
using System.Text;
using FinanceAPP.Models;

namespace FinanceAPP.Services;

/// <summary>
/// 通达信行情数据服务
/// 通过TCP二进制协议直连通达信服务器获取K线数据
/// </summary>
public class TdxService : IMarketDataService, IDisposable
{
    private readonly DatabaseService _db;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private (string ip, int port)? _connectedServer;

    // 数据库symbol前缀，用于区分数据来源
    private const string SourceTag = "TDX";

    public TdxService(DatabaseService db)
    {
        _db = db;
    }

    public async Task<MarketChartData> GetChartDataAsync(string symbol, string range = "6mo", string interval = "1d", Action<string>? onProgress = null)
    {
        // 解析股票代码 -> 通达信 market + code + 证券类型
        if (!TryParseSymbol(symbol, out ushort market, out string tdxCode, out string dbName, out SecurityType secType))
        {
            throw new ArgumentException($"无法识别的代码: {symbol}。支持A股(600519)、指数(399001/999999)、期货(IF2406)、港股(HK00700)。");
        }

        string typeDesc = secType switch
        {
            SecurityType.Index => "指数",
            SecurityType.Future => "期货",
            _ => "股票"
        };

        int targetCount = range switch
        {
            "1mo" => 30,
            "3mo" => 90,
            "6mo" => 130,
            "1y" => 250,
            "2y" => 500,
            "5y" => 1250,
            "10y" => 2500,
            _ when range.StartsWith("custom:") => int.Parse(range[7..]),
            _ => 130
        };

        // 确定K线类别
        ushort category = interval switch
        {
            "1wk" => TdxProtocol.CatWeekly,
            "1mo" => TdxProtocol.CatMonthly,
            _ => TdxProtocol.CatDaily
        };

        // === 增量更新：先查数据库 ===
        bool isDaily = category == TdxProtocol.CatDaily;
        if (isDaily)
        {
            var cached = await TryGetFromCacheAsync(dbName, targetCount, onProgress);
            if (cached != null)
                return cached;

            // 增量补充历史数据
            var incremental = await FetchIncrementalAsync(dbName, market, tdxCode, targetCount, onProgress);
            if (incremental != null)
                return incremental;
        }

        // === 直接从TDX获取 ===
        onProgress?.Invoke($"正在从通达信获取 {symbol}({typeDesc}) 数据...");
        var bars = await FetchBarsFromTdx(market, tdxCode, category, targetCount, onProgress);

        if (bars.Count == 0)
        {
            throw new Exception(secType == SecurityType.Future
                ? "通达信标准服务器不支持期货数据。"
                : "未能获取到任何数据，请检查股票代码或网络连接。");
        }

        // 存入数据库（仅日K）
        if (isDaily)
        {
            await SaveToDatabaseAsync(dbName, bars, onProgress);
        }

        // 转换为MarketChartData
        var candles = bars
            .Select(b => new CandleStickData
            {
                Date = b.DateTime,
                Open = b.Open,
                High = b.High,
                Low = b.Low,
                Close = b.Close,
                Volume = b.Volume,
                Amount = b.Amount,
                Amplitude = b.High > 0 && b.Low > 0 ? (b.High - b.Low) / b.Open * 100 : 0
            })
            .OrderBy(c => c.Date)
            .ToList();

        return new MarketChartData
        {
            Symbol = dbName,
            Currency = secType == SecurityType.HKStock ? "HKD" : "CNY",
            ExchangeName = secType switch
            {
                SecurityType.Future => "FUT",
                SecurityType.Index => market == TdxProtocol.MarketSH ? "SHI" : "SZI",
                SecurityType.HKStock => "HK",
                _ => market == TdxProtocol.MarketSH ? "SH" : "SZ"
            },
            Candles = candles,
            FromCache = false
        };
    }

    // === 数据库缓存逻辑 ===

    private async Task<MarketChartData?> TryGetFromCacheAsync(string dbName, int targetCount, Action<string>? onProgress)
    {
        var dbCandles = _db.GetCandles(dbName, targetCount);
        if (dbCandles.Count == 0) return null;

        // 检查是否需要更新最新数据
        var lastDate = dbCandles.Last().Date;

        // 数据量足够且日期最新 -> 直接返回缓存
        if (dbCandles.Count >= targetCount * 0.8 && _db.IsDataFresh(dbName))
        {
            onProgress?.Invoke($"📦 本地缓存({dbCandles.Count}条)");
            return new MarketChartData
            {
                Symbol = dbName,
                Currency = "CNY",
                Candles = dbCandles,
                FromCache = true
            };
        }

        // 数据量够但日期不最新 -> 尝试增量更新最新几天
        if (dbCandles.Count >= targetCount * 0.8)
        {
            onProgress?.Invoke("正在更新最新数据...");
            try
            {
                if (!await EnsureConnectedAsync())
                {
                    onProgress?.Invoke("⚠️ 无法连接通达信服务器，使用本地缓存");
                    return BuildCacheResult(dbName, dbCandles);
                }

                // 获取最新数据（start=0, count=30 应该够覆盖假期）
                string tdxCode = ExtractTdxCode(dbName);
                ushort market = ExtractMarket(dbName);
                var recentBars = await FetchBarsFromTdx(market, tdxCode, TdxProtocol.CatDaily, 30, null);
                var newBars = recentBars.Where(b => b.DateTime.Date > lastDate).ToList();

                if (newBars.Count > 0)
                {
                    await SaveToDatabaseAsync(dbName, newBars, null);
                    dbCandles.AddRange(newBars.Select(b => new CandleStickData
                    {
                        Date = b.DateTime,
                        Open = b.Open,
                        High = b.High,
                        Low = b.Low,
                        Close = b.Close,
                        Volume = b.Volume,
                        Amount = b.Amount,
                        Amplitude = b.High > 0 && b.Low > 0 ? (b.High - b.Low) / b.Open * 100 : 0
                    }).OrderBy(c => c.Date));
                    onProgress?.Invoke($"✅ 已更新{newBars.Count}条最新数据");
                }
                else
                {
                    onProgress?.Invoke("📦 本地数据已是最新");
                }

                return BuildCacheResult(dbName, dbCandles);
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"⚠️ 更新失败({ex.Message.Split('\n')[0]})，使用本地缓存");
                return BuildCacheResult(dbName, dbCandles);
            }
        }

        return null; // 数据不够，需要补充历史
    }

    private async Task<MarketChartData?> FetchIncrementalAsync(string dbName, ushort market, string tdxCode, int targetCount, Action<string>? onProgress)
    {
        var dbCandles = _db.GetCandles(dbName, int.MaxValue);
        if (dbCandles.Count == 0) return null;

        int need = targetCount - dbCandles.Count;
        if (need <= 0)
        {
            onProgress?.Invoke($"📦 本地缓存({dbCandles.Count}条)");
            return BuildCacheResult(dbName, dbCandles);
        }

        onProgress?.Invoke($"📦 本地已有{dbCandles.Count}条，需补充{need}条历史数据");

        var earliestDate = dbCandles.First().Date;

        // 从本地最早日期往前抓取
        int batches = (need + TdxProtocol.MaxCount - 1) / TdxProtocol.MaxCount;
        int totalFetched = 0;
        int startIdx = 0; // TDX中start=0是最新，需要从已有数据的数量开始往前

        for (int batch = 0; batch < batches; batch++)
        {
            onProgress?.Invoke($"正在补充历史数据... 第{batch + 1}批");

            try
            {
                if (!await EnsureConnectedAsync())
                    break;

                ushort fetchCount = (ushort)Math.Min(TdxProtocol.MaxCount, need - totalFetched);
                var bars = await SendGetBarsRequest(market, tdxCode, TdxProtocol.CatDaily,
                    (ushort)(dbCandles.Count + startIdx), fetchCount);

                startIdx += bars.Count;

                var olderBars = bars.Where(b => b.DateTime.Date < earliestDate).ToList();
                if (olderBars.Count == 0) break;

                await SaveToDatabaseAsync(dbName, olderBars, null);
                totalFetched += olderBars.Count;
                earliestDate = olderBars.First().DateTime;

                onProgress?.Invoke($"✅ 第{batch + 1}批已保存{olderBars.Count}条");

                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"⚠️ 第{batch + 1}批获取失败({ex.Message.Split('\n')[0]})");
                break;
            }
        }

        // 补充历史后，再检查是否需要更新最新数据
        if (!_db.IsDataFresh(dbName))
        {
            onProgress?.Invoke("正在更新最新数据...");
            try
            {
                if (await EnsureConnectedAsync())
                {
                    var latestDate = _db.GetLatestDate(dbName) ?? DateTime.MinValue;
                    var recentBars = await FetchBarsFromTdx(market, tdxCode, TdxProtocol.CatDaily, 30, null);
                    var newBars = recentBars.Where(b => b.DateTime.Date > latestDate).ToList();
                    if (newBars.Count > 0)
                    {
                        await SaveToDatabaseAsync(dbName, newBars, null);
                        onProgress?.Invoke($"✅ 更新了{newBars.Count}条最新数据");
                    }
                }
            }
            catch { }
        }

        // 从数据库读取合并后的数据
        var merged = _db.GetCandles(dbName, targetCount);
        onProgress?.Invoke($"✅ 数据就绪，共{merged.Count}条");
        return BuildCacheResult(dbName, merged);
    }

    // === TDX通信 ===

    private async Task<List<TdxBar>> FetchBarsFromTdx(ushort market, string code, ushort category, int targetCount, Action<string>? onProgress)
    {
        var allBars = new List<TdxBar>();
        int start = 0;
        int batches = (targetCount + TdxProtocol.MaxCount - 1) / TdxProtocol.MaxCount;

        for (int batch = 0; batch < batches; batch++)
        {
            ushort count = (ushort)Math.Min(TdxProtocol.MaxCount, targetCount - allBars.Count);

            if (batch > 0)
            {
                onProgress?.Invoke($"正在获取数据... 第{batch + 1}/{batches}批");
                await Task.Delay(300);
            }

            bool success = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    if (!await EnsureConnectedAsync())
                    {
                        await Task.Delay(1000 * (retry + 1));
                        continue;
                    }

                    var bars = await SendGetBarsRequest(market, code, category, (ushort)start, count);
                    if (bars.Count > 0)
                    {
                        allBars.AddRange(bars);
                        start += bars.Count;
                        success = true;
                        break;
                    }
                    else
                    {
                        // 没有更多数据
                        success = true;
                        break;
                    }
                }
                catch (Exception)
                {
                    // 断开当前连接，重试时会重新连接
                    Disconnect();
                    if (retry < 2) await Task.Delay(1000 * (retry + 1));
                }
            }

            if (!success)
            {
                onProgress?.Invoke($"⚠️ 第{batch + 1}批获取失败，已获取{allBars.Count}条");
                break;
            }

            if (allBars.Count >= targetCount) break;
        }

        // TDX返回的数据是最新在前，反转为最老在前
        allBars.Reverse();
        return allBars;
    }

    private async Task<List<TdxBar>> SendGetBarsRequest(ushort market, string code, ushort category, ushort start, ushort count)
    {
        if (_stream == null) throw new InvalidOperationException("未连接到服务器");

        byte[] packet = TdxProtocol.BuildGetBarsPacket(category, market, code, start, count);
        await _stream.WriteAsync(packet);

        byte[] response = TdxProtocol.ReadResponse(_stream);
        return TdxProtocol.ParseBarsResponse(response, category);
    }

    // === 连接管理 ===

    private async Task<bool> EnsureConnectedAsync()
    {
        if (_client?.Connected == true && _stream != null) return true;
        return await ConnectAsync();
    }

    private async Task<bool> ConnectAsync()
    {
        Disconnect();

        foreach (var (ip, port) in TdxProtocol.Servers)
        {
            try
            {
                _client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _client.ConnectAsync(ip, port, cts.Token);
                _client.ReceiveTimeout = 10000;
                _client.SendTimeout = 5000;
                _stream = _client.GetStream();
                
                // 发送3条设置命令，每条发送后读取响应
                foreach (var cmd in TdxProtocol.SetupCommands)
                {
                    await _stream.WriteAsync(cmd);
                    TdxProtocol.ReadSetupResponse(_stream);
                }

                _connectedServer = (ip, port);
                return true;
            }
            catch
            {
                Disconnect();
            }
        }

        return false;
    }

    private void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        _connectedServer = null;
    }

    // === 数据库操作 ===

    private async Task SaveToDatabaseAsync(string dbName, List<TdxBar> bars, Action<string>? onProgress)
    {
        var candles = bars.Select(b => new CandleStickData
        {
            Date = b.DateTime,
            Open = b.Open,
            High = b.High,
            Low = b.Low,
            Close = b.Close,
            Volume = b.Volume,
            Amount = b.Amount,
            Amplitude = b.High > 0 && b.Low > 0 ? (b.High - b.Low) / b.Open * 100 : 0
        }).ToList();

        await Task.Run(() => _db.SaveCandles(dbName, candles));

        if (onProgress != null && candles.Count > 0)
        {
            int totalCount = _db.GetCandleCount(dbName);
            onProgress($"✅ 已保存{candles.Count}条，本地共{totalCount}条");
        }
    }

    // === 工具方法 ===

    private MarketChartData BuildCacheResult(string dbName, List<CandleStickData> candles)
    {
        string exchange = "TDX";
        if (dbName.Contains(".SH.") || dbName.Contains(".SHI.")) exchange = "SH";
        else if (dbName.Contains(".SZ.") || dbName.Contains(".SZI.")) exchange = "SZ";
        else if (dbName.Contains(".HK.")) exchange = "HK";
        else if (dbName.Contains(".FUT.")) exchange = "FUT";

        return new MarketChartData
        {
            Symbol = dbName,
            ExchangeName = exchange,
            Currency = dbName.Contains(".HK.") ? "HKD" : "CNY",
            Candles = candles,
            FromCache = true
        };
    }

    /// <summary>
    /// 证券类型
    /// </summary>
    private enum SecurityType { Stock, Index, Future, HKStock }

    /// <summary>
    /// 已知的上海指数代码（排除000001因为与深证股票冲突）
    /// </summary>
    private static readonly HashSet<string> KnownSHIndices = new()
    {
        "000300", "000016", "000688", "000905", "000852", "000002"
    };

    /// <summary>
    /// 期货品种 -> 市场代码映射
    /// </summary>
    private static readonly (string prefix, ushort market)[] FuturePrefixes =
    {
        // 中金所 (47)
        ("IF", 47), ("IH", 47), ("IC", 47), ("IM", 47), ("T", 47), ("TF", 47), ("TS", 47),
        // 上期所 (30)
        ("CU", 30), ("AL", 30), ("ZN", 30), ("AU", 30), ("AG", 30), ("RB", 30), ("HC", 30),
        ("FU", 30), ("BU", 30), ("RU", 30), ("NI", 30), ("SN", 30), ("PB", 30), ("BC", 30),
        ("LU", 30), ("NR", 30), ("SS", 30), ("SP", 30), ("AO", 30), ("BR", 30),
        // 大商所 (29)
        ("M", 29), ("I", 29), ("J", 29), ("JM", 29), ("L", 29), ("P", 29), ("C", 29),
        ("CS", 29), ("A", 29), ("B", 29), ("JD", 29), ("BB", 29), ("FB", 29), ("RR", 29),
        ("LH", 29), ("EG", 29), ("pg", 29), ("eb", 29), ("v", 29), ("y", 29),
        // 郑商所 (48)
        ("CF", 48), ("SR", 48), ("TA", 48), ("MA", 48), ("OI", 48), ("RI", 48), ("FG", 48),
        ("SA", 48), ("ZC", 48), ("AP", 48), ("CJ", 48), ("UR", 48), ("SF", 48), ("SM", 48),
        ("PF", 48), ("PK", 48), ("SH", 48), ("PX", 48), ("EC", 48), ("SI", 48), ("LC", 48),
    };

    /// <summary>
    /// 解析股票代码
    /// 支持: 600519(股票), 399001(深证指数), 999999(上证指数), IF2406(期货)
    /// </summary>
    private static bool TryParseSymbol(string symbol, out ushort market, out string tdxCode, out string dbName, out SecurityType secType)
    {
        market = 0;
        tdxCode = "";
        dbName = "";
        secType = SecurityType.Stock;

        string code = symbol.Trim().ToUpper();

        // 去掉后缀 (.SH, .SZ 等)
        string suffix = "";
        if (code.Contains('.'))
        {
            var parts = code.Split('.');
            code = parts[0];
            if (parts.Length > 1) suffix = parts[1].ToUpper();
        }

        // === 港股检测: HK开头 (如 HK00700, HK0700) ===
        if (code.StartsWith("HK"))
        {
            string hkCode = code[2..].TrimStart('0');
            // 港股代码补齐为5位
            hkCode = hkCode.PadLeft(5, '0');
            if (!hkCode.All(char.IsDigit) || hkCode.Length != 5)
                return false;
            market = TdxProtocol.MarketHK;
            tdxCode = hkCode;
            secType = SecurityType.HKStock;
            dbName = $"{hkCode}.HK.{SourceTag}";
            return true;
        }

        // === 期货检测: 代码以字母开头 ===
        if (code.Length > 0 && char.IsLetter(code[0]))
        {
            foreach (var (prefix, mkt) in FuturePrefixes)
            {
                if (code.StartsWith(prefix))
                {
                    market = mkt;
                    tdxCode = code;
                    secType = SecurityType.Future;
                    dbName = $"{code}.FUT.{SourceTag}";
                    return true;
                }
            }
            return false;
        }

        // 纯数字代码
        if (code.Length != 6 || !code.All(char.IsDigit))
            return false;

        tdxCode = code;

        // === 指数检测 ===
        // 399xxx -> 深证指数
        if (code.StartsWith("399"))
        {
            market = TdxProtocol.MarketSZ;
            secType = SecurityType.Index;
            dbName = $"{code}.SZI.{SourceTag}";
            return true;
        }
        // 999xxx -> 上证指数 (TDX用999999表示上证综指)
        if (code.StartsWith("999"))
        {
            market = TdxProtocol.MarketSH;
            secType = SecurityType.Index;
            dbName = $"{code}.SHI.{SourceTag}";
            return true;
        }
        // 已知的上海指数代码 (000300沪深300, 000016上证50等)
        if (KnownSHIndices.Contains(code) || (suffix == "SH" && code.StartsWith("000")))
        {
            market = TdxProtocol.MarketSH;
            secType = SecurityType.Index;
            dbName = $"{code}.SHI.{SourceTag}";
            return true;
        }

        // === 股票检测 (TDX协议: 0=SZ深圳, 1=SH上海) ===
        if (code.StartsWith("6"))  // 60xxxx, 68xxxx
        {
            market = TdxProtocol.MarketSH;
            dbName = $"{code}.SH.{SourceTag}";
        }
        else if (code.StartsWith("0") || code.StartsWith("3"))  // 00xxxx, 30xxxx
        {
            market = TdxProtocol.MarketSZ;
            dbName = $"{code}.SZ.{SourceTag}";
        }
        else if (code.StartsWith("5"))  // 51xxxx ETF (SH)
        {
            market = TdxProtocol.MarketSH;
            dbName = $"{code}.SH.{SourceTag}";
        }
        else if (code.StartsWith("1"))  // 159xxx ETF (SZ)
        {
            market = TdxProtocol.MarketSZ;
            dbName = $"{code}.SZ.{SourceTag}";
        }
        else
        {
            return false;
        }

        return true;
    }

    private static string ExtractTdxCode(string dbName)
    {
        return dbName.Split('.')[0];
    }

    private static ushort ExtractMarket(string dbName)
    {
        if (dbName.Contains(".SH.") || dbName.Contains(".SHI.")) return TdxProtocol.MarketSH;
        if (dbName.Contains(".SZ.") || dbName.Contains(".SZI.")) return TdxProtocol.MarketSZ;
        if (dbName.Contains(".HK.")) return TdxProtocol.MarketHK;
        if (dbName.Contains(".FUT."))
        {
            string code = dbName.Split('.')[0];
            foreach (var (prefix, mkt) in FuturePrefixes)
                if (code.StartsWith(prefix)) return mkt;
        }
        return TdxProtocol.MarketSZ;
    }

    /// <summary>
    /// 查询股票名称（从本地TDXExport文件夹和数据库查找，不依赖网络）
    /// </summary>
    public Task<string?> SearchStockNameAsync(string code)
    {
        // 港股代码
        if (code.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
        {
            string hkCode = code[2..].PadLeft(5, '0');
            // 先查数据库
            var dbName = $"{hkCode}.HK.TDX";
            var info = _db.GetSymbolInfo(dbName);
            if (info.HasValue && !string.IsNullOrEmpty(info.Value.name)) return Task.FromResult<string?>(info.Value.name);
            // 查TDXExport文件夹
            var fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TDXExport", $"HK#{hkCode}.txt");
            if (File.Exists(fileName))
            {
                var name = ReadNameFromFile(fileName);
                if (!string.IsNullOrEmpty(name)) return Task.FromResult<string?>(name);
            }
            return Task.FromResult<string?>(null);
        }

        // A股代码
        if (code.Length == 6 && code.All(char.IsDigit))
        {
            string exchange = code.StartsWith("6") ? "SH" : "SZ";
            // 先查TDXExport文件夹
            var fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TDXExport", $"{exchange}#{code}.txt");
            if (File.Exists(fileName))
            {
                var name = ReadNameFromFile(fileName);
                if (!string.IsNullOrEmpty(name)) return Task.FromResult<string?>(name);
            }

            // 查数据库
            var dbName = $"{code}.{exchange}.TDX";
            var info = _db.GetSymbolInfo(dbName);
            if (info.HasValue && !string.IsNullOrEmpty(info.Value.name)) return Task.FromResult<string?>(info.Value.name);

            // 数据库有数据但没名称，返回代码作为名称
            var range = _db.GetDataRange(dbName);
            if (range != null) return Task.FromResult<string?>(code);
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 从TDX导出txt文件第一行读取股票名称
    /// </summary>
    private static string? ReadNameFromFile(string filePath)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var firstLine = File.ReadLines(filePath, Encoding.GetEncoding(936)).FirstOrDefault();
            if (firstLine != null)
            {
                // 格式: "600519 贵州茅台 日K线 前复权"
                var fields = firstLine.Split(' ', (StringSplitOptions)1);
                if (fields.Length >= 2) return fields[1];
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
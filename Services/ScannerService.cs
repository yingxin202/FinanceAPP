using System.IO;
using System.Net.Sockets;
using System.Text;
using FinanceAPP.Models;

namespace FinanceAPP.Services;

public class ScanResult
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Date { get; set; }
    public double Close { get; set; }
    public double PriceChange { get; set; }      // 涨跌幅%
    public double VolumeRatio { get; set; }       // 量比（当日成交量 / 前5日平均成交量）
    public double Turnover { get; set; }          // 成交额
}

public class ScannerService
{
    private readonly DatabaseService _db;

    // 并发TDX连接数
    private const int MaxConcurrency = 5;

    // TDX导出文件夹路径
    private static readonly string TdxExportPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "TDXExport");

    public ScannerService(DatabaseService db)
    {
        _db = db;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 获取A股列表（优先本地数据源，不依赖网络）
    /// 1. 先从TDXExport文件夹扫描文件名（最快，含股票名称）
    /// 2. 其次从数据库获取已导入的股票代码
    /// </summary>
    public async Task<List<(string code, string name)>> GetAStockListAsync()
    {
        // 方式1：从TDXExport文件夹扫描
        var list = GetStockListFromTdxExport();
        if (list.Count > 0) return list;

        // 方式2：从数据库获取
        list = GetStockListFromDatabase();
        if (list.Count > 0) return list;

        return new List<(string code, string name)>();
    }

    /// <summary>
    /// 从TDXExport文件夹扫描股票列表（含名称）
    /// </summary>
    private List<(string code, string name)> GetStockListFromTdxExport()
    {
        var list = new List<(string code, string name)>();
        if (!Directory.Exists(TdxExportPath)) return list;

        var files = Directory.GetFiles(TdxExportPath, "*.txt");
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var parts = fileName.Replace(".txt", "").Split('#');
            if (parts.Length < 2) continue;

            string exchange = parts[0].ToUpper();
            string code = parts[1];
            if (code.Length != 6 || !code.All(char.IsDigit)) continue;
            if (exchange != "SH" && exchange != "SZ" && exchange != "BJ") continue;

            // 读取第一行获取股票名称
            string name = code;
            try
            {
                var firstLine = File.ReadLines(file, Encoding.GetEncoding(936)).FirstOrDefault();
                if (firstLine != null)
                {
                    // 格式: "600519 贵州茅台 日K线 前复权"
                    var fields = firstLine.Split(' ', (StringSplitOptions)1);
                    if (fields.Length >= 2) name = fields[1];
                }
            }
            catch { }

            // 排除ST、退市股票
            if (name.Contains("ST") || name.Contains("退")) continue;

            list.Add((code, name));
        }

        return list;
    }

    /// <summary>
    /// 从数据库获取已导入的股票列表
    /// </summary>
    private List<(string code, string name)> GetStockListFromDatabase()
    {
        var list = new List<(string code, string name)>();
        var symbols = _db.GetAllSymbolsWithName();
        foreach (var (symbol, name) in symbols)
        {
            // 解析: 600519.SH.TDX -> code=600519
            var parts = symbol.Split('.');
            if (parts.Length < 2) continue;
            string code = parts[0];
            if (code.Length != 6 || !code.All(char.IsDigit)) continue;
            // 使用数据库中的名称，如果为空则用代码
            list.Add((code, string.IsNullOrEmpty(name) ? code : name));
        }
        return list;
    }

    /// <summary>
    /// 扫描指定日期量价齐升/齐跌的股票
    /// </summary>
    /// <param name="targetDate">目标日期</param>
    /// <param name="volumeRatioThreshold">量比阈值（默认2.0）</param>
    /// <param name="priceChangeThreshold">涨/跌幅阈值%（默认5.0）</param>
    /// <param name="onProgress">进度回调 (scanned, total, matched)</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="isDecline">true=量价齐跌（放量下跌），false=量价齐升（放量上涨）</param>
    public async Task<List<ScanResult>> ScanAsync(
        DateTime targetDate,
        double volumeRatioThreshold = 2.0,
        double priceChangeThreshold = 5.0,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        bool isDecline = false)
    {
        var stockList = await GetAStockListAsync();
        var results = new List<ScanResult>();
        int scanned = 0;
        int total = stockList.Count;
        var lockObj = new object();

        // 使用SemaphoreSlim控制并发
        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>();

        foreach (var (code, name) in stockList)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await semaphore.WaitAsync(cancellationToken);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await ScanSingleStockAsync(code, name, targetDate, volumeRatioThreshold, priceChangeThreshold, isDecline);
                    if (result != null)
                    {
                        lock (lockObj)
                        {
                            results.Add(result);
                        }
                    }
                }
                catch { }
                finally
                {
                    lock (lockObj)
                    {
                        scanned++;
                        if (scanned % 50 == 0 || scanned == total)
                        {
                            onProgress?.Invoke(scanned, total, results.Count);
                        }
                    }
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        onProgress?.Invoke(scanned, total, results.Count);

        // 按量比降序排序
        return results.OrderByDescending(r => r.VolumeRatio).ToList();
    }

    /// <summary>
    /// 扫描单只股票（优先使用本地缓存，最大限度减少TDX远程调用）
    /// </summary>
    private async Task<ScanResult?> ScanSingleStockAsync(string code, string name, DateTime targetDate, double volThreshold, double priceThreshold, bool isDecline = false)
    {
        // 确定市场
        ushort market = code.StartsWith("6") ? TdxProtocol.MarketSH : TdxProtocol.MarketSZ;
        string dbName = code.StartsWith("6") ? $"{code}.SH.TDX" : $"{code}.SZ.TDX";

        // 1. 先查缓存（取较多数据，尽量覆盖目标日期）
        var candles = _db.GetCandles(dbName, 30);

        // 2. 检查缓存是否包含目标日期的数据
        //    要求：缓存最新日期 >= 目标日期，且目标日期及之前至少有6条数据
        DateTime? cacheLatestDate = candles.Count > 0 ? candles[^1].Date.Date : (DateTime?)null;
        bool cacheCoversTarget = candles.Count >= 6 &&
            cacheLatestDate.HasValue &&
            cacheLatestDate.Value >= targetDate.Date &&
            candles.Count(c => c.Date.Date <= targetDate.Date) >= 6;

        // 3. 缓存不够新时，从TDX获取最新数据更新缓存
        if (!cacheCoversTarget)
        {
            try
            {
                // 获取较多数据以覆盖目标日期及前5日
                var bars = await FetchBarsFromTdxDirect(market, code, 30);
                var newCandles = bars.Select(b => new CandleStickData
                {
                    Date = b.DateTime,
                    Open = b.Open,
                    High = b.High,
                    Low = b.Low,
                    Close = b.Close,
                    Volume = b.Volume,
                    Amount = b.Amount,
                    Amplitude = b.Open > 0 ? (b.High - b.Low) / b.Open * 100 : 0
                }).OrderBy(c => c.Date).ToList();

                // 保存到数据库，后续扫描可直接用缓存
                if (newCandles.Count > 0)
                {
                    _db.SaveCandles(dbName, newCandles);
                    // 重新从数据库读取合并后的完整数据
                    candles = _db.GetCandles(dbName, 30);
                }
            }
            catch
            {
                // TDX失败时，仍然尝试用已有缓存数据
                if (candles.Count < 6) return null;
            }
        }

        if (candles.Count < 6) return null;

        // 4. 查找目标日期的K线
        // 如果找不到精确匹配，找目标日期之前最近的交易日
        var targetCandle = candles.FirstOrDefault(c => c.Date.Date == targetDate.Date);
        if (targetCandle == null)
        {
            // 找目标日期之前最近的
            targetCandle = candles.Where(c => c.Date.Date <= targetDate.Date).OrderByDescending(c => c.Date).FirstOrDefault();
            if (targetCandle == null) return null;
        }

        int idx = candles.IndexOf(targetCandle);
        if (idx < 5) return null; // 需要前5天计算平均成交量

        // 5. 计算量比 = 当日成交量 / 前5日平均成交量
        double avgVolume = 0;
        for (int i = 1; i <= 5; i++)
        {
            avgVolume += candles[idx - i].Volume;
        }
        avgVolume /= 5;

        if (avgVolume <= 0) return null;
        double volumeRatio = targetCandle.Volume / avgVolume;

        // 6. 计算涨幅 = (当日收盘 - 前日收盘) / 前日收盘 * 100
        if (idx < 1) return null;
        double prevClose = candles[idx - 1].Close;
        if (prevClose <= 0) return null;
        double priceChange = (targetCandle.Close - prevClose) / prevClose * 100;

        // 7. 过滤（齐跌模式：放量且跌幅达到阈值；齐升模式：放量且涨幅达到阈值）
        if (isDecline)
        {
            if (volumeRatio < volThreshold || priceChange > -priceThreshold)
                return null;
        }
        else if (volumeRatio < volThreshold || priceChange < priceThreshold)
            return null;

        return new ScanResult
        {
            Code = code,
            Name = name,
            Date = targetCandle.Date,
            Close = targetCandle.Close,
            PriceChange = priceChange,
            VolumeRatio = volumeRatio,
            Turnover = targetCandle.Amount
        };
    }

    /// <summary>
    /// 轻量级TDX连接：直接连接服务器获取数据，不复用TdxService的连接
    /// </summary>
    private async Task<List<TdxBar>> FetchBarsFromTdxDirect(ushort market, string code, int count)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(TdxProtocol.Servers[0].ip, TdxProtocol.Servers[0].port, cts.Token);
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 3000;
        using var stream = client.GetStream();

        // 发送设置命令
        foreach (var cmd in TdxProtocol.SetupCommands)
        {
            await stream.WriteAsync(cmd);
            TdxProtocol.ReadSetupResponse(stream);
        }

        // 请求K线数据
        var packet = TdxProtocol.BuildGetBarsPacket(TdxProtocol.CatDaily, market, code, 0, (ushort)Math.Min(count, 800));
        await stream.WriteAsync(packet);
        var response = TdxProtocol.ReadResponse(stream);
        var bars = TdxProtocol.ParseBarsResponse(response, TdxProtocol.CatDaily);
        bars.Reverse(); // TDX返回最新在前，反转为最老在前
        return bars;
    }

    // ==================== 量价平稳扫描 ====================

    /// <summary>
    /// 扫描指定日期前N天内价格平稳、成交量不大的股票
    /// </summary>
    /// <param name="targetDate">目标日期</param>
    /// <param name="days">观察天数（近期N个交易日）</param>
    /// <param name="maxFluctuation">最大波动率%（近期N日最高-最低/均价）</param>
    /// <param name="maxVolRatio">最大量比（近期均量/前期均量）</param>
    /// <param name="onProgress">进度回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<List<ScanResult>> ScanStableAsync(
        DateTime targetDate,
        int days = 5,
        double maxFluctuation = 10.0,
        double maxVolRatio = 1.0,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var stockList = await GetAStockListAsync();
        var results = new List<ScanResult>();
        int scanned = 0;
        int total = stockList.Count;
        var lockObj = new object();

        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>();

        foreach (var (code, name) in stockList)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await semaphore.WaitAsync(cancellationToken);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await ScanStableSingleStockAsync(code, name, targetDate, days, maxFluctuation, maxVolRatio);
                    if (result != null)
                    {
                        lock (lockObj)
                        {
                            results.Add(result);
                        }
                    }
                }
                catch { }
                finally
                {
                    lock (lockObj)
                    {
                        scanned++;
                        if (scanned % 50 == 0 || scanned == total)
                        {
                            onProgress?.Invoke(scanned, total, results.Count);
                        }
                    }
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        onProgress?.Invoke(scanned, total, results.Count);

        // 按波动率升序排序（越平稳越靠前）
        return results.OrderBy(r => r.PriceChange).ToList();
    }

    /// <summary>
    /// 扫描单只股票是否量价平稳（优先使用本地缓存）
    /// </summary>
    private async Task<ScanResult?> ScanStableSingleStockAsync(
        string code, string name, DateTime targetDate, int days,
        double maxFluctuation, double maxVolRatio)
    {
        ushort market = code.StartsWith("6") ? TdxProtocol.MarketSH : TdxProtocol.MarketSZ;
        string dbName = code.StartsWith("6") ? $"{code}.SH.TDX" : $"{code}.SZ.TDX";

        // 需要 days*2 + 5 条数据（近期N天 + 前期N天比较 + 余量）
        int needCount = days * 2 + 5;
        var candles = _db.GetCandles(dbName, needCount);

        // 检查缓存是否覆盖目标日期
        DateTime? cacheLatestDate = candles.Count > 0 ? candles[^1].Date.Date : (DateTime?)null;
        bool cacheCoversTarget = candles.Count >= needCount &&
            cacheLatestDate.HasValue &&
            cacheLatestDate.Value >= targetDate.Date;

        // 缓存不够新时，从TDX获取最新数据
        if (!cacheCoversTarget)
        {
            try
            {
                var bars = await FetchBarsFromTdxDirect(market, code, Math.Min(needCount, 100));
                var newCandles = bars.Select(b => new CandleStickData
                {
                    Date = b.DateTime,
                    Open = b.Open,
                    High = b.High,
                    Low = b.Low,
                    Close = b.Close,
                    Volume = b.Volume,
                    Amount = b.Amount,
                    Amplitude = b.Open > 0 ? (b.High - b.Low) / b.Open * 100 : 0
                }).OrderBy(c => c.Date).ToList();

                if (newCandles.Count > 0)
                {
                    _db.SaveCandles(dbName, newCandles);
                    candles = _db.GetCandles(dbName, needCount);
                }
            }
            catch
            {
                if (candles.Count < needCount) return null;
            }
        }

        if (candles.Count < needCount) return null;

        // 找到目标日期的K线（或最近的之前交易日）
        var targetCandle = candles.FirstOrDefault(c => c.Date.Date == targetDate.Date);
        if (targetCandle == null)
        {
            targetCandle = candles.Where(c => c.Date.Date <= targetDate.Date)
                .OrderByDescending(c => c.Date).FirstOrDefault();
            if (targetCandle == null) return null;
        }

        int idx = candles.IndexOf(targetCandle);
        // 需要至少 days*2 条前期数据
        if (idx < days * 2 - 1) return null;

        // 提取近期N天和前期N天的数据
        var recentDays = candles.Skip(idx - days + 1).Take(days).ToList();
        var previousDays = candles.Skip(idx - days * 2 + 1).Take(days).ToList();

        if (recentDays.Count < days || previousDays.Count < days) return null;

        // 计算价格波动率 = (近期最高 - 近期最低) / 近期均价 * 100
        double maxHigh = recentDays.Max(c => c.High);
        double minLow = recentDays.Min(c => c.Low);
        double avgClose = recentDays.Average(c => c.Close);
        if (avgClose <= 0) return null;
        double fluctuation = (maxHigh - minLow) / avgClose * 100;

        // 计算量比 = 近期均量 / 前期均量
        double recentAvgVol = recentDays.Average(c => c.Volume);
        double previousAvgVol = previousDays.Average(c => c.Volume);
        if (previousAvgVol <= 0) return null;
        double volRatio = recentAvgVol / previousAvgVol;

        // 过滤
        if (fluctuation > maxFluctuation || volRatio > maxVolRatio)
            return null;

        return new ScanResult
        {
            Code = code,
            Name = name,
            Date = targetCandle.Date,
            Close = targetCandle.Close,
            PriceChange = fluctuation,   // 复用为波动率
            VolumeRatio = volRatio,      // 复用为近期/前期量比
            Turnover = recentAvgVol      // 复用为近期平均成交量
        };
    }

    // ==================== 区间涨跌幅扫描 ====================

    /// <summary>
    /// 扫描指定日期区间内涨跌幅达到阈值的股票
    /// 区间涨跌幅 = (终止日收盘 - 起始日收盘) / 起始日收盘 * 100
    /// </summary>
    /// <param name="startDate">起始日期</param>
    /// <param name="endDate">终止日期</param>
    /// <param name="changeThreshold">涨跌幅阈值%（正数）</param>
    /// <param name="isDecline">true=查区间大跌（跌幅≥阈值），false=查区间大涨（涨幅≥阈值）</param>
    /// <param name="onProgress">进度回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<List<ScanResult>> ScanRangeAsync(
        DateTime startDate,
        DateTime endDate,
        double changeThreshold,
        bool isDecline,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var stockList = await GetAStockListAsync();
        var results = new List<ScanResult>();
        int scanned = 0;
        int total = stockList.Count;
        var lockObj = new object();

        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>();

        foreach (var (code, name) in stockList)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await semaphore.WaitAsync(cancellationToken);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await ScanRangeSingleStockAsync(code, name, startDate, endDate, changeThreshold, isDecline);
                    if (result != null)
                    {
                        lock (lockObj)
                        {
                            results.Add(result);
                        }
                    }
                }
                catch { }
                finally
                {
                    lock (lockObj)
                    {
                        scanned++;
                        if (scanned % 50 == 0 || scanned == total)
                        {
                            onProgress?.Invoke(scanned, total, results.Count);
                        }
                    }
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        onProgress?.Invoke(scanned, total, results.Count);

        // 按区间涨跌幅绝对值降序排序（涨幅大的在前，跌幅大的在后）
        return results.OrderByDescending(r => Math.Abs(r.PriceChange)).ToList();
    }

    /// <summary>
    /// 扫描单只股票的区间涨跌幅（优先使用本地缓存）
    /// </summary>
    private async Task<ScanResult?> ScanRangeSingleStockAsync(
        string code, string name, DateTime startDate, DateTime endDate,
        double changeThreshold, bool isDecline)
    {
        ushort market = code.StartsWith("6") ? TdxProtocol.MarketSH : TdxProtocol.MarketSZ;
        string dbName = code.StartsWith("6") ? $"{code}.SH.TDX" : $"{code}.SZ.TDX";

        // 估算需要的K线条数（交易日约为日历天数的0.7倍，留余量）
        int calendarDays = (endDate - startDate).Days;
        int needCount = Math.Max((int)(calendarDays * 0.75) + 10, 15);

        var candles = _db.GetCandles(dbName, needCount);

        // 检查缓存是否覆盖终止日期
        DateTime? cacheLatestDate = candles.Count > 0 ? candles[^1].Date.Date : (DateTime?)null;
        bool cacheCoversTarget = cacheLatestDate.HasValue && cacheLatestDate.Value >= endDate.Date;

        // 缓存不够新时，从TDX获取最新数据
        if (!cacheCoversTarget)
        {
            try
            {
                var bars = await FetchBarsFromTdxDirect(market, code, Math.Min(needCount, 800));
                var newCandles = bars.Select(b => new CandleStickData
                {
                    Date = b.DateTime,
                    Open = b.Open,
                    High = b.High,
                    Low = b.Low,
                    Close = b.Close,
                    Volume = b.Volume,
                    Amount = b.Amount,
                    Amplitude = b.Open > 0 ? (b.High - b.Low) / b.Open * 100 : 0
                }).OrderBy(c => c.Date).ToList();

                if (newCandles.Count > 0)
                {
                    _db.SaveCandles(dbName, newCandles);
                    candles = _db.GetCandles(dbName, needCount);
                }
            }
            catch
            {
                // TDX失败时继续用已有缓存
            }
        }

        if (candles.Count < 2) return null;

        // 缓存数据需完整覆盖起始日期（缓存最早日期 <= 起始日期）
        if (candles[0].Date.Date > startDate.Date) return null;

        // 找起始日K线（<= 起始日期的最近交易日）
        var startCandle = candles
            .Where(c => c.Date.Date <= startDate.Date)
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();
        if (startCandle == null || startCandle.Close <= 0) return null;

        // 找终止日K线（<= 终止日期的最近交易日，且晚于起始日）
        var endCandle = candles
            .Where(c => c.Date.Date <= endDate.Date && c.Date > startCandle.Date)
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();
        if (endCandle == null) return null;

        // 计算区间涨跌幅
        double change = (endCandle.Close - startCandle.Close) / startCandle.Close * 100;

        // 过滤
        if (isDecline)
        {
            if (change > -changeThreshold) return null;
        }
        else
        {
            if (change < changeThreshold) return null;
        }

        // 计算区间累计成交额（不含起始日，含终止日）
        double totalAmount = candles
            .Where(c => c.Date > startCandle.Date && c.Date <= endCandle.Date)
            .Sum(c => c.Amount);

        return new ScanResult
        {
            Code = code,
            Name = name,
            Date = endCandle.Date,
            Close = endCandle.Close,
            PriceChange = change,          // 区间涨跌幅
            VolumeRatio = startCandle.Close, // 复用为起始日收盘价
            Turnover = totalAmount         // 复用为区间累计成交额
        };
    }
}

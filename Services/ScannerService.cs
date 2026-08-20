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
        AppDomain.CurrentDomain.BaseDirectory, "TDXEXPORT");

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
    /// 扫描指定日期量价齐升的股票
    /// </summary>
    /// <param name="targetDate">目标日期</param>
    /// <param name="volumeRatioThreshold">量比阈值（默认2.0）</param>
    /// <param name="priceChangeThreshold">涨幅阈值%（默认5.0）</param>
    /// <param name="onProgress">进度回调 (scanned, total, matched)</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<List<ScanResult>> ScanAsync(
        DateTime targetDate,
        double volumeRatioThreshold = 2.0,
        double priceChangeThreshold = 5.0,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
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
                    var result = await ScanSingleStockAsync(code, name, targetDate, volumeRatioThreshold, priceChangeThreshold);
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
    private async Task<ScanResult?> ScanSingleStockAsync(string code, string name, DateTime targetDate, double volThreshold, double priceThreshold)
    {
        // 确定市场
        ushort market = code.StartsWith("6") ? TdxProtocol.MarketSH : TdxProtocol.MarketSZ;
        string dbName = code.StartsWith("6") ? $"{code}.SH.TDX" : $"{code}.SZ.TDX";

        // 1. 先查缓存（取较多数据，尽量覆盖目标日期）
        var candles = _db.GetCandles(dbName, 30);

        // 2. 检查缓存数据是否覆盖目标日期
        bool cacheCoversTarget = candles.Count >= 6 &&
            candles.Any(c => c.Date.Date <= targetDate.Date) &&
            // 目标日期前至少有5条数据（计算5日均量）
            candles.TakeWhile(c => c.Date.Date <= targetDate.Date).Count() >= 6;

        // 3. 缓存不足时才从TDX获取
        if (!cacheCoversTarget)
        {
            try
            {
                var bars = await FetchBarsFromTdxDirect(market, code, 10);
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
                    candles = newCandles;
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

        // 7. 过滤
        if (volumeRatio < volThreshold || priceChange < priceThreshold)
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
}
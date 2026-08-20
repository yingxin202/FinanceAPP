using System.IO;
using System.Text;
using FinanceAPP.Models;

namespace FinanceAPP.Services;

/// <summary>
/// 通达信导出数据导入服务
/// 支持导入通达信客户端导出的txt格式日线数据
/// </summary>
public class TdxImportService
{
    private readonly DatabaseService _db;

    public TdxImportService(DatabaseService db)
    {
        _db = db;
        // 注册GBK编码支持
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 导入指定文件夹下的所有通达信导出文件
    /// </summary>
    /// <param name="folderPath">文件夹路径</param>
    /// <param name="onProgress">进度回调 (imported, total, currentFile)</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<(int imported, int failed, int total)> ImportFolderAsync(
        string folderPath,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(folderPath, "*.txt");
        int imported = 0;
        int failed = 0;
        int total = files.Length;

        for (int i = 0; i < files.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var file = files[i];
            var fileName = Path.GetFileName(file);

            try
            {
                onProgress?.Invoke(i, total, fileName);

                var (code, exchange, _) = ParseFileName(fileName);
                if (code == null) { failed++; continue; }

                // 解析K线数据和股票名称
                var (candles, stockName) = await Task.Run(() => ParseFile(file), cancellationToken);
                if (candles.Count == 0) { failed++; continue; }

                string dbName = $"{code}.{exchange}.TDX";
                _db.SaveCandles(dbName, candles);
                _db.SaveSymbolInfo(dbName, stockName, exchange, "CNY");

                imported++;
            }
            catch
            {
                failed++;
            }
        }

        onProgress?.Invoke(total, total, "");
        return (imported, failed, total);
    }

    /// <summary>
    /// 解析文件名: SH#600519.txt -> ("600519", "SH", "")
    /// </summary>
    private (string? code, string exchange, string name) ParseFileName(string fileName)
    {
        // 去掉 .txt 后缀
        string baseName = fileName.Replace(".txt", "");
        var parts = baseName.Split('#');
        if (parts.Length < 2) return (null, "", "");

        string exchange = parts[0].ToUpper();
        string code = parts[1];

        // 验证代码格式
        if (code.Length != 6 || !code.All(char.IsDigit)) return (null, "", "");

        // 北京交易所暂时也导入，标记为BJ
        if (exchange != "SH" && exchange != "SZ" && exchange != "BJ")
            return (null, "", "");

        return (code, exchange, "");
    }

    /// <summary>
    /// 解析通达信导出的txt文件，返回K线数据和股票名称
    /// </summary>
    private (List<CandleStickData> candles, string name) ParseFile(string filePath)
    {
        var candles = new List<CandleStickData>();
        string stockName = "";

        // 通达信导出文件使用GBK编码
        var lines = File.ReadAllLines(filePath, Encoding.GetEncoding(936));

        // 第一行格式: "600519 贵州茅台 日K线 前复权"
        if (lines.Length > 0)
        {
            var titleFields = lines[0].Split(' ', (StringSplitOptions)1);
            if (titleFields.Length >= 2) stockName = titleFields[1];
        }

        // 跳过前2行（标题行和表头行）
        for (int i = 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // 制表符分隔: 日期 开盘 最高 最低 收盘 成交量 成交额
            var fields = line.Split('\t');
            if (fields.Length < 7) continue;

            try
            {
                // 解析日期: 2001/08/27
                if (!DateTime.TryParseExact(fields[0].Trim(), "yyyy/MM/dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
                    continue;

                // 解析价格（前复权可能有负数）
                if (!double.TryParse(fields[1].Trim(), out var open)) continue;
                if (!double.TryParse(fields[2].Trim(), out var high)) continue;
                if (!double.TryParse(fields[3].Trim(), out var low)) continue;
                if (!double.TryParse(fields[4].Trim(), out var close)) continue;
                if (!double.TryParse(fields[5].Trim(), out var volume)) continue;
                if (!double.TryParse(fields[6].Trim(), out var amount)) continue;

                double amplitude = open > 0 ? (high - low) / open * 100 : 0;

                candles.Add(new CandleStickData
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
            catch { }
        }

        return (candles, stockName);
    }
}
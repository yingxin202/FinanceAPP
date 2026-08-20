using Newtonsoft.Json;

namespace FinanceAPP.Models;

/// <summary>
/// 股票K线数据项
/// </summary>
public class CandleStickData
{
    public DateTime Date { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
    public double Amount { get; set; }      // 成交额
    public double Amplitude { get; set; }   // 振幅(%)
}

/// <summary>
/// 行情数据（通用返回结构）
/// </summary>
public class MarketChartData
{
    public string Symbol { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string ExchangeName { get; set; } = string.Empty;
    public List<CandleStickData> Candles { get; set; } = new();
    /// <summary>是否来自本地数据库缓存</summary>
    public bool FromCache { get; set; }
}

// ===== 东方财富 API JSON 响应模型 =====

public class EastmoneyResponse
{
    [JsonProperty("rc")]
    public int Rc { get; set; }

    [JsonProperty("rt")]
    public int Rt { get; set; }

    [JsonProperty("data")]
    public EastmoneyData? Data { get; set; }
}

public class EastmoneyData
{
    [JsonProperty("code")]
    public string Code { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("market")]
    public int Market { get; set; }

    [JsonProperty("klines")]
    public List<string> Klines { get; set; } = new();
}

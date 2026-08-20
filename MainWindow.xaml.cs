using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScottPlot;
using FinanceAPP.Models;
using FinanceAPP.Services;

namespace FinanceAPP;

public partial class MainWindow : Window
{
    private readonly DatabaseService _database = new();
    private readonly YahooFinanceService _yahooService;
    private readonly TdxService _tdxService;
    private readonly ScannerService _scannerService;
    private readonly TdxImportService _importService;
    private IMarketDataService _dataService;
    private MarketChartData? _lastData;
    private bool _syncing;
    private ScottPlot.Plottables.VerticalLine? _cursorLineV;
    private ScottPlot.Plottables.HorizontalLine? _cursorLineH;
    private CancellationTokenSource? _scanCts;
    private List<ScanResult> _allScanResults = new();
    private int _scanCurrentPage = 0;
    private const int ScanPageSize = 50;
    // 走势比对最后查询数据（用于切换显示模式时重绘）
    private double[]? _compareXs;
    private double[]? _compareYs1;
    private double[]? _compareYs2;
    private string[]? _compareDates;
    private string _compareSym1 = "";
    private string _compareSym2 = "";
    private double _compareBase1;
    private double _compareBase2;

    private static readonly Color BgColor = Color.FromHex("#181825");
    private static readonly Color AxisColor = Color.FromHex("#A6ADC8");
    private static readonly Color GridColor = Color.FromHex("#2A2B3C");
    private static readonly Color UpColor = Color.FromHex("#FF4D4F");
    private static readonly Color DownColor = Color.FromHex("#00CE76");

    // 自选列表持久化文件
    private static readonly string WatchlistPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "watchlist.json");

    // 比对配置持久化文件
    private static readonly string ComparePairsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "compare_pairs.json");

    // 界面设置持久化文件
    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        _yahooService = new YahooFinanceService(_database);
        _tdxService = new TdxService(_database);
        _scannerService = new ScannerService(_database);
        _importService = new TdxImportService(_database);
        _dataService = _tdxService;
        SetupAxisSync();
        PriceChart.MouseMove += PriceChart_MouseMove;
        PriceChart.MouseLeave += PriceChart_MouseLeave;
        PriceChart.PreviewMouseWheel += Chart_PreviewMouseWheel;
        VolumeChart.PreviewMouseWheel += Chart_PreviewMouseWheel;
        LoadSettings();
        LoadWatchlist();
        LoadComparePairs();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<UiSettings>(json);
            if (s == null) return;
            if (s.RangeIndex >= 0 && s.RangeIndex < RangeCombo.Items.Count)
                RangeCombo.SelectedIndex = s.RangeIndex;
            if (s.DataSourceIndex >= 0 && s.DataSourceIndex < DataSourceCombo.Items.Count)
                DataSourceCombo.SelectedIndex = s.DataSourceIndex;
            ShowMA.IsChecked = s.ShowMA;
            if (s.TrendIndex >= 0 && s.TrendIndex < TrendMethod.Items.Count)
                TrendMethod.SelectedIndex = s.TrendIndex;
            if (s.VolumeRowHeight > 30)
                VolumeRow.Height = new GridLength(s.VolumeRowHeight);
            if (s.CompareRangeIndex >= 0 && s.CompareRangeIndex < CompareRangeCombo.Items.Count)
                CompareRangeCombo.SelectedIndex = s.CompareRangeIndex;
            if (s.CompareDataSourceIndex >= 0 && s.CompareDataSourceIndex < CompareDataSourceCombo.Items.Count)
                CompareDataSourceCombo.SelectedIndex = s.CompareDataSourceIndex;
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var s = new UiSettings
            {
                RangeIndex = RangeCombo.SelectedIndex,
                DataSourceIndex = DataSourceCombo.SelectedIndex,
                ShowMA = ShowMA.IsChecked ?? true,
                TrendIndex = TrendMethod.SelectedIndex,
                VolumeRowHeight = VolumeRow.Height.Value,
                CompareRangeIndex = CompareRangeCombo.SelectedIndex,
                CompareDataSourceIndex = CompareDataSourceCombo.SelectedIndex
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    /// <summary>
    /// 同步价格图和成交量图的X轴（拖动/缩放联动）
    /// </summary>
    private void SetupAxisSync()
    {
        PriceChart.Plot.RenderManager.AxisLimitsChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            var limits = PriceChart.Plot.Axes.GetLimits();
            VolumeChart.Plot.Axes.SetLimitsX(limits.Left, limits.Right);
            VolumeChart.Refresh();
            _syncing = false;
        };

        VolumeChart.Plot.RenderManager.AxisLimitsChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            var limits = VolumeChart.Plot.Axes.GetLimits();
            PriceChart.Plot.Axes.SetLimitsX(limits.Left, limits.Right);
            PriceChart.Refresh();
            _syncing = false;
        };
    }

    /// <summary>
    /// 非对称放缩：X轴放缩幅度大于Y轴，便于观察K线整体走势
    /// </summary>
    private void Chart_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var chart = (ScottPlot.WPF.WpfPlot)sender;
        var plot = chart.Plot;
        var limits = plot.Axes.GetLimits();

        // 获取鼠标在数据坐标中的位置
        var pos = e.GetPosition(chart);
        double dpiScale = 1.0;
        var src = PresentationSource.FromVisual(chart);
        if (src != null)
            dpiScale = src.CompositionTarget.TransformToDevice.M11;
        var pixel = new ScottPlot.Pixel((float)(pos.X * dpiScale), (float)(pos.Y * dpiScale));
        var coords = plot.GetCoordinates(pixel);

        // 非对称放缩因子：X轴变化是Y轴的约3倍
        bool zoomIn = e.Delta > 0;
        double xFactor = zoomIn ? 0.83 : 1.20;   // X: ±17%~20%
        double yFactor = zoomIn ? 0.94 : 1.06;   // Y: ±6%

        double xRange = limits.Right - limits.Left;

        // 处理Y轴可能反转的情况（镜像翻转时 Top < Bottom）
        bool yInverted = limits.Top < limits.Bottom;
        double yMin = Math.Min(limits.Bottom, limits.Top);
        double yMax = Math.Max(limits.Bottom, limits.Top);
        double yRange = yMax - yMin;

        double newXRange = Math.Max(2, xRange * xFactor);
        double newYRange = Math.Max(0.0001, yRange * yFactor);

        // 以鼠标位置为中心放缩
        double leftRatio = xRange > 0 ? (coords.X - limits.Left) / xRange : 0.5;
        double bottomRatio = yRange > 0 ? (coords.Y - yMin) / yRange : 0.5;

        double newLeft = coords.X - leftRatio * newXRange;
        double newRight = newLeft + newXRange;
        double newBottom = coords.Y - bottomRatio * newYRange;
        double newTop = newBottom + newYRange;

        // 根据是否翻转设置Y轴方向
        if (yInverted)
            plot.Axes.SetLimits(newLeft, newRight, newTop, newBottom);
        else
            plot.Axes.SetLimits(newLeft, newRight, newBottom, newTop);

        chart.Refresh();

        e.Handled = true;
    }

    private async void SearchBtn_Click(object sender, RoutedEventArgs e) => await SearchSymbolAsync();

    private async void SymbolInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchSymbolAsync();
    }

    private async void QuickBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string symbol)
        {
            SymbolInput.Text = symbol;
            await SearchSymbolAsync();
        }
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastData != null) RenderChart(_lastData);
    }

    /// <summary>
    /// 起始日期变更时重新查询（如果有已查询的股票）
    /// </summary>
    private void StartDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 仅在DatePicker已初始化后触发
        if (!IsLoaded) return;
    }

    /// <summary>
    /// 根据起始日期计算自定义范围字符串 "custom:N"
    /// 交易日 ≈ 日历日 × 5/7 + 10天缓冲
    /// </summary>
    private static string GetRangeString(string rangeTag, DatePicker? datePicker)
    {
        if (datePicker?.SelectedDate is DateTime startDate)
        {
            int calendarDays = (DateTime.Today - startDate).Days;
            int tradingDays = (int)(calendarDays * 5.0 / 7.0) + 10;
            return $"custom:{Math.Max(5, tradingDays)}";
        }
        return rangeTag;
    }

    private async Task SearchSymbolAsync()
    {
        var symbol = SymbolInput.Text.Trim();
        if (string.IsNullOrEmpty(symbol)) { SetStatus("请输入股票/商品代码"); return; }

        var rangeTag = (RangeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "3mo";
        rangeTag = GetRangeString(rangeTag, StartDatePicker);

        // 根据下拉框选择数据源
        var sourceTag = (DataSourceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tdx";
        _dataService = sourceTag switch
        {
            "yahoo" => _yahooService,
            _ => _tdxService
        };

        SetLoading(true);
        var sourceName = sourceTag switch
        {
            "yahoo" => "Yahoo Finance",
            _ => "通达信"
        };
        SetStatus($"正在查询 {symbol} ({sourceName}) ...");

        try
        {
            var data = await _dataService.GetChartDataAsync(symbol, rangeTag, "1d",
                msg => SetStatus(msg));

            if (data.Candles.Count == 0) { SetStatus("未获取到数据，请检查代码或网络连接"); return; }

            _lastData = data;
            RenderChart(data);
            PlaceholderText.Visibility = Visibility.Collapsed;

            var first = data.Candles.First();
            var last = data.Candles.Last();
            var source = data.FromCache ? "📦 本地缓存" : "🌐 在线获取";
            SetStatus($"✅ {data.Symbol} | {data.ExchangeName} | 货币: {data.Currency} | {source}  |  左键拖动平移 | 滚轮缩放 | 双击重置");
            DataInfoText.Text = $"数据: {data.Candles.Count} 条 | {first.Date:yyyy-MM-dd} ~ {last.Date:yyyy-MM-dd} | 最新收盘: {last.Close:F2}";
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 查询失败: {ex.Message}");
            DataInfoText.Text = "";
        }
        finally { SetLoading(false); }
    }

    /// <summary>
    /// 渲染图表（蜡烛图+均线+DWT趋势+成交量）
    /// </summary>
    private void RenderChart(MarketChartData data)
    {
        bool hasVolume = data.Candles.Any(c => c.Volume > 0);
        bool showMA = ShowMA.IsChecked ?? true;
        string trendMethod = (TrendMethod.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        int n = data.Candles.Count;
        bool isLongRange = n > 250;
        string dateFormat = isLongRange ? "yyyy-MM" : "MM-dd";

        // 显示/隐藏成交量图
        VolumeRow.Height = new GridLength(hasVolume ? 100 : 0);
        VolumeChart.Visibility = hasVolume ? Visibility.Visible : Visibility.Collapsed;

        // ===== 价格图 =====
        var pPlot = PriceChart.Plot;
        pPlot.Clear();
        SetupPlotStyle(pPlot, $"{data.Symbol}  K线图  |  {data.ExchangeName}  |  {data.Currency}");

        // 蜡烛图（Sequential=true 使用连续索引，消除节假日空缺）
        var ohlcs = new List<OHLC>();
        for (int i = 0; i < n; i++)
        {
            var c = data.Candles[i];
            ohlcs.Add(new OHLC(c.Open, c.High, c.Low, c.Close, c.Date, TimeSpan.FromDays(1)));
        }
        var candlePlot = pPlot.Add.Candlestick(ohlcs);
        candlePlot.Sequential = true;        // 连续排列，去除节假日空缺
        candlePlot.RisingColor = UpColor;    // 红涨
        candlePlot.FallingColor = DownColor;  // 绿跌

        // 均线
        if (showMA)
        {
            AddMA(pPlot, data, 5, Color.FromHex("#FFD700"), "MA5");
            AddMA(pPlot, data, 10, Color.FromHex("#FF6BA6"), "MA10");
            AddMA(pPlot, data, 20, Color.FromHex("#00BFFF"), "MA20");
            AddMA(pPlot, data, 60, Color.FromHex("#BA55D3"), "MA60");
        }

        // 趋势线（DWT / SG / DWT+SG）
        if (trendMethod != "none" && n >= 8)
        {
            AddTrendLine(pPlot, data, trendMethod);
        }

        SetDateTicks(pPlot, data, dateFormat);
        pPlot.Legend.IsVisible = true;
        pPlot.Legend.Alignment = Alignment.UpperRight;
        pPlot.Axes.AutoScale();

        // 镜像翻转：Y轴反向
        bool mirrorFlip = MirrorFlip.IsChecked == true;
        if (mirrorFlip)
        {
            var limits = pPlot.Axes.GetLimits();
            pPlot.Axes.SetLimitsY(limits.Top, limits.Bottom);
        }

        // 十字光标线（鼠标悬停时显示）
        _cursorLineV = pPlot.Add.VerticalLine(0);
        _cursorLineV.Color = Color.FromHex("#89B4FA");
        _cursorLineV.LineWidth = 1;
        _cursorLineV.IsVisible = false;

        _cursorLineH = pPlot.Add.HorizontalLine(0);
        _cursorLineH.Color = Color.FromHex("#89B4FA");
        _cursorLineH.LineWidth = 1;
        _cursorLineH.IsVisible = false;

        // ===== 成交量图 =====
        if (hasVolume)
        {
            var vPlot = VolumeChart.Plot;
            vPlot.Clear();
            SetupPlotStyle(vPlot, null);

            var bars = new List<Bar>();
            for (int i = 0; i < n; i++)
            {
                var c = data.Candles[i];
                bars.Add(new Bar
                {
                    Position = i,
                    Value = c.Volume,
                    FillColor = c.Close >= c.Open ? UpColor : DownColor
                });
            }
            vPlot.Add.Bars(bars);
            vPlot.YLabel("成交量");

            SetDateTicks(vPlot, data, dateFormat);
            vPlot.Axes.AutoScale();

            // 镜像翻转：成交量图也Y轴反向
            if (mirrorFlip)
            {
                var vLimits = vPlot.Axes.GetLimits();
                vPlot.Axes.SetLimitsY(vLimits.Top, vLimits.Bottom);
            }

            VolumeChart.Refresh();
        }

        PriceChart.Refresh();
    }

    /// <summary>
    /// 设置图表暗色主题样式
    /// </summary>
    private void SetupPlotStyle(Plot plot, string? title)
    {
        plot.FigureBackground.Color = BgColor;
        plot.DataBackground.Color = BgColor;
        plot.Axes.Color(AxisColor);           // 方法调用，不是属性赋值
        plot.Grid.MajorLineColor = GridColor;
        plot.Grid.MinorLineColor = Color.FromHex("#1E1E2E");

        if (title != null)
        {
            plot.Title(title);
        }
    }

    /// <summary>
    /// 设置日期刻度（X轴显示交易日日期，无节假日空缺）
    /// </summary>
    private void SetDateTicks(Plot plot, MarketChartData data, string dateFormat)
    {
        int n = data.Candles.Count;
        int tickCount = Math.Min(12, n);
        int step = Math.Max(1, n / tickCount);
        var positions = new List<double>();
        var labels = new List<string>();
        for (int i = 0; i < n; i += step)
        {
            positions.Add(i);
            labels.Add(data.Candles[i].Date.ToString(dateFormat));
        }
        plot.Axes.Bottom.SetTicks(positions.ToArray(), labels.ToArray());
    }

    /// <summary>
    /// 添加移动平均线
    /// </summary>
    private void AddMA(Plot plot, MarketChartData data, int period, Color color, string label)
    {
        if (data.Candles.Count < period) return;

        var xs = new List<double>();
        var ys = new List<double>();
        for (int i = period - 1; i < data.Candles.Count; i++)
        {
            double sum = 0;
            for (int j = 0; j < period; j++)
                sum += data.Candles[i - j].Close;
            xs.Add(i);
            ys.Add(sum / period);
        }
        var sp = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
        sp.LineColor = color;
        sp.LineWidth = 1.3f;
        sp.MarkerSize = 0;
        sp.LegendText = label;
    }

    /// <summary>
    /// 鼠标悬停在蜡烛图上时显示当日详细信息
    /// </summary>
    private void PriceChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_lastData == null || _lastData.Candles.Count == 0)
        {
            TooltipBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var pos = e.GetPosition(PriceChart);

        // WPF的GetPosition返回DIP（设备无关像素），ScottPlot的DataRect是实际像素
        // 需要乘以DPI缩放因子转换为实际像素
        double dpiScale = 1.0;
        var source = PresentationSource.FromVisual(PriceChart);
        if (source != null)
            dpiScale = source.CompositionTarget.TransformToDevice.M11;

        var pixel = new ScottPlot.Pixel((float)(pos.X * dpiScale), (float)(pos.Y * dpiScale));

        // 检查鼠标是否在绘图区域内
        var dataRect = PriceChart.Plot.RenderManager.LastRender.DataRect;
        if (dataRect.Width <= 0 || pixel.X < dataRect.Left || pixel.X > dataRect.Right)
        {
            HideTooltip();
            return;
        }

        var coords = PriceChart.Plot.GetCoordinates(pixel);

        // Clamp索引到有效范围（处理轴边距区域）
        int index = Math.Clamp((int)Math.Round(coords.X), 0, _lastData.Candles.Count - 1);
        var c = _lastData.Candles[index];

        // 更新十字光标线
        if (_cursorLineV != null && _cursorLineH != null)
        {
            _cursorLineV.X = index;
            _cursorLineV.IsVisible = true;
            _cursorLineH.Y = coords.Y;
            _cursorLineH.IsVisible = true;
            PriceChart.Refresh();
        }

        // 涨跌幅 = (今收 - 昨收) / 昨收 * 100，首日用开盘价作参考
        double prevClose = index > 0 ? _lastData.Candles[index - 1].Close : c.Open;
        double change = c.Close - prevClose;
        double changePct = prevClose != 0 ? change / prevClose * 100 : 0;
        string arrow = change >= 0 ? "▲" : "▼";
        string changeStr = $"{(change >= 0 ? "+" : "")}{change:F2}";
        string pctStr = $"{(changePct >= 0 ? "+" : "")}{changePct:F2}%";

        // 计算各均线值及增长率
        string maText = CalcMALine(_lastData, index);

        TooltipText.Text =
            $"{c.Date:yyyy-MM-dd}\n" +
            $"开 {c.Open:F2}    收 {c.Close:F2}\n" +
            $"高 {c.High:F2}    低 {c.Low:F2}\n" +
            $"{arrow} {changeStr}  ({pctStr})\n" +
            $"振幅 {c.Amplitude:F2}%\n" +
            $"量 {Fmt(c.Volume)}    额 {Fmt(c.Amount)}" +
            (maText.Length > 0 ? "\n" + maText : "");

        TooltipBorder.Visibility = Visibility.Visible;

        // 定位提示框（避免超出边界）
        double offsetX = pos.X + 15;
        double offsetTop = pos.Y + 15;
        if (offsetX + 200 > PriceChart.ActualWidth)
            offsetX = pos.X - 210;
        if (offsetTop + 160 > PriceChart.ActualHeight)
            offsetTop = pos.Y - 170;

        TooltipBorder.Margin = new Thickness(offsetX, offsetTop, 0, 0);
    }

    /// <summary>
    /// 鼠标离开图表时隐藏提示框和光标线
    /// </summary>
    private void PriceChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HideTooltip();
    }

    private void HideTooltip()
    {
        TooltipBorder.Visibility = Visibility.Collapsed;
        if (_cursorLineV != null) _cursorLineV.IsVisible = false;
        if (_cursorLineH != null) _cursorLineH.IsVisible = false;
        PriceChart.Refresh();
    }

    /// <summary>
    /// 格式化成交量/成交额（万、亿）
    /// </summary>
    private static string Fmt(double val)
    {
        if (val >= 100_000_000) return $"{val / 100_000_000:F2}亿";
        if (val >= 10_000) return $"{val / 10_000:F2}万";
        return val.ToString("F0");
    }

    /// <summary>
    /// 计算MA5/10/20/60的当前值及相对前一日的增长率
    /// </summary>
    private static string CalcMALine(MarketChartData data, int index)
    {
        int[] periods = { 5, 10, 20, 60 };
        var parts = new List<string>();

        foreach (int p in periods)
        {
            if (index < p - 1) continue; // 数据不足

            double sum = 0;
            for (int j = 0; j < p; j++)
                sum += data.Candles[index - j].Close;
            double ma = sum / p;

            // 前一日MA
            double prevMa = 0;
            if (index >= p)
            {
                double ps = 0;
                for (int j = 0; j < p; j++)
                    ps += data.Candles[index - 1 - j].Close;
                prevMa = ps / p;
            }

            double rate = prevMa > 0 ? (ma - prevMa) / prevMa * 100 : 0;
            string a = rate >= 0 ? "▲" : "▼";
            parts.Add($"MA{p} {ma:F2} {a}{Math.Abs(rate):F2}%");
        }

        // 两两一行排列
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i += 2)
        {
            if (i + 1 < parts.Count)
                sb.AppendLine($"{parts[i]}   {parts[i + 1]}");
            else
                sb.AppendLine(parts[i]);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 添加趋势线（支持Raw、DWT、SG、DWT+SG四种方法）
    /// </summary>
    private void AddTrendLine(Plot plot, MarketChartData data, string method)
    {
        var closes = data.Candles.Select(c => c.Close).ToArray();
        int n = closes.Length;
        double[] trend;
        string label;
        ScottPlot.Color lineColor;

        switch (method)
        {
            case "raw":
                trend = closes;
                label = "Raw收盘价";
                lineColor = Color.FromHex("#F9E2AF"); // 暖黄色，区分于其他趋势线
                break;
            case "dwt":
                int levels = WaveletService.AutoSelectLevels(n);
                trend = WaveletService.ExtractTrend(closes, levels);
                label = $"DWT趋势(db4,L{levels})";
                lineColor = Color.FromHex("#FFFFFF"); // 白色
                break;
            case "sg":
                int halfWin = Math.Min(10, n / 5);
                trend = WaveletService.SavitzkyGolaySmooth(closes, halfWin, 3);
                label = "SG平滑";
                lineColor = Color.FromHex("#FFFFFF"); // 白色
                break;
            case "combined":
            default:
                levels = WaveletService.AutoSelectLevels(n);
                trend = WaveletService.ExtractTrendCombined(closes, levels);
                label = $"DWT+SG(db4,L{levels})";
                lineColor = Color.FromHex("#FFFFFF"); // 白色
                break;
        }

        var xs = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
        var sp = plot.Add.Scatter(xs, trend);
        sp.LineColor = lineColor;
        sp.LineWidth = 1.8f;
        sp.MarkerSize = 0;
        sp.LegendText = label;
    }

    private void SetLoading(bool isLoading)
    {
        LoadingBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        SearchBtn.IsEnabled = !isLoading;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        SaveComparePairs();
        _database.Dispose();
        _yahooService.Dispose();
        _tdxService.Dispose();
        base.OnClosed(e);
    }

    // === 自选列表功能 ===

    private void LoadWatchlist()
    {
        try
        {
            if (File.Exists(WatchlistPath))
            {
                var json = File.ReadAllText(WatchlistPath);
                var items = JsonSerializer.Deserialize<List<WatchlistItem>>(json);
                if (items != null)
                {
                    WatchlistBox.ItemsSource = items;
                    return;
                }
            }
        }
        catch { }
        // 默认列表
        WatchlistBox.ItemsSource = new List<WatchlistItem>
        {
            new("600519", "贵州茅台"),
            new("000001", "平安银行"),
            new("300750", "宁德时代"),
        };
    }

    private void SaveWatchlist()
    {
        try
        {
            var items = WatchlistBox.ItemsSource as List<WatchlistItem>;
            if (items == null)
            {
                items = WatchlistBox.Items.OfType<WatchlistItem>().ToList();
            }
            var json = JsonSerializer.Serialize(items);
            File.WriteAllText(WatchlistPath, json);
        }
        catch { }
    }

    private async void AddWatchlistBtn_Click(object sender, RoutedEventArgs e)
    {
        var input = WatchlistInput.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        // 解析输入: "600519 贵州茅台" 或 "600519"
        string code, name;
        var spaceIdx = input.IndexOf(' ');
        if (spaceIdx > 0)
        {
            code = input[..spaceIdx].Trim();
            name = input[(spaceIdx + 1)..].Trim();
        }
        else
        {
            code = input;
            name = "";
        }

        // 获取当前列表
        var items = (WatchlistBox.ItemsSource as List<WatchlistItem>) ??
                    WatchlistBox.Items.OfType<WatchlistItem>().ToList();

        // 检查重复
        if (items.Any(x => x.Code == code))
        {
            SetStatus($"⚠️ {code} 已在列表中");
            return;
        }

        // 如果用户没提供名称，尝试查询股票名称
        if (string.IsNullOrEmpty(name))
        {
            SetStatus($"正在验证 {code} ...");
            var found = await _tdxService.SearchStockNameAsync(code);
            if (found == null)
            {
                SetStatus($"⚠️ 未找到 {code}，请检查代码是否正确");
                return;
            }
            name = found;
        }

        items.Add(new WatchlistItem(code, name));
        WatchlistBox.ItemsSource = null;
        WatchlistBox.ItemsSource = items;
        SaveWatchlist();
        WatchlistInput.Text = "";
        SetStatus($"✅ 已添加 {name} ({code})");
    }

    private void RemoveWatchlistBtn_Click(object sender, RoutedEventArgs e)
    {
        if (WatchlistBox.SelectedItem is not WatchlistItem item) return;

        var items = (WatchlistBox.ItemsSource as List<WatchlistItem>) ??
                    WatchlistBox.Items.OfType<WatchlistItem>().ToList();
        items.Remove(item);
        WatchlistBox.ItemsSource = null;
        WatchlistBox.ItemsSource = items;
        SaveWatchlist();
        SetStatus($"✅ 已删除 {item.Name} ({item.Code})");
    }

    private void WatchlistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WatchlistBox.SelectedItem is not WatchlistItem item) return;

        SymbolInput.Text = item.Code;
        _ = SearchSymbolAsync();
    }

    // === 比对配置功能 ===

    private void LoadComparePairs()
    {
        try
        {
            if (File.Exists(ComparePairsPath))
            {
                var json = File.ReadAllText(ComparePairsPath);
                var items = JsonSerializer.Deserialize<List<ComparePair>>(json);
                if (items != null)
                    ComparePairsBox.ItemsSource = items;
            }
        }
        catch { }
    }

    private void SaveComparePairs()
    {
        try
        {
            var items = ComparePairsBox.ItemsSource as List<ComparePair>
                        ?? ComparePairsBox.Items.OfType<ComparePair>().ToList();
            var json = JsonSerializer.Serialize(items);
            File.WriteAllText(ComparePairsPath, json);
        }
        catch { }
    }

    private void AddComparePairBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = ComparePairName.Text.Trim();
        var code1 = ComparePairCode1.Text.Trim();
        var code2 = ComparePairCode2.Text.Trim();

        if (string.IsNullOrEmpty(code1) || string.IsNullOrEmpty(code2))
        {
            CompareStatus.Text = "请输入两只股票代码";
            return;
        }

        if (string.IsNullOrEmpty(name))
            name = $"{code1} vs {code2}";

        var oldItems = (ComparePairsBox.ItemsSource as List<ComparePair>) ?? new List<ComparePair>();
        var items = new List<ComparePair>(oldItems) { new ComparePair(name, code1, code2) };
        ComparePairsBox.ItemsSource = items;

        ComparePairName.Text = "";
        ComparePairCode1.Text = "";
        ComparePairCode2.Text = "";
        SaveComparePairs();
        CompareStatus.Text = $"✅ 已添加比对配置: {name}";
    }

    private void RemoveComparePairBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ComparePairsBox.SelectedItem is not ComparePair item) return;

        var oldItems = (ComparePairsBox.ItemsSource as List<ComparePair>) ?? new List<ComparePair>();
        var items = new List<ComparePair>(oldItems);
        items.Remove(item);
        ComparePairsBox.ItemsSource = items;
        SaveComparePairs();
        CompareStatus.Text = $"✅ 已删除: {item.Name}";
    }

    private void ComparePairsBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ComparePairsBox.SelectedItem is not ComparePair item) return;

        CompareInput1.Text = item.Code1;
        CompareInput2.Text = item.Code2;
        _ = CompareSymbolsAsync();
    }

    private void WatchlistInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddWatchlistBtn_Click(sender, e);
        }
    }

    // ======================== 走势比对功能 ========================

    private async void CompareBtn_Click(object sender, RoutedEventArgs e) => await CompareSymbolsAsync();

    private void CompareInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CompareBtn_Click(sender, e);
    }

    private async Task CompareSymbolsAsync()
    {
        string symbol1 = CompareInput1.Text.Trim();
        string symbol2 = CompareInput2.Text.Trim();

        if (string.IsNullOrEmpty(symbol1) || string.IsNullOrEmpty(symbol2))
        {
            CompareStatus.Text = "请输入两只股票代码";
            return;
        }

        string rangeTag = (CompareRangeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "3mo";
        rangeTag = GetRangeString(rangeTag, CompareStartDatePicker);
        string sourceTag = (CompareDataSourceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tdx";

        IMarketDataService dataService = sourceTag switch
        {
            "yahoo" => _yahooService,
            _ => _tdxService
        };

        var sourceName = sourceTag switch
        {
            "yahoo" => "Yahoo Finance",
            _ => "通达信"
        };

        ComparePlaceholder.Visibility = Visibility.Collapsed;
        CompareLoading.Visibility = Visibility.Visible;
        CompareStatus.Text = $"正在获取 {symbol1} 和 {symbol2} ({sourceName}) ...";

        try
        {
            // 并行获取两只股票数据
            var task1 = dataService.GetChartDataAsync(symbol1, rangeTag, "1d",
                msg => Dispatcher.Invoke(() => CompareStatus.Text = $"{symbol1}: {msg}"));
            var task2 = dataService.GetChartDataAsync(symbol2, rangeTag, "1d",
                msg => Dispatcher.Invoke(() => CompareStatus.Text = $"{symbol2}: {msg}"));
            await Task.WhenAll(task1, task2);

            var data1 = task1.Result;
            var data2 = task2.Result;

            if (data1.Candles.Count == 0 || data2.Candles.Count == 0)
            {
                CompareStatus.Text = "未获取到数据，请检查股票代码";
                ComparePlaceholder.Visibility = Visibility.Visible;
                return;
            }

            RenderComparison(data1, data2, symbol1, symbol2);
            CompareStatus.Text = $"✅ {symbol1}: {data1.Candles.Count}条  {symbol2}: {data2.Candles.Count}条  " +
                                 $"({(data1.FromCache || data2.FromCache ? "含缓存" : "在线获取")})";
        }
        catch (Exception ex)
        {
            CompareStatus.Text = $"❌ {ex.Message.Split('\n')[0]}";
        }
        finally
        {
            CompareLoading.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 绘制两只股票的归一化走势比对图（百分比变化）
    /// </summary>
    private void RenderComparison(MarketChartData data1, MarketChartData data2, string sym1, string sym2)
    {
        var plot = CompareChart.Plot;
        plot.Clear();

        // 找到两只股票的共同日期
        var dates1 = data1.Candles.ToDictionary(c => c.Date.Date, c => c.Close);
        var dates2 = data2.Candles.ToDictionary(c => c.Date.Date, c => c.Close);
        var commonDates = dates1.Keys.Intersect(dates2.Keys).OrderBy(d => d).ToList();

        if (commonDates.Count < 2)
        {
            CompareStatus.Text = "两只股票没有足够的共同交易日";
            return;
        }

        // 归一化：以第一天收盘价为基准，计算每日涨跌幅百分比
        double base1 = dates1[commonDates[0]];
        double base2 = dates2[commonDates[0]];

        var xs = new double[commonDates.Count];
        var ys1 = new double[commonDates.Count];
        var ys2 = new double[commonDates.Count];
        var dateLabels = new string[commonDates.Count];

        for (int i = 0; i < commonDates.Count; i++)
        {
            xs[i] = i;
            ys1[i] = (dates1[commonDates[i]] - base1) / base1 * 100;
            ys2[i] = (dates2[commonDates[i]] - base2) / base2 * 100;
            dateLabels[i] = commonDates[i].ToString("MM-dd");
        }

        // 保存数据供模式切换时重绘
        _compareXs = xs;
        _compareYs1 = ys1;
        _compareYs2 = ys2;
        _compareDates = dateLabels;
        _compareSym1 = sym1;
        _compareSym2 = sym2;
        _compareBase1 = base1;
        _compareBase2 = base2;

        DrawComparisonPlot();
    }

    /// <summary>
    /// 根据当前显示模式绘制比对图
    /// </summary>
    private void DrawComparisonPlot()
    {
        if (_compareXs == null || _compareYs1 == null || _compareYs2 == null || _compareDates == null) return;

        var plot = CompareChart.Plot;
        plot.Clear();

        var modeTag = (CompareMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "line";
        bool isDwt = modeTag == "dwt";

        double[] drawYs1, drawYs2;
        string modeLabel;

        if (isDwt)
        {
            int levels = int.Parse((DwtLevelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "5");
            drawYs1 = WaveletService.ExtractTrend(_compareYs1, levels);
            drawYs2 = WaveletService.ExtractTrend(_compareYs2, levels);
            modeLabel = $"DWT(L{levels})";
        }
        else
        {
            drawYs1 = _compareYs1;
            drawYs2 = _compareYs2;
            modeLabel = "折线";
        }

        // 绘制两条线
        var sp1 = plot.Add.Scatter(_compareXs, drawYs1);
        sp1.LineColor = Color.FromHex("#89B4FA");
        sp1.LineWidth = 1.5f;
        sp1.MarkerSize = 0;
        sp1.LegendText = $"{_compareSym1} ({_compareBase1:F2})";

        var sp2 = plot.Add.Scatter(_compareXs, drawYs2);
        sp2.LineColor = Color.FromHex("#FAB387");
        sp2.LineWidth = 1.5f;
        sp2.MarkerSize = 0;
        sp2.LegendText = $"{_compareSym2} ({_compareBase2:F2})";

        // 零线
        var zeroLine = plot.Add.HorizontalLine(0);
        zeroLine.Color = Color.FromHex("#45475A");
        zeroLine.LineWidth = 1;

        // 样式
        plot.FigureBackground.Color = BgColor;
        plot.DataBackground.Color = BgColor;
        plot.Axes.Color(AxisColor);
        plot.Grid.MajorLineColor = GridColor;
        plot.Grid.MinorLineColor = Color.FromHex("#1E1E2E");
        plot.Title($"走势比对: {_compareSym1} vs {_compareSym2} [{modeLabel}] (基准日: {_compareDates[0]})");
        plot.YLabel("涨跌幅 (%)");
        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperLeft;

        // X轴日期标签
        int tickStep = Math.Max(1, _compareDates.Length / 8);
        var positions = new List<double>();
        var labels = new List<string>();
        for (int i = 0; i < _compareDates.Length; i += tickStep)
        {
            positions.Add(i);
            labels.Add(_compareDates[i]);
        }
        plot.Axes.Bottom.SetTicks(positions.ToArray(), labels.ToArray());

        plot.Axes.AutoScale();
        CompareChart.Refresh();
    }

    /// <summary>
    /// 比对图显示模式切换
    /// </summary>
    private void CompareMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DwtLevelLabel == null || DwtLevelCombo == null || CompareMode == null) return;

        // 控制DWT参数可见性
        bool isDwt = (CompareMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "dwt";
        DwtLevelLabel.Visibility = isDwt ? Visibility.Visible : Visibility.Collapsed;
        DwtLevelCombo.Visibility = isDwt ? Visibility.Visible : Visibility.Collapsed;

        // 如果有数据则重绘
        if (_compareXs != null)
            DrawComparisonPlot();
    }

    // ==================== 量价扫描 ====================

    private async void ScanStartBtn_Click(object sender, RoutedEventArgs e)
    {
        // 解析参数
        DateTime targetDate = ScanDatePicker.SelectedDate ?? DateTime.Today;
        if (!double.TryParse(ScanVolRatioInput.Text, out double volRatio) || volRatio < 1.0)
        {
            ScanProgressText.Text = "⚠️ 量比阈值需≥1.0";
            return;
        }
        if (!double.TryParse(ScanPriceChgInput.Text, out double priceChg))
        {
            ScanProgressText.Text = "⚠️ 涨幅阈值格式错误";
            return;
        }

        // UI状态
        ScanStartBtn.IsEnabled = false;
        ScanCancelBtn.IsEnabled = true;
        ScanProgressBar.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = true;
        ScanPlaceholder.Visibility = Visibility.Visible;
        ScanPlaceholder.Text = "正在获取A股列表...";
        ScanResultsGrid.ItemsSource = null;
        ScanProgressText.Text = "";

        _scanCts = new CancellationTokenSource();

        try
        {
            var results = await _scannerService.ScanAsync(
                targetDate, volRatio, priceChg,
                (scanned, total, matched) => Dispatcher.Invoke(() =>
                {
                    ScanProgressText.Text = $"已扫描 {scanned}/{total} | 符合条件: {matched}";
                    ScanPlaceholder.Text = $"正在扫描... {scanned}/{total}";
                }),
                _scanCts.Token);

            // 显示结果
            _allScanResults = results;
            _scanCurrentPage = 0;
            ShowScanPage();

            ScanPlaceholder.Visibility = results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            ScanPlaceholder.Text = results.Count > 0 ? "" : "未找到符合条件的股票";
            ScanProgressText.Text = $"扫描完成: {results.Count} 只股票符合条件";
        }
        catch (OperationCanceledException)
        {
            ScanProgressText.Text = "扫描已取消";
            ScanPlaceholder.Text = "扫描已取消";
        }
        catch (Exception ex)
        {
            ScanProgressText.Text = $"❌ 扫描失败: {ex.Message}";
            ScanPlaceholder.Text = $"扫描失败: {ex.Message}";
        }
        finally
        {
            ScanStartBtn.IsEnabled = true;
            ScanCancelBtn.IsEnabled = false;
            ScanProgressBar.Visibility = Visibility.Collapsed;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private void ScanCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        ScanProgressText.Text = "正在取消...";
    }

    private async void ScanResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ScanResultsGrid.SelectedItem is not ScanResult result) return;

        // 切换到K线图Tab
        MainTabControl.SelectedIndex = 0;

        // 填入代码并查询
        SymbolInput.Text = result.Code;
        await SearchSymbolAsync();
    }

    // ==================== 通达信数据导入 ====================

    private async void ImportTdxBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择通达信导出数据文件夹",
            InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TDXExport")
        };

        if (dialog.ShowDialog() != true) return;

        string folder = dialog.FolderName;
        var files = Directory.GetFiles(folder, "*.txt");
        if (files.Length == 0)
        {
            ScanProgressText.Text = "⚠️ 文件夹中没有找到txt文件";
            return;
        }

        // 确认导入
        var result = MessageBox.Show(
            $"找到 {files.Length} 个文件，是否开始导入？\n这可能会覆盖数据库中已有的同名数据。",
            "确认导入", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        // UI状态
        ImportTdxBtn.IsEnabled = false;
        ScanStartBtn.IsEnabled = false;
        ScanProgressBar.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Value = 0;
        ScanProgressBar.Maximum = files.Length;
        ScanPlaceholder.Visibility = Visibility.Visible;
        ScanPlaceholder.Text = "正在导入...";
        ScanProgressText.Text = "";

        _scanCts = new CancellationTokenSource();

        try
        {
            var (imported, failed, total) = await _importService.ImportFolderAsync(
                folder,
                (done, total2, file) => Dispatcher.Invoke(() =>
                {
                    ScanProgressBar.Value = done;
                    ScanProgressText.Text = $"导入中 {done}/{total2}: {file}";
                    ScanPlaceholder.Text = $"正在导入... {done}/{total2}";
                }),
                _scanCts.Token);

            ScanProgressBar.Value = files.Length;
            ScanPlaceholder.Visibility = Visibility.Collapsed;
            ScanProgressText.Text = $"✅ 导入完成: 成功 {imported} 只, 失败 {failed} 只, 共 {total} 只";

            MessageBox.Show(
                $"导入完成！\n成功: {imported} 只\n失败: {failed} 只\n总计: {total} 只",
                "导入结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ScanProgressText.Text = "导入已取消";
            ScanPlaceholder.Text = "导入已取消";
        }
        catch (Exception ex)
        {
            ScanProgressText.Text = $"❌ 导入失败: {ex.Message}";
        }
        finally
        {
            ImportTdxBtn.IsEnabled = true;
            ScanStartBtn.IsEnabled = true;
            ScanProgressBar.Visibility = Visibility.Collapsed;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    // ==================== 分页 ====================

    private void ShowScanPage()
    {
        int totalPages = (_allScanResults.Count + ScanPageSize - 1) / ScanPageSize;
        if (totalPages == 0) totalPages = 1;

        if (_scanCurrentPage >= totalPages) _scanCurrentPage = totalPages - 1;
        if (_scanCurrentPage < 0) _scanCurrentPage = 0;

        var pageData = _allScanResults
            .Skip(_scanCurrentPage * ScanPageSize)
            .Take(ScanPageSize)
            .ToList();

        ScanResultsGrid.ItemsSource = pageData;
        ScanPageInfo.Text = $"{_scanCurrentPage + 1} / {totalPages}";

        // 按钮状态
        ScanPrevPageBtn.IsEnabled = _scanCurrentPage > 0;
        ScanNextPageBtn.IsEnabled = _scanCurrentPage < totalPages - 1;
        ScanPagerPanel.Visibility = _allScanResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScanPrevPageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCurrentPage > 0)
        {
            _scanCurrentPage--;
            ShowScanPage();
        }
    }

    private void ScanNextPageBtn_Click(object sender, RoutedEventArgs e)
    {
        int totalPages = (_allScanResults.Count + ScanPageSize - 1) / ScanPageSize;
        if (_scanCurrentPage < totalPages - 1)
        {
            _scanCurrentPage++;
            ShowScanPage();
        }
    }
}

/// <summary>
/// 自选列表项
/// </summary>
public record WatchlistItem(string Code, string Name);

/// <summary>
/// 比对配置项
/// </summary>
public record ComparePair(string Name, string Code1, string Code2);

/// <summary>
/// 界面设置（持久化）
/// </summary>
public class UiSettings
{
    public int RangeIndex { get; set; }
    public int DataSourceIndex { get; set; }
    public bool ShowMA { get; set; } = true;
    public int TrendIndex { get; set; }
    public double VolumeRowHeight { get; set; } = 120;
    public int CompareRangeIndex { get; set; }
    public int CompareDataSourceIndex { get; set; }
}

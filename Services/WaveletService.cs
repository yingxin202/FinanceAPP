namespace FinanceAPP.Services;

/// <summary>
/// 趋势线分析服务
/// 使用 db4 (Daubechies-4) 小波变换提取价格趋势
/// </summary>
public static class WaveletService
{
    // 滤波器系数（与 PyWavelets db4 完全一致）
    private static readonly double[] Db4DecLo = {
       -0.0105974017857068,  0.0328830116666785,  0.0308413818359870, -0.1870348117188813,
       -0.0279837694166831,  0.6308807679294803,  0.7148465705525415,  0.2303778133086964
    };

    private static readonly double[] Db4DecHi = {
       -0.2303778133086964,  0.7148465705525415, -0.6308807679294803, -0.0279837694166831,
        0.1870348117188813,  0.0308413818359870, -0.0328830116666785, -0.0105974017857068
    };

    private static readonly double[] Db4RecLo = {
        0.2303778133086964,  0.7148465705525415,  0.6308807679294803, -0.0279837694166831,
       -0.1870348117188813,  0.0308413818359870,  0.0328830116666785, -0.0105974017857068
    };

    private static readonly double[] Db4RecHi = {
       -0.0105974017857068, -0.0328830116666785,  0.0308413818359870,  0.1870348117188813,
       -0.0279837694166831, -0.6308807679294803,  0.7148465705525415, -0.2303778133086964
    };

    private const int FilterLen = 8;

    // ================================================================
    //  DWT 趋势提取
    // ================================================================

    public static double[] ExtractTrend(double[] prices, int levels = 5)
    {
        int n = prices.Length;
        if (n < FilterLen) return (double[])prices.Clone();

        // 补齐到2的幂次方，确保每层分解长度都是偶数
        int paddedLen = NextPow2(n);
        double[] padded = new double[paddedLen];
        Array.Copy(prices, padded, n);
        // 用最后几个值填充，减少边界突变
        double lastVal = prices[n - 1];
        for (int i = n; i < paddedLen; i++)
            padded[i] = lastVal;

        levels = Math.Min(levels, MaxLevel(paddedLen));

        var (approx, details, sizes) = Wavedec(padded, levels);

        // 置零所有细节系数
        for (int i = 0; i < details.Length; i++)
            Array.Clear(details[i], 0, details[i].Length);

        var trendPadded = Waverec(approx, details, sizes, paddedLen);

        // 截取原始长度
        var trend = new double[n];
        Array.Copy(trendPadded, trend, n);
        return trend;
    }

    public static double[] ExtractTrendCombined(double[] prices, int levels = 5)
    {
        var dwtTrend = ExtractTrend(prices, levels);
        int halfWindow = Math.Min(10, prices.Length / 5);
        return SavitzkyGolaySmooth(dwtTrend, halfWindow, 3);
    }

    // ================================================================
    //  db4 小波变换核心（周期延拓，仅用于2的幂次方长度）
    // ================================================================

    private static (double[] approx, double[][] details, int[] sizes) Wavedec(double[] signal, int level)
    {
        var sizes = new int[level + 1];
        sizes[0] = signal.Length;

        var details = new double[level][];
        var current = (double[])signal.Clone();

        for (int lv = 0; lv < level; lv++)
        {
            int n = current.Length;
            int half = n / 2;  // 2的幂次方保证偶数
            var approx = new double[half];
            var detail = new double[half];

            for (int j = 0; j < half; j++)
            {
                double a = 0, d = 0;
                for (int k = 0; k < FilterLen; k++)
                {
                    // 标准卷积: (2j - k + L/2) mod n
                    int idx = ((2 * j - k + FilterLen / 2) % n + n) % n;
                    a += current[idx] * Db4DecLo[k];
                    d += current[idx] * Db4DecHi[k];
                }
                approx[j] = a;
                detail[j] = d;
            }

            details[level - 1 - lv] = detail;
            sizes[lv + 1] = half;
            current = approx;
        }

        return (current, details, sizes);
    }

    private static double[] Waverec(double[] approx, double[][] details, int[] sizes, int outputLength)
    {
        var current = approx;
        int level = details.Length;

        for (int lv = 0; lv < level; lv++)
        {
            int targetLen = sizes[level - 1 - lv];
            current = IdwtLevel(current, details[lv], targetLen);
        }

        var result = new double[outputLength];
        Array.Copy(current, result, Math.Min(outputLength, current.Length));
        return result;
    }

    private static double[] IdwtLevel(double[] approx, double[] detail, int outputLength)
    {
        int m = approx.Length;
        int upLen = 2 * m;
        var signal = new double[outputLength];

        for (int j = 0; j < outputLength; j++)
        {
            double s = 0;
            for (int k = 0; k < FilterLen; k++)
            {
                // 重构: (i - k + L/2 - 1) mod upLen
                int idx = ((j - k + FilterLen / 2 - 1) % upLen + upLen) % upLen;
                if (idx % 2 == 0)
                {
                    int coeffIdx = idx / 2;
                    s += approx[coeffIdx] * Db4RecLo[k];
                    s += detail[coeffIdx] * Db4RecHi[k];
                }
            }
            signal[j] = s;
        }

        return signal;
    }

    private static int MaxLevel(int signalLength)
    {
        int level = 0;
        int len = signalLength;
        while (len >= FilterLen)
        {
            len /= 2;
            level++;
        }
        return Math.Max(1, level);
    }

    private static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    // ================================================================
    //  Savitzky-Golay 多项式滤波
    // ================================================================

    public static double[] SavitzkyGolaySmooth(double[] data, int halfWindow = 8, int polyOrder = 3)
    {
        int n = data.Length;
        if (n < 2 * halfWindow + 1)
            halfWindow = Math.Max(2, n / 4);

        double[] result = new double[n];

        for (int i = 0; i < n; i++)
        {
            var xs = new double[2 * halfWindow + 1];
            var ys = new double[2 * halfWindow + 1];

            for (int j = -halfWindow; j <= halfWindow; j++)
            {
                int idx = Math.Clamp(i + j, 0, n - 1);
                xs[j + halfWindow] = j;
                ys[j + halfWindow] = data[idx];
            }

            result[i] = PolyFitAtCenter(xs, ys, polyOrder);
        }

        return result;
    }

    // ================================================================
    //  辅助方法
    // ================================================================

    private static double PolyFitAtCenter(double[] xs, double[] ys, int degree)
    {
        int m = degree + 1;
        int n = xs.Length;

        double[,] AtA = new double[m, m];
        double[] Aty = new double[m];

        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++)
                    sum += Math.Pow(xs[k], i + j);
                AtA[i, j] = sum;
            }
            double sumY = 0;
            for (int k = 0; k < n; k++)
                sumY += ys[k] * Math.Pow(xs[k], i);
            Aty[i] = sumY;
        }

        double[] coeffs = SolveLinearSystem(AtA, Aty, m);
        return coeffs[0];
    }

    private static double[] SolveLinearSystem(double[,] A, double[] b, int n)
    {
        double[,] aug = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                aug[i, j] = A[i, j];
            aug[i, n] = b[i];
        }

        for (int col = 0; col < n; col++)
        {
            int maxRow = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(aug[row, col]) > Math.Abs(aug[maxRow, col]))
                    maxRow = row;

            if (maxRow != col)
                for (int j = 0; j <= n; j++)
                    (aug[col, j], aug[maxRow, j]) = (aug[maxRow, j], aug[col, j]);

            for (int row = col + 1; row < n; row++)
            {
                double factor = aug[row, col] / aug[col, col];
                for (int j = col; j <= n; j++)
                    aug[row, j] -= factor * aug[col, j];
            }
        }

        double[] x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = aug[i, n];
            for (int j = i + 1; j < n; j++)
                sum -= aug[i, j] * x[j];
            x[i] = sum / aug[i, i];
        }

        return x;
    }

    public static int AutoSelectLevels(int dataLength)
    {
        return dataLength switch
        {
            < 16 => 1,
            < 32 => 2,
            < 64 => 3,
            < 128 => 4,
            < 256 => 5,
            < 512 => 6,
            _ => 7
        };
    }
}
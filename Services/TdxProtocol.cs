using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;

namespace FinanceAPP.Services;

/// <summary>
/// 通达信TDX二进制协议底层实现
/// 基于tdxpy协议，直连通达信行情服务器获取K线数据
/// </summary>
internal static class TdxProtocol
{
    /// <summary>响应头长度（16字节: uint32+uint32+uint32+uint16+uint16）</summary>
    public const int RSP_HEADER_LEN = 0x10;

    /// <summary>K线类别</summary>
    public const ushort CatDaily = 4;
    public const ushort CatWeekly = 5;
    public const ushort CatMonthly = 6;

    /// <summary>市场代码</summary>
    public const ushort MarketSZ = 0;       // 深圳
    public const ushort MarketSH = 1;       // 上海
    public const ushort MarketDCE = 29;     // 大连商品期货
    public const ushort MarketSHFE = 30;    // 上海期货
    public const ushort MarketCFFEX = 47;   // 中国金融期货
    public const ushort MarketCZCE = 48;    // 郑州商品期货
    public const ushort MarketHK = 71;      // 香港

    /// <summary>单次最大获取条数</summary>
    public const ushort MaxCount = 800;

    /// <summary>
    /// 通达信行情服务器列表（IP, 端口）
    /// 来自mootdx云端服务器列表
    /// </summary>
    public static readonly (string ip, int port)[] Servers =
    {
        ("180.153.18.172", 80),     // 上海(唯一验证可用的服务器, mootdx BESTIP)
    };

    /// <summary>
    /// 连接设置命令（3条，依次发送，每条发送后读取响应）
    /// </summary>
    public static readonly byte[][] SetupCommands =
    {
        new byte[] { 0x0c, 0x02, 0x18, 0x93, 0x00, 0x01, 0x03, 0x00, 0x03, 0x00, 0x0d, 0x00, 0x01 },
        new byte[] { 0x0c, 0x02, 0x18, 0x94, 0x00, 0x01, 0x03, 0x00, 0x03, 0x00, 0x0d, 0x00, 0x02 },
        new byte[] {
            0x0c, 0x03, 0x18, 0x99, 0x00, 0x01, 0x20, 0x00, 0x20, 0x00, 0xdb, 0x0f, 0xd5, 0xd0,
            0xc9, 0xcc, 0xd6, 0xa4, 0xa8, 0xaf, 0x00, 0x00, 0x00, 0x8f, 0xc2, 0x25, 0x40, 0x13,
            0x00, 0x00, 0xd5, 0x00, 0xc9, 0xcc, 0xbd, 0xf0, 0xd7, 0xea, 0x00, 0x00, 0x00, 0x02
        },
    };

    /// <summary>
    /// 构建K线请求包（38字节）
    /// 对应Python: struct.pack("<HIHHHH6sHHHHIIH", *values)
    /// </summary>
    public static byte[] BuildGetBarsPacket(ushort category, ushort market, string code, ushort start, ushort count)
    {
        byte[] packet = new byte[38];
        int offset = 0;
        WriteUShortLE(packet, offset, 0x010C); offset += 2;
        WriteUInt32LE(packet, offset, 0x01016408); offset += 4;
        WriteUShortLE(packet, offset, 0x001C); offset += 2;
        WriteUShortLE(packet, offset, 0x001C); offset += 2;
        WriteUShortLE(packet, offset, 0x052D); offset += 2;
        WriteUShortLE(packet, offset, market); offset += 2;
        byte[] codeBytes = new byte[6];
        byte[] ascii = Encoding.ASCII.GetBytes(code);
        Array.Copy(ascii, 0, codeBytes, 0, Math.Min(6, ascii.Length));
        Array.Copy(codeBytes, 0, packet, offset, 6);
        offset += 6;
        WriteUShortLE(packet, offset, category); offset += 2;
        WriteUShortLE(packet, offset, 1); offset += 2;
        WriteUShortLE(packet, offset, start); offset += 2;
        WriteUShortLE(packet, offset, count); offset += 2;
        WriteUInt32LE(packet, offset, 0); offset += 4;
        WriteUInt32LE(packet, offset, 0); offset += 4;
        WriteUShortLE(packet, offset, 0);
        return packet;
    }

    /// <summary>
    /// 构建指数K线请求包（38字节）
    /// cmd=0x010D, 格式与股票K线请求相同
    /// </summary>
    public static byte[] BuildGetIndexBarsPacket(ushort category, ushort market, string code, ushort start, ushort count)
    {
        byte[] packet = new byte[38];
        int offset = 0;
        WriteUShortLE(packet, offset, 0x010D); offset += 2;  // cmd=0x010D for index bars
        WriteUInt32LE(packet, offset, 0x01016408); offset += 4;
        WriteUShortLE(packet, offset, 0x001C); offset += 2;
        WriteUShortLE(packet, offset, 0x001C); offset += 2;
        WriteUShortLE(packet, offset, 0x052D); offset += 2;
        WriteUShortLE(packet, offset, market); offset += 2;
        byte[] codeBytes = new byte[6];
        byte[] ascii = Encoding.ASCII.GetBytes(code);
        Array.Copy(ascii, 0, codeBytes, 0, Math.Min(6, ascii.Length));
        Array.Copy(codeBytes, 0, packet, offset, 6);
        offset += 6;
        WriteUShortLE(packet, offset, category); offset += 2;
        WriteUShortLE(packet, offset, 1); offset += 2;
        WriteUShortLE(packet, offset, start); offset += 2;
        WriteUShortLE(packet, offset, count); offset += 2;
        WriteUInt32LE(packet, offset, 0); offset += 4;
        WriteUInt32LE(packet, offset, 0); offset += 4;
        WriteUShortLE(packet, offset, 0);
        return packet;
    }

    /// <summary>
    /// 读取Setup响应（16字节头+body，内容忽略）
    /// </summary>
    public static void ReadSetupResponse(NetworkStream stream, int timeoutMs = 5000)
    {
        var (zipSize, _) = ReadResponseHeader(stream, timeoutMs);
        if (zipSize == 0) return;
        byte[]? body = ReadExact(stream, zipSize);
        if (body == null)
            throw new IOException("读取Setup响应体失败：连接已关闭");
    }

    /// <summary>
    /// 从TCP流中读取完整响应
    /// 响应格式: 16字节头 + zip_size字节body
    /// 如果 zip_size != unzip_size，用zlib解压body
    /// </summary>
    public static byte[] ReadResponse(NetworkStream stream, int timeoutMs = 10000)
    {
        var (zipSize, unzipSize) = ReadResponseHeader(stream, timeoutMs);
        if (zipSize == 0) return Array.Empty<byte>();
        byte[]? body = ReadExact(stream, zipSize);
        if (body == null)
            throw new IOException("读取响应体失败：连接已关闭");
        if (zipSize != unzipSize && unzipSize > 0)
            return ZLibDecompress(body, unzipSize);
        return body;
    }

    /// <summary>
    /// 解析K线响应数据（解压后的body）
    /// </summary>
    public static List<TdxBar> ParseBarsResponse(byte[] response, ushort category)
    {
        var bars = new List<TdxBar>();
        if (response.Length < 2) return bars;

        int pos = 0;
        ushort retCount = ReadUShortLE(response, pos);
        pos += 2;

        long preDiffBase = 0;
        int recordExtraBytes = 0; // 指数记录可能多4字节，自动检测

        for (int i = 0; i < retCount; i++)
        {
            if (pos + 4 > response.Length) break;

            int year, month, day, hour = 15, minute = 0;

            // 日期格式: category < 4 or 7 or 8 用 zipDay+minutes, 否则用 YYYYMMDD
            if (category < 4 || category == 7 || category == 8)
            {
                ushort zipDay = ReadUShortLE(response, pos);
                ushort minutes = ReadUShortLE(response, pos + 2);
                year = (zipDay >> 11) + 2004;
                month = (zipDay % 2048) / 100;
                day = (zipDay % 2048) % 100;
                if (minutes > 0) { hour = minutes / 60; minute = minutes % 60; }
            }
            else
            {
                // YYYYMMDD (uint32 LE)
                uint dateRaw = ReadUInt32LE(response, pos);
                year = (int)(dateRaw / 10000);
                month = (int)((dateRaw / 100) % 100);
                day = (int)(dateRaw % 100);
            }
            pos += 4;

            // 检查是否有足够空间读取价格（至少4字节，价格是变长的）
            if (pos + 4 > response.Length) break;
            long priceOpenDiff = GetPrice(response, ref pos);
            if (pos + 4 > response.Length) break;
            long priceCloseDiff = GetPrice(response, ref pos);
            if (pos + 4 > response.Length) break;
            long priceHighDiff = GetPrice(response, ref pos);
            if (pos + 4 > response.Length) break;
            long priceLowDiff = GetPrice(response, ref pos);

            long openRaw = priceOpenDiff + preDiffBase;
            double open = openRaw / 1000.0;
            long closeRaw = openRaw + priceCloseDiff;
            double close = closeRaw / 1000.0;
            double high = (openRaw + priceHighDiff) / 1000.0;
            double low = (openRaw + priceLowDiff) / 1000.0;
            preDiffBase = closeRaw;

            // 第一条记录: 自动检测是否有额外字节（指数记录多4字节）
            if (i == 0 && pos + 12 <= response.Length)
            {
                // 尝试标准结构: vol(4) + amount(4) = 8字节
                int testPos1 = pos + 8;
                bool stdValid = testPos1 + 4 <= response.Length && IsValidDate(response, testPos1, category);
                // 尝试扩展结构: vol(4) + extra(4) + amount(4) = 12字节
                int testPos2 = pos + 12;
                bool extValid = testPos2 + 4 <= response.Length && IsValidDate(response, testPos2, category);

                if (!stdValid && extValid)
                    recordExtraBytes = 4;
            }

            if (pos + 8 + recordExtraBytes > response.Length) break;
            uint volRaw = ReadUInt32LE(response, pos);
            pos += 4;
            double volume = GetVolume(volRaw);

            // 跳过额外字节（指数的额外字段）
            pos += recordExtraBytes;

            uint amountRaw = ReadUInt32LE(response, pos);
            pos += 4;
            double amount = GetVolume(amountRaw);

            if (year >= 1990 && month >= 1 && month <= 12 && day >= 1 && day <= 31 && close > 0)
            {
                bars.Add(new TdxBar
                {
                    DateTime = new DateTime(year, month, day, hour, minute, 0),
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Amount = amount,
                    Volume = volume
                });
            }
        }

        return bars;
    }

    // === 辅助方法 ===

    /// <summary>
    /// 检查指定位置是否为有效日期
    /// </summary>
    private static bool IsValidDate(byte[] data, int pos, ushort category)
    {
        if (pos + 4 > data.Length) return false;
        if (category < 4 || category == 7 || category == 8)
        {
            ushort zipDay = ReadUShortLE(data, pos);
            int year = (zipDay >> 11) + 2004;
            int month = (zipDay % 2048) / 100;
            int day = (zipDay % 2048) % 100;
            return year >= 1990 && year <= 2100 && month >= 1 && month <= 12 && day >= 1 && day <= 31;
        }
        else
        {
            uint dateRaw = ReadUInt32LE(data, pos);
            int year = (int)(dateRaw / 10000);
            int month = (int)((dateRaw / 100) % 100);
            int day = (int)(dateRaw % 100);
            return year >= 1990 && year <= 2100 && month >= 1 && month <= 12 && day >= 1 && day <= 31;
        }
    }

    /// <summary>
    /// 变长价格解码（类似UTF-8编码）
    /// </summary>
    public static long GetPrice(byte[] data, ref int pos)
    {
        if (pos >= data.Length) return 0;
        int posByte = 6;
        byte b = data[pos];
        long intData = b & 0x3F;
        bool sign = (b & 0x40) != 0;
        bool cont = (b & 0x80) != 0;

        if (cont)
        {
            while (true)
            {
                pos++;
                if (pos >= data.Length) break;
                b = data[pos];
                intData += (long)(b & 0x7F) << posByte;
                posByte += 7;
                if ((b & 0x80) == 0) break;
            }
        }

        pos++;

        if (sign) intData = -intData;
        return intData;
    }

    /// <summary>
    /// 通达信成交量解码（浮点数编码）
    /// </summary>
    public static double GetVolume(uint vol)
    {
        int logpoint = (int)(vol >> 24);
        int hleax = (int)((vol >> 16) & 0xFF);
        int lheax = (int)((vol >> 8) & 0xFF);
        int lleax = (int)(vol & 0xFF);

        int dwEcx = logpoint * 2 - 0x7F;
        int dwEdx = logpoint * 2 - 0x86;
        int dwEsi = logpoint * 2 - 0x8E;
        int dwEax = logpoint * 2 - 0x96;

        int tmpEax = Math.Abs(dwEcx);
        double dblXmm6 = Math.Pow(2.0, tmpEax);
        if (dwEcx < 0) dblXmm6 = 1.0 / dblXmm6;

        double dblXmm0;
        if (hleax > 0x80)
        {
            int dwTmpEax = dwEdx + 1;
            double tmpDblXmm3 = Math.Pow(2.0, dwTmpEax);
            dblXmm0 = Math.Pow(2.0, dwEdx) * 128.0 + (hleax & 0x7F) * tmpDblXmm3;
        }
        else
        {
            if (dwEdx >= 0)
                dblXmm0 = Math.Pow(2.0, dwEdx) * hleax;
            else
                dblXmm0 = (1.0 / Math.Pow(2.0, -dwEdx)) * hleax;
        }

        double dblXmm3 = Math.Pow(2.0, dwEsi) * lheax;
        double dblXmm1 = Math.Pow(2.0, dwEax) * lleax;

        if ((hleax & 0x80) != 0)
        {
            dblXmm3 *= 2.0;
            dblXmm1 *= 2.0;
        }

        return dblXmm6 + dblXmm0 + dblXmm3 + dblXmm1;
    }

    private static (ushort zipSize, ushort unzipSize) ReadResponseHeader(NetworkStream stream, int timeoutMs)
    {
        stream.ReadTimeout = timeoutMs;
        byte[]? header = ReadExact(stream, RSP_HEADER_LEN);
        if (header == null)
            throw new IOException("读取响应头失败：连接已关闭");
        ushort zipSize = ReadUShortLE(header, 12);
        ushort unzipSize = ReadUShortLE(header, 14);
        return (zipSize, unzipSize);
    }

    private static byte[] ZLibDecompress(byte[] compressed, int expectedSize)
    {
        using var ms = new MemoryStream(compressed);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var result = new MemoryStream(expectedSize > 0 ? expectedSize : 0);
        zlib.CopyTo(result);
        return result.ToArray();
    }

    public static ushort ReadUShortLE(byte[] buf, int offset)
    {
        return (ushort)(buf[offset] | (buf[offset + 1] << 8));
    }

    public static uint ReadUInt32LE(byte[] buf, int offset)
    {
        return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
    }

    private static void WriteUShortLE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    /// <summary>从流中精确读取n个字节</summary>
    public static byte[]? ReadExact(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0) return null;
            totalRead += read;
        }
        return buffer;
    }
}

/// <summary>通达信K线数据条</summary>
internal class TdxBar
{
    public DateTime DateTime { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Amount { get; set; }
    public double Volume { get; set; }
}
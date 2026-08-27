using System.ComponentModel;
using System.Runtime.InteropServices;
using TColor = Terminal.Gui.Drawing.Color;

namespace SkillTipsResponseAnalyzer;

internal static class RankImageRasterizer
{
    const string FontFace = "Segoe UI Variable Display";
    const int FontWeightSemibold = 600;
    const uint BiRgb = 0;
    const uint DibRgbColors = 0;
    const uint DefaultCharSet = 1;
    const uint OutDefaultPrecision = 0;
    const uint ClipDefaultPrecision = 0;
    const uint AntialiasedQuality = 4;
    const uint DefaultPitchAndFamily = 0;
    const int Transparent = 1;
    const uint ClrInvalid = 0xFFFFFFFF;
    const uint DtCenter = 0x00000001;
    const uint DtVCenter = 0x00000004;
    const uint DtSingleLine = 0x00000020;
    const uint DtNoPrefix = 0x00000800;
    const int ImageUsablePercent = 84;

    public static TColor[,] Rasterize(
        string rank,
        int pixelWidth,
        int pixelHeight,
        TColor foreground,
        TColor background)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("预测等级字体栅格化仅支持 Windows GDI。");

        ArgumentException.ThrowIfNullOrEmpty(rank);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
            throw Win32Error(nameof(CreateCompatibleDC));

        nint bitmap = IntPtr.Zero;
        nint oldBitmap = IntPtr.Zero;
        nint font = IntPtr.Zero;
        nint oldFont = IntPtr.Zero;
        try
        {
            var pixelCount = checked(pixelWidth * pixelHeight);
            var byteCount = checked(pixelCount * 4);
            var bitmapInfo = new BitmapInfo
            {
                Header = new()
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = pixelWidth,
                    Height = -pixelHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)byteCount
                }
            };
            bitmap = CreateDIBSection(
                dc,
                ref bitmapInfo,
                DibRgbColors,
                out var bits,
                IntPtr.Zero,
                0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw Win32Error(nameof(CreateDIBSection));

            oldBitmap = SelectObject(dc, bitmap);
            if (IsInvalidHandle(oldBitmap))
                throw Win32Error(nameof(SelectObject));

            var pixels = new byte[byteCount];
            for (var index = 0; index < byteCount; index += 4)
            {
                pixels[index] = background.B;
                pixels[index + 1] = background.G;
                pixels[index + 2] = background.R;
            }
            Marshal.Copy(pixels, 0, bits, byteCount);

            if (SetTextColor(dc, ToColorRef(foreground)) == ClrInvalid)
                throw Win32Error(nameof(SetTextColor));
            if (SetBkColor(dc, ToColorRef(background)) == ClrInvalid)
                throw Win32Error(nameof(SetBkColor));
            if (SetBkMode(dc, Transparent) == 0)
                throw Win32Error(nameof(SetBkMode));

            var usableWidth = Math.Max(1, pixelWidth * ImageUsablePercent / 100);
            var usableHeight = Math.Max(1, pixelHeight * ImageUsablePercent / 100);
            var safeRect = new NativeRect
            {
                Left = (pixelWidth - usableWidth) / 2,
                Top = (pixelHeight - usableHeight) / 2,
                Right = (pixelWidth - usableWidth) / 2 + usableWidth,
                Bottom = (pixelHeight - usableHeight) / 2 + usableHeight
            };
            var fontHeight = FindLargestFontHeight(dc, rank, usableWidth, usableHeight);
            font = CreateRankFont(fontHeight);
            oldFont = SelectObject(dc, font);
            if (IsInvalidHandle(oldFont))
                throw Win32Error(nameof(SelectObject));

            if (DrawTextW(
                    dc,
                    rank,
                    rank.Length,
                    ref safeRect,
                    DtCenter | DtVCenter | DtSingleLine | DtNoPrefix) == 0)
                throw Win32Error(nameof(DrawTextW));
            if (!GdiFlush())
                throw Win32Error(nameof(GdiFlush));

            Marshal.Copy(bits, pixels, 0, byteCount);
            var image = new TColor[pixelWidth, pixelHeight];
            for (var y = 0; y < pixelHeight; y++)
            {
                for (var x = 0; x < pixelWidth; x++)
                {
                    var index = (y * pixelWidth + x) * 4;
                    image[x, y] = new(
                        pixels[index + 2],
                        pixels[index + 1],
                        pixels[index],
                        255);
                }
            }
            return image;
        }
        finally
        {
            if (!IsInvalidHandle(oldFont))
                SelectObject(dc, oldFont);
            if (font != IntPtr.Zero)
                DeleteObject(font);
            if (!IsInvalidHandle(oldBitmap))
                SelectObject(dc, oldBitmap);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    static int FindLargestFontHeight(nint dc, string rank, int maxWidth, int maxHeight)
    {
        var low = 1;
        var high = maxHeight;
        var best = 0;
        while (low <= high)
        {
            var candidate = low + (high - low) / 2;
            var size = MeasureText(dc, rank, candidate);
            if (size.Width <= maxWidth && size.Height <= maxHeight)
            {
                best = candidate;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        return best != 0
            ? best
            : throw new InvalidOperationException("预测等级无法在当前 raster graphics 区域内完整显示。");
    }

    static NativeSize MeasureText(nint dc, string rank, int fontHeight)
    {
        var font = CreateRankFont(fontHeight);
        var oldFont = SelectObject(dc, font);
        if (IsInvalidHandle(oldFont))
        {
            var errorCode = Marshal.GetLastWin32Error();
            DeleteObject(font);
            throw new Win32Exception(errorCode, $"{nameof(SelectObject)} 失败。");
        }

        try
        {
            if (!GetTextExtentPoint32W(dc, rank, rank.Length, out var size))
                throw Win32Error(nameof(GetTextExtentPoint32W));
            return size;
        }
        finally
        {
            SelectObject(dc, oldFont);
            DeleteObject(font);
        }
    }

    static nint CreateRankFont(int height)
    {
        var font = CreateFontW(
            -height,
            0,
            0,
            0,
            FontWeightSemibold,
            0,
            0,
            0,
            DefaultCharSet,
            OutDefaultPrecision,
            ClipDefaultPrecision,
            AntialiasedQuality,
            DefaultPitchAndFamily,
            FontFace);
        return font != IntPtr.Zero
            ? font
            : throw Win32Error(nameof(CreateFontW));
    }

    static bool IsInvalidHandle(nint value)
        => value == IntPtr.Zero || value == new nint(-1);

    static uint ToColorRef(TColor color)
        => (uint)(color.R | color.G << 8 | color.B << 16);

    static Win32Exception Win32Error(string operation)
        => new(Marshal.GetLastWin32Error(), $"{operation} 失败。");

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint UnusedColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern nint CreateDIBSection(
        nint dc,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTextExtentPoint32W(
        nint dc,
        string text,
        int length,
        out NativeSize size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int DrawTextW(
        nint dc,
        string text,
        int length,
        ref NativeRect rect,
        uint format);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern uint SetTextColor(nint dc, uint color);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern uint SetBkColor(nint dc, uint color);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern int SetBkMode(nint dc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteDC(nint dc);
}

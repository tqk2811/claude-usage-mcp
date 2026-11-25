using System.Runtime.InteropServices;

namespace ClaudeUsageTray.Tray;

/// <summary>
/// Draws a short text (the percent, or <c>!</c>/<c>?</c>) as a square HICON with plain GDI: no WPF rendering and no
/// System.Drawing (the idle process still has WPF core for its message window and dispatcher, see <c>App</c>). The text is rendered white-on-black with grayscale
/// antialiasing into a scratch DIB, used as coverage, and composited (premultiplied alpha) over the background color.
/// The font height is fitted once per digit count (reference "88" / "100"), so equal digit counts look the same size.
/// Not thread-safe: use from one thread (the tray dispatcher).
/// The caller owns the returned icon and must <see cref="DestroyIconHandle"/> it.
/// </summary>
public sealed class NumberIconRenderer
{
    private const string FallbackFace = "Segoe UI";
    private const int FwNormal = 400, FwBold = 700, DefaultCharset = 1, AntialiasedQuality = 4, Transparent = 1;
    private const byte InkThreshold = 24;

    private readonly Dictionary<(string Face, bool Bold, int Size, string Reference), Fit> _fits = [];

    /// <summary>Small icon size of the system DPI (what the notification area shows).</summary>
    public static int SystemIconSize => Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));

    public unsafe IntPtr Render(string text, uint textArgb, uint backgroundArgb, int size, string fontFace, bool bold)
    {
        int maskWidth = size * 4, maskHeight = size * 2;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr maskBitmap = IntPtr.Zero, colorBitmap = IntPtr.Zero, monoBitmap = IntPtr.Zero;
        try
        {
            BitmapInfo maskInfo = BitmapInfo.Create(maskWidth, maskHeight);
            maskBitmap = CreateDIBSection(dc, ref maskInfo, 0, out IntPtr maskBits, IntPtr.Zero, 0);
            if (maskBitmap == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection failed.");
            IntPtr oldBitmap = SelectObject(dc, maskBitmap);
            SetBkMode(dc, Transparent);
            SetTextColor(dc, 0x00FFFFFF);
            Span<byte> mask = new((void*)maskBits, maskWidth * maskHeight * 4);

            string face = ResolveFace(dc, fontFace);
            Fit fit = GetFit(dc, mask, maskWidth, maskHeight, face, bold, size, text.Length >= 3 ? "100" : "88");
            Ink ink = DrawText(dc, mask, maskWidth, maskHeight, text, fit.Em, face, bold, size);
            SelectObject(dc, oldBitmap);

            // Horizontal: center this text's ink. Vertical: center the reference digits' ink (cap height), so every
            // value of one digit count sits on the same baseline in both icons.
            int offsetX = ink.Width > 0 ? ink.Left - (size - ink.Width) / 2 : 0;
            int offsetY = fit.InkTop - (size - fit.InkHeight) / 2;

            BitmapInfo colorInfo = BitmapInfo.Create(size, size);
            colorBitmap = CreateDIBSection(dc, ref colorInfo, 0, out IntPtr colorBits, IntPtr.Zero, 0);
            if (colorBitmap == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection failed.");
            Composite(mask, maskWidth, maskHeight, offsetX, offsetY, new Span<byte>((void*)colorBits, size * size * 4), size, textArgb, backgroundArgb);

            // With a 32bpp color bitmap carrying alpha, the AND mask is ignored; all zero = no XOR effects.
            byte[] monoBits = new byte[(size + 15) / 16 * 2 * size];
            monoBitmap = CreateBitmap(size, size, 1, 1, monoBits);
            IconInfo info = new() { fIcon = true, hbmMask = monoBitmap, hbmColor = colorBitmap };
            IntPtr icon = CreateIconIndirect(ref info);
            if (icon == IntPtr.Zero)
                throw new InvalidOperationException("CreateIconIndirect failed.");
            return icon;
        }
        finally
        {
            if (monoBitmap != IntPtr.Zero)
                DeleteObject(monoBitmap);
            if (colorBitmap != IntPtr.Zero)
                DeleteObject(colorBitmap);
            if (maskBitmap != IntPtr.Zero)
                DeleteObject(maskBitmap);
            DeleteDC(dc);
        }
    }

    /// <summary>
    /// Largest font height at which <paramref name="reference"/> ("88" for 1-2 characters, "100" for 3) fits the icon;
    /// cached, so the same digit count always gets the same size whatever the actual digits are.
    /// </summary>
    private Fit GetFit(IntPtr dc, Span<byte> mask, int maskWidth, int maskHeight, string face, bool bold, int size, string reference)
    {
        var key = (face, bold, size, reference);
        if (_fits.TryGetValue(key, out Fit cached))
            return cached;
        Fit fit = new(4, 0, size);
        for (int em = (int)(size * 1.6); em >= 4; em--)
        {
            Ink ink = DrawText(dc, mask, maskWidth, maskHeight, reference, em, face, bold, size);
            if (ink.Width > 0 && ink.Width <= size && ink.Height <= size)
            {
                fit = new Fit(em, ink.Top, ink.Height);
                break;
            }
        }
        _fits[key] = fit;
        return fit;
    }

    private static Ink DrawText(IntPtr dc, Span<byte> mask, int maskWidth, int maskHeight, string text, int em, string face, bool bold, int size)
    {
        mask.Clear();
        IntPtr font = CreateFont(-em, 0, 0, 0, bold ? FwBold : FwNormal, 0, 0, 0, DefaultCharset, 0, 0, AntialiasedQuality, 0, face);
        IntPtr oldFont = SelectObject(dc, font);
        TextOut(dc, size / 2, 0, text, text.Length);
        GdiFlush();
        SelectObject(dc, oldFont);
        DeleteObject(font);
        return FindInk(mask, maskWidth, maskHeight);
    }

    /// <summary>GDI silently substitutes a missing face; compare what it selected and fall back to Segoe UI.</summary>
    private static unsafe string ResolveFace(IntPtr dc, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Length >= 32)
            return FallbackFace;
        IntPtr font = CreateFont(-12, 0, 0, 0, FwNormal, 0, 0, 0, DefaultCharset, 0, 0, AntialiasedQuality, 0, requested);
        IntPtr old = SelectObject(dc, font);
        char* buffer = stackalloc char[64];
        buffer[0] = (char)0;
        GetTextFace(dc, 64, buffer);
        SelectObject(dc, old);
        DeleteObject(font);
        return string.Equals(new string(buffer), requested, StringComparison.OrdinalIgnoreCase) ? requested : FallbackFace;
    }

    private readonly record struct Fit(int Em, int InkTop, int InkHeight);

    private readonly record struct Ink(int Left, int Top, int Width, int Height);

    public static void DestroyIconHandle(IntPtr icon)
    {
        if (icon != IntPtr.Zero)
            DestroyIcon(icon);
    }

    private static Ink FindInk(ReadOnlySpan<byte> mask, int width, int height)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                if (mask[row + x * 4 + 1] < InkThreshold)
                    continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return right < 0 ? default : new Ink(left, top, right - left + 1, bottom - top + 1);
    }

    private static void Composite(ReadOnlySpan<byte> mask, int maskWidth, int maskHeight, int offsetX, int offsetY,
        Span<byte> target, int size, uint textArgb, uint backgroundArgb)
    {
        float textA = (textArgb >> 24) / 255f;
        float bgA = (backgroundArgb >> 24) / 255f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int mx = x + offsetX, my = y + offsetY;
                float coverage = mx >= 0 && my >= 0 && mx < maskWidth && my < maskHeight
                    ? mask[(my * maskWidth + mx) * 4 + 1] / 255f
                    : 0f;
                float ta = textA * coverage;
                float ba = bgA * (1 - ta);
                int i = (y * size + x) * 4;
                // BGRA, premultiplied.
                target[i] = (byte)Math.Round((textArgb & 0xFF) * ta + (backgroundArgb & 0xFF) * ba);
                target[i + 1] = (byte)Math.Round(((textArgb >> 8) & 0xFF) * ta + ((backgroundArgb >> 8) & 0xFF) * ba);
                target[i + 2] = (byte)Math.Round(((textArgb >> 16) & 0xFF) * ta + ((backgroundArgb >> 16) & 0xFF) * ba);
                target[i + 3] = (byte)Math.Round(255 * (ta + ba));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        public uint bmiColors;

        /// <summary>32bpp top-down.</summary>
        public static BitmapInfo Create(int width, int height) => new()
        {
            biSize = 40,
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[] lpBits);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
    private static extern IntPtr CreateFont(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic,
        uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality,
        uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
    private static extern bool TextOut(IntPtr hdc, int x, int y, string lpString, int c);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextFaceW")]
    private static extern unsafe int GetTextFace(IntPtr hdc, int c, char* lpName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

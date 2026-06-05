using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Generates the tray icon at runtime (no image assets): a VM-monitor glyph with a network
/// connection indicator on a blue rounded background.  The accent colour on the connection dot
/// shows state: green when the VM is bridged to a physical LAN, orange for NAT/fallback.
/// Two multi-size .ico files are written next to the exe and swapped on state changes; writing
/// to disk lets H.NotifyIcon reload them and avoids the GDI handle leak of Bitmap.GetHicon().
///
/// Icon version: v2 — new glyph with 64/48 px frames for 4K tray clarity.
/// </summary>
internal static class IconGenerator
{
    // v2 — rename forces regeneration on first run after upgrade; old v1 files are ignored.
    private const string BridgedFile  = "icon-bridged-v2.ico";
    private const string FallbackFile = "icon-fallback-v2.ico";

    // Frame sizes baked into each .ico.  64/48 are picked by Windows on 4K (200 %+ DPI)
    // without upscaling; 32/24/20/16 cover 100–150 % tray DPI.
    private static readonly int[] IconSizes = [64, 48, 32, 24, 20, 16];

    // Background: Windows blue for both states — consistent identity at a glance.
    private static readonly Color Background    = Color.FromArgb(255, 0x00, 0x78, 0xD7);

    // Accent dot: green = connected to LAN (bridged), orange = NAT/fallback.
    private static readonly Color BridgedDot  = Color.FromArgb(255, 0x10, 0xB9, 0x81);  // AppColors.Green
    private static readonly Color FallbackDot = Color.FromArgb(255, 0xFF, 0x8C, 0x00);  // AppColors.Orange

    /// <summary>
    /// Returns the path to the .ico for the given state, generating it on first call.
    /// </summary>
    internal static string GenerateAndSave(string outputDirectory, bool bridged)
    {
        var icoPath = Path.Combine(outputDirectory, bridged ? BridgedFile : FallbackFile);
        if (!File.Exists(icoPath))
            SaveAsIco(icoPath, bridged ? BridgedDot : FallbackDot);
        return icoPath;
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    // Glyph is designed in a 16-unit logical space and scaled to each frame size.
    // Layout:
    //   ┌──────────────────────────────┐  ← blue rounded background
    //   │   ┌──────────────────────┐   │  ← VM monitor frame (white stroke)
    //   │   │   ─────────────────  │   │  ← screen content line
    //   │   │   ─────────────────  │   │
    //   │   └──────┬───────────────┘   │  ← monitor stand stub
    //   │          │                   │  ← network cable
    //   │         (●)                  │  ← connection dot: green/orange
    //   └──────────────────────────────┘
    private static Bitmap RenderIconBitmap(int size, Color dotColor)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 16f, size / 16f);

        // ── Blue rounded background ──────────────────────────────────────────
        using (var bgBrush = new SolidBrush(Background))
        using (var bgPath  = RoundedRect(new RectangleF(0.5f, 0.5f, 15f, 15f), 3.2f))
            g.FillPath(bgBrush, bgPath);

        using var whitePen   = new Pen(Color.White, 1.1f) { LineJoin = LineJoin.Round };
        using var whiteFill  = new SolidBrush(Color.White);
        using var dotFill    = new SolidBrush(dotColor);

        // ── VM monitor frame ─────────────────────────────────────────────────
        // Outer monitor bezel: rounded rect, white stroke
        using (var monPath = RoundedRect(new RectangleF(2.0f, 1.5f, 12.0f, 8.5f), 1.2f))
            g.DrawPath(whitePen, monPath);

        // Two screen content lines inside the monitor
        g.DrawLine(whitePen, 3.5f, 4.0f, 12.5f, 4.0f);
        g.DrawLine(whitePen, 3.5f, 6.0f,  9.0f, 6.0f);

        // ── Stand + cable ────────────────────────────────────────────────────
        // Short stand stub from bottom of monitor to cable
        g.DrawLine(whitePen, 8.0f, 10.0f, 8.0f, 11.5f);

        // ── Connection indicator dot ─────────────────────────────────────────
        // Filled circle at cable end — green (bridged) or orange (fallback)
        g.FillEllipse(dotFill, 5.8f, 11.5f, 4.4f, 4.4f);
        // White ring around dot for contrast on any backdrop
        g.DrawEllipse(whitePen, 5.8f, 11.5f, 4.4f, 4.4f);

        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d    = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X,         r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Writes a valid ICO with one PNG-compressed frame per size (Vista+ PNG-in-ICO).</summary>
    private static void SaveAsIco(string filePath, Color dotColor)
    {
        var frames = Array.ConvertAll(IconSizes, s =>
        {
            using var bmp = RenderIconBitmap(s, dotColor);
            using var ms  = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        });

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write((short)0);                // reserved
        bw.Write((short)1);                // type: icon
        bw.Write((short)IconSizes.Length); // image count

        int dataOffset = 6 + IconSizes.Length * 16;
        for (int i = 0; i < IconSizes.Length; i++)
        {
            var sz = IconSizes[i];
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // 0 encodes 256 in ICO format
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)0);             // colour count
            bw.Write((byte)0);             // reserved
            bw.Write((short)1);            // colour planes
            bw.Write((short)32);           // bits per pixel
            bw.Write(frames[i].Length);    // data size
            bw.Write(dataOffset);          // data offset
            dataOffset += frames[i].Length;
        }

        foreach (var frame in frames)
            bw.Write(frame);
    }
}

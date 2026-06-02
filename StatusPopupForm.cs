using System.Drawing;
using System.Drawing.Text;

namespace HyperVNetworkSwitcher;

/// <summary>
/// Borderless flyout form that renders the two-section network status table
/// (HOST NETWORK / VIRTUAL MACHINE).  Positioned just above the system tray
/// in the bottom-right corner of the working area.
///
/// Toggled by a left-click on the tray icon; auto-hides when it loses focus.
/// </summary>
internal sealed class StatusPopupForm : Form
{
    private const int LabelX   = 12;
    private const int LabelGap = 10;
    private const int InitialW = 320;
    private const int TopPad   = 8;
    private const int BotPad   = 10;
    private const int SepH     = 14;
    private const int Border   = 1;    // thin border drawn by form background

    private static readonly string[] LabelNames =
        ["Adapter", "IP", "Gateway", "DNS", "VM", "Switch", "Rule"];

    // ── Live data ─────────────────────────────────────────────────────────────
    private string _hostAdapter = "—";
    private string _hostIp      = "—";
    private string _gateway     = "—";
    private string _dns         = "—";
    private string _vmName      = "—";
    private string _switchName  = "—";
    private string _ruleName    = "—";
    private string _vmIp        = "querying…";

    private readonly Panel _canvas;

    // Cached GDI resources — created once and reused on every repaint instead of being
    // allocated (and finalised) per Paint.  Disposed with the form.
    private readonly Font  _headerFont  = new("Segoe UI", 7.5f, FontStyle.Bold);
    private readonly Font  _labelFont   = new("Segoe UI", 8.5f, FontStyle.Regular);
    private readonly Font  _valueFont   = new("Segoe UI", 8.5f, FontStyle.Regular);
    private readonly Brush _accentBrush = new SolidBrush(Color.FromArgb(0, 120, 215));
    private readonly Brush _labelBrush  = new SolidBrush(Color.FromArgb(108, 108, 112));
    private readonly Brush _valueBrush  = new SolidBrush(SystemColors.MenuText);
    private readonly Pen   _dividerPen  = new(Color.FromArgb(215, 215, 218), 1f);

    // ── Construction ──────────────────────────────────────────────────────────

    public StatusPopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        BackColor       = Color.FromArgb(180, 180, 185); // 1 px border colour
        Padding         = new Padding(Border);

        _canvas = new Panel
        {
            BackColor = SystemColors.Menu,
            Size      = new Size(InitialW, 200),
            Dock      = DockStyle.Fill
        };
        _canvas.Paint += OnCanvasPaint;
        Controls.Add(_canvas);

        ClientSize = new Size(InitialW + Border * 2, 200 + Border * 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headerFont.Dispose();  _labelFont.Dispose();  _valueFont.Dispose();
            _accentBrush.Dispose(); _labelBrush.Dispose(); _valueBrush.Dispose();
            _dividerPen.Dispose();
        }
        base.Dispose(disposing);
    }

    // Drop shadow via window-class style flag
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
    }

    // Auto-hide on focus loss (e.g. user clicks elsewhere or opens context menu)
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Hide(); }

    // ── Public update API ─────────────────────────────────────────────────────

    public void Update(MatchResult result, string vmIp = "querying…")
    {
        _hostAdapter = result.HostAdapterName;
        _hostIp      = result.HostIp;
        _gateway     = result.Gateway;
        _dns         = result.DnsServers.Count > 0
                           ? string.Join("  ·  ", result.DnsServers.Take(2))
                           : "—";
        _vmName      = string.Join(", ", result.TargetVms);
        _switchName  = result.VirtualSwitch;
        _ruleName    = result.RuleName;
        _vmIp        = vmIp;
        _canvas.Invalidate();
    }

    public void SetVmIp(string vmIp)
    {
        _vmIp = vmIp;
        _canvas.Invalidate();
    }

    /// <summary>
    /// Positions the form just above the system tray and makes it visible.
    /// Uses the screen that contains the cursor to handle multi-monitor setups.
    /// </summary>
    public void ShowNearTray()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        PlaceNearTray(wa);
        _canvas.Invalidate();
        Visible = true;
        Activate();
    }

    private void PlaceNearTray(Rectangle workingArea)
    {
        var x = workingArea.Right  - Width  - 4;
        var y = workingArea.Bottom - Height - 4;
        Location = new Point(
            Math.Max(workingArea.Left, x),
            Math.Max(workingArea.Top,  y));
    }

    // ── Painting ──────────────────────────────────────────────────────────────

    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // ── DPI-adaptive layout ───────────────────────────────────────────────
        int maxLabelW = LabelNames
            .Max(s => (int)Math.Ceiling(g.MeasureString(s, _labelFont).Width));
        int valueX = LabelX + maxLabelW + LabelGap;

        int rowH    = (int)Math.Ceiling(_labelFont.GetHeight(g)) + 4;
        int headerH = (int)Math.Ceiling(_headerFont.GetHeight(g)) + 3;

        int requiredH = TopPad + headerH + rowH * 4 + SepH + headerH + rowH * 4 + BotPad;

        int longestValueW = new[] { _hostAdapter, _hostIp, _gateway, _dns,
                                    _vmName, _switchName, _ruleName, _vmIp }
            .Max(s => (int)Math.Ceiling(g.MeasureString(s, _valueFont).Width));
        int requiredW = valueX + longestValueW + LabelX;

        int newW = Math.Max(_canvas.Width, requiredW);
        if (_canvas.Height != requiredH || _canvas.Width != newW)
        {
            _canvas.Size = new Size(newW, requiredH);
            ClientSize   = new Size(newW + Border * 2, requiredH + Border * 2);
            var wa = Screen.FromControl(this).WorkingArea;
            PlaceNearTray(wa);
            return; // size change triggers fresh repaint
        }

        int y = TopPad;

        // ── Section 1: HOST NETWORK ───────────────────────────────────────────
        g.DrawString("HOST NETWORK", _headerFont, _accentBrush, LabelX, y); y += headerH;
        Row(g, "Adapter", _hostAdapter, y, valueX); y += rowH;
        Row(g, "IP",      _hostIp,      y, valueX); y += rowH;
        Row(g, "Gateway", _gateway,     y, valueX); y += rowH;
        Row(g, "DNS",     _dns,         y, valueX); y += rowH;

        g.DrawLine(_dividerPen, LabelX, y + SepH / 2, _canvas.Width - LabelX, y + SepH / 2);
        y += SepH;

        // ── Section 2: VIRTUAL MACHINE ────────────────────────────────────────
        g.DrawString("VIRTUAL MACHINE", _headerFont, _accentBrush, LabelX, y); y += headerH;
        Row(g, "VM",     _vmName,     y, valueX); y += rowH;
        Row(g, "Switch", _switchName, y, valueX); y += rowH;
        Row(g, "Rule",   _ruleName,   y, valueX); y += rowH;
        Row(g, "IP",     _vmIp,       y, valueX);
    }

    private void Row(Graphics g, string label, string value, int y, int valueX)
    {
        g.DrawString(label, _labelFont, _labelBrush, LabelX, y);
        g.DrawString(value, _valueFont, _valueBrush, valueX, y);
    }
}

using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Shared colour constants and pre-allocated brushes (avoids allocating brushes on every UI
/// refresh).  Mirrors the LenovoTray palette but uses the app's blue accent — matching the
/// bridged tray icon — instead of Lenovo red.
/// </summary>
internal static class AppColors
{
    // ── Semantic state ──────────────────────────────────────────────────────────
    internal static readonly Color Green  = Color.FromArgb(255, 0x10, 0xB9, 0x81);  // running
    internal static readonly Color Orange = Color.FromArgb(255, 0xFF, 0x8C, 0x00);  // paused / saved
    internal static readonly Color Grey   = Color.FromArgb(255, 0x9E, 0x9E, 0x9E);  // off

    // ── State indicator dots ────────────────────────────────────────────────────
    internal static readonly SolidColorBrush IndicatorGreenBrush  = new(Green);
    internal static readonly SolidColorBrush IndicatorOrangeBrush = new(Orange);
    internal static readonly SolidColorBrush IndicatorGreyBrush   = new(Grey);

    // ── CPU arc-gauge fills (by load) ───────────────────────────────────────────
    internal static readonly SolidColorBrush GaugeLowBrush  = new(Green);   // ≤ 50 %
    internal static readonly SolidColorBrush GaugeMedBrush  = new(Orange);  // ≤ 85 %
    internal static readonly SolidColorBrush GaugeHighBrush = new(Color.FromArgb(255, 0xE8, 0x11, 0x23)); // > 85 %
}

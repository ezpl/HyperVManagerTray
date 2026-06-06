using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Shared colour constants and pre-allocated brushes (avoids allocating brushes on every UI
/// refresh).  Uses the same palette as LenovoTray for visual consistency across both tray apps.
/// </summary>
internal static class AppColors
{
    // ── Semantic state ──────────────────────────────────────────────────────────
    internal static readonly Color Green  = Color.FromArgb(255, 0x10, 0xB9, 0x81);  // running
    internal static readonly Color Orange = Color.FromArgb(255, 0xFF, 0x8C, 0x00);  // paused / saved
    internal static readonly Color Grey   = Color.FromArgb(255, 0x9E, 0x9E, 0x9E);  // off
    internal static readonly Color DangerRed = Color.FromArgb(255, 0xE2, 0x00, 0x1A); // high load / error

    // ── Badge backgrounds (semi-transparent fills) ──────────────────────────────
    internal static readonly SolidColorBrush BadgeActiveBrush   = new(Color.FromArgb(20, 0x10, 0xB9, 0x81));
    internal static readonly SolidColorBrush BadgeInactiveBrush = new(Color.FromArgb(12, 0x80, 0x80, 0x80));

    // ── State indicator dots ────────────────────────────────────────────────────
    internal static readonly SolidColorBrush IndicatorGreenBrush  = new(Green);
    internal static readonly SolidColorBrush IndicatorOrangeBrush = new(Orange);
    internal static readonly SolidColorBrush IndicatorGreyBrush   = new(Grey);

    // ── Progress-bar / meter fills (by load) ────────────────────────────────────
    internal static readonly SolidColorBrush GaugeLowBrush  = new(Green);      // ≤ 50 %
    internal static readonly SolidColorBrush GaugeMedBrush  = new(Orange);     // ≤ 85 %
    internal static readonly SolidColorBrush GaugeHighBrush = new(DangerRed);  // > 85 %
}

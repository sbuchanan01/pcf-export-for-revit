using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcfExport.Revit
{
    /// <summary>
    /// Runtime icon generator for ribbon buttons. Uses
    /// <see cref="DrawingVisual"/> + <see cref="RenderTargetBitmap"/> to render
    /// Segoe MDL2 Assets glyphs into 16 / 32 px ImageSources at startup —
    /// avoids shipping bitmap files alongside the DLL and keeps the icons
    /// crisp at both ribbon sizes.
    /// </summary>
    internal static class RibbonIconFactory
    {
        // ── Color palette ──────────────────────────────────────────────────
        private static readonly Color TagAmber    = Color.FromRgb(0xDA, 0xA5, 0x20); // tag
        private static readonly Color ForestGreen = Color.FromRgb(0x2E, 0x8B, 0x2E); // upload / export

        // ── Public API ─────────────────────────────────────────────────────

        /// <summary>Tag — Assign Tag button.</summary>
        public static ImageSource Tag(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", TagAmber));

        /// <summary>Upload / outgoing arrow — Export button.</summary>
        public static ImageSource Upload(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ForestGreen));

        // ── Internals ──────────────────────────────────────────────────────

        private static ImageSource Render(int size, Action<DrawingContext, int> draw)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                draw(dc, size);
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static void DrawGlyph(DrawingContext dc, int size,
                                      string text, Color color) =>
            DrawText(dc, size, text,
                fontFamily: "Segoe MDL2 Assets",
                weight: FontWeights.Normal,
                color: color,
                sizeFraction: 0.85);

        private static void DrawText(DrawingContext dc, int size, string text,
            string fontFamily, FontWeight weight, Color color, double sizeFraction)
        {
            var typeface = new Typeface(
                new FontFamily(fontFamily),
                FontStyles.Normal, weight, FontStretches.Normal);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var ft = new FormattedText(
                text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * sizeFraction,
                brush,
                pixelsPerDip: 1.0);
            double x = (size - ft.Width)  / 2.0;
            double y = (size - ft.Height) / 2.0;
            dc.DrawText(ft, new Point(x, y));
        }
    }
}

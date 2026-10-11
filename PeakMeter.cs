using System;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DarkVolumeMixer
{
    public static class MeterMath
    {
        /// <summary>Untergrenze der Anzeige in dB. Alles darunter ist "still".</summary>
        public const float FloorDb = -50f;

        /// <summary>Wandelt einen linearen Spitzenwert (0..1) in 0..100 % auf dB-Skala um.</summary>
        public static float ToPercent(float linearPeak)
        {
            if (linearPeak <= 0.00001f) return 0f;
            float db = 20f * MathF.Log10(linearPeak);
            return Math.Clamp((db - FloorDb) / -FloorDb * 100f, 0f, 100f);
        }
    }

    /// <summary>
    /// Schlanker, segmentierter Pegelbalken (grün → gelb → orange → rot).
    /// Zeichnet direkt per DrawingContext – deutlich günstiger als eine ProgressBar mit Template.
    /// </summary>
    public sealed class PeakMeter : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(PeakMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        // Vollständig qualifiziert: "Orientation" ist wegen WinForms sonst mehrdeutig.
        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
            nameof(Orientation), typeof(System.Windows.Controls.Orientation), typeof(PeakMeter),
            new FrameworkPropertyMetadata(System.Windows.Controls.Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));

        public System.Windows.Controls.Orientation Orientation
        {
            get => (System.Windows.Controls.Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        private const int SegmentCount = 24;
        private const double Gap = 1.5;

        // Schwellen als Anteil der Gesamthöhe (bei -50 dB Skala: grün bis ca. -20 dB, rot ab ca. -3 dB)
        private const double YellowFrom = 0.60;
        private const double OrangeFrom = 0.82;
        private const double RedFrom = 0.94;

        private static readonly Brush DimBrush = MakeBrush(0x1C, 0x1C, 0x20);
        private static readonly Brush GreenBrush = MakeBrush(0x00, 0xD2, 0x94);
        private static readonly Brush YellowBrush = MakeBrush(0xF2, 0xD6, 0x00);
        private static readonly Brush OrangeBrush = MakeBrush(0xFF, 0x95, 0x00);
        private static readonly Brush RedBrush = MakeBrush(0xFF, 0x3B, 0x30);

        public PeakMeter()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush BrushFor(int segmentIndex)
        {
            double position = (segmentIndex + 1) / (double)SegmentCount;
            if (position > RedFrom) return RedBrush;
            if (position > OrangeFrom) return OrangeBrush;
            if (position > YellowFrom) return YellowBrush;
            return GreenBrush;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double clamped = Math.Clamp(Value, 0, 100);
            int lit = (int)Math.Round(clamped / 100.0 * SegmentCount);

            if (Orientation == System.Windows.Controls.Orientation.Horizontal)
            {
                // Von links (leise) nach rechts (laut)
                double segW = (w - Gap * (SegmentCount - 1)) / SegmentCount;
                if (segW < 1) return;

                for (int i = 0; i < SegmentCount; i++)
                {
                    double x = i * (segW + Gap);
                    dc.DrawRectangle(i < lit ? BrushFor(i) : DimBrush, null, new Rect(x, 0, segW, h));
                }
                return;
            }

            double segH = (h - Gap * (SegmentCount - 1)) / SegmentCount;
            if (segH < 1) return;

            for (int i = 0; i < SegmentCount; i++)
            {
                double y = h - (i + 1) * segH - i * Gap;
                dc.DrawRectangle(i < lit ? BrushFor(i) : DimBrush, null, new Rect(0, y, w, segH));
            }
        }
    }
}

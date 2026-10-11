using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace DarkVolumeMixer
{
    /// <summary>
    /// Slider, bei dem ein Klick auf die Schiene den Regler direkt an die Klickstelle setzt
    /// (und man danach sofort weiterziehen kann). Funktioniert für vertikal und horizontal.
    /// Grund: Im eigenen Template waren die RepeatButtons ohne Command – ein Klick auf die Schiene tat nichts.
    /// </summary>
    public class FaderSlider : Slider
    {
        private bool _isDraggingTrack;

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // Klick auf den Thumb selbst: normales Verhalten (Thumb zieht sich selbst).
            if (IsEnabled &&
                GetTemplateChild("PART_Track") is Track track &&
                track.Thumb is { IsMouseOver: false })
            {
                _isDraggingTrack = true;
                CaptureMouse();
                SetValueFromPoint(track, e.GetPosition(track));
                e.Handled = true;
                return;
            }

            base.OnPreviewMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isDraggingTrack &&
                e.LeftButton == MouseButtonState.Pressed &&
                GetTemplateChild("PART_Track") is Track track)
            {
                SetValueFromPoint(track, e.GetPosition(track));
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            EndTrackDrag();
            base.OnPreviewMouseLeftButtonUp(e);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _isDraggingTrack = false;
        }

        private void EndTrackDrag()
        {
            if (!_isDraggingTrack) return;
            _isDraggingTrack = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        // Thumb-Mitte folgt exakt dem Mauszeiger: der Thumb-Mittelpunkt wandert zwischen
        // halber Thumb-Höhe/-Breite am einen Ende und am anderen Ende des Tracks.
        private void SetValueFromPoint(Track track, Point point)
        {
            double thumbSize = 0;
            double length, pos;

            if (Orientation == System.Windows.Controls.Orientation.Vertical)
            {
                thumbSize = track.Thumb?.ActualHeight ?? 0;
                length = track.ActualHeight - thumbSize;
                pos = point.Y - thumbSize / 2;
            }
            else
            {
                thumbSize = track.Thumb?.ActualWidth ?? 0;
                length = track.ActualWidth - thumbSize;
                pos = point.X - thumbSize / 2;
            }
            if (length <= 0) return;

            double fraction = Math.Clamp(pos / length, 0, 1);

            // Vertikal: oben = Maximum. Horizontal: rechts = Maximum. IsDirectionReversed dreht das um.
            if (Orientation == System.Windows.Controls.Orientation.Vertical) fraction = 1 - fraction;
            if (track.IsDirectionReversed) fraction = 1 - fraction;

            double v = Minimum + fraction * (Maximum - Minimum);

            if (IsSnapToTickEnabled && TickFrequency > 0)
                v = Minimum + Math.Round((v - Minimum) / TickFrequency) * TickFrequency;

            Value = Math.Clamp(v, Minimum, Maximum);
        }
    }
}

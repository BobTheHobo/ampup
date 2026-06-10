using System;
using System.Windows;
using System.Windows.Media;

namespace AmpUp.Controls
{
    /// <summary>
    /// Ambient radial glow behind each mixer channel knob.
    /// Tinted with the knob's LED color, pulses with audio level.
    /// Parent drives animation by calling Tick() on a 50ms interval.
    /// </summary>
    public class ChannelGlowControl : FrameworkElement
    {
        private const float BaseOpacity = 0f;        // no glow when silent
        private const float PeakOpacity = 0.22f;   // max audio glow
        private const float AttackRate = 0.4f;      // fast rise
        private const float DecayRate = 0.92f;      // slow fade

        private readonly VisualCollection _visuals;
        private readonly DrawingVisual _drawingVisual;

        private float _targetLevel;
        private float _smoothedLevel;
        private Color _glowColor = ThemeManager.Accent;
        private byte _lastAlpha;
        private bool _gradientRendered;

        public ChannelGlowControl()
        {
            _drawingVisual = new DrawingVisual();
            _drawingVisual.Opacity = 0; // silent until the first tick raises it
            _visuals = new VisualCollection(this) { _drawingVisual };
            IsHitTestVisible = false; // don't block mouse events to controls above

            // The gradient is recorded once per color/size change — keep it in
            // sync when the layout resizes the control.
            SizeChanged += (_, _) => RenderGradient();
        }

        #region Layout

        protected override Size MeasureOverride(Size availableSize)
        {
            // Take whatever space the parent gives us
            return new Size(
                double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? 100 : availableSize.Height);
        }

        #endregion

        #region Visual tree plumbing

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index)
        {
            if (index < 0 || index >= _visuals.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _visuals[index];
        }

        #endregion

        #region Public API

        public Color GlowColor
        {
            get => _glowColor;
            set
            {
                if (_glowColor != value)
                {
                    _glowColor = value;
                    RenderGradient();
                }
            }
        }

        public void SetLevel(float level)
        {
            _targetLevel = Math.Clamp(level, 0f, 1f);
        }

        /// <summary>
        /// Called by parent's 50ms timer. Smooths level and updates the glow.
        /// The gradient itself is recorded once per color/size change — per
        /// tick we only adjust the DrawingVisual's composition-side Opacity,
        /// so no drawing is re-recorded during audio.
        /// </summary>
        public void Tick()
        {
            // Smooth: fast attack, slow decay
            if (_targetLevel > _smoothedLevel)
                _smoothedLevel = _smoothedLevel + (_targetLevel - _smoothedLevel) * AttackRate;
            else
                _smoothedLevel *= DecayRate;

            if (_smoothedLevel < 0.005f)
                _smoothedLevel = 0f;

            if (!_gradientRendered)
                RenderGradient();

            float opacity = BaseOpacity + _smoothedLevel * (PeakOpacity - BaseOpacity);
            byte alpha = (byte)(opacity * 255);

            // Skip the (cheap) opacity update if nothing changed
            if (alpha == _lastAlpha) return;
            _lastAlpha = alpha;
            _drawingVisual.Opacity = alpha / 255.0;
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Records the radial gradient at full alpha into the DrawingVisual.
        /// Called only on color or size change — per-tick brightness is driven
        /// by <see cref="Visual"/> opacity instead, which scales all gradient
        /// stops uniformly (equivalent to the previous baked-in alpha).
        /// </summary>
        private void RenderGradient()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
            {
                _gradientRendered = false;
                return;
            }

            using (DrawingContext dc = _drawingVisual.RenderOpen())
            {
                var gradient = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.45),
                    GradientOrigin = new Point(0.5, 0.4),
                    RadiusX = 0.7,
                    RadiusY = 0.65,
                    GradientStops = new GradientStopCollection
                    {
                        // Full alpha here — runtime brightness comes from
                        // _drawingVisual.Opacity (0xFF * 0.4 = 0x66 mid stop).
                        new GradientStop(Color.FromArgb(0xFF, _glowColor.R, _glowColor.G, _glowColor.B), 0.0),
                        new GradientStop(Color.FromArgb(0x66, _glowColor.R, _glowColor.G, _glowColor.B), 0.5),
                        new GradientStop(Color.FromArgb(0x00, _glowColor.R, _glowColor.G, _glowColor.B), 1.0),
                    }
                };
                gradient.Freeze();

                dc.DrawRectangle(gradient, null, new Rect(0, 0, w, h));
            }

            _gradientRendered = true;
        }

        #endregion
    }
}

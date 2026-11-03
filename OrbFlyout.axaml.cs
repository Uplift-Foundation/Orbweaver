using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Orbweaver
{
    public partial class OrbFlyout : Window
    {
        private const int FlyMs = 160;
        private static readonly TimeSpan FlyTick = TimeSpan.FromMilliseconds(1000.0 / 60);

        // Distance from the orb's centre to each button's centre, in
        // DIPs. Large enough that the side buttons clear the orb's
        // circle with a small gap, without spreading the arc so far
        // the buttons feel detached from the orb they belong to.
        internal const double ArcRadius = 56;
        internal const double ButtonHalf = 12;

        private DispatcherTimer? _flyTimer;
        private PixelPoint _flyFrom;
        private PixelPoint _flyTo;
        private long _flyStartedAt;

        private static readonly IBrush ArrangeNormalFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush ArrangeActiveFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        private static readonly IBrush SpeakNormalFill = new SolidColorBrush(Color.Parse("#E0202024"));
        internal static readonly IBrush SpeakActiveFill = new SolidColorBrush(Color.Parse("#E04A90D9"));

        // Amber rather than the speaking blue, so "working on it" and "playing"
        // are told apart at a glance and not only by the glyph. The neural engine
        // takes a few seconds to reach its first sound (see NeuralSpeech), and a
        // stop button sitting over silence reads as a hang.
        internal static readonly IBrush SpeakPreparingFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        public event Action? MicClicked;
        public event Action? ArrangeClicked;
        public event Action? SettingsClicked;
        public event Action? SpeakClicked;
        public event Action? ChatClicked;

        // Where the orb's centre maps to in this window's DIP space.
        // Computed by LayoutArc, read by OrbWindow.EnsureFlyoutShown to
        // position the window so the arc sits concentric with the orb.
        public double ArcOriginX { get; private set; }
        public double ArcOriginY { get; private set; }

        public OrbFlyout()
        {
            InitializeComponent();

            // Drawn rather than typed — see SymbolMarks.
            SettingsMark.Data = StreamGeometry.Parse(SymbolMarks.Gear);
            ChatMark.Data = StreamGeometry.Parse(SymbolMarks.Keyboard);

            LayoutArc();
            LabelButtons();

            ArrangeButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ArrangeClicked?.Invoke();
            };

            SettingsButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SettingsClicked?.Invoke();
            };

            SpeakButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SpeakClicked?.Invoke();
            };

            MicButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                MicClicked?.Invoke();
            };

            ChatButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ChatClicked?.Invoke();
            };

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();
                this.AcceptFirstClick();
            };
        }

        public void SetMicVisible(bool visible)
        {
            MicButton.IsVisible = visible;
            LayoutArc();
        }

        public void SetChatVisible(bool visible)
        {
            ChatButton.IsVisible = visible;
            LayoutArc();
        }

        // Computes button positions along a semicircular arc and sizes
        // the Canvas to the tight bounding box around all buttons.
        // Angles are in degrees, measured from the positive X axis with
        // Y pointing down (screen coordinates): 90° is straight down
        // (6 o'clock), <90° swings right (toward 5), >90° swings left
        // (toward 7).
        // The order buttons appear along the arc, left end to right end. Which
        // of them are actually on the arc is each one's IsVisible; the angles
        // are derived from how many that turns out to be.
        private Grid[] ArcButtons => new[]
        {
            ArrangeButton, SettingsButton, SpeakButton, MicButton, ChatButton
        };

        // What each button is, in the compact form of the bubble the orb's own
        // tooltip uses — same palette, no tail. The tail belongs to a thought
        // rising from an orb; under a button it points at nothing.
        //
        // A word or two, not a sentence. These first read "Arrange orbs into the
        // chosen shape" and the like, which is a help topic: the bubble ended up
        // wider than the arc of buttons it was labelling, and a caption that
        // takes a moment to read is one you stop waiting for. The button is
        // already in front of the pointer — the label only has to name it.
        //
        // Set here rather than in the XAML because ToolTip.Tip="some text" does
        // not work in this app and cannot: App.axaml strips the ToolTip template
        // to a bare ContentPresenter so an orb's thought bubble can *be* the
        // tooltip, which leaves a plain string as unstyled text floating on the
        // desktop with no background. That is exactly how the first version of
        // these tips shipped, and exactly what "weird and hard to see" meant.
        //
        // Below the button rather than above it. These sit under the orb, and a
        // bubble above one would cover the orb the user is pointing at.
        private void LabelButtons()
        {
            Label(ArrangeButton, "Arrange");
            Label(SettingsButton, "Settings");
            Label(SpeakButton, "Read aloud");
            Label(MicButton, "Dictate");
            Label(ChatButton, "Chat");

            static void Label(Control button, string text)
            {
                ToolTip.SetTip(button, OrbWindow.ThoughtBubble(text, null, compact: true));
                ToolTip.SetPlacement(button, PlacementMode.Bottom);
                ToolTip.SetShowDelay(button, 250);
            }
        }

        private void LayoutArc()
        {
            // Spread evenly between the two ends, which keeps the arc symmetric
            // about 90° (straight down) whichever set is showing and spreads it
            // wider as buttons are added rather than packing them tighter —
            // ArcRadius is fixed, so the spacing between neighbours is what has
            // to give.
            //
            // This used to be two hardcoded angle arrays chosen by a single
            // `if (MicButton.IsVisible)`. The values below reproduce those
            // exactly — three buttons still land on 140/90/40 and four on
            // 140/106.7/73.3/40 — but a second independently-hidden button made
            // the old shape unrepresentable rather than merely inconvenient:
            // it needed one branch per combination, and got them wrong in the
            // combinations nobody had on screen while writing it.
            const double FromAngle = 140.0;
            const double ToAngle = 40.0;

            var buttons = ArcButtons.Where(b => b.IsVisible).ToArray();
            if (buttons.Length == 0) return;

            var angles = new double[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                // A lone button goes straight down rather than to the left end,
                // which is what the midpoint gives and what the division by
                // zero below would not.
                angles[i] = buttons.Length == 1
                    ? (FromAngle + ToAngle) / 2
                    : FromAngle + (ToAngle - FromAngle) * i / (buttons.Length - 1);
            }

            var cx = new double[angles.Length];
            var cy = new double[angles.Length];
            for (int i = 0; i < angles.Length; i++)
            {
                var rad = angles[i] * Math.PI / 180.0;
                cx[i] = ArcRadius * Math.Cos(rad);
                cy[i] = ArcRadius * Math.Sin(rad);
            }

            double left = double.MaxValue, top = double.MaxValue;
            double right = double.MinValue, bottom = double.MinValue;
            for (int i = 0; i < angles.Length; i++)
            {
                left = Math.Min(left, cx[i] - ButtonHalf);
                right = Math.Max(right, cx[i] + ButtonHalf);
                top = Math.Min(top, cy[i] - ButtonHalf);
                bottom = Math.Max(bottom, cy[i] + ButtonHalf);
            }

            var w = Math.Ceiling(right - left);
            var h = Math.Ceiling(bottom - top);

            Root.Width = w;
            Root.Height = h;
            Width = w;
            Height = h;

            for (int i = 0; i < buttons.Length; i++)
            {
                Canvas.SetLeft(buttons[i], cx[i] - ButtonHalf - left);
                Canvas.SetTop(buttons[i], cy[i] - ButtonHalf - top);
            }

            ArcOriginX = -left;
            ArcOriginY = -top;
        }

        public void SetArranged(bool arranged)
        {
            ArrangeFill.Fill = arranged ? ArrangeActiveFill : ArrangeNormalFill;
        }

        // Three looks, because there are three things the button can mean. Blue
        // with a stop square while audio plays, amber with an hourglass while the
        // engine is still working towards its first sound, and the plain speaker
        // otherwise. Pressing it cancels in either of the first two states, so
        // both have to read as "press again to stop".
        public void SetSpeakState(TextToSpeech.SpeakState state)
        {
            SpeakFill.Fill = state switch
            {
                TextToSpeech.SpeakState.Speaking => SpeakActiveFill,
                TextToSpeech.SpeakState.Preparing => SpeakPreparingFill,
                _ => SpeakNormalFill
            };

            var (glyph, mark) = SymbolMarks.SpeakLook(state);

            SpeakGlyph.Text = glyph;
            SpeakGlyph.IsVisible = glyph is not null;
            SpeakStopMark.Data = mark is null ? null : StreamGeometry.Parse(mark);
            SpeakStopMark.IsVisible = mark is not null;

            // And what it says it is. This button is three things depending on
            // state, and a tooltip fixed at "read aloud" would be wrong on two
            // of them — the glyph already changes, and the words have to agree
            // with the glyph or they are worse than no words.
            ToolTip.SetTip(SpeakButton, OrbWindow.ThoughtBubble(state switch
            {
                TextToSpeech.SpeakState.Speaking => "Stop",
                TextToSpeech.SpeakState.Preparing => "Preparing…",
                _ => "Read aloud"
            }, null, compact: true));
        }

        // Through RealHover (CB-302): the flyout is a window of its own and can
        // miss its leave just like an orb, after which Root.IsPointerOver alone
        // would hold it open for a pointer that is long gone.
        public bool IsPointerOverFlyout => RealHover.IsOver(this, Root);

        public void ShowNear(PixelPoint from, PixelPoint to, Window owner)
        {
            if (IsVisible)
            {
                Position = to;
                return;
            }

            Position = from;
            Opacity = 0;
            Show();
            this.PlaceJustBehind(owner);
            AnimateTo(from, to);
        }

        public new void Hide()
        {
            _flyTimer?.Stop();
            base.Hide();
        }

        private void AnimateTo(PixelPoint from, PixelPoint to)
        {
            _flyTimer?.Stop();
            _flyFrom = from;
            _flyTo = to;
            _flyStartedAt = Environment.TickCount64;

            _flyTimer = new DispatcherTimer { Interval = FlyTick };
            _flyTimer.Tick += (_, _) =>
            {
                var elapsed = Environment.TickCount64 - _flyStartedAt;
                var t = Math.Min(1.0, elapsed / (double)FlyMs);
                var eased = 1 - Math.Pow(1 - t, 3);

                Position = new PixelPoint(
                    (int)Math.Round(_flyFrom.X + (_flyTo.X - _flyFrom.X) * eased),
                    (int)Math.Round(_flyFrom.Y + (_flyTo.Y - _flyFrom.Y) * eased));
                Opacity = eased;

                if (t < 1.0) return;

                _flyTimer!.Stop();
                Position = _flyTo;
                Opacity = 1;
                this.PlaceInFront();
            };
            _flyTimer.Start();
        }
    }
}

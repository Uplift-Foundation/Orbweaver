using Avalonia;

namespace Orbweaver
{
    // CB-286: keeps a chat panel invisible until it has been laid out at the
    // size it is about to be shown at.
    //
    // The transient panel is one window reused across orbs, so when Bind
    // changes its Width and Height the first frame can still carry the layout
    // (and the per-row widths) of the conversation shown before — measured
    // headless: Bounds and every row's width still the previous panel's right
    // after a visible rebind. The window is transparent, so a frame drawn from
    // that layout would be seen against whatever is behind it. Nothing here claims to know what the
    // native layer presents; the gate just refuses to let anything be seen until the layout
    // agrees with the target, and fails open so a panel that never settles is
    // still a panel.
    //
    // Pure: every effect is a delegate, so the sequencing is tested without a
    // window.
    internal sealed class ChatPanelReveal
    {
        // Frames that must pass before a settled reading is believed, and the
        // frame after which the panel is shown regardless.
        internal const int MinFrames = 2;
        internal const int MaxFrames = 12;

        private const double SizeTolerance = 0.5;
        private const double ScaleTolerance = 0.01;

        private readonly Func<bool> _settled;
        private readonly Action _hide;
        private readonly Action _reveal;
        private readonly Action<Action> _nextFrame;
        private readonly Func<Action, IDisposable> _startDeadline;

        private int _generation;
        private int _frames;
        private IDisposable? _deadline;

        public bool Pending { get; private set; }

        public ChatPanelReveal(
            Func<bool> settled, Action hide, Action reveal,
            Action<Action> nextFrame, Func<Action, IDisposable> startDeadline)
        {
            _settled = settled;
            _hide = hide;
            _reveal = reveal;
            _nextFrame = nextFrame;
            _startDeadline = startDeadline;
        }

        // Whether the laid-out size and the window's scale are the ones the
        // panel is about to be shown at. A screen scale that is not a usable
        // number skips the scale half rather than failing it forever.
        public static bool IsSettled(Size target, Size laidOut, double windowScale, double screenScale)
        {
            if (Math.Abs(target.Width - laidOut.Width) > SizeTolerance) return false;
            if (Math.Abs(target.Height - laidOut.Height) > SizeTolerance) return false;
            if (!double.IsFinite(screenScale) || screenScale <= 0) return true;

            return Math.Abs(windowScale - screenScale) <= ScaleTolerance;
        }

        public void Begin()
        {
            _deadline?.Dispose();
            _generation++;
            _frames = 0;
            Pending = true;
            _hide();

            var generation = _generation;
            _deadline = _startDeadline(() =>
            {
                if (generation != _generation || !Pending) return;
                Finish();
            });
        }

        // Asks for the first frame. Separate from Begin because Bind hides the
        // panel before it shows it, and a window that is not open yet is not
        // promised to tick animation frames at all — so the frames are only
        // counted from after Show(), and the deadline covers the gap anyway.
        public void Start()
        {
            if (Pending) RequestFrame(_generation);
        }

        // Stops without revealing: the panel is being hidden, and whatever is
        // pending must not show it again.
        public void Cancel()
        {
            _generation++;
            Pending = false;
            _deadline?.Dispose();
            _deadline = null;
        }

        private void RequestFrame(int generation) => _nextFrame(() =>
        {
            if (generation != _generation || !Pending) return;

            _frames++;
            if (_frames >= MaxFrames || (_frames >= MinFrames && _settled()))
            {
                Finish();
                return;
            }

            RequestFrame(generation);
        });

        private void Finish()
        {
            Pending = false;
            _deadline?.Dispose();
            _deadline = null;
            _reveal();
        }
    }
}

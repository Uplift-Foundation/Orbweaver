using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Orbweaver
{
    // What an amber Paperclip orb is waiting on, answerable in place (CB-427).
    //
    // One instance per production process (a second open would answer the same
    // items twice), but the constructor takes its three effects so a suite can
    // drive it with a fake IPaperclipActions and no browser:
    //   actions  the seam answers go through, null while Paperclip is off
    //   open     "Review in Paperclip", given the need's own page
    // (The rescan after an answer is the implementation's: PaperclipActions.)
    //
    // Honest about what happened to each answer: Done removes the row, an item
    // already answered elsewhere says so and goes too (it is not an error), and
    // a failure keeps the row answerable with the reason beside it. Rows that
    // were answered here stay hidden until the poll stops listing them, so a
    // stale snapshot cannot resurrect a decided item.
    public partial class PaperclipNeedsPanel : Window
    {
        internal sealed class NeedRow
        {
            internal PaperclipNeed Need { get; }
            internal Border Card { get; }
            internal Button? ApproveButton { get; init; }
            internal Button? DeclineButton { get; init; }
            internal Button ReviewButton { get; init; } = null!;
            internal TextBlock Status { get; init; } = null!;
            internal bool Busy { get; set; }
            internal string Message { get; set; } = "";
            internal NeedRow(PaperclipNeed need, Border card) { Need = need; Card = card; }
        }

        private static PaperclipNeedsPanel? _instance;

        private readonly Func<IPaperclipActions?> _actions;
        private readonly Action<string> _open;
        private readonly Dictionary<string, NeedRow> _rows = new();
        private readonly Dictionary<string, string> _messages = new();
        private readonly HashSet<string> _answered = new();
        // Cards kept on screen to say "already answered", removed when the poll drops the item.
        private readonly Dictionary<string, Border> _decided = new();
        // How long an answer's words ("Approved.", "Already answered in Paperclip")
        // stay readable, whatever the poll does meanwhile.
        internal static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);
        internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        // Runs `Sweep` after the delay; a test captures it instead of waiting.
        internal Action<TimeSpan, Action> Schedule { get; set; } = (d, a) => DispatcherTimer.RunOnce(a, d);
        internal static Func<PaperclipNeedsPanel> Create { get; set; } = () => new PaperclipNeedsPanel();
        private DateTime _holdUntil = DateTime.MinValue;
        private string _agentName = "";
        private string _agentId = "";

        public PaperclipNeedsPanel() : this(
            () => PaperclipSessions.Actions, url => CloudSessionLink.Open(url)) { }

        internal PaperclipNeedsPanel(Func<IPaperclipActions?> actions, Action<string> open)
        {
            _actions = actions;
            _open = open;
            InitializeComponent();
            KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
            // A closed Avalonia window cannot be shown again, so the singleton must not
            // outlive its window (the second amber click used to throw).
            Closed += (_, _) => { if (ReferenceEquals(_instance, this)) _instance = null; };
            Deactivated += (_, _) => Dispatcher.UIThread.Post(CloseIfIdle, DispatcherPriority.Background);
        }

        // The orb's update path: an open panel follows its orb's needs. A closed
        // or absent panel is left alone (never created here).
        internal static void Refresh(SessionStatus status)
        {
            if (_instance is { IsVisible: true } p && p._agentId == status.PaperclipAgentId) p.Present(status);
        }

        internal static bool IsOpen => _instance is { IsVisible: true };

        internal IReadOnlyCollection<NeedRow> NeedRows => _rows.Values;
        internal NeedRow? RowFor(string needId) => _rows.TryGetValue(needId, out var r) ? r : null;
        internal string HeadingText => Heading.Text ?? "";
        internal string? FooterText => Footer.IsVisible ? Footer.Text : null;

        internal static void ShowFor(SessionStatus status, PixelPoint anchor, PixelRect work)
        {
            _instance ??= Create();
            _instance.Present(status);
            if (!_instance.IsVisible) _instance.Show();
            var size = new PixelSize((int)(_instance.Width * _instance.RenderScaling),
                Math.Max(80, (int)(_instance.Bounds.Height * _instance.RenderScaling)));
            _instance.Position = ChatPanelPlacement.Resolve(anchor, size, 8, work, Array.Empty<PixelRect>());
            _instance.Activate();
        }

        // Called with the orb's latest status, on show and on every update while
        // open, so a need that leaves Paperclip leaves the panel too.
        internal void Present(SessionStatus status)
        {
            _agentName = status.Title;
            _agentId = status.PaperclipAgentId;
            var needs = status.Needs;
            var live = needs.Select(n => n.Id).ToHashSet();
            _answered.RemoveWhere(id => !live.Contains(id));
            foreach (var gone in _rows.Keys.Where(id => !live.Contains(id)).ToList())
            {
                Rows.Children.Remove(_rows[gone].Card);
                _rows.Remove(gone);
                _messages.Remove(gone);
            }

            foreach (var need in needs)
            {
                if (_answered.Contains(need.Id) || _rows.ContainsKey(need.Id)) continue;
                var row = BuildRow(need);
                _rows[need.Id] = row;
                Rows.Children.Add(row.Card);
            }

            UpdateHeading();
            if (_rows.Count == 0 && Clock() >= _holdUntil && IsVisible) Close();
        }

        private void UpdateHeading() =>
            Heading.Text = _rows.Count == 0 ? _agentName
                : _rows.Count == 1 ? _agentName + " is waiting on you"
                : $"{_agentName} is waiting on {_rows.Count} things";

        private NeedRow BuildRow(PaperclipNeed need)
        {
            var title = new TextBlock
            {
                Text = need.Title, FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
            };
            var status = new TextBlock
            {
                FontSize = 10.5, TextWrapping = TextWrapping.Wrap, IsVisible = false,
                Foreground = new SolidColorBrush(Color.Parse("#FFFFB4A8")),
            };
            var review = Link("Review in Paperclip");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            Button? approve = null, decline = null;
            if (PaperclipActions.Answerable(need))
            {
                approve = Pill("Approve", "#FF2E7D4F");
                decline = Pill("Decline", "#FF8A3A3A");
                buttons.Children.Add(approve);
                buttons.Children.Add(decline);
            }
            buttons.Children.Add(review);

            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(title);
            // What tells two of one kind apart, when Paperclip gave one: dim, short, wrapped.
            if (!string.IsNullOrWhiteSpace(need.Summary))
                body.Children.Add(new TextBlock
                {
                    Text = need.Summary.Length > 240 ? need.Summary[..240].TrimEnd() + "…" : need.Summary,
                    FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#B3FFFFFF")),
                });
            body.Children.Add(buttons);
            body.Children.Add(status);
            var card = new Border
            {
                Padding = new Thickness(10), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.Parse("#26FFFFFF")),
                Child = body,
            };
            var row = new NeedRow(need, card)
            {
                ApproveButton = approve, DeclineButton = decline, ReviewButton = review, Status = status,
            };
            if (approve != null) approve.Click += async (_, _) => await AnswerAsync(row, approve: true);
            if (decline != null) decline.Click += async (_, _) => await AnswerAsync(row, approve: false);
            review.Click += (_, _) => _open(need.Url);
            return row;
        }

        private static Button Pill(string text, string fill) => new()
        {
            Content = text, FontSize = 11, Padding = new Thickness(12, 4),
            Background = new SolidColorBrush(Color.Parse(fill)), Foreground = Brushes.White,
        };

        private static Button Link(string text) => new()
        {
            Content = text, FontSize = 10.5, Padding = new Thickness(6, 4),
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.Parse("#FF8FB8FF")),
        };

        // One answer. The row's buttons are held while it is in flight so a
        // double click cannot answer twice. Done removes the row at once; an item
        // answered elsewhere is replaced by that sentence for the life of the
        // panel (ordinary text, not an error); anything else puts Why under the
        // row in the warning colour and leaves it answerable.
        internal async Task AnswerAsync(NeedRow row, bool approve)
        {
            if (row.Busy) return;
            var actions = _actions();
            if (actions == null)
            {
                Show(row, "Paperclip is not connected.");
                return;
            }
            SetBusy(row, true);
            PaperclipOutcome result;
            try
            {
                result = await actions.DecideAsync(row.Need, approve, default);
            }
            catch (Exception ex)
            {
                // Never the exception's own text: it can carry an address or a path.
                DiagnosticsLog.Write("paperclip: answer threw " + ex.GetType().Name);
                result = new(PaperclipActionResult.Unreachable, PaperclipActions.UnreachableWhy);
            }

            SetBusy(row, false);
            if (!result.Settled)
            {
                Show(row, result.Why ?? PaperclipActions.RefusedWhy);
                return;
            }

            _answered.Add(row.Need.Id);
            _rows.Remove(row.Need.Id);
            if (result.Result == PaperclipActionResult.AlreadyDecided)
            {
                row.Card.Child = new TextBlock
                {
                    Text = result.Why ?? PaperclipActions.AlreadyDecidedWhy, FontSize = 11,
                    Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                };
                _decided[row.Need.Id] = row.Card;
            }
            else Rows.Children.Remove(row.Card);
            UpdateHeading();
            Footer.Text = result.IsDone ? (result.Why ?? (approve ? "Approved." : "Declined.")) : null;
            Footer.IsVisible = result.IsDone;
            _holdUntil = Clock() + Hold;
            Schedule(Hold, Sweep);
        }

        private void Show(NeedRow row, string message)
        {
            row.Message = message;
            _messages[row.Need.Id] = message;
            row.Status.Text = message;
            row.Status.IsVisible = true;
        }

        private static void SetBusy(NeedRow row, bool busy)
        {
            row.Busy = busy;
            if (row.ApproveButton != null) row.ApproveButton.IsEnabled = !busy;
            if (row.DeclineButton != null) row.DeclineButton.IsEnabled = !busy;
        }

        // The hold is over: the answered-elsewhere sentences and the footer go, and
        // the panel with them when nothing is left to answer. A later answer
        // moved the deadline, so an early sweep does nothing.
        internal void Sweep()
        {
            if (Clock() < _holdUntil) return;
            foreach (var card in _decided.Values) Rows.Children.Remove(card);
            _decided.Clear();
            Footer.IsVisible = false;
            UpdateHeading();
            if (_rows.Count == 0 && IsVisible) Close();
        }

        private void CloseIfIdle()
        {
            if (!IsActive && !_rows.Values.Any(r => r.Busy)) Close();
        }
    }
}

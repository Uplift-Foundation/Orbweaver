using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Orbweaver
{
    // The detail behind one account's rings: two bars, sometimes a third, and
    // the reset times the rings cannot show.
    //
    // A third small window rather than a reuse of either existing one. OrbFlyout
    // is the right *shape* — a hover surface that never takes focus — but it is
    // an arc of round buttons; ChatPanel is the right *lifecycle* but carries a
    // transcript virtualiser, backlog paging, markdown, attachments, dictation
    // and eight resize strips behind it. What is borrowed is named where it is
    // used: the hover bridge's confirmation rule from OrbWindow, and the
    // flip-above-rather-than-clamp anchoring from ChatPanel.Reposition.
    //
    // One instance per open card, unlike ChatPanel's single static one, because
    // the entire point of pinning is two accounts visible at once.
    internal partial class UsageCard : Window
    {
        // How far below the orb the card sits. Enough that the card is clearly
        // a separate thing, small enough that the pointer crosses the gap before
        // the hover bridge's grace period runs out.
        private const double Gap = 8;

        private double _sessionFraction;
        private double _weeklyFraction;
        private double _extraFraction;
        private bool _pinned;

        public UsageCard()
        {
            InitializeComponent();

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();
                this.AcceptFirstClick();
            };

            Root.PointerEntered += (_, _) => PointerEnteredCard?.Invoke(this);
            Root.PointerExited += (_, _) => PointerExitedCard?.Invoke(this);

            PinButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                PinToggled?.Invoke(this);
            };

            // The bars are a fraction of whatever width the card ended up, and
            // that width is not known until layout has run. Recomputing on
            // SizeChanged as well as on update is what stops every bar from
            // being drawn at zero on the first frame.
            SessionTrack.SizeChanged += (_, _) => ApplyBars();
            WeeklyTrack.SizeChanged += (_, _) => ApplyBars();
            ExtraTrack.SizeChanged += (_, _) => ApplyBars();
        }

        internal string AccountKey { get; private set; } = string.Empty;

        internal event Action<UsageCard>? PointerEnteredCard;
        internal event Action<UsageCard>? PointerExitedCard;
        internal event Action<UsageCard>? PinToggled;

        // Asked by the hover bridge's confirmation tick, in the moment between
        // the pointer leaving the orb and arriving here. Reads the transparent
        // Root rather than the window, which is why that Root has a background
        // at all.
        internal bool IsPointerOverCard => Root.IsPointerOver;

        internal bool IsPinned => _pinned;

        // Exposed for the headless suite, which asserts on what a person would
        // have read rather than on how it was laid out.
        internal string SessionText => SessionPercent.Text ?? string.Empty;

        internal string WeeklyText => WeeklyPercent.Text ?? string.Empty;

        internal string ExtraNoteText => ExtraNote.Text ?? string.Empty;

        internal bool ShowsExtraBar => ExtraTrack.IsVisible;

        internal bool ShowsStaleNote => StaleNote.IsVisible;

        internal string StaleText => StaleNote.Text ?? string.Empty;

        // Named for the suite that clicks it. Exposed as a property rather than
        // reached through the generated field so a rename of the XAML element
        // breaks the compile rather than a test's assumptions.
        internal Control PinControl => PinButton;

        internal void SetPinned(bool pinned)
        {
            _pinned = pinned;
            PinGlyph.Opacity = pinned ? 1 : 0.5;
            PinButton.Background = pinned
                ? new SolidColorBrush(Color.Parse("#26FFFFFF"))
                : Brushes.Transparent;
        }

        // CB-287 read-backs: what the card shows, not how it is drawn.
        internal string AccountNameText => AccountName.Text ?? string.Empty;

        internal string? AccountDotHex => (AccountDot.Fill as SolidColorBrush)?.Color.ToString();

        // displayName is CB-287's override of the name only. The dot below stays
        // coloured from the *derived* label: colour is identity and the name is
        // presentation, so renaming an account must not re-colour it.
        internal void UpdateFrom(AccountUsage usage, string? email, DateTimeOffset now, string? displayName = null)
        {
            AccountKey = usage.ConfigDir ?? string.Empty;

            AccountName.Text = displayName ?? usage.Label;
            AccountWhere.Text = email ?? Where(usage.ConfigDir);
            AccountDot.Fill = new SolidColorBrush(
                Color.Parse(AgentPalette.HexFor(usage.Label)));

            var session = usage.LiveSession(now);
            var weekly = usage.LiveWeekly(now);

            _sessionFraction = Window(SessionRow, SessionTrack, SessionPercent,
                                      SessionResets, SessionFill, session, ShortTime);
            _weeklyFraction = Window(WeeklyRow, WeeklyTrack, WeeklyPercent,
                                     WeeklyResets, WeeklyFill, weekly, DayAndTime);

            ApplyExtra(usage.Extra);

            // Two different silences, and they are not the same news. An account
            // with no subscription windows is working exactly as intended; an
            // account nobody has managed to ask might be at 99%.
            if (!usage.Available)
            {
                StaleNote.IsVisible = true;
                StaleNote.Text = "No subscription limits on this account.";
            }
            else if (usage.IsStale(now))
            {
                StaleNote.IsVisible = true;
                // "As of", not "last read", because for two of the three
                // sources those are different moments. Claude Code is asked and
                // answers about now; Codex and Grok are read out of a file their
                // CLI last wrote whenever it last ran, so the age that matters
                // to someone looking at a percentage is the age of the number,
                // not of the file read. See AccountUsage.AsOf.
                StaleNote.Text = $"Usage as of {Ago(now - usage.AsOf)} ago.";
            }
            else if (session is null && weekly is null)
            {
                StaleNote.IsVisible = true;
                StaleNote.Text = "No reading yet.";
            }
            else
            {
                StaleNote.IsVisible = false;
            }

            ApplyBars();
        }

        // One window's row, returning the fraction its bar should fill.
        //
        // A window with no live reading hides its whole row rather than showing
        // a bar at zero, for the reason that runs through this feature: an empty
        // gauge is a claim, and it is the wrong one.
        private static double Window(
            Control row, Control track, TextBlock percent, TextBlock resets,
            Border fill, UsageWindow? window, Func<DateTimeOffset, string> format)
        {
            if (window is null)
            {
                row.IsVisible = false;
                track.IsVisible = false;
                return 0;
            }

            row.IsVisible = true;
            track.IsVisible = true;

            // Floor, matching what the CLI's own /usage prints, so a person
            // comparing the two never sees them disagree by a point.
            percent.Text = Math.Floor(window.Percent).ToString(CultureInfo.CurrentCulture) + "%";
            resets.Text = window.ResetsAt is { } at ? format(at.ToLocalTime()) : string.Empty;

            fill.Background = new SolidColorBrush(Color.Parse(
                UsageRingGeometry.ColourFor(
                    window.Percent,
                    AccountOrbWindow.CalmHex,
                    AccountOrbWindow.WarnHex,
                    AccountOrbWindow.DangerHex)));

            return Math.Clamp(window.Percent / 100.0, 0, 1);
        }

        private void ApplyExtra(ExtraUsage? extra)
        {
            if (extra?.Percent is not { } percent)
            {
                ExtraRow.IsVisible = false;
                ExtraTrack.IsVisible = false;
                _extraFraction = 0;

                ExtraNote.Text = extra is null
                    ? string.Empty
                    : ExtraSentence(extra);
                ExtraNote.IsVisible = ExtraNote.Text.Length > 0;
                return;
            }

            ExtraRow.IsVisible = true;
            ExtraTrack.IsVisible = true;
            ExtraNote.IsVisible = false;

            ExtraAmount.Text = Money(extra.UsedMinor, extra);
            ExtraOf.Text = "of " + Money(extra.LimitMinor, extra);

            ExtraFill.Background = new SolidColorBrush(Color.Parse(
                UsageRingGeometry.ColourFor(
                    percent,
                    AccountOrbWindow.CalmHex,
                    AccountOrbWindow.WarnHex,
                    AccountOrbWindow.DangerHex)));

            _extraFraction = Math.Clamp(percent / 100.0, 0, 1);
        }

        // Why there is no extra-usage bar, in words — and only in words this app
        // can stand behind.
        //
        // The first version of this method translated `disabled_reason` into
        // English, and mapped the one code it had ever seen,
        // "org_level_disabled_until", to "Extra usage is off for your
        // organisation". That sentence was shown to a user whose organisation
        // had not switched anything off; what had actually happened was the
        // month's extra-usage budget running out. The word "until" in that code
        // was the clue, and reading it as a settled policy rather than a
        // deadline was an invention.
        //
        // So the order below is deliberate: **the explicit booleans first, the
        // opaque string never.** `spend_limit_reached` and `user_disabled` are
        // specific named facts the API asserts. `disabled_reason` is shown
        // verbatim, in parentheses, so it stays diagnosable — but it is never
        // paraphrased, because a code seen once with no documentation behind it
        // is not a sentence.
        internal static string ExtraSentence(ExtraUsage extra)
        {
            if (extra.SpendLimitReached) return "Extra usage limit reached for this month.";

            if (extra.Enabled) return "Extra usage is on, with no limit set.";

            if (extra.UserDisabled) return "Extra usage is switched off for this account.";

            return string.IsNullOrEmpty(extra.DisabledReason)
                ? "Extra usage is not active."
                : "Extra usage is not active right now (" + extra.DisabledReason + ").";
        }

        internal static string Money(long? minor, ExtraUsage extra)
        {
            if (minor is null) return "—";

            var divisor = Math.Pow(10, extra.DecimalPlaces);
            var amount = minor.Value / divisor;
            var symbol = extra.Currency == "USD" ? "$" : extra.Currency + " ";

            return symbol + amount.ToString(
                "N" + extra.DecimalPlaces.ToString(CultureInfo.InvariantCulture),
                CultureInfo.CurrentCulture);
        }

        internal static string ShortTime(DateTimeOffset at) =>
            at.ToString("h:mm tt", CultureInfo.CurrentCulture);

        internal static string DayAndTime(DateTimeOffset at) =>
            at.ToString("ddd h:mm tt", CultureInfo.CurrentCulture);

        internal static string Ago(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1)) return "moments";
            if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m";
            if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h";
            return $"{(int)span.TotalDays}d";
        }

        private static string Where(string? configDir) =>
            string.IsNullOrEmpty(configDir) ? "~/.claude" : configDir;

        private void ApplyBars()
        {
            Fill(SessionTrack, SessionFill, _sessionFraction);
            Fill(WeeklyTrack, WeeklyFill, _weeklyFraction);
            Fill(ExtraTrack, ExtraFill, _extraFraction);
        }

        private static void Fill(Control track, Border fill, double fraction)
        {
            var width = track.Bounds.Width;
            fill.Width = width > 0 ? width * fraction : 0;
        }

        // Below the orb, unless below is off the screen, in which case above.
        //
        // Flip rather than clamp, which is ChatPanel's rule and worth keeping
        // for its reason: a clamped card slides up until it covers the orb the
        // pointer is resting on, and covering the orb breaks the hover bridge
        // that is keeping the card open in the first place.
        internal void ShowNear(AccountOrbWindow orb)
        {
            if (!IsVisible) Show();

            Reposition(orb);
            this.PlaceInFront();
        }

        internal void Reposition(AccountOrbWindow orb)
        {
            // The orb's centre in screen space. PointToScreen rather than
            // arithmetic on Position, because Position is physical pixels and
            // these are device-independent ones — the two agree only at 100%
            // scaling, which is the one machine a bug like that gets tested on.
            var centre = orb.PointToScreen(new Point(36, 36));
            var screen = Screens.ScreenFromPoint(centre) ?? Screens.Primary;
            var scale = screen?.Scaling ?? 1.0;

            var cardWidth = (int)Math.Round(Width * scale);
            var cardHeight = (int)Math.Round(Bounds.Height * scale);
            if (cardHeight <= 0) cardHeight = (int)Math.Round(200 * scale);

            var orbHalf = (int)Math.Round(36 * scale);
            var gap = (int)Math.Round(Gap * scale);

            var x = centre.X - cardWidth / 2;
            var below = centre.Y + orbHalf + gap;
            var above = centre.Y - orbHalf - gap - cardHeight;

            var work = screen?.WorkingArea;
            var y = below;

            if (work is { } area)
            {
                if (below + cardHeight > area.Y + area.Height && above >= area.Y) y = above;

                x = Math.Max(area.X, Math.Min(x, area.X + area.Width - cardWidth));
            }

            Position = new PixelPoint(x, y);
        }
    }
}

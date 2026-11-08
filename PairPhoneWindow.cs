using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QRCoder;

namespace Orbweaver
{
    // What the pairing window needs from the relay client, as an interface so
    // the UI suite can drive every state with a fake instead of a socket.
    internal interface IPairingClient
    {
        Task<RelayLink.PairOffer> BeginPairingAsync(CancellationToken token = default);
        bool Allow(RelayLink.PairRequest request);
        void Deny(RelayLink.PairRequest request);
        void CancelPairing();
        event Action<RelayLink.PairRequest> PairRequested;
    }

    // "Pair phone…" from the tray (CB-314): a QR code for the phone to scan,
    // then the phone's name and the six-digit check code both screens show,
    // and Allow. The owner's click is the authorisation — nothing pairs
    // without it — and the code is what makes that click informed: a relay
    // that swapped a key in the middle changes the number, and the two
    // screens stop agreeing.
    //
    // One window at a time, through Toggle, in NewChatWindow's singleton shape
    // and with the same split: the decisions are covered, Present/Activate/
    // OnClosed are OS work and excluded.
    internal sealed class PairPhoneWindow : Window
    {
        internal enum Stage { Loading, Showing, Asking, Paired, Expired, Failed }

        private static PairPhoneWindow? _open;

        internal static PairPhoneWindow? OpenForTests { get => _open; set => _open = value; }
        internal static Action<PairPhoneWindow>? PresentForTests;

        internal static void Toggle(IPairingClient client)
        {
            if (_open is not null)
            {
                Activate(_open);
                return;
            }
            _open = new PairPhoneWindow(client, () => DateTimeOffset.UtcNow, startTimer: true);
            _open.Closed += OnClosed;
            Present(_open);
            _ = _open.BeginAsync();
        }

        // Both OS halves defer to PresentForTests when a test has set it, so the
        // open-versus-bring-forward decision above runs in a test unchanged.
        [ExcludeFromCodeCoverage]
        private static void Present(PairPhoneWindow window)
        {
            if (PresentForTests is { } fake) { fake(window); return; }
            MacOSActivation.SetRegular();
            window.Show();
            window.Activate();
        }

        [ExcludeFromCodeCoverage]
        private static void Activate(PairPhoneWindow window)
        {
            if (PresentForTests is { } fake) { fake(window); return; }
            MacOSActivation.SetRegular();
            window.Activate();
        }

        [ExcludeFromCodeCoverage]
        private static void OnClosed(object? sender, EventArgs e)
        {
            (sender as PairPhoneWindow)?.Forget();
            MacOSActivation.SetAccessory();
        }

        private readonly IPairingClient _client;
        private readonly Func<DateTimeOffset> _now;
        private readonly DispatcherTimer? _timer;
        private RelayLink.PairOffer? _offer;
        private RelayLink.PairRequest? _request;
        private CancellationTokenSource _cts = new();

        internal Stage Current { get; private set; } = Stage.Loading;
        internal TextBlock Heading { get; } = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Name = "PairHeading", TextWrapping = TextWrapping.Wrap };
        internal TextBlock Detail { get; } = new() { FontSize = 12, Opacity = 0.75, Name = "PairDetail", TextWrapping = TextWrapping.Wrap };
        internal TextBlock Countdown { get; } = new() { FontSize = 12, Opacity = 0.6, Name = "PairCountdown" };
        internal TextBlock Code { get; } = new() { FontSize = 34, FontWeight = FontWeight.Bold, Name = "PairCode", HorizontalAlignment = HorizontalAlignment.Center };
        internal QrView Qr { get; } = new() { Name = "PairQr", Width = 320, Height = 320, HorizontalAlignment = HorizontalAlignment.Center };
        internal Button AllowButton { get; } = new() { Content = "Allow", Name = "PairAllow", IsDefault = true };
        internal Button DenyButton { get; } = new() { Content = "Deny", Name = "PairDeny" };
        internal Button RetryButton { get; } = new() { Content = "Try again", Name = "PairRetry" };
        internal Button CloseButton { get; } = new() { Content = "Close", Name = "PairClose" };

        internal PairPhoneWindow(IPairingClient client, Func<DateTimeOffset> now, bool startTimer = false)
        {
            _client = client;
            _now = now;
            Title = "Pair phone";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            CanResize = false;

            AllowButton.Click += (_, _) => OnAllow();
            DenyButton.Click += (_, _) => OnDeny();
            RetryButton.Click += (_, _) => _ = BeginAsync();
            CloseButton.Click += (_, _) => Close();
            _client.PairRequested += OnPairRequested;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(DenyButton);
            buttons.Children.Add(AllowButton);
            buttons.Children.Add(RetryButton);
            buttons.Children.Add(CloseButton);

            var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            body.Children.Add(Heading);
            body.Children.Add(Qr);
            body.Children.Add(Code);
            body.Children.Add(Detail);
            body.Children.Add(Countdown);
            body.Children.Add(buttons);
            Content = body;

            if (startTimer)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (_, _) => Tick();
                _timer.Start();
            }
            Show(Stage.Loading);
        }

        // Asks the relay for an offer and shows its QR code. Any failure is a
        // stage the window can show, never an exception out of a click.
        internal async Task BeginAsync()
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _request = null;
            Show(Stage.Loading);
            try
            {
                var offer = await _client.BeginPairingAsync(token);
                if (token.IsCancellationRequested) return;
                _offer = offer;
                Qr.Modules = QrView.Encode(offer.QrText);
                Show(Stage.Showing);
            }
            catch (Exception ex) when (ex is HttpRequestException or RelayHttpException or IOException
                                       or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (token.IsCancellationRequested) return;
                Show(Stage.Failed, ex is TaskCanceledException ? "The relay did not answer." : ex.Message);
            }
        }

        // Called from the client's thread; the stage changes on the UI thread.
        private void OnPairRequested(RelayLink.PairRequest request) =>
            Dispatcher.UIThread.Post(() => Ask(request));

        internal void Ask(RelayLink.PairRequest request)
        {
            // Showing and Asking are only ever reached with an offer in hand.
            if (Current is not (Stage.Showing or Stage.Asking) || _offer!.OfferId != request.OfferId) return;
            _request = request;
            Show(Stage.Asking);
        }

        internal void OnAllow()
        {
            if (_request is null) return;
            if (_client.Allow(_request)) Show(Stage.Paired);
            else Detail.Text = "Lost the connection to the relay. Allow again once it is back.";
        }

        internal void OnDeny()
        {
            if (_request is not null) _client.Deny(_request);
            _request = null;
            Show(Stage.Showing);
        }

        // Once a second: the countdown, and the offer's expiry.
        internal void Tick()
        {
            if (_offer is null || Current is not (Stage.Showing or Stage.Asking)) return;
            var left = _offer.ExpiresAt - _now();
            if (left <= TimeSpan.Zero)
            {
                _client.CancelPairing();
                Show(Stage.Expired);
                return;
            }
            Countdown.Text = $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
        }

        internal void Forget()
        {
            _timer?.Stop();
            _cts.Cancel();
            _client.PairRequested -= OnPairRequested;
            if (Current is Stage.Showing or Stage.Asking) _client.CancelPairing();
            if (ReferenceEquals(_open, this)) _open = null;
        }

        private void Show(Stage stage, string? error = null)
        {
            Current = stage;
            Qr.IsVisible = stage == Stage.Showing;
            Code.IsVisible = stage == Stage.Asking;
            Countdown.IsVisible = stage is Stage.Showing or Stage.Asking;
            AllowButton.IsVisible = stage == Stage.Asking;
            DenyButton.IsVisible = stage == Stage.Asking;
            RetryButton.IsVisible = stage is Stage.Expired or Stage.Failed;
            CloseButton.IsVisible = stage is Stage.Paired or Stage.Expired or Stage.Failed or Stage.Loading;

            (Heading.Text, Detail.Text) = stage switch
            {
                Stage.Loading => ("Pair a phone", "Contacting the relay…"),
                Stage.Showing => ("Scan with Orbweaver on your iPhone",
                    $"Your phone will then show a six-digit code. {Brand.DisplayName} shows the same code here before anything is paired."),
                Stage.Asking => ($"Pair {_request!.Name}?",
                    "Allow only if your phone shows this same code. A different code means something between them changed a key."),
                Stage.Paired => ($"Paired with {_request!.Name}",
                    "Its orbs appear on the phone while both are online. Remove it any time in Settings › Paired phones."),
                Stage.Expired => ("The code expired", "Codes last five minutes. Try again for a new one."),
                _ => ("Couldn't start pairing", error!),   // Failed always carries its reason
            };
            Code.Text = stage == Stage.Asking ? _request!.CheckCodeDisplay : "";
            if (stage == Stage.Showing && _request is null) Tick();
        }
    }

    // A QR code drawn as rectangles, with no bitmap round trip: QRCoder
    // computes the module matrix — which already includes the four-module
    // quiet zone — and Render fills one square per dark module. Black on white whatever the theme —
    // phone cameras read dark-on-light, and a dark-mode inversion is exactly
    // the thing some scanners refuse.
    internal sealed class QrView : Control
    {
        internal bool[,]? Modules { get; set { field = value; InvalidateVisual(); } }

        internal static bool[,] Encode(string text)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var matrix = data.ModuleMatrix;
            var size = matrix.Count;
            var modules = new bool[size, size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    modules[y, x] = matrix[y][x];
            return modules;
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
            if (Modules is not { } m) return;
            // Whole pixels per module, aligned to whole pixels: a fractional
            // module size antialiases a hairline seam between neighbouring
            // squares, and a decoder reads the seams as light modules. Measured
            // in CB-314: at 3.2 px per module OpenCV decoded nothing from this
            // view, while QRCoder's own PNG of the same text decoded in full.
            var count = m.GetLength(0);
            var side = Math.Min(Bounds.Width, Bounds.Height);
            var module = Math.Max(1, Math.Floor(side / count));
            var origin = Math.Floor((side - module * count) / 2);
            for (var y = 0; y < count; y++)
                for (var x = 0; x < count; x++)
                    if (m[y, x])
                        context.FillRectangle(Brushes.Black, new Rect(origin + x * module, origin + y * module, module, module));
        }
    }
}

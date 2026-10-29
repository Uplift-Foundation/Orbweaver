using Xunit;

namespace Orbweaver.Tests
{
    // Which gateway sessions have no chat surface behind them (CB-270).
    //
    // The rule fails toward None on every doubt, so the cases are arranged as a
    // positive for each native kind followed by every way surface evidence, or
    // the lack of a classification, has to push it back to None. The real rows
    // this was measured against (2026.10.5, scrubbed) are in
    // OpenClawSessionsParseTests and OpenClawGatewayPayloadTests; what each main
    // row carried is recorded there by category.
    public class OpenClawNativeTests
    {
        [Fact]
        public void ADashboardRowWithNoSurfaceEvidenceIsDashboard() =>
            Assert.Equal(OpenClawNativeKind.Dashboard, OpenClawNative.Of("dashboard", null, false, null));

        [Fact]
        public void AMainRowWithNoSurfaceEvidenceIsMain() =>
            Assert.Equal(OpenClawNativeKind.Main, OpenClawNative.Of("main", null, false, null));

        // The WhatsApp main measured live: classification main, provider and
        // deliveryContext and lastChannel all present.
        [Fact]
        public void AWhatsAppMainIsNone() =>
            Assert.Equal(OpenClawNativeKind.None, OpenClawNative.Of("main", "whatsapp", true, "whatsapp"));

        // Each piece of evidence on its own is enough: a row that says only one
        // of the three still has a surface, or might.
        [Theory]
        [InlineData("main", "whatsapp", false, null)]
        [InlineData("main", null, true, null)]
        [InlineData("main", null, false, "whatsapp")]
        [InlineData("dashboard", "discord", false, null)]
        [InlineData("dashboard", null, true, null)]
        [InlineData("dashboard", null, false, "discord")]
        public void AnySingleKindOfSurfaceEvidenceMakesItNone(
            string classification, string? provider, bool deliveryContext, string? lastChannel) =>
            Assert.Equal(
                OpenClawNativeKind.None,
                OpenClawNative.Of(classification, provider, deliveryContext, lastChannel));

        [Theory]
        [InlineData("channel")]
        [InlineData("direct")]
        [InlineData("group")]
        [InlineData("cron")]
        [InlineData("subagent")]
        [InlineData("something-new")]
        [InlineData("")]
        [InlineData(null)]
        public void AnythingElseIsNoneEvenWithNoSurfaceEvidence(string? classification) =>
            Assert.Equal(OpenClawNativeKind.None, OpenClawNative.Of(classification, null, false, null));

        [Theory]
        [InlineData("DASHBOARD", OpenClawNativeKind.Dashboard)]
        [InlineData("Dashboard", OpenClawNativeKind.Dashboard)]
        [InlineData("MAIN", OpenClawNativeKind.Main)]
        [InlineData("Main", OpenClawNativeKind.Main)]
        public void TheClassificationIsCaseInsensitive(string classification, OpenClawNativeKind expected) =>
            Assert.Equal(expected, OpenClawNative.Of(classification, null, false, null));

        // Whitespace is blank, not a provider: a gateway that wrote "" or " "
        // for a field it has nothing for has not named a surface.
        [Theory]
        [InlineData("", "")]
        [InlineData(" ", "\t")]
        [InlineData(null, " ")]
        public void BlankEvidenceIsNoEvidence(string? provider, string? lastChannel) =>
            Assert.Equal(OpenClawNativeKind.Main, OpenClawNative.Of("main", provider, false, lastChannel));
    }
}

using System.Text;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-340: AccountIdentity, the account id a phone merges usage cards on. The fixtures keep the
/// key names and value shapes of the real identity files read off a MacBook (see
/// docs/account-identity-findings.md), with every value replaced by a fabricated one. The shapes
/// are what can break; the values are nobody's business.
/// </summary>
public class AccountIdentityTests
{
    private const string Account = "6f1c2a8e-3b4d-4e5f-9a7b-1c2d3e4f5a6b";
    private const string Org = "0a9b8c7d-6e5f-4a3b-8c2d-1e0f9a8b7c6d";
    private const string CodexAccount = "7e6d5c4b-3a29-4180-8f7e-6d5c4b3a2918";
    private const string CodexUser = "user-q3vx9klm2npr8stt";
    private const string GrokUser = "4c3b2a19-0f8e-4d7c-b6a5-948372615f0e";
    private const string GrokTeam = "8a7b6c5d-4e3f-4210-9fed-cba987654321";

    // ~/.claude.json as it is on a logged-in profile: many unrelated keys, and oauthAccount
    // holding the two ids beside the email and display name.
    private static string ClaudeFile(string account = Account, string org = Org, string email = "user@example.com") => """
    {"numStartups":12,"installMethod":"native","autoUpdates":true,"firstStartTime":"2026-01-02T03:04:05.000Z",
     "oauthAccount":{"accountUuid":"@ACCOUNT@","emailAddress":"@EMAIL@","organizationUuid":"@ORG@",
       "displayName":"User","hasExtraUsageEnabled":false,"organizationRole":"admin"},
     "projects":{}}
    """.Replace("@ACCOUNT@", account).Replace("@EMAIL@", email).Replace("@ORG@", org);

    private static string Segment(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string payloadJson) => "eyJhbGciOiJSUzI1NiJ9." + Segment(payloadJson) + ".c2ln";

    // ~/.codex/auth.json: no top-level email, a tokens object whose id_token carries the claims.
    private static string CodexFile(string idToken, string? accountId = CodexAccount) => """
    {"auth_mode":"chatgpt","OPENAI_API_KEY":null,
     "tokens":{"id_token":"@TOKEN@","access_token":"<never read>","refresh_token":"<never read>"@ACCOUNT@},
     "last_refresh":"2026-11-16T09:00:00Z"}
    """.Replace("@TOKEN@", idToken).Replace("@ACCOUNT@", accountId is null ? "" : ",\"account_id\":\"" + accountId + "\"");

    private static string CodexNested(string user = CodexUser) =>
        Jwt("""{"email":"user@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"@A@","chatgpt_user_id":"@U@"}}"""
            .Replace("@A@", CodexAccount).Replace("@U@", user));

    private static string GrokFile(string user = GrokUser, string team = GrokTeam) => """
    {"a7f3c0e9d2b14e6f8a5c3d1e0f9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a":
      {"user_id":"@U@","principal_id":"@U@","team_id":"@T@","email":"user@example.com"}}
    """.Replace("@U@", user).Replace("@T@", team);

    // ---- Derive ---------------------------------------------------------------------

    [Fact]
    public void Derive_is_32_lowercase_hex_and_carries_none_of_its_inputs()
    {
        var id = AccountIdentity.Derive("claude", Account, Org)!;
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.DoesNotContain(Account, id);
        Assert.DoesNotContain("example.com", id);
        Assert.Equal("ac7d4a85766f600f0bbb94dfc5321dc3", id);   // the vector's, so a second machine agrees
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("user@example.com")]
    public void Derive_gives_nothing_for_a_missing_blank_or_email_like_first_id(string? id1) =>
        Assert.Null(AccountIdentity.Derive("claude", id1, Org));

    [Fact]
    public void Derive_trims_and_lowercases_so_a_provider_changing_case_does_not_split_an_account()
    {
        var plain = AccountIdentity.Derive("claude", Account, Org);
        Assert.Equal(plain, AccountIdentity.Derive("claude", "  " + Account.ToUpperInvariant() + "\t\n", Org.ToUpperInvariant()));
    }

    [Fact]
    public void Derive_separates_by_cli_and_by_second_id_and_treats_an_absent_second_id_as_empty()
    {
        var claude = AccountIdentity.Derive("claude", Account, Org);
        Assert.NotEqual(claude, AccountIdentity.Derive("codex", Account, Org));
        Assert.NotEqual(claude, AccountIdentity.Derive("claude", Account, null));
        Assert.Equal(AccountIdentity.Derive("claude", Account, null), AccountIdentity.Derive("claude", Account, "  "));
        Assert.NotEqual(claude, AccountIdentity.Derive("claude", Account.Replace('6', '7'), Org));
    }

    [Fact]
    public void Short_is_the_first_six_hex_and_never_longer_than_the_id()
    {
        Assert.Equal("ac7d4a", AccountIdentity.Short("ac7d4a85766f600f0bbb94dfc5321dc3"));
        Assert.Equal("abc", AccountIdentity.Short("abc"));
    }

    // ---- Claude ---------------------------------------------------------------------

    [Fact]
    public void Claude_reads_both_ids_and_ignores_the_email_and_everything_else()
    {
        var id = AccountIdentity.Claude(ClaudeFile());
        Assert.Equal(AccountIdentity.Derive("claude", Account, Org), id);
        // The same account after an email change, on another machine, in another key order.
        Assert.Equal(id, AccountIdentity.Claude(ClaudeFile(email: "renamed@example.org")));
        Assert.Equal(id, AccountIdentity.Claude(
            """{"oauthAccount":{"organizationUuid":"@O@","accountUuid":"@A@"}}""".Replace("@O@", Org).Replace("@A@", Account)));
    }

    [Fact]
    public void Claude_in_another_account_or_without_an_organisation_is_a_different_id()
    {
        var id = AccountIdentity.Claude(ClaudeFile());
        Assert.NotEqual(id, AccountIdentity.Claude(ClaudeFile(account: "d2e3f4a5-b6c7-4d8e-9f0a-1b2c3d4e5f60")));
        Assert.NotEqual(id, AccountIdentity.Claude("""{"oauthAccount":{"accountUuid":"@A@"}}""".Replace("@A@", Account)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"numStartups":1}""")]                                  // never logged in: no oauthAccount
    [InlineData("""{"oauthAccount":"nope"}""")]
    [InlineData("""{"oauthAccount":{"emailAddress":"user@example.com"}}""")] // an email alone is not an identity
    [InlineData("""{"oauthAccount":{"accountUuid":42}}""")]
    [InlineData("""{"oauthAccount":{"accountUuid":"user@example.com"}}""")]
    public void Claude_without_an_account_uuid_has_no_id(string? json) => Assert.Null(AccountIdentity.Claude(json));

    // ---- Codex ----------------------------------------------------------------------

    [Fact]
    public void Codex_reads_the_account_id_and_the_user_claim_nested_under_the_auth_object()
    {
        var id = AccountIdentity.Codex(CodexFile(CodexNested()));
        Assert.Equal(AccountIdentity.Derive("codex", CodexAccount, CodexUser), id);
        Assert.Equal("9e234d40bc093dc3c4d9c4800657b849", id);
    }

    [Fact]
    public void Codex_reads_the_user_claim_at_the_top_level_too_and_gets_the_same_id()
    {
        var top = Jwt("""{"email":"user@example.com","chatgpt_user_id":"@U@"}""".Replace("@U@", CodexUser));
        Assert.Equal(AccountIdentity.Codex(CodexFile(CodexNested())), AccountIdentity.Codex(CodexFile(top)));
    }

    [Fact]
    public void Codex_prefers_the_top_level_claim_over_the_nested_one()
    {
        var both = Jwt("""{"chatgpt_user_id":"@U@","https://api.openai.com/auth":{"chatgpt_user_id":"user-other"}}""".Replace("@U@", CodexUser));
        Assert.Equal(AccountIdentity.Derive("codex", CodexAccount, CodexUser), AccountIdentity.Codex(CodexFile(both)));
    }

    [Theory]
    [InlineData("not.a-jwt")]                         // the payload does not decode
    [InlineData("onlyonesegment")]
    [InlineData("a.b")]                                // a one-character segment is not valid base64
    [InlineData("a.@@@@.c")]
    [InlineData("")]
    public void Codex_with_an_undecodable_token_still_has_an_id_and_it_is_a_different_one(string idToken)
    {
        var id = AccountIdentity.Codex(CodexFile(idToken));
        Assert.Equal(AccountIdentity.Derive("codex", CodexAccount, null), id);
        Assert.NotEqual(AccountIdentity.Codex(CodexFile(CodexNested())), id);
    }

    [Theory]
    [InlineData("""["a"]""")]                                              // the payload is not an object
    [InlineData("""{"email":"user@example.com"}""")]                       // no claim at all
    [InlineData("""{"https://api.openai.com/auth":"nope"}""")]             // the auth claim is not an object
    [InlineData("""{"https://api.openai.com/auth":{"chatgpt_account_id":"x"}}""")]
    [InlineData("""{"chatgpt_user_id":7}""")]
    public void Codex_with_a_token_that_decodes_to_no_user_claim_leaves_the_second_id_empty(string payload)
    {
        Assert.Equal(AccountIdentity.Derive("codex", CodexAccount, null), AccountIdentity.Codex(CodexFile(Jwt(payload))));
    }

    [Fact]
    public void Codex_with_a_payload_that_is_not_json_leaves_the_second_id_empty() =>
        Assert.Equal(AccountIdentity.Derive("codex", CodexAccount, null),
            AccountIdentity.Codex(CodexFile("a." + Segment("{not json") + ".c")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"auth_mode":"apikey","OPENAI_API_KEY":"sk-never-read"}""")]   // no tokens object
    [InlineData("""{"tokens":"nope"}""")]
    [InlineData("""{"tokens":{"id_token":"a.b.c"}}""")]                            // no account_id
    [InlineData("""{"tokens":{"account_id":5}}""")]
    public void Codex_without_an_account_id_has_no_id(string? json) => Assert.Null(AccountIdentity.Codex(json));

    [Fact]
    public void Codex_never_reads_the_other_tokens_or_the_api_key()
    {
        // The id does not move when the secrets do, which is the observable half of "never read".
        var a = CodexFile(CodexNested()).Replace("<never read>", "secret-one");
        var b = CodexFile(CodexNested()).Replace("<never read>", "secret-two").Replace("\"OPENAI_API_KEY\":null", "\"OPENAI_API_KEY\":\"sk-x\"");
        Assert.Equal(AccountIdentity.Codex(a), AccountIdentity.Codex(b));
    }

    [Fact]
    public void ChatGptUserId_is_null_for_a_token_with_too_few_segments() =>
        Assert.Null(AccountIdentity.ChatGptUserId("one"));

    // ---- Grok -----------------------------------------------------------------------

    [Fact]
    public void Grok_reads_the_user_and_team_ids_of_the_entry_that_has_the_email()
    {
        var id = AccountIdentity.Grok(GrokFile());
        Assert.Equal(AccountIdentity.Derive("grok", GrokUser, GrokTeam), id);
        Assert.Equal("3ffe8259aadeb6e413c6d9d9a1de6d05", id);
    }

    [Fact]
    public void Grok_skips_entries_that_are_not_objects_or_have_no_email_and_takes_the_first_that_does()
    {
        var json = """
        {"version":2,"empty":{"user_id":"x","email":"  "},"noemail":{"user_id":"y"},
         "real":{"user_id":"@U@","team_id":"@T@","email":"user@example.com"},
         "later":{"user_id":"z","email":"other@example.com"}}
        """.Replace("@U@", GrokUser).Replace("@T@", GrokTeam);
        Assert.Equal(AccountIdentity.Grok(GrokFile()), AccountIdentity.Grok(json));
    }

    [Fact]
    public void Grok_without_a_team_is_a_different_id()
    {
        Assert.NotEqual(AccountIdentity.Grok(GrokFile()),
            AccountIdentity.Grok("""{"k":{"user_id":"4c3b2a19-0f8e-4d7c-b6a5-948372615f0e","email":"user@example.com"}}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("[]")]
    [InlineData("""{"k":"string"}""")]
    [InlineData("""{"k":{"email":"user@example.com"}}""")]                  // an email with no user id
    [InlineData("""{"k":{"user_id":"user@example.com","email":"user@example.com"}}""")]
    public void Grok_without_a_usable_user_id_has_no_id(string? json) => Assert.Null(AccountIdentity.Grok(json));

    // ---- the pollers carry it ---------------------------------------------------------

    private const string ClaudeAnswer =
        """{"type":"control_response","response":{"subtype":"success","request_id":"cb-usage","response":{"subscription_type":"team","rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":33,"resets_at":"2026-08-30T17:29:00+00:00"},"seven_day":{"utilization":84,"resets_at":"2026-09-03T04:59:59+00:00"}}}}}""";

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_Claude_reading_carries_the_id_and_the_label_from_the_same_file()
    {
        var usage = UsagePoller.Assemble(ClaudeFile(), "/Users/x/.claude-work", ClaudeAnswer, Now)!;
        Assert.Equal(AccountIdentity.Claude(ClaudeFile()), usage.AccountId);
        Assert.Equal("user", usage.Label);
        Assert.Equal(33, usage.Session!.Percent);
    }

    [Fact]
    public void A_Claude_profile_never_logged_in_still_reads_and_has_no_id()
    {
        var usage = UsagePoller.Assemble("""{"numStartups":1}""", "/Users/x/.claude-work", ClaudeAnswer, Now)!;
        Assert.Null(usage.AccountId);
        Assert.Equal("work", usage.Label);
    }

    [Fact]
    public void A_Claude_answer_that_cannot_be_read_is_no_reading_at_all() =>
        Assert.Null(UsagePoller.Assemble(ClaudeFile(), null, "garbage", Now));

    private const string GrokCredits =
        """{"ctx":{"config":{"creditUsagePercent":14.0,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-08-31T06:30:23+00:00","end":"2026-09-07T06:30:23+00:00"}},"subscriptionTier":"SuperGrok"}}""";

    [Fact]
    public void The_Grok_reading_carries_the_id_and_the_label_from_the_same_file()
    {
        var usage = GrokUsagePoller.Assemble(GrokFile(), "/Users/w/.grok", GrokCredits, Now)!;
        Assert.Equal(AccountIdentity.Grok(GrokFile()), usage.AccountId);
        Assert.Equal("user", usage.Label);
        Assert.Equal(AccountUsageSource.Grok, usage.Source);
    }

    [Fact]
    public void A_Grok_line_that_cannot_be_read_is_no_reading_at_all() =>
        Assert.Null(GrokUsagePoller.Assemble(GrokFile(), "/Users/w/.grok", "garbage", Now));

    private const string CodexLimits =
        """{"primary":{"used_percent":17.0,"window_minutes":300,"resets_at":1788239518},"secondary":{"used_percent":3.0,"window_minutes":10080,"resets_at":1788807866},"plan_type":"team"}""";

    [Fact]
    public void The_Codex_live_reading_carries_the_id_from_the_auth_file()
    {
        var home = Directory.CreateTempSubdirectory("cb340-codex-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(home, "auth.json"), CodexFile(CodexNested()));
            var readings = CodexUsagePoller.ReadFrom([home], Now, _ => CodexLimits);

            var usage = Assert.Single(readings);
            Assert.Equal(AccountIdentity.Codex(CodexFile(CodexNested())), usage.AccountId);
            Assert.Equal(17.0, usage.Session!.Percent);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void The_Codex_snapshot_fallback_carries_the_id_too()
    {
        var home = Directory.CreateTempSubdirectory("cb340-codex-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(home, "auth.json"), CodexFile(CodexNested()));
            var day = Directory.CreateDirectory(Path.Combine(home, "sessions", "2026", "08", "30"));
            File.WriteAllText(Path.Combine(day.FullName, "rollout-2026-08-30T10-00-00-abc.jsonl"),
                """{"timestamp":"2026-08-30T10:00:00.000Z","payload":{"type":"token_count","rate_limits":""" + CodexLimits + "}}\n");

            // The live answer is empty, so the snapshot from the rollout is what is read.
            var usage = Assert.Single(CodexUsagePoller.ReadFrom([home], Now, _ => null));
            Assert.Equal(AccountIdentity.Codex(CodexFile(CodexNested())), usage.AccountId);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void A_Codex_home_with_no_auth_file_reads_and_has_no_id()
    {
        var home = Directory.CreateTempSubdirectory("cb340-codex-").FullName;
        try
        {
            var usage = Assert.Single(CodexUsagePoller.ReadFrom([home], Now, _ => CodexLimits));
            Assert.Null(usage.AccountId);
        }
        finally { Directory.Delete(home, true); }
    }
}

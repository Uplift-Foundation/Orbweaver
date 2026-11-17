using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// QA probes (Ines): AccountIdentity against an independent computation, and the merge-relevant edge cases.
public class QaAccountIdentityProbeTests
{
    private static string Segment(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Jwt(string payload) => "eyJhbGciOiJSUzI1NiJ9." + Segment(payload) + ".c2ln";

    [Fact] public void DeriveMatchesAnIndependentPythonHmac()
    {
        // computed with python hmac/hashlib, not with this code
        Assert.Equal("ac7d4a85766f600f0bbb94dfc5321dc3", AccountIdentity.Derive("claude", "6f1c2a8e-3b4d-4e5f-9a7b-1c2d3e4f5a6b", "0a9b8c7d-6e5f-4a3b-8c2d-1e0f9a8b7c6d"));
        Assert.Equal("5f50833fd10f915dc65b88b63d4d6f46", AccountIdentity.Derive("claude", "abc", null));
        Assert.Equal("5f50833fd10f915dc65b88b63d4d6f46", AccountIdentity.Derive("claude", "abc", ""));
        Assert.Equal("d0cad2b2130b53147b9eebfb9b9794b6", AccountIdentity.Derive("codex", "abc", "user-1"));
    }
    [Fact] public void CaseAndAsciiWhitespaceDoNotSplitAnAccount()
    {
        var a = AccountIdentity.Derive("claude", "ABC", " User-1 ");
        Assert.Equal(a, AccountIdentity.Derive("claude", "abc", "user-1"));
        Assert.Equal(a, AccountIdentity.Derive("claude", "\tabc\n", "USER-1\r"));
    }
    [Fact] public void CliAndEachIdChangeTheId()
    {
        var b = AccountIdentity.Derive("claude", "abc", "x");
        Assert.NotEqual(b, AccountIdentity.Derive("codex", "abc", "x"));
        Assert.NotEqual(b, AccountIdentity.Derive("claude", "abc", "y"));
        Assert.NotEqual(b, AccountIdentity.Derive("claude", "abd", "x"));
        Assert.NotEqual(AccountIdentity.Derive("claude", "abc", ""), AccountIdentity.Derive("claude", "abc", "x"));
    }
    [Fact] public void NoIdOneOrAnEmailGivesNone()
    {
        Assert.Null(AccountIdentity.Derive("claude", null, "x")); Assert.Null(AccountIdentity.Derive("claude", "   ", "x"));
        Assert.Null(AccountIdentity.Derive("claude", "a@b.c", "x"));
    }
    [Fact] public void IdIsAlways32LowerHex()
    {
        var id = AccountIdentity.Derive("claude", "abc", "x")!; Assert.Equal(32, id.Length); Assert.True(id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
        Assert.True(Orbweaver.RelayUsage.IsAccountId(id));
    }
    [Fact] public void ClaudeFileWithoutOauthAccountOrAccountUuidHasNoId()
    {
        Assert.Null(AccountIdentity.Claude("{\"numStartups\":1}")); Assert.Null(AccountIdentity.Claude("{\"oauthAccount\":{\"organizationUuid\":\"o\"}}"));
        Assert.Null(AccountIdentity.Claude("not json")); Assert.Null(AccountIdentity.Claude(null)); Assert.Null(AccountIdentity.Claude("[]"));
        Assert.Null(AccountIdentity.Claude("{\"oauthAccount\":{\"accountUuid\":\"u@x\"}}"));
    }
    [Fact] public void ClaudeSameAccountWithAndWithoutOrgAreDifferentIdsNotAWrongMerge()
    {
        var withOrg = AccountIdentity.Claude("{\"oauthAccount\":{\"accountUuid\":\"u\",\"organizationUuid\":\"o\"}}");
        var noOrg = AccountIdentity.Claude("{\"oauthAccount\":{\"accountUuid\":\"u\"}}");
        Assert.NotNull(withOrg); Assert.NotNull(noOrg); Assert.NotEqual(withOrg, noOrg);
    }
    [Fact] public void CodexUserIdTopLevelAndNestedAgree_AndAnUndecodableTokenStillGivesAnAccountOnlyId()
    {
        string F(string tok) => "{\"tokens\":{\"id_token\":\"" + tok + "\",\"account_id\":\"acct\"}}";
        var nested = AccountIdentity.Codex(F(Jwt("{\"https://api.openai.com/auth\":{\"chatgpt_user_id\":\"user-1\"}}")));
        var top = AccountIdentity.Codex(F(Jwt("{\"chatgpt_user_id\":\"user-1\"}")));
        Assert.NotNull(nested); Assert.Equal(nested, top);
        Assert.Equal(AccountIdentity.Derive("codex", "acct", "user-1"), nested);
        var junk = AccountIdentity.Codex(F("garbage")); Assert.Equal(AccountIdentity.Derive("codex", "acct", null), junk); Assert.NotEqual(nested, junk);
        Assert.Null(AccountIdentity.Codex("{\"tokens\":{\"id_token\":\"x\"}}"));              // no account_id => no id
        Assert.Null(AccountIdentity.Codex("{\"OPENAI_API_KEY\":\"k\"}"));                      // API-key login: no tokens => no id
    }
    [Fact] public void CodexNeverReadsAccessOrRefreshTokensOrApiKey()
    {
        var withSecrets = "{\"OPENAI_API_KEY\":\"sk-AAAA\",\"tokens\":{\"account_id\":\"acct\",\"access_token\":\"AT1\",\"refresh_token\":\"RT1\"}}";
        var otherSecrets = "{\"OPENAI_API_KEY\":\"sk-BBBB\",\"tokens\":{\"account_id\":\"acct\",\"access_token\":\"AT2\",\"refresh_token\":\"RT2\"}}";
        Assert.Equal(AccountIdentity.Codex(withSecrets), AccountIdentity.Codex(otherSecrets));
    }
    [Fact] public void GrokUsesTheFirstEntryWithAnEmailAndSkipsOthers()
    {
        var j = "{\"meta\":1,\"a\":{\"user_id\":\"u0\"},\"b\":{\"email\":\"e@x\",\"user_id\":\"u1\",\"team_id\":\"t1\"},\"c\":{\"email\":\"f@x\",\"user_id\":\"u2\"}}";
        Assert.Equal(AccountIdentity.Derive("grok", "u1", "t1"), AccountIdentity.Grok(j));
        Assert.Null(AccountIdentity.Grok("{\"a\":{\"email\":\"e@x\"}}"));                     // no user_id
        Assert.Null(AccountIdentity.Grok("{\"a\":{\"email\":\"  \",\"user_id\":\"u\"}}"));   // blank email entry is not an account
    }
    [Fact] public void ShortIsSixHexAndNeverLongerThanTheId() { Assert.Equal("abcdef", AccountIdentity.Short("abcdef0123")); Assert.Equal("ab", AccountIdentity.Short("ab")); }
}

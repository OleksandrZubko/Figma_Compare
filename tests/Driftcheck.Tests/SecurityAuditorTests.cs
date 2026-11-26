using Driftcheck.Mcp.Qa;

namespace Driftcheck.Tests;

public class SecurityAuditorTests
{
    const string WindowInsecure = """
          Window #7 Window{5f516c5 u0 co.gifted.app/co.gifted.app.MainActivity}:
            mOwnerUid=10216 showForAllUsers=false package=co.gifted.app appop=NONE
            mAttrs={(0,0)(fillxfill) sim={adjust=resize} ty=BASE_APPLICATION wanim=0x1030309
              fl=LAYOUT_IN_SCREEN LAYOUT_INSET_DECOR SPLIT_TOUCH HARDWARE_ACCELERATED DRAWS_SYSTEM_BAR_BACKGROUNDS
            mViewVisibility=0x0 mHaveFrame=true mObscured=false
          Window #8 Window{7d98836 u0 com.google.android.apps.nexuslauncher/com.google.android.apps.nexuslauncher.NexusLauncherActivity}:
              fl=LAYOUT_IN_SCREEN SECURE
          mCurrentFocus=Window{5f516c5 u0 co.gifted.app/co.gifted.app.MainActivity}
        """;

    const string WindowSecure = """
          Window #7 Window{5f516c5 u0 co.gifted.app/co.gifted.app.MainActivity}:
            mAttrs={(0,0)(fillxfill) ty=BASE_APPLICATION
              fl=LAYOUT_IN_SCREEN SECURE HARDWARE_ACCELERATED
          mCurrentFocus=Window{5f516c5 u0 co.gifted.app/co.gifted.app.MainActivity}
        """;

    [Fact]
    public void FlagSecure_NotSet_IgnoresOtherPackagesWindows()
    {
        var (secure, found) = SecurityAuditor.ParseFlagSecure(WindowInsecure, "co.gifted.app");
        Assert.True(found);
        Assert.False(secure);
    }

    [Fact]
    public void FlagSecure_Set()
    {
        var (secure, found) = SecurityAuditor.ParseFlagSecure(WindowSecure, "co.gifted.app");
        Assert.True(found);
        Assert.True(secure);
    }

    [Fact]
    public void FlagSecure_NoWindow_NotFound() =>
        Assert.False(SecurityAuditor.ParseFlagSecure(WindowSecure, "com.other").Found);

    [Fact]
    public void PackageFlags_ParsesFlagLines()
    {
        const string dump = """
            Packages:
              Package [co.gifted.app] (abc):
                flags=[ HAS_CODE ALLOW_CLEAR_USER_DATA ALLOW_BACKUP ]
                privateFlags=[ PRIVATE_FLAG_ACTIVITIES_RESIZE_MODE_RESIZEABLE_VIA_SDK_VERSION ]
                pkgFlags=[ HAS_CODE DEBUGGABLE ALLOW_BACKUP ]
            """;
        var flags = SecurityAuditor.ParsePackageFlags(dump);
        Assert.Contains("ALLOW_BACKUP", flags);
        Assert.Contains("DEBUGGABLE", flags);
        Assert.Equal(flags.Distinct().Count(), flags.Count);
        Assert.DoesNotContain("PRIVATE_FLAG_ACTIVITIES_RESIZE_MODE_RESIZEABLE_VIA_SDK_VERSION", flags);
    }

    [Fact]
    public void PackageFlags_ReleaseBuildHasNoDebuggable() =>
        Assert.DoesNotContain("DEBUGGABLE", SecurityAuditor.ParsePackageFlags("    pkgFlags=[ HAS_CODE ALLOW_CLEAR_USER_DATA ]"));

    [Fact]
    public void WebViewDevtools_MatchesOnlyAppPids()
    {
        const string unix = """
            0000000000000000: 00000003 00000000 00000000 0005 03 105266 @jdwp-control
            0000000000000000: 00000002 00000000 00010000 0001 01 120001 @webview_devtools_remote_4321
            """;
        Assert.True(SecurityAuditor.HasWebViewDevtools(unix, [4321]));
        Assert.False(SecurityAuditor.HasWebViewDevtools(unix, [999]));
        Assert.False(SecurityAuditor.HasWebViewDevtools("0000000000000000: 00000003 00000000 00000000 0005 03 105266 @jdwp-control", [4321]));
        Assert.True(SecurityAuditor.HasWebViewDevtools("0000: 2 0 10000 1 1 5 @chrome_devtools_remote", []));
    }

    [Fact]
    public void FindSecrets_Jwt_IsMasked()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmYWtlLXVzZXIifQ.c2lnbmF0dXJlLWZha2U";
        var m = Assert.Single(SecurityAuditor.FindSecrets($"token={jwt}", "ReactNativeJS"));
        Assert.Equal("jwt", m.Kind);
        Assert.Equal("eyJh…" + jwt[^2..], m.Masked);
        Assert.Equal("ReactNativeJS", m.Where);
    }

    [Theory]
    [InlineData("Authorization: Bearer fake0123456789abcdefghij", "bearer")]
    [InlineData("key AIzaFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE1", "google-api-key")]
    [InlineData("id AKIAFAKEFAKEFAKE1234", "aws-key")]
    [InlineData("sk_test_FAKEFAKEFAKEFAKE1234", "stripe-key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private-key")]
    [InlineData("{\"password\":\"hunter2fake\"}", "password")]
    [InlineData("<string name=\"user_password\">hunter2fake</string>", "password")]
    [InlineData("mail jane.doe@example.com sent", "email")]
    [InlineData("phone +380501234567 ok", "phone")]
    [InlineData("card 4111 1111 1111 1111 paid", "card-number")]
    public void FindSecrets_Positive(string text, string kind) =>
        Assert.Contains(SecurityAuditor.FindSecrets(text, "x"), m => m.Kind == kind);

    [Theory]
    [InlineData("order id 1234567890123456")]
    [InlineData("card 1111 1111 1111 1111")]
    [InlineData("timestamp 1795688435684")]
    [InlineData("bypass=on and password: null")]
    [InlineData("Bearer short")]
    [InlineData("call 0501234567")]
    [InlineData("hello world, nothing here")]
    public void FindSecrets_Negative(string text) =>
        Assert.Empty(SecurityAuditor.FindSecrets(text, "x"));

    [Fact]
    public void FindSecrets_CardIsMasked()
    {
        var m = Assert.Single(SecurityAuditor.FindSecrets("4111-1111-1111-1111", "x"));
        Assert.Equal("4111…11", m.Masked);
    }

    [Fact]
    public void Luhn()
    {
        Assert.True(SecurityAuditor.LuhnValid("4111111111111111"));
        Assert.True(SecurityAuditor.LuhnValid("4111 1111 1111 1111"));
        Assert.False(SecurityAuditor.LuhnValid("1234567890123456"));
        Assert.False(SecurityAuditor.LuhnValid(""));
    }

    [Theory]
    [InlineData("card 4111 1111 1111 1111 declined", true)]   // Visa test number
    [InlineData("pan=5555555555554444", true)]                // Mastercard test number
    [InlineData("key 2960123412341269 ok", false)]            // passes Luhn, no network starts with 29
    [InlineData("hash a4111111111111111", false)]             // passes Luhn but glued to a letter: part of an identifier
    public void CardNumbersNeedARealIssuerPrefix(string text, bool found) =>
        Assert.Equal(found, SecurityAuditor.FindSecrets(text, "test").Any(m => m.Kind == "card-number"));
}

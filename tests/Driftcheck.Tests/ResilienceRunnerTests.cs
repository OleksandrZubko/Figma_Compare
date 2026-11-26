using Driftcheck.Mcp;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Qa;

namespace Driftcheck.Tests;

public class ResilienceRunnerTests
{
    // Trimmed from `dumpsys package co.gifted.app` on an Android 15 emulator (all revoked) plus a granted variant.
    const string Dumpsys = """
          gids=[3003]
          runtime permissions:
            android.permission.POST_NOTIFICATIONS: granted=false, flags=[ USER_SENSITIVE_WHEN_GRANTED|USER_SENSITIVE_WHEN_DENIED]
            android.permission.CAMERA: granted=true, flags=[ USER_SENSITIVE_WHEN_GRANTED|USER_SENSITIVE_WHEN_DENIED]
            android.permission.RECORD_AUDIO: granted=true, flags=[ USER_SENSITIVE_WHEN_GRANTED|USER_SENSITIVE_WHEN_DENIED]

        Queries:
          system apps queryable: false
            android.permission.NOT_A_RUNTIME_ONE: granted=true
        """;

    [Fact]
    public void ParsesGrantedPermissionsOnlyFromTheRuntimeSection()
    {
        var granted = ResilienceRunner.ParseGrantedPermissions(Dumpsys);
        Assert.Equal(["android.permission.CAMERA", "android.permission.RECORD_AUDIO"], granted);
    }

    [Fact]
    public void NoGrantedPermissionsWhenAllRevoked()
    {
        const string text = """
              runtime permissions:
                android.permission.CAMERA: granted=false, flags=[ USER_SENSITIVE_WHEN_GRANTED]
            """;
        Assert.Empty(ResilienceRunner.ParseGrantedPermissions(text));
        Assert.Empty(ResilienceRunner.ParseGrantedPermissions(""));
    }

    [Theory]
    [InlineData("1\n", 1)]
    [InlineData("0", 0)]
    [InlineData("null\n", null)]
    [InlineData("", null)]
    [InlineData("  3 ", 3)]
    public void ParsesSettingInt(string output, int? expected) =>
        Assert.Equal(expected, ResilienceRunner.ParseSettingInt(output));

    [Fact]
    public void TextOverlapIsJaccardOnNonEmptyTexts()
    {
        Assert.Equal(1.0, ResilienceRunner.TextOverlap(["a", "b"], ["b", "a", " ", ""]));
        Assert.Equal(1.0 / 3, ResilienceRunner.TextOverlap(["a", "b"], ["b", "c"]), 6);
        Assert.Equal(0.0, ResilienceRunner.TextOverlap(["a"], []));
        Assert.Equal(1.0, ResilienceRunner.TextOverlap([], [""]));
    }

    [Fact]
    public void MissingTextsListsWhatDisappeared() =>
        Assert.Equal(["Home", "Cart"], ResilienceRunner.MissingTexts(["Home", "Cart", "Menu"], ["Menu", "Login"]));

    [Fact]
    public void ParsesResumedActivity()
    {
        const string text = "  topResumedActivity=ActivityRecord{43bdc4c u0 co.gifted.app/.MainActivity t673}\n";
        Assert.Equal("co.gifted.app/.MainActivity", ResilienceRunner.ParseResumedActivity(text));
        Assert.Null(ResilienceRunner.ParseResumedActivity("nothing here"));
    }

    [Fact]
    public void ReadsPngSize()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,
            0, 0, 0x04, 0x38, 0, 0, 0x09, 0x60];
        Assert.Equal((1080, 2400), ResilienceRunner.PngSize(png));
        Assert.Null(ResilienceRunner.PngSize([1, 2, 3]));
    }

    [Fact]
    public void UnknownScenarioListsValidNames()
    {
        var ex = Assert.Throws<ArgumentException>(() => ResilienceRunner.NormalizeScenarios(["offline", "teleport"]));
        Assert.Contains("teleport", ex.Message);
        foreach (var name in ResilienceRunner.AllScenarios) Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void AllOrEmptyMeansEveryScenario()
    {
        Assert.Equal(ResilienceRunner.AllScenarios, ResilienceRunner.NormalizeScenarios([]));
        Assert.Equal(ResilienceRunner.AllScenarios, ResilienceRunner.NormalizeScenarios(["ALL"]));
        Assert.Equal(["back", "rotation"], ResilienceRunner.NormalizeScenarios([" Back", "rotation", "back"]));
    }

    static ScreenCapture Screen(params string[] texts) =>
        new("test", "unused.png", 400, 800, 1,
            texts.Select((t, i) => new ScreenElement(null, "android.widget.TextView", t, null, new Rect(0, i * 40, 300, 30))).ToList());

    [Theory]
    [InlineData("No internet connection", true)]
    [InlineData("Couldn't load your rewards. Try again", true)]
    [InlineData("Немає з'єднання з інтернетом", true)]
    [InlineData("You're offline", true)]
    [InlineData("My rewards", false)]
    [InlineData("Connected accounts", false)]
    public void RecognizesOfflineMessages(string text, bool offline) =>
        Assert.Equal(offline, ResilienceRunner.HasOfflineMessage(Screen(text)));

    [Fact]
    public void RecognizesASignInScreen()
    {
        Assert.True(ResilienceRunner.LooksLikeLogin(Screen("Welcome back", "Email", "Password", "Log in")));
        Assert.True(ResilienceRunner.LooksLikeLogin(Screen("Увійти", "Пароль")));
        Assert.False(ResilienceRunner.LooksLikeLogin(Screen("My rewards", "Account", "Log in to Shopify partner"))); // One word isn't a login screen.
    }

    [Fact]
    public void RecognizesABlankScreen()
    {
        Assert.True(ResilienceRunner.IsBlank(Png(400, 800, drawContent: false)));
        Assert.False(ResilienceRunner.IsBlank(Png(400, 800, drawContent: true)));
    }

    [Fact]
    public void OfflineIsAnAliasOfOfflineAction() =>
        Assert.Equal(["offline-action", "rotation"], ResilienceRunner.NormalizeScenarios(["offline", "rotation"]));

    static byte[] Png(int w, int h, bool drawContent)
    {
        using var bitmap = new SkiaSharp.SKBitmap(w, h);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            if (drawContent)
            {
                using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x26, 0x2B, 0x36) };
                for (var i = 0; i < 12; i++) canvas.DrawRect(20, 80 + i * 55, 360, 30, paint);
            }
        }
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

using Driftcheck.Mcp;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Qa;

namespace Driftcheck.Tests;

public class ContentAuditorTests
{
    static ScreenCapture Screen(params ScreenElement[] elements) => new("test", "unused.png", 400, 800, 1, elements);

    static ScreenElement Text(string text, string? id = null, double y = 100) =>
        new(id, "android.widget.TextView", text, null, new Rect(10, y, 200, 30));

    static IReadOnlyList<QaIssue> Audit(string text, string? locale = null, params string[] allow) =>
        ContentAuditor.Audit(Screen(Text(text)), locale, allow);

    static bool Has(IReadOnlyList<QaIssue> issues, string category) => issues.Any(i => i.Category == category);

    static RuntimeProps RuntimeWith(string value) =>
        new("android.widget.TextView", 1, new RuntimeText(value, 14, null, null, false, null, null, null), null);

    // ---- 1. placeholders ----

    [Theory]
    [InlineData("null")]
    [InlineData("Hello, undefined")]
    [InlineData("NaN%")]
    [InlineData("Total: Infinity")]
    [InlineData("[object Object]")]
    [InlineData("Invalid Date")]
    [InlineData("Hello {{name}}")]
    [InlineData("Hello {name}")]
    [InlineData("Hello ${user.name}")]
    [InlineData("You have %d items")]
    [InlineData("Hi %1$s")]
    [InlineData("Hi %@")]
    [InlineData("Lorem ipsum dolor sit amet")]
    [InlineData("TODO: change this")]
    [InlineData("FIXME")]
    [InlineData("Line one\\nLine two")]
    [InlineData("Tom &amp; Jerry")]
    [InlineData("a&nbsp;b")]
    [InlineData("It&#39;s")]
    [InlineData("<b>Bold</b>")]
    [InlineData("Line<br/>break")]
    [InlineData("a</br>b")]
    public void Placeholders_AreErrors(string text)
    {
        var issues = Audit(text);
        var issue = Assert.Single(issues, i => i.Category == "placeholder");
        Assert.Equal(QaSeverity.Error, issue.Severity);
        Assert.Contains(text, issue.Detail);
        Assert.NotNull(issue.Fix);
        Assert.Equal(new Rect(10, 100, 200, 30), issue.Bounds);
    }

    [Theory]
    [InlineData("Null Island")]
    [InlineData("Undefined behavior is covered in the guide")]
    [InlineData("nullable types")]
    [InlineData("non-null")]
    [InlineData("Banana")]
    [InlineData("100% sure")]
    [InlineData("50% off, save 20%")]
    [InlineData("Tom & Jerry")]
    [InlineData("1 < 2 and 3 > 2")]
    [InlineData("Todo list")]
    [InlineData("C:\\new folder")]
    [InlineData("Welcome back, Anna")]
    public void Placeholders_NegativeCases(string text) =>
        Assert.False(Has(Audit(text), "placeholder"));

    // ---- 2. i18n keys ----

    [Theory]
    [InlineData("home.title")]
    [InlineData("checkout_button_label")]
    [InlineData("errors.network.offline")]
    public void I18nKeys_AreWarnings(string text)
    {
        var issue = Assert.Single(Audit(text), i => i.Category == "i18n-key");
        Assert.Equal(QaSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("example.com")]
    [InlineData("logo.png")]
    [InlineData("report.pdf")]
    [InlineData("v1.2.3")]
    [InlineData("3.14")]
    [InlineData("e.g")]
    [InlineData("Home")]
    [InlineData("Home screen")]
    [InlineData("Home.Title")]
    public void I18nKeys_NegativeCases(string text) =>
        Assert.False(Has(Audit(text), "i18n-key"));

    // ---- 3. float artifacts ----

    [Theory]
    [InlineData("0.30000000000000004")]
    [InlineData("Total 12.9999999")]
    public void FloatArtifacts_AreWarnings(string text)
    {
        var issue = Assert.Single(Audit(text), i => i.Category == "number-format");
        Assert.Equal(QaSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData("12.99")]
    [InlineData("3.14159")]
    [InlineData("Version 1.2.3456789")]
    public void FloatArtifacts_NegativeCases(string text) =>
        Assert.False(Has(Audit(text), "number-format"));

    // ---- 4. whitespace ----

    [Theory]
    [InlineData(" Hello")]
    [InlineData("Hello ")]
    [InlineData("Hello  world")]
    [InlineData("Hello , world")]
    [InlineData("Really ?")]
    public void Whitespace_IsInfo(string text)
    {
        var issue = Assert.Single(Audit(text), i => i.Category == "whitespace");
        Assert.Equal(QaSeverity.Info, issue.Severity);
    }

    [Theory]
    [InlineData("Hello, world")]
    [InlineData("Wait...")]
    [InlineData("Hello world!")]
    [InlineData("Loading ...")]
    [InlineData("   ")]
    public void Whitespace_NegativeCases(string text) =>
        Assert.False(Has(Audit(text), "whitespace"));

    [Fact]
    public void Whitespace_FrenchAllowsSpaceBeforeHighPunctuation()
    {
        Assert.False(Has(Audit("Vraiment ?", "fr-FR"), "whitespace"));
        Assert.False(Has(Audit("Total : 5", "fr-FR"), "whitespace"));
        Assert.True(Has(Audit("Bonjour , monde", "fr-FR"), "whitespace"));
    }

    // ---- 5. untranslated ----

    [Fact]
    public void Untranslated_CyrillicLocaleFlagsLatinWords()
    {
        var issue = Assert.Single(Audit("Pay now", "uk-UA"), i => i.Category == "untranslated");
        Assert.Equal(QaSeverity.Warning, issue.Severity);
        Assert.True(Has(Audit("Додати в cart", "uk-UA"), "untranslated"));
        Assert.True(Has(Audit("Checkout", "ru-RU"), "untranslated"));
    }

    [Theory]
    [InlineData("Оплатити замовлення")]
    [InlineData("Введіть PIN-код")]
    [InlineData("Сума: 100 USD")]
    [InlineData("Напишіть на support@example.com")]
    [InlineData("Відкрийте https://example.com/help")]
    [InlineData("Ліміт 20 Mbps")]
    public void Untranslated_CyrillicNegativeCases(string text) =>
        Assert.False(Has(Audit(text, "uk-UA"), "untranslated"));

    [Fact]
    public void Untranslated_AllowlistIsCaseInsensitive()
    {
        Assert.True(Has(Audit("Оплата через Monobank", "uk-UA"), "untranslated"));
        Assert.False(Has(Audit("Оплата через Monobank", "uk-UA", "monobank"), "untranslated"));
        Assert.False(Has(Audit("Оплата через Apple Pay", "uk-UA", "Apple Pay"), "untranslated"));
        Assert.False(Has(Audit("Apple Pay", "uk-UA", "apple pay"), "untranslated"));
    }

    [Fact]
    public void Untranslated_LatinLanguageNeedsTwoEnglishFunctionWords()
    {
        Assert.True(Has(Audit("Please enter your password", "de-DE"), "untranslated"));
        Assert.True(Has(Audit("Save the date for you", "de-DE"), "untranslated"));
        Assert.False(Has(Audit("Bitte Passwort eingeben", "de-DE"), "untranslated"));
        Assert.False(Has(Audit("Mehr to", "de-DE"), "untranslated"));
        Assert.False(Has(Audit("Please enter your password", "en-US"), "untranslated"));
        Assert.False(Has(Audit("Please enter your password"), "untranslated"));
    }

    [Fact]
    public void Untranslated_SerbianOnlyWhenCyrillic()
    {
        Assert.True(Has(Audit("Pay now", "sr-Cyrl-RS"), "untranslated"));
        Assert.False(Has(Audit("Plati sada", "sr-Latn-RS"), "untranslated"));
    }

    // ---- 6. locale formats ----

    [Theory]
    [InlineData("11/26/2026", "uk-UA")]
    [InlineData("11/26/2026", "de-DE")]
    [InlineData("26/11/2026", "de-DE")]
    [InlineData("11.26.2026", "de-DE")]
    [InlineData("11/26/2026", "en-GB")]
    [InlineData("26.11.2026", "en-US")]
    [InlineData("26/11/2026", "en-US")]
    public void Dates_ContradictingCulture(string text, string locale)
    {
        var issue = Assert.Single(Audit("Due " + text, locale), i => i.Category == "locale-format");
        Assert.Equal(QaSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData("26.11.2026", "uk-UA")]
    [InlineData("26.11.2026", "de-DE")]
    [InlineData("11/26/2026", "en-US")]
    [InlineData("2026-11-26", "uk-UA")]
    [InlineData("2026-11-26", "en-US")]
    [InlineData("05.06.2026", "de-DE")]
    public void Dates_MatchingCultureOrIso(string text, string locale) =>
        Assert.False(Has(Audit(text, locale), "locale-format"));

    [Fact]
    public void Dates_NotCheckedWithoutLocale() =>
        Assert.False(Has(Audit("11/26/2026"), "locale-format"));

    [Theory]
    [InlineData("Oct 24, 2028", "uk-UA")]
    [InlineData("24 October 2028", "de-DE")]
    [InlineData("Created in January", "uk-UA")]
    [InlineData("Due Dec. 5", "de-DE")]
    public void EnglishMonths_InNonEnglishLocale(string text, string locale) =>
        Assert.True(Has(Audit(text, locale), "locale-format"));

    [Theory]
    [InlineData("Oct 24, 2028", "en-US")]
    [InlineData("January", "en-GB")]
    [InlineData("24. Dezember 2028", "de-DE")]
    [InlineData("24 August 2028", "de-DE")]
    [InlineData("May the force", "uk-UA")]
    [InlineData("Mar", "es-ES")]
    [InlineData("24 жовтня 2028", "uk-UA")]
    public void EnglishMonths_NegativeCases(string text, string locale) =>
        Assert.DoesNotContain(Audit(text, locale), i => i.Category == "locale-format" && i.Title.Contains("month"));

    [Theory]
    [InlineData("Total 1,146.91", "de-DE")]
    [InlineData("Total 1,146.91", "uk-UA")]
    [InlineData("Total 1.146,91", "en-US")]
    [InlineData("$12.99", "de-DE")]
    [InlineData("$12.99", "uk-UA")]
    [InlineData("12,99 €", "en-US")]
    [InlineData("EUR 12,99", "en-US")]
    public void Numbers_ContradictingCulture(string text, string locale)
    {
        var issue = Assert.Single(Audit(text, locale), i => i.Category == "locale-format");
        Assert.Contains("decimal separator", issue.Title);
    }

    [Theory]
    [InlineData("Total 1,146.91", "en-US")]
    [InlineData("Total 1.146,91", "de-DE")]
    [InlineData("12,99 €", "de-DE")]
    [InlineData("$12.99", "en-US")]
    [InlineData("1,146", "de-DE")]
    [InlineData("Version 1.5", "de-DE")]
    [InlineData("12.99", "de-DE")]
    [InlineData("1,5 kg", "en-US")]
    public void Numbers_AmbiguousOrMatching(string text, string locale) =>
        Assert.False(Has(Audit(text, locale), "locale-format"));

    [Fact]
    public void UnknownLocale_DoesNotThrow()
    {
        var issues = Audit("11/26/2026 and Pay now", "xx-XX");
        Assert.False(Has(issues, "locale-format"));
    }

    // ---- sources, dedupe, allowlist, shape ----

    [Fact]
    public void ContentDescriptionAndRuntimeTextAreChecked()
    {
        var capture = Screen(
            new ScreenElement("a", "android.view.View", null, "null", new Rect(0, 0, 10, 10)),
            new ScreenElement("b", "android.widget.TextView", "Hello", null, new Rect(0, 20, 100, 20), RuntimeWith("Hello undefined")));

        var issues = ContentAuditor.Audit(capture);

        Assert.Equal(2, issues.Count(i => i.Category == "placeholder"));
        Assert.Contains(issues, i => i.Element == "a" && i.Detail == "\"null\"");
        Assert.Contains(issues, i => i.Element == "b" && i.Detail.Contains("Hello undefined"));
    }

    [Fact]
    public void RuntimeTextNotLongerThanTextIsIgnored()
    {
        var capture = Screen(new ScreenElement("b", "android.widget.TextView", "Hello world", null, new Rect(0, 0, 100, 20), RuntimeWith("null")));
        Assert.Empty(ContentAuditor.Audit(capture));
    }

    [Fact]
    public void IdenticalTextAtSamePlaceIsReportedOnce()
    {
        var bounds = new Rect(0, 0, 100, 20);
        var capture = Screen(
            new ScreenElement("a", "android.widget.TextView", "null", "null", bounds),
            new ScreenElement("b", "android.widget.TextView", "null", null, bounds));
        Assert.Single(ContentAuditor.Audit(capture));

        var different = Screen(
            new ScreenElement("a", "android.widget.TextView", "null", null, bounds),
            new ScreenElement("b", "android.widget.TextView", "null", null, new Rect(0, 50, 100, 20)));
        Assert.Equal(2, ContentAuditor.Audit(different).Count);
    }

    [Fact]
    public void Allowlist_SkipsWholeTextAndPhrasesInsideText()
    {
        Assert.Empty(Audit("NaN", null, "nan"));
        Assert.Empty(Audit("home.title", null, "Home.Title"));
        Assert.False(Has(Audit("Welcome to null Inc", null, "null Inc"), "placeholder"));
        Assert.True(Has(Audit("Welcome to null Inc", null, "Inc"), "placeholder"));
    }

    [Fact]
    public void IssueCarriesElementIdOrShortText()
    {
        var withId = ContentAuditor.Audit(Screen(Text("null", "label_total")));
        Assert.Equal("label_total", Assert.Single(withId).Element);

        var longText = "undefined " + new string('x', 60);
        var noId = ContentAuditor.Audit(Screen(Text(longText)));
        Assert.True(Assert.Single(noId).Element!.Length <= 31);
    }

    [Fact]
    public void CleanScreen_HasNoIssues()
    {
        var capture = Screen(Text("Welcome back, Anna"), Text("Total: $1,146.91", y: 140), Text("Pay now", y: 180));
        Assert.Empty(ContentAuditor.Audit(capture, "en-US"));
        Assert.Empty(ContentAuditor.Audit(Screen(Text("Оплатити"), Text("Сума: 1 146,91 ₴", y: 140)), "uk-UA"));
    }
}

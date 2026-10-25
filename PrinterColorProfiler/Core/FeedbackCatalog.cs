namespace PrinterColorProfiler.Core;

public sealed record FeedbackOption(string Id, string Label, string Group, string Hint);

// Stable ids are stored in sessions and drive ProfileBuilder; labels are display text only.
// Sessions written before ids existed store the Hebrew label, which Resolve() still accepts.
public static class FeedbackCatalog
{
    public const string Sky = "sky-magenta", Darker = "too-dark", Lighter = "too-light",
        ContrastHigh = "contrast-high", ContrastLow = "contrast-low", BlackBlocked = "black-blocked", BlackWeak = "black-weak",
        WhitesBurnt = "whites-burnt", SkinYellow = "skin-yellow", SkinRed = "skin-red", SkinMagenta = "skin-magenta",
        SkinGreen = "skin-green", SkinDark = "skin-dark", SkinLight = "skin-light", RedsStrong = "reds-strong",
        RedsWeak = "reds-weak", BluesStrong = "blues-strong", BluesWeak = "blues-weak", GreensStrong = "greens-strong",
        GreensWeak = "greens-weak", YellowsStrong = "yellows-strong", YellowsWeak = "yellows-weak", GraysCast = "grays-cast",
        Gradients = "gradients", Banding = "banding", ShadowDetail = "shadow-detail", HighlightDetail = "highlight-detail";

    public static IReadOnlyList<string> Groups { get; } = ["שמיים", "בהירות וניגודיות", "שחורים ולבנים", "גווני עור", "צבעים", "חלקות ופרטים"];

    public static IReadOnlyList<FeedbackOption> All { get; } =
    [
        new(Sky, FineTune.SkyMagentaLabel, "שמיים", "מפחית מג׳נטה רק בגוני תכלת וכחול בהירים."),
        new(Darker, "כהה מדי", "בהירות וניגודיות", "מבהיר את כל הטווח במידה שווה."),
        new(Lighter, "בהיר מדי", "בהירות וניגודיות", "מכהה את כל הטווח במידה שווה."),
        new(ContrastHigh, "קונטרסט חזק מדי", "בהירות וניגודיות", "מקרב צללים והבהרות לאמצע."),
        new(ContrastLow, "קונטרסט חלש מדי", "בהירות וניגודיות", "מרחיק צללים והבהרות מהאמצע."),
        new(BlackBlocked, "שחור סתום", "שחורים ולבנים", "פותח את האזורים הכהים ביותר."),
        new(BlackWeak, "שחור חלש", "שחורים ולבנים", "מעמיק את השחורים."),
        new(WhitesBurnt, "לבנים שרופים", "שחורים ולבנים", "מחזיר פרטים בגוונים הבהירים ביותר. לבן הנייר מוגן."),
        new(GraysCast, "אפורים לא ניטרליים", "שחורים ולבנים", "מצמצם גוון צבע באפורים."),
        new(SkinYellow, "עור צהוב מדי", "גווני עור", "פועל רק על דגימות העור בדף."),
        new(SkinRed, "עור אדום מדי", "גווני עור", "פועל רק על דגימות העור בדף."),
        new(SkinMagenta, "עור ורוד / מג׳נטה מדי", "גווני עור", "פועל רק על דגימות העור בדף."),
        new(SkinGreen, "עור ירוק מדי", "גווני עור", "פועל רק על דגימות העור בדף."),
        new(SkinDark, "עור כהה מדי", "גווני עור", "פועל רק על דגימות העור בדף."),
        new(SkinLight, "עור בהיר מדי", "גווני עור", "פועל רק על דגימות העור בדף."),
        new(RedsStrong, "אדומים חזקים מדי", "צבעים", ""),
        new(RedsWeak, "אדומים חלשים מדי", "צבעים", ""),
        new(BluesStrong, "כחולים חזקים מדי", "צבעים", ""),
        new(BluesWeak, "כחולים חלשים מדי", "צבעים", ""),
        new(GreensStrong, "ירוקים חזקים מדי", "צבעים", ""),
        new(GreensWeak, "ירוקים חלשים מדי", "צבעים", ""),
        new(YellowsStrong, "צהובים חזקים מדי", "צבעים", ""),
        new(YellowsWeak, "צהובים חלשים מדי", "צבעים", ""),
        new(Gradients, "מעברים לא חלקים", "חלקות ופרטים", "מחליק קלות את נתוני המדידה בין צבעים קרובים."),
        new(Banding, "פסים (banding)", "חלקות ופרטים", "אותה החלקה, לפסים נראים במעברים."),
        new(ShadowDetail, "פרטים נעלמים באזורים כהים", "חלקות ופרטים", ""),
        new(HighlightDetail, "פרטים נעלמים באזורים בהירים", "חלקות ופרטים", ""),
    ];

    private static readonly Dictionary<string, string> LegacyLabels = new()
    {
        ["gradients לא חלקים"] = Gradients,
        ["banding"] = Banding,
    };

    /// <summary>Returns the stable id for an id, a current label or a legacy label; unknown text is returned unchanged.</summary>
    public static string Resolve(string idOrLabel)
    {
        if (All.Any(o => o.Id == idOrLabel)) return idOrLabel;
        if (LegacyLabels.TryGetValue(idOrLabel, out var legacy)) return legacy;
        return All.FirstOrDefault(o => o.Label == idOrLabel)?.Id ?? idOrLabel;
    }

    public static string LabelOf(string idOrLabel)
    {
        string id = Resolve(idOrLabel);
        return All.FirstOrDefault(o => o.Id == id)?.Label ?? idOrLabel;
    }
}

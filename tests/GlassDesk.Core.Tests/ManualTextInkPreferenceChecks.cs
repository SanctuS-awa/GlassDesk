using System;
using System.Text.Json;
using GlassDesk.Core;

internal static class ManualTextInkPreferenceChecks
{
    public static void Run(Action<bool, string> check)
    {
        var defaults = new WidgetPreferences();
        check(defaults.Version == 4 && defaults.TextInk == WidgetTextInk.Black,
            "manual text ink defaults to black without changing preference version 4");

        var invalid = new WidgetPreferences { TextInk = (WidgetTextInk)99 }.Validated();
        check(invalid.TextInk == WidgetTextInk.Black,
            "unknown serialized text ink values normalize to black");

        var legacy = JsonSerializer.Deserialize<WidgetPreferences>(
            "{\"Version\":4,\"ClockShowTime\":true}")!.Validated();
        check(legacy.Version == 4 && legacy.ClockShowTime && legacy.TextInk == WidgetTextInk.Black,
            "version 4 preferences without the optional text ink field keep their values and default to black");

        var validatedWhite = new WidgetPreferences
        {
            Version = 4, TextInk = WidgetTextInk.White, ClockShowWeekday = true
        }.Validated();
        check(validatedWhite.Version == 4 && validatedWhite.TextInk == WidgetTextInk.White
            && validatedWhite.ClockShowWeekday,
            "valid white selection survives validation without changing unrelated preferences");

        var serialized = JsonSerializer.Serialize(new WidgetPreferences { TextInk = WidgetTextInk.White });
        var roundTrip = JsonSerializer.Deserialize<WidgetPreferences>(serialized)?.Validated();
        check(roundTrip is { Version: 4, TextInk: WidgetTextInk.White },
            "white text selection survives the preference JSON round trip");
    }
}

using GlassDesk.Core;

static class HomePresentationChecks
{
    public static void Run(Action<bool,string> check)
    {
        var standard = HomePresentationLayout.Resolve(960, 512);
        var compact = HomePresentationLayout.Resolve(820, 456);

        check(CardsFit(standard), "standard Home cards fit the canvas without overlap");
        check(CardsFit(compact), "compact Home cards fit the canvas without overlap");
        check(HasMagazineSpacing(standard, 120) && HasMagazineSpacing(compact, 100),
            "Home keeps weather and calendar left of music with magazine spacing");
        check(StatusFits(standard) && StatusFits(compact),
            "Home status indicators fit the width without overlap and plug precedes battery");
        check(DockFits(standard) && DockFits(compact),
            "Home dock, folder, navigation and supported app icons fit the width");
    }

    private static bool CardsFit(HomePresentationLayout layout)
    {
        FloatRect[] cards = [layout.Weather, layout.ClockDate, layout.Calendar, layout.Media];
        for (int index = 0; index < cards.Length; index++)
        {
            FloatRect card = cards[index];
            if (card.X < 0 || card.Y < 0 || card.Right > layout.CanvasWidth || card.Bottom > layout.CanvasHeight)
                return false;
            for (int other = index + 1; other < cards.Length; other++)
                if (card.Intersects(cards[other])) return false;
        }
        return true;
    }

    private static bool HasMagazineSpacing(HomePresentationLayout layout, float minimumGap) =>
        layout.Weather.X < layout.Calendar.X
        && layout.Media.X > layout.Calendar.Right
        && layout.Media.X - layout.Calendar.Right >= minimumGap;

    private static bool StatusFits(HomePresentationLayout layout)
    {
        FloatRect[] indicators = [layout.StatusBattery, layout.StatusPlug, layout.StatusWifiWithPlug];
        for (int index = 0; index < indicators.Length; index++)
        {
            FloatRect indicator = indicators[index];
            if (indicator.X < 0 || indicator.Right > layout.Width) return false;
            for (int other = index + 1; other < indicators.Length; other++)
                if (indicator.Intersects(indicators[other])) return false;
        }
        return layout.StatusPlug.Right <= layout.StatusBattery.X;
    }

    private static bool DockFits(HomePresentationLayout layout)
    {
        if (!WithinWidth(layout.Dock, layout.Width)
            || !WithinWidth(layout.DockFolder, layout.Width)
            || !WithinWidth(layout.DockNavigation, layout.Width)
            || layout.DockFolder.Width < 44 || layout.DockFolder.Height < 44
            || layout.DockNavigation.Width < 44 || layout.DockNavigation.Height < 44)
            return false;

        for (int index = 0; index < WidgetActions.HomeAppCount; index++)
            if (!WithinWidth(layout.DockAppIcon(index), layout.Width)) return false;
        return true;
    }

    private static bool WithinWidth(FloatRect rect, float width) => rect.X >= 0 && rect.Right <= width;
}

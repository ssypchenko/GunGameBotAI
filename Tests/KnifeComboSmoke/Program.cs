using GunGameBotAI.Services;

static void Check(bool result, string description)
{
    if (!result)
        throw new InvalidOperationException($"Knife combo smoke failed: {description}");
}

int hits = 0;
Check(!KnifeComboPolicy.ShouldUseSecondary(hits), "first primary");
hits = KnifeComboPolicy.OnConfirmedHit(hits, secondary: false);
Check(hits == 1 && !KnifeComboPolicy.ShouldUseSecondary(hits), "second primary");
hits = KnifeComboPolicy.OnConfirmedHit(hits, secondary: false);
Check(hits == 2 && KnifeComboPolicy.ShouldUseSecondary(hits), "secondary after two hits");
hits = KnifeComboPolicy.OnConfirmedHit(hits, secondary: true);
Check(hits == 0 && !KnifeComboPolicy.ShouldUseSecondary(hits), "combo restarts");
Check(KnifeComboPolicy.IsTimelyConfirmation(10.5f, 10.0f), "timely hit");
Check(!KnifeComboPolicy.IsTimelyConfirmation(11.0f, 10.0f), "stale hit");
Check(!KnifeComboPolicy.IsTimelyConfirmation(9.0f, 10.0f), "reversed timestamp");
Check(!KnifeComboPolicy.IsTimelyConfirmation(9.0f, float.NegativeInfinity), "unset swing");
Console.WriteLine("Knife combo smoke checks passed.");

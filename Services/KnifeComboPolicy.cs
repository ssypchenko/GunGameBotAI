namespace GunGameBotAI.Services;

/// <summary>
/// The combo advances on confirmed hits, not on attempted swings.
/// </summary>
public static class KnifeComboPolicy
{
    public static bool ShouldUseSecondary(int confirmedPrimaryHits) =>
        confirmedPrimaryHits >= 2;

    public static int OnConfirmedHit(int confirmedPrimaryHits, bool secondary) =>
        secondary ? 0 : Math.Clamp(confirmedPrimaryHits + 1, 0, 2);

    public static bool IsTimelyConfirmation(float now, float issuedAt) =>
        float.IsFinite(now) &&
        float.IsFinite(issuedAt) &&
        now >= issuedAt &&
        now - issuedAt <= 0.80f;
}

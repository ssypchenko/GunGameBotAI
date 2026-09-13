using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace GunGameBotAI.Config;

public sealed class GunGameBotAIConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;

    public bool EnabledOnLoad { get; set; } = false;
    public bool Debug { get; set; } = false;
    public float DecisionIntervalSeconds { get; set; } = 0.10f;
    public int FastActuatorEveryTicks { get; set; } = 2;

    public bool AggressiveStateEnabled { get; set; } = true;
    public bool DisablePanic { get; set; } = true;
    public bool DisableSurprise { get; set; } = true;
    public bool DisableIgnoreEnemies { get; set; } = true;
    public bool PreventSleeping { get; set; } = true;
    public bool PreventPoliteWaiting { get; set; } = true;

    public bool IdleRepathEnabled { get; set; } = true;
    public float IdleRepathSeconds { get; set; } = 4.0f;

    public bool StuckRecoveryEnabled { get; set; } = true;
    public float StuckHardSeconds { get; set; } = 1.0f;
    public float StuckSoftSeconds { get; set; } = 3.0f;
    public float StuckMinProgress { get; set; } = 75.0f;

    public bool LadderAssistEnabled { get; set; } = true;
    public float LadderAssistCooldownSeconds { get; set; } = 0.35f;
    public int LadderAssistMaxAttempts { get; set; } = 3;
    public float LadderAssistEntryDistance { get; set; } = 150.0f;
    public float LadderAssistVerticalThreshold { get; set; } = 24.0f;
    public float LadderAssistForwardMove { get; set; } = 200.0f;
    public float LadderAssistSideMove { get; set; } = 80.0f;

    public bool CombatStrafeEnabled { get; set; } = true;
    public bool CounterStrafeEnabled { get; set; } = true;
    public bool SniperPeekEnabled { get; set; } = true;

    public bool KnifeRushEnabled { get; set; } = true;
    public int KnifeRushChancePercent { get; set; } = 50;
    public float KnifeRushTriggerDistance { get; set; } = 400.0f;
    public float KnifeRushAbortDistance { get; set; } = 700.0f;
    public float KnifeRushTimeoutSeconds { get; set; } = 5.0f;
    public float KnifeRushLostSightSeconds { get; set; } = 1.25f;
    public float KnifeRushCooldownSeconds { get; set; } = 8.0f;
    public float KnifeRushZigZagMinInterval { get; set; } = 0.16f;
    public float KnifeRushZigZagMaxInterval { get; set; } = 0.30f;
    public float KnifeRushLateralImpulse { get; set; } = 160.0f;
    public float KnifeRushForwardBoost { get; set; } = 40.0f;
    public float KnifeRushAttackDistance { get; set; } = 78.0f;
    public float KnifeRushSecondaryAttackDistance { get; set; } = 60.0f;
    public int KnifeRushSecondaryAttackChancePercent { get; set; } = 35;
    public bool KnifeRushAllowOnGrenadeLevel { get; set; } = false;

    public bool GrenadeLevelEnabled { get; set; } = true;
    public bool AimEnhancementEnabled { get; set; } = false;

    public int MaxWeaponSwitchRetries { get; set; } = 5;
    public float WeaponSwitchRetryIntervalSeconds { get; set; } = 0.10f;

    public void Validate(Action<string> warn)
    {
        DecisionIntervalSeconds = Clamp(DecisionIntervalSeconds, 0.05f, 0.25f, 0.10f, nameof(DecisionIntervalSeconds), warn);
        FastActuatorEveryTicks = Clamp(FastActuatorEveryTicks, 1, 2, 2, nameof(FastActuatorEveryTicks), warn);
        IdleRepathSeconds = Clamp(IdleRepathSeconds, 0.5f, 30.0f, 4.0f, nameof(IdleRepathSeconds), warn);
        StuckHardSeconds = Clamp(StuckHardSeconds, 0.25f, 10.0f, 1.0f, nameof(StuckHardSeconds), warn);
        StuckSoftSeconds = Clamp(StuckSoftSeconds, StuckHardSeconds, 30.0f, Math.Max(3.0f, StuckHardSeconds), nameof(StuckSoftSeconds), warn);
        StuckMinProgress = Clamp(StuckMinProgress, 1.0f, 1000.0f, 75.0f, nameof(StuckMinProgress), warn);

        LadderAssistCooldownSeconds = Clamp(LadderAssistCooldownSeconds, 0.1f, 2.0f, 0.35f, nameof(LadderAssistCooldownSeconds), warn);
        LadderAssistMaxAttempts = Clamp(LadderAssistMaxAttempts, 1, 10, 3, nameof(LadderAssistMaxAttempts), warn);
        LadderAssistEntryDistance = Clamp(LadderAssistEntryDistance, 32.0f, 500.0f, 150.0f, nameof(LadderAssistEntryDistance), warn);
        LadderAssistVerticalThreshold = Clamp(LadderAssistVerticalThreshold, 8.0f, 256.0f, 24.0f, nameof(LadderAssistVerticalThreshold), warn);
        LadderAssistForwardMove = Clamp(LadderAssistForwardMove, 50.0f, 450.0f, 200.0f, nameof(LadderAssistForwardMove), warn);
        LadderAssistSideMove = Clamp(LadderAssistSideMove, 0.0f, 250.0f, 80.0f, nameof(LadderAssistSideMove), warn);

        KnifeRushChancePercent = Clamp(KnifeRushChancePercent, 0, 100, 50, nameof(KnifeRushChancePercent), warn);
        KnifeRushTriggerDistance = Clamp(KnifeRushTriggerDistance, 100.0f, 1000.0f, 400.0f, nameof(KnifeRushTriggerDistance), warn);
        KnifeRushAbortDistance = Clamp(KnifeRushAbortDistance, KnifeRushTriggerDistance, 2000.0f, Math.Max(700.0f, KnifeRushTriggerDistance), nameof(KnifeRushAbortDistance), warn);
        KnifeRushTimeoutSeconds = Clamp(KnifeRushTimeoutSeconds, 0.5f, 30.0f, 5.0f, nameof(KnifeRushTimeoutSeconds), warn);
        KnifeRushLostSightSeconds = Clamp(KnifeRushLostSightSeconds, 0.1f, 10.0f, 1.25f, nameof(KnifeRushLostSightSeconds), warn);
        KnifeRushCooldownSeconds = Clamp(KnifeRushCooldownSeconds, 0.0f, 60.0f, 8.0f, nameof(KnifeRushCooldownSeconds), warn);
        KnifeRushZigZagMinInterval = Clamp(KnifeRushZigZagMinInterval, 0.05f, 2.0f, 0.16f, nameof(KnifeRushZigZagMinInterval), warn);
        KnifeRushZigZagMaxInterval = Clamp(KnifeRushZigZagMaxInterval, KnifeRushZigZagMinInterval, 2.0f, Math.Max(0.30f, KnifeRushZigZagMinInterval), nameof(KnifeRushZigZagMaxInterval), warn);
        KnifeRushLateralImpulse = Clamp(KnifeRushLateralImpulse, 0.0f, 1000.0f, 160.0f, nameof(KnifeRushLateralImpulse), warn);
        KnifeRushForwardBoost = Clamp(KnifeRushForwardBoost, 0.0f, 500.0f, 40.0f, nameof(KnifeRushForwardBoost), warn);
        KnifeRushAttackDistance = Clamp(KnifeRushAttackDistance, 20.0f, 200.0f, 78.0f, nameof(KnifeRushAttackDistance), warn);
        KnifeRushSecondaryAttackDistance = Clamp(KnifeRushSecondaryAttackDistance, 20.0f, KnifeRushAttackDistance, 60.0f, nameof(KnifeRushSecondaryAttackDistance), warn);
        KnifeRushSecondaryAttackChancePercent = Clamp(KnifeRushSecondaryAttackChancePercent, 0, 100, 35, nameof(KnifeRushSecondaryAttackChancePercent), warn);

        MaxWeaponSwitchRetries = Clamp(MaxWeaponSwitchRetries, 1, 10, 5, nameof(MaxWeaponSwitchRetries), warn);
        WeaponSwitchRetryIntervalSeconds = Clamp(WeaponSwitchRetryIntervalSeconds, 0.05f, 2.0f, 0.10f, nameof(WeaponSwitchRetryIntervalSeconds), warn);

        if (Version < 1)
        {
            warn("ConfigVersion was below 1; using version 1.");
            Version = 1;
        }
    }

    private static int Clamp(int value, int minimum, int maximum, int fallback, string name, Action<string> warn)
    {
        if (value >= minimum && value <= maximum)
            return value;

        int corrected = Math.Clamp(value == 0 ? fallback : value, minimum, maximum);
        warn($"{name}={value} is outside {minimum}..{maximum}; using {corrected}.");
        return corrected;
    }

    private static float Clamp(float value, float minimum, float maximum, float fallback, string name, Action<string> warn)
    {
        if (float.IsFinite(value) && value >= minimum && value <= maximum)
            return value;

        float corrected = float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
        warn($"{name}={value} is outside {minimum}..{maximum}; using {corrected:0.###}.");
        return corrected;
    }
}

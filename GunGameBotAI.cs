using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using GunGameBotAI.Config;
using GunGameBotAI.Models;
using GunGameBotAI.Services;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace GunGameBotAI;

public sealed class GunGameBotAI : BasePlugin, IPluginConfig<GunGameBotAIConfig>
{
    private readonly Random _random = new();
    private readonly CorrectionLogger _corrections;
    private readonly BotRegistry _registry = new();
    private readonly ButtonPulseService _buttonPulses;
    private readonly BotSensorService _sensor = new();
    private readonly AggressionService _aggression;
    private readonly IdleRecoveryService _idleRecovery;
    private readonly CombatMovementService _combatMovement;
    private readonly GrenadeLevelService _grenadeLevel;
    private readonly WeaponActivationService _weaponActivation;
    private readonly KnifeRushService _knifeRush;
    private readonly GeometrySafetyService _geometrySafety;
    private LadderMapService? _ladderMap;
    private readonly Dictionary<string, float> _lastErrorAt = new();
    private const float BotSpawnGraceSeconds = 0.40f;

    /// <summary>
    /// Newly created/spawned bots must not be inspected through CCSBot/pawn native
    /// state immediately. The Source 2 bot object can exist before all of its
    /// internal AI state is fully initialised.
    ///
    /// We track both UserId and controller Handle so slot reuse cannot accidentally
    /// inherit the previous bot's grace state.
    /// </summary>
    private readonly Dictionary<
        int,
        (int? UserId, nint ControllerHandle, float ReadyAt)>
        _botSpawnGrace = new();

    private Timer? _decisionTimer;
    private Timer? _actuatorTimer;
    private IAPI? _gunGameApi;
    private bool _subscribedToGunGameApi;
    private bool _loaded;
    private bool _enabled;
    private bool _mapChanging;

    public GunGameBotAI()
    {
        _corrections = new CorrectionLogger(
            () => Config.Debug && Config.VerboseCorrectionDebug,
            message => Logger.LogInformation("[GunGameBotAI][DEBUG] {Message}", message));
        _buttonPulses = new ButtonPulseService(_corrections);
        _aggression = new AggressionService(_corrections);
        _idleRecovery = new IdleRecoveryService(_corrections);
        _combatMovement = new CombatMovementService(_corrections);
        _grenadeLevel = new GrenadeLevelService(_corrections);
        _weaponActivation = new WeaponActivationService(new NativeSelectItemWeaponSwitchBackend(), _corrections);
        _knifeRush = new KnifeRushService(_random, _weaponActivation, _buttonPulses, _corrections, DebugLog);
        _geometrySafety = new GeometrySafetyService(
            message => Logger.LogInformation("[GunGameBotAI][GEOMETRY] {Message}", message));
    }

    public override string ModuleName => "GunGame Bot AI";
    public override string ModuleVersion => "0.7.28";
    public override string ModuleAuthor => "Sergey";
    public override string ModuleDescription => "Bounded GunGame bot behaviour improvements.";

    public GunGameBotAIConfig Config { get; set; } = new();

    public static PluginCapability<IAPI> APICapability { get; } = new("gungame:api");

    public void OnConfigParsed(GunGameBotAIConfig config)
    {
        config.Validate(message => Logger.LogWarning("[GunGameBotAI] {Message}", message));
        Config = config;
        ApplyConfigToServices();

        if (!_loaded)
            _enabled = Config.EnabledOnLoad;
    }

    public override void Load(bool hotReload)
    {
        _loaded = true;
        ApplyConfigToServices();

        LadderMapStore ladderMapStore = new(
            ModuleDirectory,
            message => Logger.LogInformation("[GunGameBotAI][LADDER] {Message}", message),
            (exception, message) => Logger.LogWarning(exception, "[GunGameBotAI][LADDER] {Message}", message));

        _ladderMap = new LadderMapService(
            ladderMapStore,
            _buttonPulses,
            _corrections,
            message => Logger.LogInformation("[GunGameBotAI][LADDER] {Message}", message),
            message => Logger.LogInformation("[GunGameBotAI][LADDER] {Message}", message))
        {
            Config = Config
        };

        string currentMap = Server.MapName;
        if (!string.IsNullOrWhiteSpace(currentMap))
            _ladderMap.OnMapStart(currentMap);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventBotTakeover>(OnBotTakeover);

        StartSharedTimers();
        Logger.LogInformation(
            "[GunGameBotAI] Loaded. Runtime is {RuntimeState}; weapon backend: {Backend}.",
            _enabled ? "enabled" : "disabled",
            _weaponActivation.BackendName);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _gunGameApi = APICapability.Get();
            if (_gunGameApi == null)
            {
                Logger.LogWarning("[GunGameBotAI] GunGame API is unavailable; inventory classification remains active.");
                return;
            }

            _gunGameApi.LevelChangeEvent += OnGunGameLevelChange;
            _gunGameApi.RestartEvent += OnGunGameRestart;
            _subscribedToGunGameApi = true;
            Logger.LogInformation("[GunGameBotAI] Connected to the optional GunGame API.");
        }
        catch (Exception exception)
        {
            _gunGameApi = null;
            Logger.LogWarning(exception, "[GunGameBotAI] GunGame API is unavailable; using inventory fallback.");
        }
    }

    public override void Unload(bool hotReload)
    {
        StopSharedTimers();
        _ladderMap?.Shutdown();
        _ladderMap = null;
        _geometrySafety.Reset();
        ReleaseAllKnownButtonPulses();
        _enabled = false;
        _buttonPulses.CancelAll();
        _registry.Clear();

        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        RemoveListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        DeregisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        DeregisterEventHandler<EventWeaponFire>(OnWeaponFire);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        DeregisterEventHandler<EventBotTakeover>(OnBotTakeover);

        if (_subscribedToGunGameApi && _gunGameApi != null)
        {
            _gunGameApi.LevelChangeEvent -= OnGunGameLevelChange;
            _gunGameApi.RestartEvent -= OnGunGameRestart;
        }

        _subscribedToGunGameApi = false;
        _gunGameApi = null;
        _loaded = false;
    }

    private void StartSharedTimers()
    {
        StopSharedTimers();
        _decisionTimer = AddTimer(
            Config.DecisionIntervalSeconds,
            DecisionLoop,
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        _actuatorTimer = AddTickTimer(
            Config.FastActuatorEveryTicks,
            ActuatorLoop,
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopSharedTimers()
    {
        try
        {
            _decisionTimer?.Kill();
            _actuatorTimer?.Kill();
        }
        catch (Exception exception)
        {
            LogRateLimited("timer-stop", exception, "Failed to stop a shared timer.");
        }

        _decisionTimer = null;
        _actuatorTimer = null;
    }
    private void StartBotSpawnGrace(
    CCSPlayerController controller,
    float now)
    {
        if (controller == null ||
            !controller.IsValid ||
            !controller.IsBot ||
            controller.IsHLTV)
        {
            return;
        }

        int slot = controller.Slot;

        _botSpawnGrace[slot] =
            (
                controller.UserId,
                controller.Handle,
                now + BotSpawnGraceSeconds
            );

        /*
        * A slot entering spawn grace must not retain actuator/runtime state
        * belonging to its previous pawn or previous life.
        */
        _buttonPulses.Cancel(slot);
        _registry.Remove(slot);
        _ladderMap?.RemoveSlot(slot, "spawn-grace");
        _geometrySafety.RemoveSlot(slot);
    }

    private bool IsBotInSpawnGrace(
        CCSPlayerController controller,
        float now)
    {
        if (controller == null ||
            !controller.IsValid ||
            !controller.IsBot ||
            controller.IsHLTV)
        {
            return false;
        }

        int slot = controller.Slot;
        int? userId = controller.UserId;
        nint controllerHandle = controller.Handle;

        /*
        * Critical protection:
        *
        * Do not rely only on EventPlayerSpawn.
        *
        * bot_add/bot_quota can expose the controller to Utilities.GetPlayers()
        * before EventPlayerSpawn has reached us. Therefore the first observation
        * of a new controller starts a grace period as well.
        */
        if (!_botSpawnGrace.TryGetValue(slot, out var grace) ||
            grace.UserId != userId ||
            grace.ControllerHandle != controllerHandle)
        {
            StartBotSpawnGrace(
                controller,
                now);

            return true;
        }

        return now < grace.ReadyAt;
    }

    private void DecisionLoop()
    {
        if (!_enabled || _mapChanging)
            return;

        float now = Server.CurrentTime;

        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (!player.IsBot || player.IsHLTV)
                continue;

            int slot = player.Slot;

            try
            {
                /*
                * IMPORTANT:
                * This check MUST happen before TryResolveLiveBot().
                *
                * TryResolveLiveBot eventually accesses PlayerPawn and pawn.Bot.
                * We deliberately avoid touching those objects during the
                * initialisation grace period.
                */
                if (IsBotInSpawnGrace(player, now))
                    continue;

                if (!BotValidation.TryResolveLiveBot(
                        slot,
                        out CCSPlayerController? controller,
                        out CCSPlayerPawn? pawn,
                        out CCSBot? bot) ||
                    controller == null ||
                    pawn == null ||
                    bot == null)
                {
                    _buttonPulses.Cancel(slot);
                    _registry.Remove(slot);
                    continue;
                }

                BotRuntimeState state =
                    _registry.GetOrCreate(slot);

                if (state.HasBeenControlledByPlayerThisRound)
                {
                    _buttonPulses.Cancel(slot);
                    _registry.DeactivateActuator(slot);
                    continue;
                }

                _geometrySafety.Observe(
                    controller,
                    pawn,
                    _ladderMap?.Ladders ??
                        Array.Empty<PhysicalLadder>(),
                    now);

                bool ladderTraversalActive =
                    _ladderMap?.ObserveAndMaybeStartTraversal(
                         pawn,
                         bot,
                         state,
                         now) == true;

                if (ladderTraversalActive)
                {
                    // Learned ladder traversal owns movement before Knife Rush
                    // or normal combat movement. This keeps the one-shot entry
                    // jump and mount observation free from movement conflicts.
                    if (_knifeRush.IsActive(state))
                    {
                        _knifeRush.Abort(
                            controller,
                            pawn,
                            state,
                            now,
                            "LADDER_TRAVERSAL",
                            startCooldown: true);
                    }


                    SetMode(
                        state,
                        BotBehaviorMode.LadderTraversal,
                        "learned physical ladder traversal owns movement");

                    _registry.ActivateActuator(slot);
                    continue;
                }

                ThinkBot(
                    controller,
                    pawn,
                    bot,
                    state,
                    now);

                if (_buttonPulses.HasPending(slot))
                {
                    _registry.ActivateActuator(slot);
                }
            }
            catch (Exception exception)
            {
                LogRateLimited(
                    $"decision-{slot}",
                    exception,
                    $"Decision failed for bot slot {slot}.");
            }
        }
    }

    private void ThinkBot(
        CCSPlayerController controller,
        CCSPlayerPawn pawn,
        CCSBot bot,
        BotRuntimeState state,
        float now)
    {
        EnemySnapshot? enemy =
            _sensor.ReadEnemy(
                pawn,
                bot,
                state,
                now);

        UpdateWeaponState(
            controller.Slot,
            pawn,
            state);

        _aggression.Apply(
            controller.Slot,
            bot,
            now);

        // Keep the generic recovery surface deliberately small. The retired
        // LadderAssist/StuckRecovery services repeatedly fought Valve bot AI and
        // never recovered a bot that had fallen into broken ladder geometry.
        _idleRecovery.TryRepath(
            pawn,
            bot,
            state,
            now);

        bool mandatoryKnife =
            state.LevelWeaponClass == WeaponClass.Knife;

        bool grenadeLevel =
            state.LevelWeaponClass == WeaponClass.Grenade;

        if (grenadeLevel &&
            !_knifeRush.Config.KnifeRushAllowOnGrenadeLevel &&
            _knifeRush.IsActive(state))
        {
            _knifeRush.Abort(
                controller,
                pawn,
                state,
                now,
                "GRENADE_LEVEL",
                startCooldown: true);
        }

        bool specialMode =
            _knifeRush.ApplyDecision(
                controller,
                pawn,
                bot,
                state,
                enemy,
                now,
                mandatoryKnife,
                grenadeLevel);

        if (mandatoryKnife)
        {
            SetMode(
                state,
                BotBehaviorMode.KnifeLevel,
                "mandatory knife level");

            _registry.ActivateActuator(state.Slot);
            return;
        }

        if (specialMode)
        {
            SetMode(
                state,
                BotBehaviorMode.OpportunisticKnifeRush,
                "Knife Rush active");

            _registry.ActivateActuator(state.Slot);
            return;
        }

        if (grenadeLevel)
        {
            SetMode(
                state,
                BotBehaviorMode.GrenadeLevel,
                "grenade level");

            _registry.DeactivateActuator(state.Slot);
            _grenadeLevel.Apply(
                state.Slot,
                pawn,
                bot,
                enemy);

            return;
        }

        SetMode(
            state,
            BotBehaviorMode.NormalGunGame,
            "normal GunGame behaviour");

        _registry.DeactivateActuator(state.Slot);
        _combatMovement.ApplyNormal(
            pawn,
            bot,
            state,
            enemy);
    }

    private void ActuatorLoop()
    {
        if (!_enabled || _mapChanging)
            return;

        float now = Server.CurrentTime;

        IReadOnlyList<int> activeSlots =
            _registry.ActiveActuatorSlots;

        for (int index = activeSlots.Count - 1;
            index >= 0;
            index--)
        {
            int slot = activeSlots[index];

            try
            {
                /*
                * Check the controller BEFORE TryResolveLiveBot().
                *
                * This avoids touching pawn.Bot during the bot's initialisation
                * grace period.
                */
                CCSPlayerController? preliminaryController =
                    Utilities.GetPlayerFromSlot(slot);

                if (preliminaryController == null ||
                    !preliminaryController.IsValid ||
                    !preliminaryController.IsBot ||
                    preliminaryController.IsHLTV)
                {
                    _buttonPulses.Cancel(slot);
                    _registry.Remove(slot);
                    continue;
                }

                if (IsBotInSpawnGrace(
                        preliminaryController,
                        now))
                {
                    _buttonPulses.Cancel(slot);
                    _registry.Remove(slot);
                    continue;
                }

                if (!BotValidation.TryResolveLiveBot(
                        slot,
                        out CCSPlayerController? controller,
                        out CCSPlayerPawn? pawn,
                        out CCSBot? bot) ||
                    controller == null ||
                    pawn == null ||
                    bot == null)
                {
                    _buttonPulses.Cancel(slot);
                    _registry.Remove(slot);
                    continue;
                }

                if (!_registry.TryGet(
                        slot,
                        out BotRuntimeState? state) ||
                    state == null ||
                    state.HasBeenControlledByPlayerThisRound)
                {
                    _buttonPulses.Cancel(slot);
                    _registry.DeactivateActuator(slot);
                    continue;
                }

                EnemySnapshot? enemy =
                    _sensor.ReadEnemy(
                        pawn,
                        bot,
                        state,
                        now);

                // Fast actuator ownership is exclusive. A learned physical
                // ladder traversal owns the one-shot jump/mount observation;
                // otherwise only Knife Rush needs fast continuous actuation.
                bool ladderTraversalOwned =
                    _ladderMap?.ApplyFast(
                        pawn,
                        bot,
                        state,
                        now) == true;

                if (!ladderTraversalOwned)
                {
                    _knifeRush.ApplyFast(
                        controller,
                        pawn,
                        bot,
                        state,
                        enemy,
                        now);
                }

                _buttonPulses.Update(
                    slot,
                    pawn);
            }
            catch (Exception exception)
            {
                LogRateLimited(
                    $"actuator-{slot}",
                    exception,
                    $"Actuator failed for bot slot {slot}.");
            }
        }
    }

    private void UpdateWeaponState(int slot, CCSPlayerPawn pawn, BotRuntimeState state)
    {
        WeaponClass oldClass = state.LevelWeaponClass;
        string? oldDesignerName = state.LevelWeaponDesignerName;

        LevelWeaponInfo info =
            WeaponClassifier.InspectLevelWeapon(pawn, state.LevelWeaponDesignerName);

        // Without GunGame API we have no authoritative progression level,
        // therefore inventory classification remains the fallback.
        if (_gunGameApi == null)
        {
            ApplyInventoryWeaponInfo(state, info);
            LogWeaponStateCorrection(slot, state, oldClass, oldDesignerName);
            return;
        }

        try
        {
            int maxLevel = _gunGameApi.GetMaxLevel();
            int grenadeLevel = maxLevel - 1;
            int level = _gunGameApi.GetPlayerLevel(slot);

            state.GunGameLevel = level;
            state.GunGameMaxLevel = maxLevel;

            // GunGame progression is authoritative for the two special levels:
            //   maxLevel     = mandatory knife
            //   maxLevel - 1 = HE grenade
            //
            // Do NOT infer either level from inventory. Immediately after spawn
            // a bot may temporarily own/hold only a knife before GunGame gives
            // the real level weapon.
            if (maxLevel > 0 && level == maxLevel)
            {
                state.LevelWeaponClass = WeaponClass.Knife;

                if (info.Class == WeaponClass.Knife &&
                    !string.IsNullOrWhiteSpace(info.DesignerName))
                {
                    state.LevelWeaponDesignerName = info.DesignerName;
                }
            }
            else if (maxLevel > 1 && level == grenadeLevel)
            {
                state.LevelWeaponClass = WeaponClass.Grenade;

                if (info.Class == WeaponClass.Grenade &&
                    !string.IsNullOrWhiteSpace(info.DesignerName))
                {
                    state.LevelWeaponDesignerName = info.DesignerName;
                }
            }
            else
            {
                // Normal GunGame level. A transient knife/grenade inventory state
                // must never promote the bot to a special progression mode.
                if (info.Class != WeaponClass.Unknown &&
                    info.Class != WeaponClass.Knife &&
                    info.Class != WeaponClass.Grenade)
                {
                    state.LevelWeaponClass = info.Class;

                    if (!string.IsNullOrWhiteSpace(info.DesignerName))
                        state.LevelWeaponDesignerName = info.DesignerName;
                }
                else if (state.LevelWeaponClass is WeaponClass.Knife or WeaponClass.Grenade)
                {
                    // We know from the GunGame level that this is NOT a special
                    // level, so clear a stale/transient special classification
                    // until the real normal weapon becomes visible in inventory.
                    state.LevelWeaponClass = WeaponClass.Unknown;
                    state.LevelWeaponDesignerName = null;
                }
            }
        }
        catch (Exception exception)
        {
            // If the API call itself fails, degrade safely to inventory for this
            // pass rather than leaving stale weapon state indefinitely.
            ApplyInventoryWeaponInfo(state, info);
            LogRateLimited(
                "gungame-level",
                exception,
                "GunGame API level read failed; inventory fallback used for this pass.");
        }

        LogWeaponStateCorrection(slot, state, oldClass, oldDesignerName);
    }

    private static void ApplyInventoryWeaponInfo(
        BotRuntimeState state,
        LevelWeaponInfo info)
    {
        if (info.Class == WeaponClass.Unknown)
            return;

        state.LevelWeaponClass = info.Class;

        if (!string.IsNullOrWhiteSpace(info.DesignerName))
            state.LevelWeaponDesignerName = info.DesignerName;
    }

    private void LogWeaponStateCorrection(int slot, BotRuntimeState state, WeaponClass oldClass, string? oldDesignerName)
    {
        _corrections.State(slot, "WeaponState", nameof(state.LevelWeaponClass), oldClass, state.LevelWeaponClass,
            "classify the current GunGame weapon");
        _corrections.State(slot, "WeaponState", nameof(state.LevelWeaponDesignerName), oldDesignerName,
            state.LevelWeaponDesignerName, "track the current GunGame weapon");
    }

    private void OnMapStart(string mapName)
    {
        _mapChanging = false;
        ResetRuntimeState();
        _ladderMap?.OnMapStart(mapName);

        if (_loaded)
            StartSharedTimers();
    }

    private void OnMapEnd()
    {
        _mapChanging = true;
        StopSharedTimers();
        _ladderMap?.OnMapEnd();
        ResetRuntimeState();
    }

    private void OnClientDisconnect(int playerSlot)
    {
        ReleaseButtonPulse(playerSlot);

        _botSpawnGrace.Remove(playerSlot);
        _ladderMap?.RemoveSlot(playerSlot, "disconnect");
        _geometrySafety.RemoveSlot(playerSlot);
        _registry.Remove(playerSlot);
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetRuntimeState();
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(
    EventPlayerSpawn @event,
    GameEventInfo info)
    {
        if (!_enabled ||
            @event.Userid is not
            {
                IsValid: true,
                IsBot: true
            } player ||
            player.IsHLTV)
        {
            return HookResult.Continue;
        }

        /*
        * Restart the full grace period from the actual spawn event.
        *
        * The bot may already have been observed between ClientPutInServer and
        * EventPlayerSpawn; that earlier grace protects creation.
        *
        * This one protects the newly spawned pawn for another 0.40 seconds.
        */
        StartBotSpawnGrace(
            player,
            Server.CurrentTime);

        /*
        * Do NOT call UpdateWeaponState() on NextFrame here.
        *
        * DecisionLoop will initialise weapon/runtime state after the grace period
        * once BotValidation confirms that the pawn and CCSBot are live.
        */
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(
    EventPlayerDeath @event,
    GameEventInfo info)
    {
        int slot = @event.Userid?.Slot ?? -1;

        if (slot >= 0)
        {
            ReleaseButtonPulse(slot);

            _botSpawnGrace.Remove(slot);
            _ladderMap?.RemoveSlot(slot, "player-death");
            _geometrySafety.RemoveSlot(slot);
            _registry.Remove(slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        if (!_enabled || @event.Userid is not { IsValid: true, IsBot: true } player || player.IsHLTV ||
            !BotValidation.TryResolveLiveBot(player.Slot, out _, out CCSPlayerPawn? pawn, out _) || pawn == null)
        {
            return HookResult.Continue;
        }

        BotRuntimeState state = _registry.GetOrCreate(player.Slot);

        // Special movement controllers own velocity/input while active.
        // Counter-strafe from normal combat movement must not fight them.
        if (state.Mode is
            BotBehaviorMode.KnifeLevel or
            BotBehaviorMode.OpportunisticKnifeRush or
            BotBehaviorMode.LadderTraversal)
        {
            return HookResult.Continue;
        }

        _combatMovement.OnWeaponFire(
            pawn,
            state,
            @event.Weapon,
            Server.CurrentTime);

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        int slot = @event.Userid?.Slot ?? -1;
        if (slot >= 0)
            OnClientDisconnect(slot);
        return HookResult.Continue;
    }

    private HookResult OnBotTakeover(
    EventBotTakeover @event,
    GameEventInfo info)
    {
        int slot = @event.Botid?.Slot ?? -1;

        if (slot >= 0 &&
            _registry.TryGet(
                slot,
                out BotRuntimeState? state) &&
            state != null)
        {
            state.HasBeenControlledByPlayerThisRound = true;

            ReleaseButtonPulse(slot);

            _botSpawnGrace.Remove(slot);
            _ladderMap?.RemoveSlot(slot, "bot-takeover");
            _geometrySafety.RemoveSlot(slot);
            _registry.DeactivateActuator(slot);
        }

        return HookResult.Continue;
    }

    private void OnGunGameLevelChange(LevelChangeEventArgs args)
    {
        if (!_enabled || !_registry.TryGet(args.Killer, out BotRuntimeState? state) || state == null)
            return;

        ReleaseButtonPulse(args.Killer);
        state.LevelWeaponClass = WeaponClass.Unknown;
        state.LevelWeaponDesignerName = null;
    }

    private void OnGunGameRestart()
    {
        ResetRuntimeState();
    }

    [ConsoleCommand("css_ggbotai_enable", "Show or set GunGameBotAI runtime state.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnEnableCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[GunGameBotAI] runtime={(_enabled ? "enabled" : "disabled")}.");
            return;
        }

        string value = command.GetArg(1);
        if (value is not ("0" or "1"))
        {
            command.ReplyToCommand("[GunGameBotAI] Usage: css_ggbotai_enable 0|1");
            return;
        }

        SetRuntimeEnabled(value == "1");
        command.ReplyToCommand($"[GunGameBotAI] runtime={(_enabled ? "enabled" : "disabled")}.");
    }

    [ConsoleCommand("css_ggbotai_status", "Show GunGameBotAI status.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        PrintStatus(command);
    }

    [ConsoleCommand("css_ggbotai_ladders", "Show learned physical ladders for the current map.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnLaddersCommand(CCSPlayerController? player, CommandInfo command)
    {
        PrintLadderMap(command);
    }

    [ConsoleCommand("css_ggbotai_ladders_reload", "Reload the current map's ladder JSON from disk.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnLaddersReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_ladderMap == null || string.IsNullOrWhiteSpace(_ladderMap.CurrentMap))
        {
            command.ReplyToCommand("[GunGameBotAI] No ladder map is currently loaded.");
            return;
        }

        _ladderMap.ReloadCurrentMap();
        command.ReplyToCommand(
            $"[GunGameBotAI] Reloaded ladder map '{_ladderMap.CurrentMap}'; physicalLadders={_ladderMap.LadderCount}.");
    }

    [ConsoleCommand("css_ggbotai_ladder_teach", "Show or set explicit manual ladder teaching mode.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnLadderTeachCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_ladderMap == null)
        {
            command.ReplyToCommand("[GunGameBotAI] Ladder map service is unavailable.");
            return;
        }

        if (command.ArgCount < 2)
        {
            command.ReplyToCommand(
                $"[GunGameBotAI] manual ladder teaching={(_ladderMap.ManualTeachingActive ? "enabled" : "disabled")}.");
            return;
        }

        if (!TryParseBinary(command.GetArg(1), out bool enabled))
        {
            command.ReplyToCommand("[GunGameBotAI] Usage: css_ggbotai_ladder_teach 0|1");
            return;
        }

        _ladderMap.SetManualTeachingActive(enabled);

        command.ReplyToCommand(
            $"[GunGameBotAI] manual ladder teaching={(enabled ? "enabled" : "disabled")}; " +
            $"trusted JSON persistence={(enabled ? "ARMED" : "LOCKED")}.");
    }

    [ConsoleCommand("css_ggbotai_ladder_jump", "Enable or disable proactive learned-ladder traversal.")]
    [CommandHelper(minArgs: 1, usage: "0|1", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnLadderJumpCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryParseBinary(command.GetArg(1), out bool enabled))
        {
            command.ReplyToCommand("[GunGameBotAI] Usage: css_ggbotai_ladder_jump 0|1");
            return;
        }

        Config.LadderEntryJumpEnabled = enabled;
        PersistConfig(command);
        command.ReplyToCommand(
            $"[GunGameBotAI] learned ladder traversal={(enabled ? "enabled" : "disabled")}.");
    }

    [ConsoleCommand("css_ggbotai_ladder_learning", "Show ladder learning mode.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnLadderLearningCommand(CCSPlayerController? player, CommandInfo command)
    {
        Config.LadderLearningEnabled = false;
        command.ReplyToCommand(
            "[GunGameBotAI] ladder learning=manual-only; bots never create persistent ladder records.");
    }

    [ConsoleCommand("css_ggbotai_debug", "Enable or disable focused GunGameBotAI diagnostics.")]
    [CommandHelper(minArgs: 1, usage: "0|1", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnDebugCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryParseBinary(command.GetArg(1), out bool enabled))
        {
            command.ReplyToCommand("[GunGameBotAI] Usage: css_ggbotai_debug 0|1");
            return;
        }

        Config.Debug = enabled;
        PersistConfig(command);

        Logger.LogInformation(
            "[GunGameBotAI] Focused diagnostics {State}. Routine correction logs are {CorrectionState}.",
            Config.Debug ? "ENABLED" : "DISABLED",
            Config.VerboseCorrectionDebug ? "ENABLED" : "DISABLED");

        command.ReplyToCommand(
            $"[GunGameBotAI] focusedDebug={(Config.Debug ? "enabled" : "disabled")}; " +
            $"verboseCorrections={(Config.VerboseCorrectionDebug ? "enabled" : "disabled")}.");
    }

    [ConsoleCommand("css_ggbotai_ladder_diag", "Enable or disable focused human ladder movement diagnostics.")]
    [CommandHelper(minArgs: 1, usage: "0|1", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnLadderDiagnosticCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryParseBinary(command.GetArg(1), out bool enabled))
        {
            command.ReplyToCommand("[GunGameBotAI] Usage: css_ggbotai_ladder_diag 0|1");
            return;
        }

        Config.LadderHumanMovementDiagnostics = enabled;
        PersistConfig(command);

        command.ReplyToCommand(
            $"[GunGameBotAI] human ladder diagnostics={(enabled ? "enabled" : "disabled")}.");
    }

    [ConsoleCommand("css_ggbotai_knife_chance", "Set Knife Rush chance percentage.")]
    [CommandHelper(minArgs: 1, usage: "0..100", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnKnifeChanceCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!int.TryParse(command.GetArg(1), out int chance) || chance < 0 || chance > 100)
        {
            command.ReplyToCommand("[GunGameBotAI] Usage: css_ggbotai_knife_chance 0..100");
            return;
        }

        Config.KnifeRushChancePercent = chance;
        PersistConfig(command);
        command.ReplyToCommand($"[GunGameBotAI] Knife Rush chance={chance}%.");
    }

    [ConsoleCommand("css_ggbotai_knife_distance", "Set Knife Rush trigger distance.")]
    [CommandHelper(minArgs: 1, usage: "100..1000", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnKnifeDistanceCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!float.TryParse(command.GetArg(1), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float distance) ||
            distance < 100.0f || distance > 1000.0f)
        {
            command.ReplyToCommand("[GunGameBotAI] Usage: css_ggbotai_knife_distance 100..1000");
            return;
        }

        Config.KnifeRushTriggerDistance = distance;
        if (Config.KnifeRushAbortDistance < distance)
            Config.KnifeRushAbortDistance = distance;
        PersistConfig(command);
        command.ReplyToCommand($"[GunGameBotAI] Knife Rush trigger distance={distance:0.###}.");
    }

    [ConsoleCommand("css_ggbotai_reload", "Reload GunGameBotAI configuration.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        bool previousEnabled = _enabled;
        try
        {
            Config.Reload();
            Config.Validate(message => Logger.LogWarning("[GunGameBotAI] {Message}", message));
            ApplyConfigToServices();
            StartSharedTimers();

            if (previousEnabled != Config.EnabledOnLoad)
                SetRuntimeEnabled(Config.EnabledOnLoad);

            command.ReplyToCommand("[GunGameBotAI] Configuration reloaded.");
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "[GunGameBotAI] Configuration reload failed.");
            command.ReplyToCommand("[GunGameBotAI] Configuration reload failed; previous runtime state remains.");
        }
    }

    [ConsoleCommand("css_ggbotai_testknife", "Test public bot knife activation.")]
    [CommandHelper(minArgs: 1, usage: "<slot>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnTestKnifeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!_enabled)
        {
            command.ReplyToCommand("[GunGameBotAI] Runtime is disabled; no weapon write was attempted.");
            return;
        }

        if (!int.TryParse(command.GetArg(1), out int slot) ||
            !BotValidation.TryResolveLiveBot(slot, out CCSPlayerController? controller,
                out CCSPlayerPawn? pawn, out _) || controller == null || pawn == null)
        {
            command.ReplyToCommand("[GunGameBotAI] Target slot is not a live bot.");
            return;
        }

        string before = _weaponActivation.GetActiveDesignerName(pawn) ?? "unknown";
        bool invoked = _weaponActivation.TryActivateKnife(controller, pawn);
        command.ReplyToCommand(
            $"[GunGameBotAI] backend={_weaponActivation.BackendName}; before={before}; invoked={invoked}; active={_weaponActivation.IsKnifeActive(pawn)}.");

        Server.NextFrame(() =>
        {
            if (!BotValidation.TryResolveLiveBot(slot, out _, out CCSPlayerPawn? currentPawn, out _) || currentPawn == null)
            {
                Server.PrintToConsole($"[GunGameBotAI] testknife slot={slot}: bot is no longer live.");
                return;
            }

            Server.PrintToConsole(
                $"[GunGameBotAI] testknife slot={slot}: active={_weaponActivation.IsKnifeActive(currentPawn)}; weapon={_weaponActivation.GetActiveDesignerName(currentPawn) ?? "unknown"}.");
        });
    }

    private void SetRuntimeEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        if (!enabled)
            ReleaseAllKnownButtonPulses();

        _enabled = enabled;
        _corrections.Action(-1, nameof(GunGameBotAI), "runtime", enabled ? "enabled" : "disabled", "operator command or configuration");
        _buttonPulses.CancelAll();
        _registry.Clear();
        _botSpawnGrace.Clear();
        _ladderMap?.ResetRuntimeTracking();

        if (!enabled)
            _knifeRush.ResetStatistics();
    }

    private void ResetRuntimeState()
    {
        _corrections.ClearThrottleState();
        ReleaseAllKnownButtonPulses();
        _buttonPulses.CancelAll();
        _registry.ResetAll();
        _botSpawnGrace.Clear();
        _ladderMap?.ResetRuntimeTracking();
        _geometrySafety.Reset();
        _knifeRush.ResetStatistics();
    }

    private void ApplyConfigToServices()
    {
        _aggression.Config = Config;

        if (_ladderMap != null)
            _ladderMap.Config = Config;

        _idleRecovery.Config = Config;
        _combatMovement.Config = Config;
        _grenadeLevel.Config = Config;
        _knifeRush.Config = Config;
        _geometrySafety.Config = Config;
    }

    private void PersistConfig(CommandInfo command)
    {
        try
        {
            Config.Update();
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "[GunGameBotAI] Could not persist the changed configuration.");
            command.ReplyToCommand("[GunGameBotAI] Runtime value changed, but configuration could not be saved.");
        }
    }

    private void PrintStatus(CommandInfo command)
    {
        int liveBots = 0;
        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (player.IsBot && !player.IsHLTV && BotValidation.TryResolveLiveBot(player.Slot, out _, out _, out _))
                liveBots++;
        }

        command.ReplyToCommand(
            $"[GunGameBotAI] runtime={(_enabled ? "enabled" : "disabled")}; " +
            $"focusedDebug={(Config.Debug ? "enabled" : "disabled")}; " +
            $"verboseCorrections={(Config.VerboseCorrectionDebug ? "enabled" : "disabled")}; " +
            $"humanLadderDiag={(Config.LadderHumanMovementDiagnostics ? "enabled" : "disabled")}; " +
            $"liveBots={liveBots}; tracked={_registry.Count}; actuator={_registry.ActiveActuatorSlots.Count}; pulses={_buttonPulses.Count}.");
        command.ReplyToCommand(
            $"[GunGameBotAI] decisionTimer={_decisionTimer != null}; actuatorTimer={_actuatorTimer != null}; decision={Config.DecisionIntervalSeconds:0.###}s; fastTicks={Config.FastActuatorEveryTicks}; backend={_weaponActivation.BackendName}; backendAvailable={_weaponActivation.IsBackendAvailable}.");
        command.ReplyToCommand(
            $"[GunGameBotAI] ladderMap={(string.IsNullOrWhiteSpace(_ladderMap?.CurrentMap) ? "none" : _ladderMap.CurrentMap)}; " +
            $"physicalLadders={_ladderMap?.LadderCount ?? 0}; candidates={_ladderMap?.CandidateCount ?? 0}; " +
            $"learning=manual-only; manualTeach={(_ladderMap?.ManualTeachingActive == true ? "enabled" : "disabled")}; " +
            $"traversal={Config.LadderEntryJumpEnabled}; " +
            $"geometrySafety={Config.GeometrySafetyDetectionEnabled}; geometryTracked={_geometrySafety.TrackedCount}.");
        command.ReplyToCommand(
            $"[GunGameBotAI] knifeRush opportunities={_knifeRush.OpportunityCount}; accepted={_knifeRush.AcceptedCount}; rejected={_knifeRush.RejectedCount}; aborted={_knifeRush.AbortCount}.");

        foreach (BotRuntimeState state in _registry.States.Values)
        {
            command.ReplyToCommand(
                $"[GunGameBotAI] slot={state.Slot}; mode={state.Mode}; weapon={state.LevelWeaponClass}; target={state.KnifeRushTargetEntityIndex?.ToString() ?? "none"}; switchAttempts={state.KnifeSwitchAttempts}.");
        }
    }

    private void PrintLadderMap(CommandInfo command)
    {
        if (_ladderMap == null ||
            string.IsNullOrWhiteSpace(
                _ladderMap.CurrentMap))
        {
            command.ReplyToCommand(
                "[GunGameBotAI] No ladder map is currently loaded.");
            return;
        }

        command.ReplyToCommand(
            $"[GunGameBotAI] ladderMap={_ladderMap.CurrentMap}; " +
            $"physicalLadders={_ladderMap.LadderCount}; " +
            $"candidates={_ladderMap.CandidateCount}; path={_ladderMap.CurrentPath}");

        foreach (PhysicalLadder ladder
                 in _ladderMap.Ladders)
        {
            System.Numerics.Vector3 anchor =
                ladder.Anchor.ToVector3();

            System.Numerics.Vector3 mount =
                ladder.BottomMount.ToVector3();

            System.Numerics.Vector3 approach =
                ladder.ApproachDirection.ToVector3();

            command.ReplyToCommand(
                $"[GunGameBotAI] ladder id={ladder.Id}; " +
                $"anchor=({anchor.X:0.###},{anchor.Y:0.###}); " +
                $"z={ladder.BottomZ:0.###}..{ladder.TopZ:0.###}; " +
                $"bottomMount=({mount.X:0.###},{mount.Y:0.###},{mount.Z:0.###}); " +
                $"approach=({approach.X:0.###},{approach.Y:0.###}); " +
                $"bottomKnown={ladder.HasBottomApproach}; observations={ladder.Observations}; " +
                $"successes={ladder.SuccessfulTraversals}; problems={ladder.ProblemCount}; " +
                $"assisted={ladder.AssistedSuccesses}/{ladder.AssistedTraversals}.");
        }
    }

    private void DebugLog(string message)
    {
        if (Config.Debug)
            Logger.LogInformation("[GunGameBotAI][DEBUG] {Message}", message);
    }

    private void ReleaseButtonPulse(int slot)
    {
        try
        {
            if (BotValidation.TryResolveLiveBot(slot, out _, out CCSPlayerPawn? pawn, out _) && pawn != null)
                _buttonPulses.Release(slot, pawn);
            else
                _buttonPulses.Cancel(slot);
        }
        catch (Exception exception)
        {
            _buttonPulses.Cancel(slot);
            LogRateLimited($"pulse-release-{slot}", exception, $"Failed to release a button pulse for bot slot {slot}.");
        }
    }

    private void ReleaseAllKnownButtonPulses()
    {
        // Snapshot the keys because Release/cleanup may indirectly mutate runtime collections.
        int[] slots = _registry.States.Keys.ToArray();
        foreach (int slot in slots)
            ReleaseButtonPulse(slot);

        // Covers any pulse that may exist without a registry entry.
        _buttonPulses.CancelAll();
    }

    private void SetMode(BotRuntimeState state, BotBehaviorMode mode, string reason)
    {
        if (state.Mode == mode)
            return;

        BotBehaviorMode oldMode = state.Mode;
        state.Mode = mode;
        _corrections.State(state.Slot, nameof(GunGameBotAI), nameof(state.Mode), oldMode, mode, reason);
    }

    private void LogRateLimited(string key, Exception exception, string message)
    {
        float now;
        try
        {
            now = Server.CurrentTime;
        }
        catch
        {
            now = 0.0f;
        }

        if (_lastErrorAt.TryGetValue(key, out float last) && now - last < 5.0f)
            return;

        _lastErrorAt[key] = now;
        Logger.LogError(exception, "[GunGameBotAI] {Message}", message);
    }

    private static bool TryParseBinary(string value, out bool result)
    {
        result = value switch
        {
            "0" => false,
            "1" => true,
            _ => false
        };
        return value is "0" or "1";
    }
}

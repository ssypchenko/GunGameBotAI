# Техническое задание на развитие GunGameBotAI

**Усиление боевого AI ботов, расширение восприятия и поэтапный перенос native/binary AI patches**

Версия документа: 1.0
Дата: 15 сентября 2026

Назначение документа: определить детальный план развития GunGameBotAI от текущего состояния до расширенного AI, включающего managed-улучшения боевого поведения и, при необходимости, отдельный native/MetaMod слой для изменения внутренних решений Valve bot AI.

Документ основан на:
- текущем репозитории GunGameBotAI;
- перечне требуемых функций, сформированном в ходе обсуждения;
- публичном проекте Austinbots/CS2-BotAI;
- текущей документации CounterStrikeSharp;
- публичных Source 2/CS2 schema/gamedata и примерах native hook/patch архитектуры.

Ключевой принцип: сначала реализовать безопасные и обновляемые функции средствами CounterStrikeSharp, а binary/native patches использовать только для тех решений Valve AI, которые невозможно надёжно изменить снаружи.

## 1. Цели проекта

1. Сделать ботов существенно более активными, агрессивными и трудными для человека без omniscient/teleport/cheat поведения.
2. Сохранить GunGame-специфику: weapon progression, Knife Rush, grenade level, persistent ladder learning, stuck recovery.
3. Разделить изменения на:
   - managed layer (CounterStrikeSharp);
   - native companion (MetaMod/C++ либо иной низкоуровневый модуль) только для внутренних Valve AI gates.
4. Все функции должны быть отключаемыми, измеримыми и безопасно выгружаться.
5. Обновление CS2 не должно приводить к применению патча к неизвестным байтам: несовпадение signature/expectedOriginal => feature disabled, сервер продолжает работу.

## 2. Текущее состояние и gap analysis

| Функция | Статус | Что есть сейчас | Целевая реализация |
|---|---|---|---|

| Idle_IsSafeAlwaysFalse | Частично | SafeTime=0, IsSleeping=false, IdleRecovery/repath уже есть, но внутреннее решение IsSafe не переопределяется. | Native patch для полной эквивалентности + managed mitigation. |

| AttackState_DodgeChance100_Always / high-skill dodge | Нет | Нет собственного dodge state machine. | CombatMovement v2; затем optional native patch для parity с Valve decision path. |

| AttackState_RetreatOnSniper_Disable | Частично | IsStopping=false мешает остановке, но Valve всё ещё может выбрать retreat. | Managed suppression + native patch для полного запрета. |

| Vision_SkipIsMovingGate | Нет | BotSensor только читает Enemy/IsEnemyVisible. | Native patch; managed layer только использует результат. |

| OnAudibleEvent_GlobalHearRange | Нет | Нет hearing service. | Native patch/детур OnAudibleEvent; optional managed reaction layer. |

| BotAimImprover: aim по реально видимой части тела | Нет | AimEnhancementEnabled присутствует в config, но сервиса AimService нет. | AimService + trace/hitbox visibility. |

| Head/body priority по оружию | Нет | Нет aim policy по WeaponClass. | AimProfile per weapon class. |

| Reload interruption при появлении врага | Нет | InReload не используется. | ReloadCombatService. |

| 42 binary patch definitions | Нет | В текущем GunGameBotAI нет универсального patch manager. | Отдельный native companion + gamedata/patch definitions. |

| Снятие fire-rate/steady-fire/zoom-fire ограничений | Очень частично | IsRapidFiring=true не эквивалентен внутренним fire gates. | Native patch group FIRING. |

| Удерживать trigger / spray на любых дистанциях | Нет/частично | Valve firing остаётся авторитетным. | Managed trigger assist там, где безопасно; native patch для внутренних gates. |

| Sniper spread gate removal | Нет | Нет. | Native patch group FIRING/SNIPER. |

| Combat strafe | Частично | IsRunning=true, IsStopping=false. | Полный CombatMovement v2. |

| Dodge during reload / crouch-dodge | Нет | Нет. | CombatMovement v2 + ReloadCombatService. |

| Vision: approach points / hiding spot / skill gates | Нет | IdleRecovery сбрасывает CheckedHidingSpotCount только как recovery. | Native VISION patches + managed diagnostics. |

| InViewCone / расширенный FOV awareness | Нет | Нет. | Native patch или hook функции видимости. |

| IsNoticable() always true | Нет | Нет. | Только native; platform-specific. |

| InvestigateNoise без SELF_DEFENSE gate | Нет | Нет. | Native HEARING patch. |

| Investigate noises активнее | Нет | Нет. | Managed reaction + native gate removal. |

| Jump/crouch movement в воздухе | Нет как combat feature | Jump используется для лестниц; KnifeRush имеет своё движение. | CombatMovement v2 air movement. |

| Counter-strafe во время выстрела | Частично | После weapon_fire напрямую демпфируется AbsVelocity. | Переписать на input-based counter-strafe. |

| Разные crouch probabilities по оружию | Нет | Нет. | WeaponMovementProfile. |

| Sniper peek/отскок после выстрела | Частично | Есть одноразовый CmdLeftMove после sniper shot. | SniperPeek FSM. |

| HasVisitedEnemySpawn=true | Да | AggressionService уже устанавливает true. | Сохранить; отдельный binary patch не обязателен. |

| Убрать artificial aim drift | Нет | Aim fields не управляются. | AimService + при необходимости native aim gate patch. |


## 3. Целевая архитектура

### 3.1 Managed layer: GunGameBotAI
Сервисы:
- AggressionService — только управляемые поля состояния/таймеры.
- CombatMovementService v2 — dodge/strafe/crouch/jump/counter-strafe/sniper movement.
- ReloadCombatService — обнаружение reload и решение о прерывании.
- AimService — выбор реально видимой точки тела и weapon-specific target priority.
- VisionAwarenessService — managed-настройки и диагностика perception state.
- HearingAwarenessService — реакция на hearing/noise state, если доступно без native.
- BotSensorService v2 — единый snapshot enemy/weapon/reload/visibility.
- KnifeRushService — оставить отдельным special movement owner.
- LadderMapService/LadderAssistService/StuckRecoveryService — оставить отдельными и приоритетными над normal combat movement.

### 3.2 Native layer: BotAIPatches companion
Отдельный модуль, который:
- не является обязательной зависимостью GunGameBotAI;
- хранит patch definitions вне кода (gamedata/JSON);
- валидирует module/signature/expected original bytes;
- применяет patches idempotently;
- восстанавливает original bytes при disable/unload;
- предоставляет status и build diagnostics;
- может включать/выключать patch groups: IDLE, DODGE, VISION, HEARING, FIRING, AIM;
- при несовпадении патча отключает только конкретную функцию, а не весь сервер/plugin.

### 3.3 Arbitration / ownership
Приоритет movement owners:
1. disabled / invalid / dead;
2. StuckRecovery;
3. Ladder entry/assist;
4. mandatory KnifeLevel;
5. Opportunistic KnifeRush;
6. GrenadeLevel special behaviour;
7. ReloadCombat emergency;
8. SniperPeek/CombatMovement;
9. Normal GunGame.

Один tick не должен одновременно получать противоречащие movement writes от двух сервисов.


## 4. Этапы реализации


### Этап 0. Baseline, инвентаризация и фиксация источников

**Цель:** получить воспроизводимую исходную точку до изменения поведения.

Работы:
- зафиксировать commit SHA GunGameBotAI;
- зафиксировать CS2 server build, ОС, MetaMod, CounterStrikeSharp и GunGame версии;
- создать `docs/ai-feature-matrix.md` с каждым требованием и статусом: managed / native / hybrid;
- собрать оригинальные 42 patch definitions, если они сохранились в старом файле/ветке/архиве;
- если 42 definitions недоступны, не реконструировать их по названию: составить список функций для reverse engineering;
- сохранить базовые gameplay логи на 2-3 картах.

Критерии приёмки:
- репозиторий собирается без изменений;
- есть baseline log;
- есть таблица функций и источников;
- для каждого binary patch указано: `KNOWN_DEFINITION` или `REQUIRES_RESEARCH`.


### Этап 1. Feature flags, diagnostics и безопасный lifecycle

**Цель:** подготовить каркас для поэтапного включения функций.

Новые config группы:
- `CombatMovementV2Enabled`
- `ReloadCombatEnabled`
- `AimEnhancementEnabled`
- `VisionEnhancementEnabled`
- `HearingEnhancementEnabled`
- `NativePatchesEnabled`
- отдельные флаги по patch groups.

Добавить:
- `css_ggbotai_features` — состояние всех feature groups;
- счётчики activations/corrections/failures;
- rate-limited diagnostics;
- на disable: полный release button/movement ownership;
- никаких writes human players;
- при hot reload/reset/map change transient state очищается.

Критерии:
- при `css_ggbotai_enable 0` не выполняются movement/button/weapon/AI writes;
- 50 циклов enable/disable без накопления timers/hooks;
- map change/hot reload не оставляет pressed buttons.


### Этап 2. CombatMovementService v2

**Цель:** закрыть большую часть combat movement требований без binary patching.

Реализовать state machine:
- `AcquireCombatMovement`
- `DodgeLeft`
- `DodgeRight`
- `CrouchDodge`
- `AirMove`
- `CounterStrafe`
- `SniperPeekOut`
- `SniperPostShotReturn`

Weapon movement profiles:
- Rifle
- SMG
- Pistol
- Shotgun
- MachineGun
- Sniper

Функции:
- dodge probability по умолчанию 100% при видимом enemy, если нет special movement owner;
- случайный/детерминированно переменный интервал смены strafe direction;
- dodge разрешён во время reload;
- crouch probability по WeaponClass;
- ограниченный jump/air strafe с cooldown;
- не прекращать движение только из-за `IsEnemySniperVisible`;
- убрать прямое нормальное редактирование `AbsVelocity` для counter-strafe;
- counter-strafe делать кратким противоположным movement input относительно horizontal velocity;
- sniper movement оформить как FSM: peek -> shot -> return/side-step.

Новые BotRuntimeState поля:
- `CombatDodgeDirection`
- `CombatDodgeUntil`
- `NextDodgeSwitchAt`
- `CombatCrouchUntil`
- `NextCombatJumpAt`
- `CounterStrafeUntil`
- `SniperPeekState`
- `SniperPeekOrigin`
- `SniperPeekDirection`

Критерии:
- CombatMovement не конфликтует с KnifeRush/Ladder/Stuck;
- после выстрела нет телепорта/резкого искусственного изменения velocity;
- бот при видимом враге не стоит без причины дольше заданного threshold;
- sniper FSM можно полностью выключить config flag.


### Этап 3. ReloadCombatService

**Цель:** прерывать неудачный reload при угрозе и разрешать движение во время reload.

Источник состояния:
- active weapon -> `CCSWeaponBase.InReload`;
- Clip1/ReserveAmmo;
- enemy visibility/distance;
- current movement owner.

Логика:
- если enemy стал видимым во время reload и оружие может быть возвращено в firing state — попытаться прервать reload;
- сначала использовать безопасный managed метод/weapon reselect;
- не спамить native SelectItem каждый tick;
- если cancel невозможен, минимум сохранить dodge/strafe;
- не прерывать reload бессмысленно при Clip1 == 0, если нет альтернативного оружия/тактики;
- на GunGame special levels не нарушать mandatory weapon.

Критерии:
- корректное определение reload;
- нет infinite weapon-switch loop;
- KnifeLevel/GrenadeLevel не ломаются;
- в debug log видна причина `reload-keep` / `reload-interrupt`.


### Этап 4. AimService: visible body + weapon priority

**Цель:** бот целится не в скрытую геометрией точку, а в реально видимую часть противника.

Подход:
1. получить enemy pawn и candidate target points;
2. candidate set минимум: head, upper chest, chest, pelvis;
3. выполнить trace line/ray до каждой точки;
4. исключить закрытые точки;
5. применить weapon-specific priority;
6. сформировать target point;
7. корректировать aim только bounded способом; не делать instant snap без отдельного config.

Приоритеты по умолчанию:
- Rifle/Pistol: head -> upper chest -> chest;
- SMG: upper chest -> chest -> head;
- Shotgun: chest -> pelvis -> head;
- MachineGun: chest -> upper chest;
- Sniper: chest/head в зависимости от видимости и weapon subtype;
- Knife: не использовать AimService, KnifeRush владеет chase.

Использовать `VisibleEnemyParts` как дополнительный сигнал, но не считать его единственным источником LOS.

Artificial aim drift:
- сначала исследовать доступные `CCSBot` поля (`AimError`, `AimFocus`, `AimFocusInterval`, `AimFocusNextUpdate`, `AimGoal`);
- не писать в read-only schema fields;
- любые прямые writes включать отдельным config и проверять на сервере;
- если drift создаётся внутренней функцией и возвращается каждый frame, переносить в native AIM patch group.

Критерии:
- при частично скрытом противнике выбранная target point проходит trace;
- выбор target point логируется в debug;
- нет aim через непрозрачные стены;
- отключение AimService возвращает vanilla aim.


### Этап 5. Vision / Awareness managed layer

**Цель:** максимально использовать доступные managed поля до native patching.

Исследовать и логировать:
- IsEnemyVisible
- IsEnemySniperVisible
- VisibleEnemyParts
- FirstSawEnemyTimestamp
- CurrentEnemyAcquireTimestamp
- AttentionInterval
- ApproachPointCount/ViewPosition
- WasSafe/SafeTime
- CheckedHidingSpotCount
- LastEnemyPosition/TargetSpot/TargetSpotPredicted

Managed улучшения:
- поддерживать active alert state;
- не разрешать stale ignore/panic/surprise state;
- ускорять повторную проверку после краткой потери LOS;
- аккуратно обновлять доступные timers, если это доказанно безопасно;
- не подменять врага omniscient способом.

Не пытаться managed-кодом имитировать `Vision_SkipIsMovingGate`, `InViewCone`, `IsNoticable` если engine уже отказался замечать цель: это задача native layer.

Критерии:
- нет wallhack-like enemy acquisition;
- managed visibility остаётся основанной на engine/trace;
- каждый write имеет config flag и debug reason.


### Этап 6. Hearing / Noise layer

**Цель:** улучшить реакцию на реальные звуковые события.

Сначала исследовать:
- какие hearing/noise поля/таймеры доступны через schema;
- можно ли получить события выстрела, шага, reload, grenade bounce, doors, flashbang через существующие game events/hooks без вмешательства в engine AI;
- может ли managed layer использовать эти события для look/repath без знания точного скрытого enemy.

Managed режим:
- на слышимый noise делать bounded look/repath/investigation;
- не присваивать enemy через стены только по звуку;
- хранить `LastHeardPosition`, `LastHeardAt`, `NoiseConfidence`.

Native режим:
- patch `InvestigateNoise` SELF_DEFENSE gate;
- `OnAudibleEvent_GlobalHearRange`;
- сохранить типы событий, которые фактически проходят через engine function.

Критерии:
- звук вызывает investigation, но не даёт точного wallhack tracking;
- hearing можно отключить независимо от vision.


### Этап 7. Native BotAIPatches companion: инфраструктура

**Цель:** создать безопасный production framework до переноса отдельных патчей.

Рекомендуемый формат: отдельный MetaMod/C++ plugin. GunGameBotAI должен работать и без него.

PatchDefinition:
- Name
- Group
- Module
- Platform
- Signature
- AddressOffset
- ExpectedOriginal pattern
- PatchBytes
- Optional RestoreBytes/auto-captured original
- Build note / last verified CS2 build

Алгоритм apply:
1. найти signature;
2. проверить unique match;
3. вычислить target address;
4. прочитать original bytes;
5. сравнить с ExpectedOriginal + wildcards;
6. изменить memory protection;
7. записать bytes;
8. verify read-back;
9. сохранить original bytes;
10. записать status.

Unload/disable:
- восстановить original bytes в обратном порядке;
- verify restore;
- не восстанавливать, если текущие bytes уже не совпадают с нашими patch bytes (защита от чужого изменения).

Команды:
- `ggbotpatch_status`
- `ggbotpatch_enable <group> 0|1`
- `ggbotpatch_verify`
- `ggbotpatch_dump <name>`

Fail-closed:
- signature missing => patch OFF;
- >1 matches => patch OFF;
- original mismatch => patch OFF;
- ни при каких условиях не писать байты «по приблизительному адресу».

Критерии:
- загрузка/выгрузка повторяема;
- все применённые patches отображают address/original/patched/status;
- обновление CS2 с изменившимися bytes приводит к отказу патча, а не к crash.


### Этап 8. Поэтапный перенос native patch groups

**Цель:** переносить внутренние Valve AI изменения небольшими наборами.

Порядок:

**8A IDLE**
- `Idle_IsSafeAlwaysFalse`;
- связанные safe/wait gates, если найдены в исходных 42 definitions.

**8B DODGE/SNIPER**
- `AttackState_DodgeChance100_Always`;
- dodge during reload;
- crouch-dodge gates;
- sniper-specific dodge restrictions;
- `AttackState_RetreatOnSniper_Disable`;
- stop-moving-on-sniper gates.

**8C VISION**
- `Vision_SkipIsMovingGate`;
- approach-point frequency gates;
- skill/movement/hiding-spot restrictions;
- `InViewCone`;
- `IsNoticable` platform-specific behaviour;
- FOV awareness.

**8D HEARING**
- `InvestigateNoise` SELF_DEFENSE bypass;
- `OnAudibleEvent_GlobalHearRange`.

**8E FIRING**
- fire-rate/steady-fire/zoom-fire gates;
- trigger hold/spray distance gates;
- sniper spread gate.

**8F AIM**
- engine-side aim drift/gates, если managed AimService не может удержать желаемую точку.

После каждой подгруппы:
- отдельный commit;
- отдельный server runtime test;
- soak test;
- сравнение с managed-only режимом.

Важно: текущий публичный Austinbots/CS2-BotAI main НЕ содержит 42 definitions. Он может быть reference для patch framework и отдельных известных патчей, но не является полным источником 42 функций.


### Этап 9. Интеграция managed + native

**Цель:** исключить конфликт двух слоёв.

GunGameBotAI при старте:
- определяет наличие native companion;
- читает capability/status;
- включает только совместимые managed функции;
- если native patch выполняет полное решение, managed layer не должен каждую decision iteration бороться с ним.

Пример:
- native `RetreatOnSniper_Disable` активен -> managed CombatMovement всё равно может strafe, но не обязан постоянно сбрасывать engine retreat state;
- native VISION активен -> BotSensor/AimService используют расширенную engine visibility, но не подменяют её;
- native FIRING активен -> managed layer не должен искусственно спамить Attack без необходимости.

Деградация:
- companion отсутствует -> managed-only режим;
- одна patch group failed -> остальные работают;
- runtime disable -> оба слоя прекращают writes.


### Этап 10. Тестирование, метрики и выпуск

Тестовые режимы:
A. Vanilla bots.
B. GunGameBotAI managed-only.
C. GunGameBotAI + native patches.

Обязательные сценарии:
- rifle close/mid/long range;
- SMG close combat;
- shotgun;
- pistols;
- AWP/SSG;
- reload under pressure;
- enemy sniper visible;
- partial cover/head-only visibility;
- footsteps/noise behind corner;
- KnifeLevel;
- GrenadeLevel;
- Opportunistic KnifeRush;
- ladder/stuck scenario;
- bot death/respawn;
- bot team switch;
- plugin enable/disable;
- hot reload;
- map change.

Метрики:
- time-to-first-shot after enemy becomes visible;
- fraction of visible-enemy combat time moving/dodging;
- average stationary interval in combat;
- reload interruptions successful/failed;
- selected aim body part distribution by weapon;
- time from audible event to investigation;
- patch application success/failure;
- server frame time before/after;
- exceptions/native errors;
- crashes = 0.

Soak:
- минимум 60 минут continuous bot play;
- многократные map changes;
- повторные load/unload native companion;
- после unload original bytes verified.

Release gate:
- managed build succeeds;
- native build succeeds на целевой ОС;
- all required patches либо VERIFIED/APPLIED, либо явно DISABLED;
- no unknown byte writes;
- no human-player state writes;
- документация config/status обновлена.


## 5. Источники и где брать информацию

| Источник | URL | Использование |
|---|---|---|

| Текущий GunGameBotAI | https://github.com/ssypchenko/GunGameBotAI | Главный источник текущей архитектуры, config и фактически реализованного поведения. |

| GunGameBotAI/AggressionService.cs | https://github.com/ssypchenko/GunGameBotAI/blob/main/Services/AggressionService.cs | Текущие managed-коррекции: AllowActive, IsRapidFiring, SafeTime, HasVisitedEnemySpawn, timers. |

| GunGameBotAI/CombatMovementService.cs | https://github.com/ssypchenko/GunGameBotAI/blob/main/Services/CombatMovementService.cs | Текущий combat movement, velocity damping counter-strafe и sniper side input. |

| GunGameBotAI/IdleRecoveryService.cs | https://github.com/ssypchenko/GunGameBotAI/blob/main/Services/IdleRecoveryService.cs | Текущий idle/repath fallback. |

| GunGameBotAI/BotSensorService.cs | https://github.com/ssypchenko/GunGameBotAI/blob/main/Services/BotSensorService.cs | Текущая модель enemy observation: bot.Enemy, IsEnemyVisible. |

| Austinbots/CS2-BotAI | https://github.com/Austinbots/CS2-BotAI | Публичный reference-проект с memory patch подходом. |

| Austinbots/CS2-BotAI BotAI.cs | https://github.com/Austinbots/CS2-BotAI/blob/main/BotAI.cs | PatchDefinition, FindSignature, expectedOriginal validation, restore-on-unload. Текущий public main содержит только 4 patch definitions, а не 42. |

| Austinbots/CS2-BotAI MemoryPatch.cs | https://github.com/Austinbots/CS2-BotAI/blob/main/MemoryPatch.cs | Пример mprotect/VirtualProtect для записи патчей в Linux/Windows. |

| CounterStrikeSharp CCSBot API | https://docs.cssharp.dev/api/CounterStrikeSharp.API.Core.CCSBot.html | Authoritative managed surface для CCSBot: aim, visibility, timers, IsEnemySniperVisible, VisibleEnemyParts, WasSafe и др. |

| CounterStrikeSharp CBot API | https://docs.cssharp.dev/api/CounterStrikeSharp.API.Core.CBot.html | Movement-level поля: IsRunning, IsCrouching, speeds, buttons, JumpTimestamp. |

| CounterStrikeSharp CCSWeaponBase API | https://docs.cssharp.dev/api/CounterStrikeSharp.API.Core.CCSWeaponBase.html | Weapon state, включая InReload и другие поля. |

| CounterStrikeSharp CBasePlayerWeapon API | https://docs.cssharp.dev/api/CounterStrikeSharp.API.Core.CBasePlayerWeapon.html | Clip, reserve ammo, next attack ticks. |

| CounterStrikeSharp generated NativeAPI | https://github.com/roflmuffin/CounterStrikeSharp/blob/main/managed/CounterStrikeSharp.API/Generated/Natives/API.cs | TraceRay/CreateRay и другие низкоуровневые native bindings; проверять текущую версию API перед использованием. |

| CounterStrikeSharp Shared Plugin API | https://github.com/roflmuffin/CounterStrikeSharp/blob/main/docfx/docs/features/shared-plugin-api.md | Если native companion будет отдавать capability/API в managed plugin. |

| SteamTracking/GameTracking-CS2 server schemas | https://github.com/SteamTracking/GameTracking-CS2/tree/master/DumpSource2/schemas/server | Актуальные schema fields текущих классов server.dll; источник для проверки полей/offsets, но не сигнатур функций. |

| Source2ZE/CS2Fixes | https://github.com/Source2ZE/CS2Fixes | Reference для production-grade MetaMod hooks, gamedata, version churn и безопасного unload. |

| CS2Fixes detours.cpp | https://github.com/Source2ZE/CS2Fixes/blob/main/src/detours.cpp | Пример организации detours/hook lifecycle. |

| CS2Fixes gamedata | https://github.com/Source2ZE/CS2Fixes/tree/main/gamedata | Пример вынесения сигнатур/offsets из кода. |

| plugify-plugin-s2sdk gamedata | https://github.com/untrustedmodders/plugify-plugin-s2sdk/blob/main/assets/gamedata.jsonc | Дополнительный reference по текущим server signatures/vtable offsets, включая WeaponServices. |

### Иерархия доверия источникам
1. **Текущий `server.dll`/`server.so` конкретного CS2 build** — единственный авторитет для фактического машинного кода, сигнатур и branch logic.
2. **Оригинальные 42 patch definitions пользователя/старого проекта**, если будут найдены — основной источник намерения и известных точек patching, но каждую сигнатуру всё равно надо перепроверять на текущем build.
3. **CounterStrikeSharp API + текущие schema dumps** — авторитет для managed полей и доступных native bindings.
4. **Austinbots/CS2-BotAI** — reference реализации memory patch safety; не считать его текущий main полным каталогом требуемых функций.
5. **CS2Fixes / plugify-plugin-s2sdk** — reference по gamedata, signatures, detours, lifecycle и update churn.
6. Старые Source/CS:GO SDK implementations можно использовать только для понимания семантики функций, но не для offsets/signatures CS2.

### Reverse engineering workflow для отсутствующего patch
- найти функцию по строкам/вызовам/RTTI/известному control flow;
- сравнить Linux и Windows реализации;
- определить минимальный patch, меняющий только нужную ветку;
- сформировать signature с wildcard для relocations;
- зафиксировать expected original bytes;
- провести A/B тест;
- документировать build, платформу, disassembly snippet и rationale.


## 6. Предлагаемая структура файлов после доработки

```text
GunGameBotAI/
  Config/
    GunGameBotAIConfig.cs
  Models/
    BotRuntimeState.cs
    CombatMovementState.cs
    AimTargetPoint.cs
    HeardNoise.cs
  Services/
    AggressionService.cs
    BotSensorService.cs
    CombatMovementService.cs
    ReloadCombatService.cs
    AimService.cs
    VisionAwarenessService.cs
    HearingAwarenessService.cs
    ...
  Native/
    WeaponSwitchNative.cs
    NativePatchCapabilityClient.cs   # только bridge/status, без 42 patches в C#
  docs/
    ai-feature-matrix.md
    combat-movement-v2.md
    aim-service.md
    native-patches.md
    validation-checklist.md

BotAIPatches/                       # отдельный MetaMod/C++ проект
  src/
    PatchManager.cpp
    PatchRegistry.cpp
    PatchGroups.cpp
    CapabilityBridge.cpp
  gamedata/
    bot_ai_patches.json
  docs/
    reverse-engineering-notes/
```


## 7. Правила безопасности и сопровождения

- Не выполнять native function call или byte patch по непроверенному ABI/signature.
- `try/catch` не считается защитой от process-level native crash.
- Patch definitions не хардкодить в managed business logic.
- Каждая native функция имеет `VerifiedBuild`/дату проверки.
- Любой mismatch оригинальных байтов = fail closed.
- Hot reload native companion допускается только если все hooks/patches гарантированно restored.
- Не добавлять обязательную зависимость на сторонний RayTrace plugin; trace backend должен быть встроенным или optional с fallback.
- Не изменять human player state.
- Не использовать omniscient enemy location как замену perception.
- Любая aggressive функция должна иметь config flag и диагностический status.
- Special GunGame modes имеют приоритет над normal combat AI.


## 8. Очерёдность ближайших работ

Рекомендуемый ближайший порядок:
1. Этап 0 + 1.
2. CombatMovement v2.
3. ReloadCombatService.
4. AimService.
5. Managed Vision/Hearing diagnostics.
6. Native patch infrastructure.
7. IDLE + DODGE/SNIPER patches.
8. VISION.
9. HEARING.
10. FIRING/AIM native gates.
11. Полная интеграция и soak testing.

Причина такого порядка: первые четыре этапа дают заметное улучшение боевого поведения без увеличения риска process-level crash; native patching добавляется только после появления полноценной диагностики и regression framework.

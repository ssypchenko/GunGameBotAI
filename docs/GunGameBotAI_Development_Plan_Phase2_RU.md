# GunGameBotAI — поэтапный план дальнейшей разработки

**Статус:** дополнение к первоначальному техническому заданию
**Целевой режим:** Counter-Strike 2 GunGame / Arms Race
**Базовый принцип:** Valve Bot AI остаётся ответственным за navigation, базовый enemy acquisition и основную combat logic. `GunGameBotAI` должен расширять и корректировать это поведение, а не заменять Valve AI собственной полноценной системой.

---

## 1. Основные принципы дальнейшей разработки

Дальнейшая разработка должна выполняться небольшими независимыми этапами.

Каждый этап должен:

1. Иметь отдельный feature flag, если он реально меняет поведение бота.
2. Иметь возможность быть полностью отключённым без выгрузки DLL.
3. Не зависеть от функций следующих этапов.
4. Не изменять Knife Rush и Ladder Management без прямой необходимости.
5. Не вмешиваться в bot movement постоянно, если достаточно кратковременной коррекции.
6. По возможности использовать существующее состояние и поведение Valve Bot AI.
7. Использовать native hooks/patches только тогда, когда CounterStrikeSharp/schema-level решения недостаточно.
8. Для native-кода всегда использовать fail-closed подход:

   * signature validation;
   * expected original bytes / offset validation;
   * pointer validation;
   * Windows/Linux separation;
   * автоматическое отключение функции при несовместимости;
   * отсутствие влияния на остальные функции плагина.
9. Не добавлять функциональность «на будущее», если нет конкретной проблемы в GunGame.
10. Любая новая AI-функция сначала должна иметь ограниченный диагностический режим.

Особенно важно избегать ситуации, когда Valve AI и `GunGameBotAI` одновременно постоянно пытаются управлять одним и тем же параметром.

---

# ЭТАП 0 — Зафиксировать стабильную базовую версию

## Цель

Перед дальнейшей разработкой зафиксировать текущее стабильное поведение как baseline.

На данный момент считаются рабочими и не должны переписываться без отдельной причины:

* Decision Loop;
* Fast Actuator Loop;
* soft enable/disable;
* aggression corrections;
* Idle Recovery;
* weapon classification;
* GunGame level detection;
* Knife Level;
* opportunistic Knife Rush;
* one-roll-per-encounter;
* knife switching;
* knife attack;
* Knife Rush zig-zag;
* ladder management;
* existing grenade-level basic behaviour;
* ButtonPulseService;
* existing diagnostics.

## Что сделать

Создать короткий baseline-документ:

```text
Stable Behaviour Baseline
-------------------------
Knife Rush: accepted
Ladder management: accepted
Weapon switching: accepted
Idle recovery: accepted
Aggression: accepted
Current crashes attributable to GunGameBotAI: none / known list
```

Также желательно сохранить:

```text
plugin commit
CounterStrikeSharp version
CS2 build
server OS
GunGame API version
```

## Что НЕ делать

На этом этапе не рефакторить существующий Knife Rush или Ladder Management только ради унификации архитектуры.

## Acceptance

Текущая версия работает в production без нового поведения.

---

# ЭТАП 1 — StuckMonitorService: обнаружение stuck без recovery

## Приоритет

Высокий, но риск очень низкий.

## Цель

Узнать, существует ли вообще реальная проблема застревания ботов вне уже решённой ladder-проблемы.

Активный `StuckRecoveryService` пока не реализовывать.

## Новая служба

```text
StuckMonitorService
```

Она должна только наблюдать.

## Источники определения stuck

Использовать два независимых сигнала.

### Valve signal

```text
bot.IsStuck
```

### Дополнительный heuristic

Например:

```text
bot хочет двигаться
AND
speed2D очень низкая
AND
позиция практически не изменилась N секунд
```

Не считать stuck:

```text
bot dead
freeze time
ladder traversal
spawn grace
бот специально остановился для attack
бот находится в специальном controlled state
```

## Состояние

Пример:

```text
StuckCandidateSince
StuckStartPosition
MaxSpeedDuringCandidate
StuckEventLogged
```

## Лог START

Пример:

```text
[GGBAI][StuckMonitor]
START
map=gg_example
bot=Bot_03
slot=7
pos=(125.4,-340.2,64.0)
mode=NormalGunGame
valveIsStuck=true
speed2D=2.8
moved2D=7.4
duration=2.1
moveType=WALK
```

## Лог END

Когда бот сам вышел:

```text
[GGBAI][StuckMonitor]
RECOVERED
map=gg_example
bot=Bot_03
duration=4.7
movedAfterDetection=185
```

## Важно

Никаких:

```text
Jump
Duck
velocity injection
repath
teleport
movement override
```

StuckMonitor только наблюдает.

## Production

Можно сразу оставить включённым в production.

Логировать только событие START и RECOVERED, не каждый Decision Loop.

## Критерий дальнейшего решения

Если за несколько недель нормальной игры нет значимых stuck events вне ladder — полноценный `StuckRecoveryService` не требуется.

Если повторяется конкретное место:

```text
map + coordinates
```

оно исследуется отдельно.

---

# ЭТАП 2 — Infrastructure для безопасного временного управления

## Приоритет

Высокий как техническая основа.

## Цель

Создать безопасный способ для будущих функций временно влиять на movement, не превращая плагин в постоянного конкурента Valve AI.

## Главное ограничение

На этом этапе НЕ переводить существующий Knife Rush и Ladder Management на новую систему.

Они уже работают.

Новая infrastructure предназначена прежде всего для:

```text
Sniper Peek
Grenade Assist
future short combat movement corrections
```

## Предлагаемая служба

```text
TransientControlService
```

или:

```text
MovementLeaseService
```

## Концепция lease

Модуль не получает постоянное управление.

Он говорит:

```text
SniperPeek wants LeftMove=-250
for maximum 3 ticks
priority=...
```

После истечения lease:

```text
GunGameBotAI прекращает писать значение
```

### Нельзя

```text
save Valve value
write our value
later restore old Valve value
```

Потому что сохранённое значение уже может быть устаревшим.

### Нужно

```text
while lease valid:
    module may apply its command

after lease:
    stop touching it
```

Valve снова получает полный контроль.

## Ownership

Минимально:

```text
Owner
ExpiresAtTick
ForwardMove?
LeftMove?
Duck?
```

## Arbitration

Если два будущих модуля захотят управление одновременно:

```text
Ladder traversal > special mandatory mode > short combat correction
```

Но существующие Ladder/KnifeRush пока можно оставить вне этого механизма.

## AbsVelocity

Не делать механизм восстановления `AbsVelocity`.

Если функция один раз дала impulse:

```text
AbsVelocity += ...
```

этот импульс считается физически совершившимся действием.

После этого просто не вмешиваться дальше.

## Cleanup

При:

```text
css_ggbotai_enable 0
round end
map end
player death
disconnect
plugin unload
```

все leases удаляются.

## Acceptance

1. При отсутствии активных leases поведение ботов абсолютно не меняется.
2. Lease автоматически заканчивается.
3. Никаких stale states после смерти/round/map.
4. Один модуль не может случайно оставить movement command навсегда.
5. Knife Rush и Ladder Management продолжают работать как раньше.

## Production

Этап можно выпустить отдельно, даже если он ещё никем активно не используется.

---

# ЭТАП 3 — VisibilityTraceService и Aim diagnostics

## Приоритет

Очень высокий.

## Цель

Подготовить фундамент для AimService без изменения aim самого бота.

## Важное архитектурное решение

Использовать встроенный CounterStrikeSharp Ray/Hull Trace API.

Для текущих версий CounterStrikeSharp отдельный старый RayTrace plugin не должен быть обязательной зависимостью.

## Новая служба

```text
VisibilityTraceService
```

## Основная функция

Пример:

```text
bool IsPointVisible(
    CCSPlayerPawn botPawn,
    Vector targetPoint,
    CCSPlayerPawn? targetPawn)
```

Использовать:

```text
Trace.TraceEndShape(...)
```

## Aim points

Подготовить набор точек относительно enemy pawn:

```text
HEAD
NECK
UPPER_CHEST
CHEST
GUT
PELVIS
LEFT_CHEST
RIGHT_CHEST
LEFT_SHOULDER
RIGHT_SHOULDER
LEFT_THIGH
RIGHT_THIGH
```

Начать с небольшого набора:

```text
HEAD
CHEST
GUT
PELVIS
```

И расширять только если есть смысл.

## Важное требование

Trace должен отвечать:

```text
видна ли эта конкретная точка
```

а не просто:

```text
Valve считает enemy visible
```

Это разные вещи.

Например:

```text
enemy.IsVisible=true

HEAD blocked
CHEST blocked
LEFT_SHOULDER visible
```

## Diagnostic mode

Пока ничего не менять.

Логировать только по отдельной debug-команде:

```text
Bot_01 enemy=Player_02
ValveVisible=true
HEAD=false
CHEST=true
GUT=true
chosen=CHEST
```

## Performance

Trace не выполнять для всех точек всех врагов каждый tick.

Только:

```text
текущий enemy
+
только когда Aim diagnostics/AimService нужен
+
Decision frequency / relevant native aim callback
```

## Acceptance

Проверить на тестовой карте:

1. полностью открытый enemy;
2. только голова видна;
3. только верхняя часть тела;
4. enemy за ящиком;
5. enemy полностью за стеной;
6. enemy появляется из-за угла.

Результат должен соответствовать реально видимой геометрии.

## Production

Можно оставить `VisibilityTraceService` установленным, но Aim modification выключенным.

---

# ЭТАП 4 — AimService v1: выбор реально видимой части тела

## Приоритет

Очень высокий.

## Цель

Улучшить aim, не заменяя Valve aim mechanics.

GunGameBotAI НЕ должен каждый tick выставлять:

```text
EyeAngles
```

Valve должен продолжать самостоятельно:

```text
поворачивать голову
сглаживать движение
вести цель
стрелять
```

Мы меняем только точку, в которую Valve решил целиться.

## Native integration

Исследовать и реализовать hook:

```text
CCSBot::PickNewAimSpot
```

Предпочтительно:

```text
PostHook
```

Valve сначала выполняет свою штатную функцию.

После этого GunGameBotAI имеет право скорректировать:

```text
m_targetSpot
```

## Aim pipeline

```text
Valve picks enemy
      |
      v
Valve PickNewAimSpot
      |
      v
GunGameBotAI PostHook
      |
      v
VisibilityTraceService
      |
      v
AimPolicyService
      |
      v
replace targetSpot only if useful
```

## AimPolicyService

Отделить policy от native hook.

Например:

```text
AimPolicyService
```

решает порядок проверки частей тела.

### Начальная policy

Rifle / pistol / SMG:

```text
HEAD
UPPER_CHEST
CHEST
GUT
```

AWP / SSG08 / shotgun:

```text
CHEST
GUT
PELVIS
HEAD
```

Это должно быть конфигурируемо позднее, но на первой версии достаточно разумных defaults.

## Основной принцип

Если Valve target point уже хорош и видим:

```text
ничего не менять
```

Если выбранная Valve точка закрыта:

```text
найти первую подходящую видимую точку
```

## Fail closed

Если:

```text
signature not found
offset invalid
enemy invalid
trace failed
target point invalid
```

результат:

```text
return HookResult.Continue
```

Valve AI продолжает работать без нашего AimService.

## Config

```text
AimEnhancementEnabled = false
```

при первом production release.

Дополнительно:

```text
AimMode = Mixed
AimDebug = false
```

## Rollout

### Test server

```text
AimEnhancementEnabled=true
```

### Production stage 1

Один сервер / ограниченное время.

### Production stage 2

Постоянно, если нет:

```text
crash
invalid signature
wall aiming
aim snapping anomalies
CPU increase
```

## Acceptance

1. Бот не целится через стены.
2. Бот выбирает видимую часть тела.
3. Голова бота не получает unnatural snapping.
4. Valve по-прежнему контролирует поворот.
5. При отключении AimService поведение сразу возвращается к обычному Valve.
6. При сломанной native signature plugin продолжает работать без AimService.

---

# ЭТАП 5 — Vision Diagnostics: почему бот не замечает врага рядом

## Приоритет

Очень высокий.

## Цель

Перед native vision patches собрать реальные данные о проблеме.

Не предполагать заранее, какой именно Valve gate виноват.

## Новая служба

```text
VisionMonitorService
```

## Определять ситуации

Enemy:

```text
alive
opposite team
distance < configurable threshold
```

но:

```text
bot.Enemy != enemy
или
bot.IsEnemyVisible == false
```

При этом VisibilityTraceService может дополнительно определить:

```text
физически была ли какая-нибудь часть enemy видима
```

## Очень полезный диагностический случай

```text
PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED
```

Пример:

```text
[GGBAI][Vision]
map=gg_x
bot=Bot_04
enemy=Bot_09
distance=420
visiblePoint=CHEST
valveEnemy=false
valveVisible=false
botYaw=...
enemyAngleFromView=72
```

## Что измерять

```text
distance
angle from bot view
relative position
whether Valve eventually acquires target
timeToAcquire
bot movement state
current behavior mode
```

## Production

Монитор может работать с rate-limit.

Подробный лог — debug only.

## Acceptance

После нескольких игровых сессий должно стать понятно:

```text
проблема FOV?
problem while moving?
approach-point scanning?
enemy noticeable gate?
Valve delay?
```

---

# ЭТАП 6 — Vision Improvements v1: улучшить look-around без снятия FOV

## Приоритет

Очень высокий.

## Цель

Сделать бота более внимательным во время движения, не превращая его в wallhack-бота.

## Основной принцип

Не давать боту информацию о противнике через стену.

Нужно заставить Valve:

```text
чаще смотреть по сторонам
чаще проверять потенциальные направления угрозы
не блокировать look-around только потому, что бот движется
```

## Сначала попробовать schema/state-level изменения

Исследовать влияние:

```text
InhibitLookAroundTimestamp
EyeAnglesUnderPathFinderControl
look-around related timers/state
```

Но менять только тогда, когда бот:

```text
не находится на ladder
не находится в Knife Rush
не выполняет special controlled movement
```

## Очень важно

Не задавать вручную:

```text
EyeAngles = random angles
```

каждые несколько ticks.

Это будет конфликтовать с Valve.

## Acceptance

На специальных тестах бот:

1. продолжает нормально перемещаться по nav;
2. чаще осматривает боковые направления;
3. быстрее обнаруживает реально видимого врага;
4. не знает о враге за стеной;
5. не теряет target во время нормального combat.

## Production

Отдельный feature flag:

```text
VisionEnhancementEnabled
```

---

# ЭТАП 7 — Selective Native Vision Patches

## Приоритет

Высокий, но только после результатов этапов 5–6.

## Цель

Если managed/state-level подход недостаточен, снять только конкретные ограничения Valve.

## Кандидаты для исследования

По поведению референсных BotAI-проектов особенно интересны функции типа:

```text
Vision_SkipIsMovingGate
Vision_AlwaysWatchApproachPoints
Vision_AlwaysEnterApproachBody
Vision_ApproachBody_SkipSkillCheck
```

Названия здесь описывают назначение; signatures должны быть независимо найдены и проверены для используемой версии CS2.

## Не переносить автоматически

На первом этапе НЕ делать:

```text
IsNoticable_AlwaysTrue
RemoveOuterFOV
RemoveInnerFOV
Global vision
```

Это может дать боту информацию, которую обычный игрок получить не должен.

## Реализация

Каждый patch должен быть самостоятельным:

```text
VisionPatchSkipMovingGateEnabled
VisionPatchApproachPointsEnabled
...
```

Не один общий:

```text
EnableAllNativePatches
```

## Patch lifecycle

```text
resolve signature
validate original bytes
save original bytes
patch
...
unload/disable
restore original bytes
```

Если validation failed:

```text
log warning
skip patch
```

Не crash.

## Rollout

Вводить **по одному patch**.

Например:

### Release A

только:

```text
SkipIsMovingGate
```

Тестировать.

### Release B

добавить:

```text
AlwaysWatchApproachPoints
```

Тестировать.

Так можно точно понять эффект каждого изменения.

## Acceptance

Основная метрика:

```text
fewer PHYSICALLY_VISIBLE_BUT_NOT_ACQUIRED events
lower average timeToAcquire
```

без появления:

```text
wall awareness
unnatural 180° instant acquisition
crashes
navigation degradation
```

---

# ЭТАП 8 — Sniper Peek v2

## Приоритет

Высокий.

## Цель

После выстрела из sniper rifle бот должен естественно возвращаться за укрытие или хотя бы менять направление движения вместо продолжения движения наружу.

## Weapon scope

Первая версия:

```text
AWP
SSG08
```

Позже при необходимости:

```text
SCAR20
G3SG1
```

## Главное изменение относительно текущей реализации

Не придумывать направление движения.

Запоминать фактическое движение самого Valve-бота.

## State

```text
LastLateralDirection
LastLateralSpeed
LastSniperShotTime
PeekReturnUntil
```

## Detection

До shot:

```text
project AbsVelocity on bot right-vector
```

Получить:

```text
moving LEFT
moving RIGHT
not moving laterally
```

## После EventWeaponFire

Если:

```text
sniper weapon
AND
grounded
AND
not ladder
AND
lateral direction known
```

создать короткий return lease:

```text
если двигался RIGHT -> short LEFT input
если двигался LEFT  -> short RIGHT input
```

## Version 1

Использовать `MovementLeaseService`.

Не изменять напрямую velocity, если input работает достаточно хорошо.

## Version 2 — только если требуется

Если тесты показывают, что Valve немедленно подавляет input, можно добавить ограниченный velocity impulse.

Но это отдельное решение после тестирования.

## FSM

```text
NORMAL
  |
  | lateral movement detected
  v
PEEK_DIRECTION_KNOWN
  |
  | sniper shot
  v
RETURN
  |
  | short timeout
  v
NORMAL
```

## Abort

Сразу прекратить Return если:

```text
ladder
airborne
dead
Knife Rush
new special movement owner
```

## Config

```text
SniperPeekEnabled
SniperPeekReturnTicks
SniperPeekInputStrength
```

## Acceptance

1. Бот действительно возвращается в противоположную сторону после shot.
2. Нет teleport-like движения.
3. Нет постоянного left/right fight с Valve.
4. Лестницы не затрагиваются.
5. После окончания lease Valve полностью управляет движением.

---

# ЭТАП 9 — Combat Crouch

## Приоритет

Средне-высокий.

## Цель

Добавить контролируемый crouch в перестрелке для изменения hitbox position и улучшения стрельбы некоторыми классами оружия.

## Не делать crouch каждый выстрел

Лучше принимать решение один раз на короткий combat episode.

## State

```text
CombatCrouchActive
CombatCrouchUntil
CombatCrouchRolled
EnemyEncounterId
```

## Weapon-class policy

Настраивать по уже существующему `WeaponClass`.

Пример начальных диапазонов:

```text
Pistol        low-medium
SMG           very low
Rifle         medium
Shotgun       very low
Sniper        low
MachineGun    high
```

Точные проценты определить тестированием.

Не копировать значения референсного плагина автоматически.

## Behaviour

При начале attack:

```text
one roll
```

Если принят:

```text
bot.IsCrouching = true
```

На:

```text
short configurable duration
```

После:

```text
stand up
```

## Не применять

```text
KnifeLevel
KnifeRush
GrenadeLevel
LadderTraversal
airborne
spawn grace
```

## Ограничения

Не позволять одному crouch episode длиться бесконечно.

Например:

```text
CombatCrouchMaxSeconds
```

## Acceptance

1. Crouch встречается периодически, а не постоянно.
2. Боты не превращаются в статичные цели.
3. После combat они встают.
4. Нет stuck crouch после death/round change.
5. Не ломается ladder logic.

---

# ЭТАП 10 — Grenade Level v2: Target Selection

## Приоритет

Высокий.

## Цель

Разделить проблему grenade level на несколько небольших задач.

На этом этапе бот ещё НЕ получает сложный автоматический бросок.

## Существующее поведение

Сохранить текущую grenade-specific movement logic.

## Добавить

```text
GrenadeTarget
GrenadeTargetLastSeenAt
GrenadeTargetPosition
GrenadeTargetVelocity
```

## Target

В первую очередь использовать:

```text
Valve current Enemy
```

Если enemy invalid:

```text
никакой искусственной информации через стены
```

## Distance policy

Определить:

```text
TooCloseDistance
PreferredThrowDistance
MaxUsefulDistance
```

Если enemy слишком близко:

```text
бот может немного увеличивать дистанцию
```

но через короткий movement lease.

## На этом этапе

НЕ нажимать Attack автоматически.

## Debug

```text
[Grenade]
target=Bot_02
dist=350
visible=true
state=TOO_CLOSE
```

## Acceptance

Target selection стабильный и не переключается бессмысленно между несколькими врагами.

---

# ЭТАП 11 — Grenade Level v3: Aim + Throw Assist

## Приоритет

Высокий.

## Цель

Помочь боту реально использовать HE grenade против текущего противника.

## Не использовать

```text
map-specific grenade lineups
pre-recorded trajectories
direct projectile spawning
teleport grenade
```

Это не нужно GunGame.

## Target prediction

Начальный вариант:

```text
predicted =
enemyPosition
+
enemyVelocity * shortPredictionTime
```

Например prediction порядка нескольких десятых секунды.

Точное значение определить тестами.

## Visibility

Использовать `VisibilityTraceService`.

Если прямая линия полностью заблокирована:

```text
не заставлять бота бросать прямо в стену
```

## Aim

Предпочтительно корректировать grenade target/aim через Valve-compatible mechanism.

Не делать постоянное прямое управление EyeAngles, если можно этого избежать.

## Throw

Использовать существующий:

```text
ButtonPulseService
```

Краткий:

```text
Attack
```

только когда:

```text
target valid
angle acceptable
grenade active
bot not ladder
bot not airborne
throw cooldown expired
```

## Первая версия

Не реализовывать идеальную баллистику.

Цель:

```text
разумный прямой или слегка упреждающий бросок
```

## Последующий optional этап

Только если реальные тесты покажут необходимость:

```text
ballistic arc solver
high/low throw
bounce prediction
```

## Acceptance

1. Бот действительно кидает grenade в сторону противника.
2. Значительно меньше бессмысленных бросков в стены.
3. Нет grenade spam.
4. После броска special assist прекращается.
5. Нет влияния на обычные gun levels.

---

# ЭТАП 12 — Auditory Awareness Diagnostics

## Приоритет

Высокий.

## Цель

Сначала определить, какие звуки Valve действительно игнорирует.

## SoundMonitorService

Собирать события:

```text
weapon_fire
weapon_reload
player_jump
grenade_thrown
```

Footsteps можно оценивать по movement speed с ограниченной частотой.

## Sound record

```text
SourcePlayer
Position
Time
Type
Team
```

## Hearing distance

Использовать реалистичный configurable radius.

Не:

```text
вся карта
```

Начальный порядок величины можно тестировать около:

```text
800–1200 units
```

## TTL

Событие должно быстро устаревать.

Например:

```text
weapon shot: short TTL
footstep: very short TTL
```

## На этом этапе

Бот НЕ реагирует.

Только:

```text
HEARD_EVENT
```

и сопоставление с дальнейшим Valve reaction.

## Измерять

```text
sound happened
bot distance
Valve enemy before event
Valve enemy after event
time until bot reacts
```

## Acceptance

Понятно, действительно ли существует проблема Valve hearing и какие типы звуков важны.

---

# ЭТАП 13 — Auditory Awareness v1

## Приоритет

Высокий после diagnostics.

## Цель

Бот должен реагировать на близкий реальный звук противника, не получая точное всезнание о противнике.

## Главное правило

Sound != visible enemy.

Sound даёт только:

```text
примерное направление угрозы
```

## Предпочтительное решение

Сначала исследовать штатный:

```text
CCSBot::OnAudibleEvent
```

и связанные Valve states.

Если возможно, лучше позволить **самому Valve обработать звук**, увеличив/сняв ненужное ограничение, чем писать собственную sound navigation систему.

## Native approach

Исследовать hook/patch вокруг:

```text
CCSBot::OnAudibleEvent
```

но НЕ делать:

```text
GlobalHearRange
```

по всей карте.

Цель:

```text
reasonable GunGame hearing range
```

## Если managed approach окажется достаточным

Можно хранить:

```text
LastHeardEnemyPosition
LastHeardAt
```

и только временно стимулировать look-around/investigation.

Но нельзя постоянно насильно поворачивать голову.

## Priority

Sound reaction работает только если:

```text
нет visible enemy
нет Knife Rush
нет LadderTraversal
нет более высокого priority combat state
```

Как только enemy визуально найден:

```text
sound behaviour прекращается
Valve combat получает приоритет
```

## Acceptance

1. Enemy стреляет рядом за углом.
2. Бот реагирует направлением внимания.
3. Бот НЕ начинает стрелять через стену.
4. Далёкие события не дают информации.
5. Несколько simultaneous sounds не вызывают хаотическое поведение.

---

# ЭТАП 14 — Исследование Valve Combat Dodge / Strafe

## Приоритет

Условный.

## Причина

По текущим наблюдениям боты уже используют strafe.

Поэтому собственный Combat Strafe пока не нужен.

## Цель этапа

Определить, действительно ли Valve combat strafe недостаточен.

## Наблюдать

При visible enemy:

```text
lateral velocity
direction changes
IsAttacking
IsEnemySniperVisible
SawEnemySniperTimer
weapon class
```

## Если strafe работает хорошо

Закрыть этап:

```text
NO DEVELOPMENT REQUIRED
```

## Если найдена проблема

Исследовать selective Valve behaviour gates, аналогичные:

```text
AttackState dodge chance
CanStrafe gate
keep-moving gate
dodge during reload
```

## Главное правило

Не писать собственный постоянный:

```text
LEFT-RIGHT-LEFT-RIGHT
```

если Valve уже делает это правильно.

Knife Rush остаётся исключением, потому что это специальный режим с отдельной целью.

---

# ЭТАП 15 — Counter-Strafe v2, только если нужен по тестам

## Приоритет

Ниже Sniper Peek, Aim, Vision и Grenade Assist.

## Цель

Улучшить остановку перед/во время точного выстрела.

Counter-strafe не является combat strafe.

```text
Combat strafe:
movement during combat

Counter-strafe:
short braking/counter-input to improve accuracy
```

## Текущую реализацию не менять заранее

Сначала сравнить:

```text
current velocity damping
vs
input-based opposite movement
```

## Experimental implementation

Если бот движется:

```text
LEFT
```

дать очень короткий:

```text
RIGHT input
```

вместо прямого изменения velocity.

И наоборот.

## Test metrics

```text
speed at shot
hit percentage
movement smoothness
Valve movement recovery
```

## Acceptance

Новая версия принимается только если реально лучше существующей.

Иначе сохранить текущую.

---

# ЭТАП 16 — Selective Native Combat AI Patches

## Приоритет

Условный Phase 3.

## Цель

Использовать только те native modifications, необходимость которых доказана предыдущими этапами.

## Возможные категории

```text
AttackState fire behaviour
Dodge gates
Strafe gates
Reload dodge
Vision gates
Audible event gates
```

## Запрет

Не создавать единый пакет:

```text
ApplyEveryPatchFromCS2-BotAI()
```

Каждый patch:

```text
отдельное назначение
отдельный feature flag
отдельный validation
отдельный acceptance test
```

## Source policy

Референсные проекты использовать для:

```text
понимания функций
поиска relevant native areas
понимания Valve decision paths
```

но не считать их signatures автоматически правильными для текущего CS2 build.

---

# ЭТАП 17 — Utility/Score AI: НЕ ПЛАНИРОВАТЬ СЕЙЧАС

## Статус

Deferred.

## Причина

Текущая explicit-priority architecture проще, надёжнее и лучше диагностируется.

Предпочтительно продолжать:

```text
Ladder / mandatory safety
    >
Knife Level
    >
Knife Rush
    >
Grenade Level
    >
Normal GunGame
```

Если когда-нибудь появится большое количество конкурирующих behaviours, utility scoring можно добавить внутри:

```text
NormalGunGame
```

но не переписывать на него весь plugin.

---

# 18. Общая система приоритетов поведения

По мере добавления новых функций рекомендуется использовать следующую модель.

```text
1. Human takeover
      -> plugin does nothing

2. Invalid/dead/spawn protection
      -> plugin does nothing

3. LadderTraversal
      -> owns necessary movement

4. Mandatory GunGame special mode
      KnifeLevel / GrenadeLevel

5. Opportunistic KnifeRush

6. Active visible-enemy combat
      Aim
      SniperPeek
      CombatCrouch
      future counter-strafe

7. Vision/look-around assistance

8. Sound investigation

9. Idle recovery

10. Normal Valve AI
```

StuckMonitor не является behaviour state.

Он только наблюдает.

---

# 19. Общие правила для native функций

Любой новый native hook/patch должен иметь следующий lifecycle:

```text
RESOLVE
    |
VALIDATE
    |
ENABLE
    |
MONITOR
    |
DISABLE / RESTORE
```

При ошибке:

```text
native feature disabled
main GunGameBotAI continues
```

Нельзя допускать:

```text
invalid signature -> server crash
```

## Startup log

Пример:

```text
[AimNative] PickNewAimSpot signature OK
[VisionNative] SkipMovingGate disabled by config
[SoundNative] OnAudibleEvent signature not found - feature unavailable
```

---

# 20. Feature flags

Минимальный рекомендуемый набор по мере реализации:

```text
StuckMonitorEnabled

AimEnhancementEnabled
AimDebug

VisionMonitorEnabled
VisionEnhancementEnabled

VisionPatchSkipMovingGateEnabled
VisionPatchApproachPointsEnabled

SniperPeekEnabled

CombatCrouchEnabled

GrenadeTargetAssistEnabled
GrenadeThrowAssistEnabled

SoundMonitorEnabled
SoundAwarenessEnabled

CombatMovementDiagnosticsEnabled
```

Не добавлять все параметры заранее.

Каждый появляется только вместе с соответствующим этапом.

---

# 21. Debug philosophy

Обычный production log не должен превращаться в per-tick trace.

Использовать три уровня:

```text
Normal
Focused diagnostics
Verbose development diagnostics
```

## Normal

Только:

```text
startup
enable/disable
native feature failure
unexpected exception
rare significant event
```

## Focused

Например:

```text
Stuck
Vision
Aim
Grenade
Sound
SniperPeek
```

## Verbose

Только временно во время разработки.

---

# 22. Общая стратегия тестирования каждого этапа

Каждый behavioral этап проходит четыре уровня.

## A. Development test

1–2 бота, контролируемая карта, detailed log.

## B. Bot-only test

Полный GunGame матч с ботами.

Проверять:

```text
behaviour
errors
CPU
server stability
state cleanup
```

## C. Mixed test

Humans + bots.

Особенно проверить:

```text
takeover
join/leave
bot removal
round transitions
GunGame level changes
```

## D. Production

Feature включается отдельно.

Если проблема:

```text
feature flag OFF
```

а не rollback всего GunGameBotAI.

---

# 23. Production acceptance rule

Этап считается принятым только когда:

```text
feature работает
AND
не ломает уже принятые функции
AND
не вызывает crash
AND
может быть независимо выключен
AND
cleanup работает
AND
лог достаточен для диагностики
```

Особенно регрессионно проверять:

```text
Knife Rush
Ladder Management
weapon switching
bot death/respawn
map change
plugin disable
```

---

# 24. Рекомендуемый фактический порядок разработки

## Group A — безопасная диагностика и infrastructure

```text
Stage 0  Stable baseline
Stage 1  StuckMonitor
Stage 2  TransientControl / MovementLease infrastructure
```

Эти этапы практически не должны менять gameplay.

## Group B — perception и aim

```text
Stage 3  VisibilityTraceService + diagnostics
Stage 4  AimService
Stage 5  Vision diagnostics
Stage 6  Managed Vision improvements
Stage 7  Selective Native Vision patches
```

Это самая важная следующая группа, потому что основная проблема GunGame — бот должен быстро обнаруживать находящегося рядом противника и правильно стрелять по реально видимой цели.

## Group C — combat mechanics

```text
Stage 8  Sniper Peek v2
Stage 9  Combat Crouch
```

## Group D — grenade level

```text
Stage 10 Grenade target selection
Stage 11 Grenade aim/throw assist
```

## Group E — hearing

```text
Stage 12 Sound diagnostics
Stage 13 Auditory Awareness
```

## Group F — conditional advanced work

```text
Stage 14 Valve strafe/dodge investigation
Stage 15 Counter-Strafe v2 if justified
Stage 16 Selective native combat patches if justified
```

## Deferred indefinitely

```text
generic StuckRecovery
flash avoidance
per-bot personality
full Utility AI
map grenade lineups
custom navigation/pathfinding
global hearing
global/FOV-less vision
```

---

# 25. Основной критерий архитектуры

При каждом новом решении задавать вопрос:

```text
Можем ли мы заставить существующий Valve AI принять более правильное решение,
не забирая у него управление полностью?
```

Если ответ:

```text
YES
```

использовать этот способ.

Предпочтительная последовательность вмешательства:

```text
1. Observe only
2. Schema/state correction
3. Short temporary command
4. Native hook around one Valve decision
5. Selective native patch
6. Full custom behaviour — только если ничего выше не работает
```

Это особенно важно после опыта с ladder management, где постоянная борьба двух систем управления оказалась значительно опаснее небольших направленных коррекций.

---

# 26. Definition of Done всей следующей фазы проекта

Следующая большая фаза GunGameBotAI считается завершённой, когда независимо и стабильно работают:

```text
Stuck diagnostics
Aim enhancement
better enemy awareness / look-around
Sniper Peek
Combat Crouch
Grenade target/throw assistance
Auditory Awareness
```

При этом:

```text
Knife Rush остаётся стабильным
Ladder Management остаётся стабильным
Valve navigation остаётся основной navigation system
нет обязательного внешнего BotController
нет обязательного старого RayTrace plugin
native features fail closed
каждую новую функцию можно отключить отдельно
```

Все остальные расширения должны добавляться только после появления конкретной наблюдаемой проблемы.

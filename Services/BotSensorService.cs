using System.Numerics;
using CounterStrikeSharp.API.Core;
using GunGameBotAI.Models;

namespace GunGameBotAI.Services;

public sealed class BotSensorService
{
    public EnemySnapshot? ReadEnemy(CCSPlayerPawn pawn, CCSBot bot, BotRuntimeState state, float now)
    {
        CCSPlayerPawn? enemyPawn;
        try
        {
            enemyPawn = bot.Enemy.Value;
        }
        catch
        {
            if (!state.KnifeRushAccepted)
                state.ClearKnifeRushEncounter();
            return null;
        }

        if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.Health <= 0 ||
            enemyPawn.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
            enemyPawn.TeamNum == pawn.TeamNum)
        {
            if (!state.KnifeRushAccepted)
                state.ClearKnifeRushEncounter();
            return null;
        }

        if (!NativeValueReader.TryGetOrigin(pawn, out Vector3 botOrigin) ||
            !NativeValueReader.TryGetOrigin(enemyPawn, out Vector3 enemyOrigin))
        {
            return null;
        }

        int entityIndex = checked((int)enemyPawn.Index);
        bool visible = bot.IsEnemyVisible;

        if (state.CurrentEnemyEntityIndex != entityIndex)
        {
            state.CurrentEnemyEntityIndex = entityIndex;
            state.EnemyEncounterStartedAt = now;
            state.EnemyLastSeenAt = visible ? now : 0.0f;
            state.KnifeRushDecisionMade = false;
        }
        else if (visible)
        {
            state.EnemyLastSeenAt = now;
        }

        return new EnemySnapshot(
            entityIndex,
            enemyPawn,
            enemyOrigin,
            NativeValueReader.Distance3D(botOrigin, enemyOrigin),
            NativeValueReader.Distance2D(botOrigin, enemyOrigin),
            visible);
    }
}

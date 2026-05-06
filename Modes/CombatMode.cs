using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Linq;
using System.Numerics;

namespace AutoExile.Modes
{
    /// <summary>
    /// Combat Mode: engages monsters based on configured combat behavior and monster filter.
    /// Lazy mode only fights when allowed monsters are already within combat range.
    /// Aggressive mode will pursue allowed monsters across the map.
    /// </summary>
    public class CombatMode : IBotMode
    {
        public string Name => "Combat";

        private string _status = "Combat mode ready";
        private string _decision = "";

        public void OnEnter(BotContext ctx)
        {
            _status = "Combat mode active";
            _decision = "waiting for targets";
            ctx.Log("Entering combat mode");
        }

        public void OnExit()
        {
            _status = "Combat mode inactive";
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc.IsLoading)
            {
                _status = "Loading...";
                _decision = "waiting";
                return;
            }

            var combatSettings = ctx.Settings.Combat;
            var style = ParseStyle(combatSettings.Style.Value);
            var monsterFilter = ParseTargetType(combatSettings.TargetType.Value);
            var combatRange = ctx.Settings.Build.CombatRange.Value;

            var hasAllowedTarget = HasAllowedTarget(gc, monsterFilter, allowDormant: style == CombatModeStyle.Aggressive);
            var hasAllowedTargetInRange = HasAllowedTarget(gc, monsterFilter, allowDormant: false, range: combatRange);

            bool shouldFight = combatSettings.EnableCombat.Value &&
                (style == CombatModeStyle.Aggressive ? hasAllowedTarget : hasAllowedTargetInRange);

            if (shouldFight)
            {
                ctx.Combat.SetProfile(new CombatProfile
                {
                    Enabled = true,
                    Positioning = style == CombatModeStyle.Aggressive
                        ? CombatPositioning.Aggressive
                        : CombatPositioning.Melee,
                });
            }
            else
            {
                ctx.Combat.SetProfile(CombatProfile.Default);
            }

            ctx.Combat.SuppressPositioning = style == CombatModeStyle.Lazy;
            ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
            ctx.Combat.Tick(ctx);

            _status = shouldFight ? (style == CombatModeStyle.Aggressive ? "Aggressive combat" : "Lazy combat")
                                  : "Waiting for allowed targets";

            if (!combatSettings.EnableCombat.Value)
                _decision = "combat disabled";
            else if (style == CombatModeStyle.Aggressive)
                _decision = hasAllowedTarget ? "pursuing allowed monsters" : "no allowed monsters present";
            else
                _decision = hasAllowedTargetInRange ? "attacking nearby monsters" : "waiting for nearby targets";
        }

        private static CombatModeStyle ParseStyle(string value)
        {
            return value switch
            {
                "Aggressive" => CombatModeStyle.Aggressive,
                _ => CombatModeStyle.Lazy,
            };
        }

        private static CombatModeMonsterType ParseTargetType(string value)
        {
            return value switch
            {
                "Rare" => CombatModeMonsterType.RareOrAbove,
                "Unique" => CombatModeMonsterType.UniqueOnly,
                _ => CombatModeMonsterType.Normal,
            };
        }

        private static bool HasAllowedTarget(GameController gc, CombatModeMonsterType filter, bool allowDormant, float range = float.MaxValue)
        {
            var playerGrid = gc.Player?.GridPosNum ?? Vector2.Zero;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster && e.IsHostile && e.IsAlive))
            {
                if (range < float.MaxValue && Vector2.Distance(entity.GridPosNum, playerGrid) > range)
                    continue;

                if (!allowDormant && !entity.IsTargetable)
                    continue;

                if (IsAllowedByFilter(entity.Rarity, filter))
                    return true;
            }
            return false;
        }

        private static bool IsAllowedByFilter(MonsterRarity rarity, CombatModeMonsterType filter)
        {
            return filter switch
            {
                CombatModeMonsterType.RareOrAbove => rarity == MonsterRarity.Rare || rarity == MonsterRarity.Unique,
                CombatModeMonsterType.UniqueOnly => rarity == MonsterRarity.Unique,
                _ => true,
            };
        }
    }

    public enum CombatModeStyle
    {
        Lazy,
        Aggressive,
    }

    public enum CombatModeMonsterType
    {
        Normal,
        RareOrAbove,
        UniqueOnly,
    }
}

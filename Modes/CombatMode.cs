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
        private int _allowedTargetCount;
        private int _allowedTargetInRangeCount;
        private CombatModeStyle _activeStyle = CombatModeStyle.Lazy;
        private CombatModeMonsterType _activeFilter = CombatModeMonsterType.Normal;

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

            _activeStyle = style;
            _activeFilter = monsterFilter;
            _allowedTargetCount = CountAllowedTargets(gc, monsterFilter, allowDormant: style == CombatModeStyle.Aggressive);
            _allowedTargetInRangeCount = CountAllowedTargets(gc, monsterFilter, allowDormant: false, range: combatRange);
            var hasAllowedTarget = _allowedTargetCount > 0;
            var hasAllowedTargetInRange = _allowedTargetInRangeCount > 0;

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

            if (!combatSettings.EnableCombat.Value)
            {
                _status = "Combat disabled";
                _decision = "combat disabled";
            }
            else if (style == CombatModeStyle.Aggressive)
            {
                _status = hasAllowedTarget ? $"Aggressive combat ({_allowedTargetCount} allowed targets)" : "Waiting for allowed targets";
                _decision = hasAllowedTarget ? "pursuing allowed monsters" : "no allowed monsters present";
            }
            else
            {
                _status = hasAllowedTargetInRange ? $"Lazy combat ({_allowedTargetInRangeCount} in range)" : "Waiting for allowed targets";
                _decision = hasAllowedTargetInRange ? "attacking nearby monsters" : "waiting for nearby targets";
            }
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

        private static int CountAllowedTargets(GameController gc, CombatModeMonsterType filter, bool allowDormant, float range = float.MaxValue)
        {
            var playerGrid = gc.Player?.GridPosNum ?? Vector2.Zero;
            var count = 0;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster && e.IsHostile && e.IsAlive))
            {
                if (range < float.MaxValue && Vector2.Distance(entity.GridPosNum, playerGrid) > range)
                    continue;

                if (!allowDormant && !entity.IsTargetable)
                    continue;

                if (IsAllowedByFilter(entity.Rarity, filter))
                    count++;
            }
            return count;
        }

        private void RenderHud(BotContext ctx)
        {
            var gfx = ctx.Graphics;
            if (gfx == null) return;
            var settings = ctx.Settings.Combat;
            if (!settings.ShowHud.Value) return;

            var x = 100f;
            var y = 180f;
            var lineHeight = 18f;
            gfx.DrawText("[CombatMode HUD]", new Vector2(x, y), SharpDX.Color.LightSkyBlue);
            y += lineHeight;
            gfx.DrawText($"Style: {_activeStyle} | Filter: {_activeFilter}", new Vector2(x, y), SharpDX.Color.LightGray);
            y += lineHeight;
            gfx.DrawText($"Allowed targets: {_allowedTargetCount} | In-range: {_allowedTargetInRangeCount}", new Vector2(x, y), SharpDX.Color.LightGray);
            y += lineHeight;
            gfx.DrawText($"Combat enabled: {settings.EnableCombat.Value} | InCombat: {ctx.Combat.InCombat} | Nearby: {ctx.Combat.NearbyMonsterCount}", new Vector2(x, y), SharpDX.Color.LightGray);
            y += lineHeight;
            var bestTarget = ctx.Combat.BestTarget;
            var bestTargetText = bestTarget != null ? $"Best target: {bestTarget.Id} ({bestTarget.Rarity})" : "Best target: none";
            gfx.DrawText(bestTargetText, new Vector2(x, y), SharpDX.Color.LightGray);
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

        public void Render(BotContext ctx)
        {
            RenderHud(ctx);
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

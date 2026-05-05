using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Pathfinding = AutoExile.Systems.Pathfinding;

namespace AutoExile.Modes
{
    /// <summary>
    /// Mapping Mode: Navigate between available destinations detected on radar.
    /// Automatically engages in combat and picks up rare items while moving.
    /// User can press 1-9 to select different destinations from the available list.
    /// </summary>
    public class MappingMode : IBotMode
    {
        public string Name => "Mapping";

        // Configuration
        public bool EnableCombat { get; set; } = true;
        public bool EnableLoot { get; set; } = true;
        public float LootRarityThreshold { get; set; } = 2f; // 0=normal, 1=magic, 2=rare, 3=unique
        public float NavigationStopDistance { get; set; } = 5f;

        // Exposed state
        public MappingModeState State => _state;
        public string StatusText => _status;
        public int CurrentDestinationIndex => _currentDestinationIndex;
        public int AvailableDestinationsCount => _destinations.Count;
        public Vector2? CurrentDestination => _currentDestination;

        // Internal state
        private MappingModeState _state = MappingModeState.Idle;
        private string _status = "Initializing...";
        private string _decision = "";
        private Vector2? _currentDestination;
        private int _currentDestinationIndex = -1;
        private List<MapDestination> _destinations = new();
        private DateTime _lastDestinationScan = DateTime.MinValue;
        private const float DestinationScanIntervalMs = 1000; // Rescan every 1 second

        // Loot tracking
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // Combat state
        private DateTime _lastCombatCheck = DateTime.MinValue;
        private const float CombatCheckIntervalMs = 100;

        // Area tracking
        private string _lastAreaName = "";

        // Helper class for destination info
        private class MapDestination
        {
            public Entity Entity { get; set; } = null!;
            public Vector2 GridPosition { get; set; }
            public string Type { get; set; } = "";  // "transition" or "portal"
            public string Name { get; set; } = "";
        }

        public void OnEnter(BotContext ctx)
        {
            _state = MappingModeState.Idle;
            _status = "Mapping mode active";
            _decision = "";
            _destinations.Clear();
            _currentDestinationIndex = -1;
            _currentDestination = null;
            _lastAreaName = "";
            _lootTracker.Reset();

            // Configure combat
            if (EnableCombat)
            {
                ctx.Combat.SetProfile(new CombatProfile
                {
                    Enabled = true,
                    Positioning = CombatPositioning.Aggressive,
                });
            }

            ctx.Log("Mapping mode active — use 1-9 to select destinations, Tab to scan");
        }

        public void OnExit()
        {
            _state = MappingModeState.Idle;
            _destinations.Clear();
            _currentDestination = null;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // Handle loading screens
            if (gc.IsLoading)
            {
                _state = MappingModeState.Loading;
                _status = "Loading...";
                _decision = "loading";
                ctx.Navigation.Stop(gc);
                return;
            }

            if (_state == MappingModeState.Loading)
            {
                _state = MappingModeState.Idle;
                _destinations.Clear();
                _currentDestinationIndex = -1;
                _lastDestinationScan = DateTime.MinValue;
            }

            // Detect area changes
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                if (!string.IsNullOrEmpty(_lastAreaName))
                {
                    OnAreaChanged(ctx);
                }
                _lastAreaName = currentArea;
            }

            var playerGridPos = GetPlayerGrid(gc);
            var isHideout = gc.Area?.CurrentArea?.IsHideout == true;

            // Combat — tick every frame when enabled
            if (!isHideout && EnableCombat)
            {
                ctx.Combat.SuppressPositioning = ctx.Navigation.IsNavigating || ctx.Interaction.IsBusy;
                ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
                ctx.Combat.Tick(ctx);
            }

            // Scan for destinations periodically
            if ((DateTime.Now - _lastDestinationScan).TotalMilliseconds >= DestinationScanIntervalMs)
            {
                ScanForDestinations(gc);
                _lastDestinationScan = DateTime.Now;
            }

            // Handle keyboard input for destination selection
            HandleDestinationSelection(ctx, gc);

            // Interact system for looting
            var interactionResult = ctx.Interaction.Tick(gc);
            _lootTracker.HandleResult(interactionResult, ctx);

            // Loot management
            if (!isHideout && EnableLoot)
            {
                TickLoot(ctx, gc, playerGridPos);
            }

            // Navigate to current destination
            if (_currentDestination.HasValue && !isHideout)
            {
                NavigateToDestination(ctx, gc, playerGridPos);
            }
            else if (_destinations.Count > 0)
            {
                _status = "Destination scan ready — press 1-9 to select";
                _decision = "scanning";
                _state = MappingModeState.Scanning;
            }
            else
            {
                _status = "No destinations found — scanning...";
                _decision = "idle";
                _state = MappingModeState.Idle;
            }
        }

        private void OnAreaChanged(BotContext ctx)
        {
            ModeHelpers.CancelAllSystems(ctx);
            _lootTracker.ResetCount();
            ctx.Loot.ClearFailed();
            _destinations.Clear();
            _currentDestinationIndex = -1;
            _currentDestination = null;
            _lastDestinationScan = DateTime.MinValue;
            _status = "Area changed — scanning for destinations";
            _decision = "area_changed";
            ctx.Log("Mapping: area changed — reset destinations");
        }

        /// <summary>
        /// Scan the current area for available transitions and portals.
        /// </summary>
        private void ScanForDestinations(GameController gc)
        {
            _destinations.Clear();

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (!entity.IsTargetable)
                    continue;

                string type = "";
                string name = "";

                // Area transitions
                if (entity.Type == EntityType.AreaTransition)
                {
                    type = "transition";
                    name = ExtractShortName(entity.Metadata ?? entity.Path ?? "Transition");
                }
                // Town portals / portals
                else if (entity.Type == EntityType.TownPortal || entity.Type == EntityType.Portal)
                {
                    type = "portal";
                    name = entity.RenderName ?? "Portal";
                }
                // League mechanic portals
                else if (IsLeagueMechanicPortal(entity))
                {
                    type = "portal";
                    name = ExtractShortName(entity.Metadata ?? entity.Path ?? "Portal");
                }
                else
                {
                    continue;
                }

                var gridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                _destinations.Add(new MapDestination
                {
                    Entity = entity,
                    GridPosition = gridPos,
                    Type = type,
                    Name = name
                });
            }

            // Maintain current selection if valid
            if (_currentDestinationIndex >= _destinations.Count)
            {
                _currentDestinationIndex = -1;
                _currentDestination = null;
            }
        }

        /// <summary>
        /// Handle 1-9 key presses to select a destination.
        /// Uses Windows GetAsyncKeyState API.
        /// </summary>
        private void HandleDestinationSelection(BotContext ctx, GameController gc)
        {
            // Check for 1-9 key presses on the number row (D1-D9 are virtual keys 0x31-0x39)
            for (int i = 6; i <= 9; i++)
            {
                int vkey = 0x30 + i; // 0x31 = '1', 0x32 = '2', etc.
                short keyState = GetAsyncKeyState(vkey);
                bool isPressed = (keyState & 0x8000) != 0; // High bit set = key pressed

                if (isPressed)
                {
                    SelectDestination(ctx, gc, i - 1);
                    return;
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// Select a destination by index from the available list.
        /// </summary>
        private void SelectDestination(BotContext ctx, GameController gc, int index)
        {
            if (index < 0 || index >= _destinations.Count)
            {
                ctx.Log($"Invalid destination index: {index}");
                return;
            }

            _currentDestinationIndex = index;
            var destination = _destinations[index];
            _currentDestination = destination.GridPosition;

            ctx.Navigation.Stop(gc);
            _state = MappingModeState.NavigatingToDestination;
            _status = $"[{index + 1}] Heading to {destination.Name}";
            _decision = $"select_dest_{index + 1}";

            ctx.Log($"Selected destination {index + 1}: {destination.Name} at ({destination.GridPosition.X:F0}, {destination.GridPosition.Y:F0})");
        }

        /// <summary>
        /// Navigate to the current destination.
        /// </summary>
        private void NavigateToDestination(BotContext ctx, GameController gc, Vector2 playerGridPos)
        {
            if (!_currentDestination.HasValue)
                return;

            var destPos = _currentDestination.Value;
            var distToDestination = Vector2.Distance(playerGridPos, destPos);

            // Reached destination
            if (distToDestination < NavigationStopDistance)
            {
                _state = MappingModeState.AtDestination;
                _status = $"At destination: {_destinations[_currentDestinationIndex].Name}";
                _decision = "arrived";
                ctx.Navigation.Stop(gc);
                return;
            }

            // Navigate if not already doing so
            if (!ctx.Navigation.IsNavigating)
            {
                // Check LOS first for direct movement
                var hasLOS = ctx.Navigation.HasWalkableLOS(gc, playerGridPos, destPos);

                if (hasLOS)
                {
                    ctx.Navigation.MoveToward(gc, destPos);
                    _state = MappingModeState.NavigatingToDestination;
                    _status = $"Moving to {_destinations[_currentDestinationIndex].Name} (LOS, dist: {distToDestination:F0})";
                    _decision = "nav_los";
                }
                else
                {
                    // Need pathfinding
                    if (ctx.Navigation.NavigateTo(gc, destPos))
                    {
                        _state = MappingModeState.NavigatingToDestination;
                        _status = $"Pathfinding to {_destinations[_currentDestinationIndex].Name} (path, dist: {distToDestination:F0})";
                        _decision = "nav_path";
                    }
                    else
                    {
                        _status = $"Navigation failed to {_destinations[_currentDestinationIndex].Name}";
                        _decision = "nav_failed";
                        ctx.Log($"Navigation failed to destination");
                    }
                }
            }
            else
            {
                _state = MappingModeState.NavigatingToDestination;
                _status = $"Moving to {_destinations[_currentDestinationIndex].Name} (dist: {distToDestination:F0})";
                _decision = "nav_active";
            }
        }

        /// <summary>
        /// Scan and pickup rare items.
        /// </summary>
        private void TickLoot(BotContext ctx, GameController gc, Vector2 playerGridPos)
        {
            if (ctx.Interaction.IsBusy)
                return;

            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            if (ctx.Loot.HasLootNearby)
            {
                var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                    _decision = $"loot: {candidate.ItemName}";
                }
            }
        }

        /// <summary>
        /// Detect league mechanic portals.
        /// </summary>
        private static bool IsLeagueMechanicPortal(Entity entity)
        {
            var path = entity.Path;
            if (string.IsNullOrEmpty(path)) return false;

            return path.Contains("SekhemaPortal")
                || path.Contains("Faridun/DjinnPortal")
                || path.Contains("HarvestPortalToggleable");
        }

        private static string ExtractShortName(string metadata)
        {
            if (string.IsNullOrEmpty(metadata)) return "Unknown";
            var lastSlash = metadata.LastIndexOf('/');
            return lastSlash >= 0 ? metadata[(lastSlash + 1)..] : metadata;
        }

        private static Vector2 GetPlayerGrid(GameController gc)
        {
            var pos = gc.Player.GridPosNum;
            return new Vector2(pos.X, pos.Y);
        }

        public void Render(BotContext ctx)
        {
            var gfx = ctx.Graphics;
            if (gfx == null) return;

            var hudX = 100f;
            var hudY = 100f;
            var lineH = 20f;

            // Status
            var color = _state switch
            {
                MappingModeState.NavigatingToDestination => SharpDX.Color.Yellow,
                MappingModeState.AtDestination => SharpDX.Color.LimeGreen,
                MappingModeState.Scanning => SharpDX.Color.Cyan,
                MappingModeState.Loading => SharpDX.Color.Gray,
                _ => SharpDX.Color.White
            };

            gfx.DrawText($"[Mapping] {_status}", new Vector2(hudX, hudY), color);
            hudY += lineH;

            gfx.DrawText($"State: {_state} | Destinations: {_destinations.Count}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            // List available destinations with keyboard shortcuts
            if (_destinations.Count > 0)
            {
                gfx.DrawText("Available destinations:", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;

                for (int i = 0; i < _destinations.Count && i < 9; i++)
                {
                    var dest = _destinations[i];
                    var isSelected = i == _currentDestinationIndex;
                    var destColor = isSelected ? SharpDX.Color.Gold : SharpDX.Color.LightGray;

                    var marker = isSelected ? "▶" : " ";
                    var destText = $"  {marker} [{i + 1}] {dest.Name} ({dest.Type})";
                    gfx.DrawText(destText, new Vector2(hudX + 20, hudY), destColor);
                    hudY += lineH;
                }
                hudY += lineH;
            }

            // Decision
            if (_decision != "")
            {
                gfx.DrawText($"Decision: {_decision}", new Vector2(hudX, hudY), SharpDX.Color.Gray);
                hudY += lineH;
            }

            // Loot counter
            if (_lootTracker.PickupCount > 0)
            {
                gfx.DrawText($"Loot: {_lootTracker.PickupCount} items", new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            // Combat status
            if (EnableCombat && ctx.Combat.InCombat)
            {
                gfx.DrawText($"Combat: {ctx.Combat.NearbyMonsterCount} nearby", new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            // Draw navigation path if active
            if (ctx.Navigation.IsNavigating && ctx.Navigation.CurrentNavPath.Count > 1)
            {
                var path = ctx.Navigation.CurrentNavPath;

                for (var i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var sa = Pathfinding.GridToScreen(ctx.Game, path[i].Position);
                    var sb = Pathfinding.GridToScreen(ctx.Game, path[i + 1].Position);
                    var windowRect = ctx.Game.Window.GetWindowRectangle();

                    if (sa.X > 0 && sa.X < windowRect.Width && sa.Y > 0 && sa.Y < windowRect.Height)
                    {
                        var lineColor = path[i + 1].Action == WaypointAction.Blink
                            ? SharpDX.Color.Magenta : SharpDX.Color.Yellow;
                        gfx.DrawLine(new Vector2(sa.X, sa.Y), new Vector2(sb.X, sb.Y), 2, lineColor);
                    }
                }
            }

            // Draw current destination marker if active
            if (_currentDestination.HasValue && _state == MappingModeState.NavigatingToDestination)
            {
                var destScreen = Pathfinding.GridToScreen(ctx.Game, _currentDestination.Value);
                var windowRect = ctx.Game.Window.GetWindowRectangle();

                if (destScreen.X > 0 && destScreen.X < windowRect.Width &&
                    destScreen.Y > 0 && destScreen.Y < windowRect.Height)
                {
                    var destVec = new Vector2(destScreen.X, destScreen.Y);
                    gfx.DrawLine(destVec + new Vector2(-15, -15), destVec + new Vector2(15, 15), 3, SharpDX.Color.Lime);
                    gfx.DrawLine(destVec + new Vector2(15, -15), destVec + new Vector2(-15, 15), 3, SharpDX.Color.Lime);
                    gfx.DrawText("DEST", destVec + new Vector2(18, -8), SharpDX.Color.Lime);
                }
            }
        }
    }

    public enum MappingModeState
    {
        Idle,
        Scanning,
        NavigatingToDestination,
        AtDestination,
        Loading
    }
}

using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector3 = System.Numerics.Vector3;

namespace SLAYER_Portals;

public class SLAYER_PortalsConfig : BasePluginConfig
{
    [JsonPropertyName("CTPortalColor")] public string CTPortalColor { get; set; } = "blue"; // The color of the CT portals. Can be any valid from (blue, orange, purple, green, white, black, red, yellow, cyan, pink)
    [JsonPropertyName("TPortalColor")] public string TPortalColor { get; set; } = "orange"; // The color of the T portals. Can be any valid from (blue, orange, purple, green, white, black, red, yellow, cyan, pink)
    [JsonPropertyName("CTPlayerPortalsCount")] public int CTPlayerPortalsCount { get; set; } = 1; // How many portals a CT player can place? (-1 for unlimited)
    [JsonPropertyName("TPlayerPortalsCount")] public int TPlayerPortalsCount { get; set; } = 1; // How many portals a T player can place? (-1 for unlimited)
    [JsonPropertyName("CTTotalPortalsCount")] public int CTTotalPortalsCount { get; set; } = -1; // How many portals can the CTs place? (-1 for unlimited)
    [JsonPropertyName("TTotalPortalsCount")] public int TTotalPortalsCount { get; set; } = -1; // How many portals can the Ts place? (-1 for unlimited)
    [JsonPropertyName("CreatePortalsOnWallOnly")] public bool CreatePortalsOnWallOnly { get; set; } = true; // Whether to only allow portal creation on walls
    [JsonPropertyName("PortalCreateOnWallDistance")] public int PortalCreateOnWallDistance { get; set; } = -1; // The maximum distance from a wall at which a portal can be created (-1 for unlimited)
}
public partial class SLAYER_Portals : BasePlugin, IPluginConfig<SLAYER_PortalsConfig>
{
    public override string ModuleName => "SLAYER_Portals";
    public override string ModuleVersion => "1.0";
    public override string ModuleAuthor => "SLAYER";
    public override string ModuleDescription => "Players can place portals and use them to teleport around the map.";
    public required SLAYER_PortalsConfig Config { get; set; }
    public void OnConfigParsed(SLAYER_PortalsConfig config)
    {
        Config = config;
    }
    private class PortalInfo
    {
        public int Team { get; set; } // The team of the player who placed the portal. This is used to enforce team-based portal limits and to determine which portal color to use for the player's portals
        public CDynamicProp? Portal1 { get; set; } // The first portal of the pair. To travel through the portal, players will need to enter Portal1 and they will come out from Portal2
        public CDynamicProp? Portal2 { get; set; } // The second portal of the pair. To travel through the portal, players will need to enter Portal2 and they will come out from Portal1
    }
    private readonly Dictionary<int, List<PortalInfo>> playerPortals = new();
    private readonly Dictionary<string, int> portalColorMapping = new()
    {
        {"blue", 1},
        {"green", 2},
        {"purple", 3},
        {"orange", 4},
        {"white", 5},
        {"black", 6},
        {"red", 7},
        {"yellow", 8},
        {"cyan", 9},
        {"pink", 10}
    };
    private readonly Dictionary<int, int> playerJustTeleported = new();
    private readonly Dictionary<uint, int> entityJustTeleported = new();
    private readonly List<uint> activeEntitiesIndex = new();
    private readonly List<CCSPlayerController> activePlayers = new(); // We will keep track of active players in this list to optimize the teleportation checks, so we don't have to loop through all players every tick. We will add players to this list when they spawn and remove them when they disconnect or die.
    private int tickCounter = 0; // We will use this counter to check for portal teleportation every 5 ticks (0.1 seconds) to prevent teleporting players too frequently when they are standing in the portal
    private const float PortalTouchWidthRange = 40f;
    private const float PortalTouchZRange = 100f;
    private const float PortalTouchDepthRange = 15f;
    private const float PortalExitVelocityBoost = 150f;
    private const float PortalExitForwardOffset = 16f;
    private const float PortalExitZOffset = 5f;
    private const int EntityTeleportCooldownTicks = 36;

    public override void Load(bool hotReload)
    {
        CRayTrace.Init();
        RegisterListener<Listeners.OnServerPrecacheResources>((manifest) =>
        {
            manifest.AddResource("models/portal/portal.vmdl");
        });
        RegisterListener<Listeners.OnTick>(() =>
        {
            tickCounter++;
            if (tickCounter >= 2)
            {
                foreach (var entityInstance in Utilities.GetAllEntities())
                {
                    if (entityInstance == null || !entityInstance.IsValid) continue;
                    if (entityInstance.DesignerName.StartsWith("prop_") || entityInstance.DesignerName.StartsWith("weapon_") || entityInstance.DesignerName.EndsWith("_projectile"))
                    {
                        var entity = new CBaseEntity(entityInstance.Handle);
                        activeEntitiesIndex.Add(entity.Index);
                    }
                }
            }
            if (tickCounter >= 5) // Check for portal teleportation every 5 ticks (0.1 seconds)
            {
                activePlayers.Clear();
                activeEntitiesIndex.Clear();
                foreach (var player in Utilities.GetPlayers())
                {
                    if (player == null || !player.IsValid || player.IsHLTV || player.Connected != PlayerConnectedState.Connected) continue;
                    activePlayers.Add(player);
                }
                tickCounter = 0; // Reset the counter after checking for teleportation
            }

            foreach (var portalInfo in GetAllActivePortalsPairs())
            {
                if (portalInfo.Portal1 != null && portalInfo.Portal1.IsValid && portalInfo.Portal2 != null && portalInfo.Portal2.IsValid)
                {
                    foreach (var player in activePlayers)
                    {
                        if (player == null || !player.IsValid || player.IsHLTV || player.Connected != PlayerConnectedState.Connected || player.TeamNum < 2 || player.PlayerPawn.Value!.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

                        var playerId = player.Slot;
                        if (playerJustTeleported.ContainsKey(playerId) && Server.TickCount - playerJustTeleported[playerId] < 36) continue; // If the player has just teleported in the last 36 ticks (0.6 seconds), we skip the teleportation checks for them to prevent them from getting teleported again immediately after teleporting
                        var playerPos = player.PlayerPawn.Value.AbsOrigin!;
                        // Check if the player is touching Portal1 and teleport them to Portal2, or if they are touching Portal2 and teleport them to Portal1
                        if (IsPlayerTouchingPortal(playerPos, portalInfo.Portal1))
                        {
                            TeleportPlayerToPortal(player, portalInfo.Portal1, portalInfo.Portal2);
                        }
                        else if (IsPlayerTouchingPortal(playerPos, portalInfo.Portal2))
                        {
                            TeleportPlayerToPortal(player, portalInfo.Portal2, portalInfo.Portal1);
                        }
                    }
                    foreach (var entityIndex in activeEntitiesIndex)
                    {
                        var entity = Utilities.GetEntityFromIndex<CBaseEntity>((int)entityIndex);
                        if (entity == null || !entity.IsValid) continue;
                        if (entity.Handle == portalInfo.Portal1.Handle || entity.Handle == portalInfo.Portal2.Handle) continue; // We don't want to check for collision with the portal itself
                        if (entityJustTeleported.TryGetValue(entity.Index, out var lastTick) && Server.TickCount - lastTick < EntityTeleportCooldownTicks) continue;

                        if (IsEntityTouchingPortal(entity.AbsOrigin!, portalInfo.Portal1))
                        {
                            TeleportEntityToPortal(entity, portalInfo.Portal1, portalInfo.Portal2);
                        }
                        else if (IsEntityTouchingPortal(entity.AbsOrigin!, portalInfo.Portal2))
                        {
                            TeleportEntityToPortal(entity, portalInfo.Portal2, portalInfo.Portal1);
                        }
                    }
                }
            }

        });
        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            playerPortals.Clear(); // Clear all portals at the start of each round
            return HookResult.Continue;
        });
        RegisterEventHandler<EventWeaponFire>((@event, info) =>
        {
            var player = @event.Userid;
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

            var weapon = @event.Weapon;
            // if weapon is not the taser or the player is not holding the attack2 button (which is usually the right mouse button), we don't want to place a portal
            if (weapon != "weapon_taser" || !player.Buttons.HasFlag(PlayerButtons.Attack2)) return HookResult.Continue;

            var playerId = player.Slot;
            if (!playerPortals.ContainsKey(playerId))
            {
                playerPortals[playerId] = new List<PortalInfo>();
            }

            var playerPortalCount = GetPlayerActivePortalsPairCount(playerId);
            var totalPlayerPortalCount = GetPlayerActivePortalsCount(playerId);

            // If the player has reached the maximum number of active portals pair they can place, we prevent them from placing more portals and send them a message in chat. 
            if (!IsOddNumber(totalPlayerPortalCount) && ((Config.CTPlayerPortalsCount != -1 && player.TeamNum == 3 && playerPortalCount >= Config.CTPlayerPortalsCount) || (Config.TPlayerPortalsCount != -1 && player.TeamNum == 2 && playerPortalCount >= Config.TPlayerPortalsCount)))
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.MaxPortalsReached", playerPortalCount, player.TeamNum == 3 ? Config.CTPlayerPortalsCount : Config.TPlayerPortalsCount]}");
                return HookResult.Continue;
            }

            var teamPortalCount = GetMaxActivePortalsForTeam(player.TeamNum);
            if ((Config.CTTotalPortalsCount != -1 && player.TeamNum == 3 && teamPortalCount >= Config.CTTotalPortalsCount) || (Config.TTotalPortalsCount != -1 && player.TeamNum == 2 && teamPortalCount >= Config.TTotalPortalsCount))
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.MaxTeamPortalsReached", teamPortalCount, player.TeamNum == 3 ? Config.CTTotalPortalsCount : Config.TTotalPortalsCount]}");
                return HookResult.Continue;
            }

            CDynamicProp? createdPortal = null;

            var pawn = player.PlayerPawn.Value!;
            var angle = new QAngle(pawn.AbsRotation!.X, pawn.AbsRotation!.Y + 90, 0); // We want the portal to be aligned with the player's view, so we use the player's view angles but set roll to 0
            var forward = GetForwardVector(pawn.AbsRotation!);

            if (Config.CreatePortalsOnWallOnly)
            {
                var eyePos = GetEyePosition(player);
                TraceShape(eyePos, pawn.V_angle, pawn, new TraceOptions(InteractionLayers.MASK_SHOT_FULL, InteractionLayers.MASK_SHOT_FULL, InteractionLayers.Player | InteractionLayers.PlayerClip | InteractionLayers.NPCClip), out var traceResult);
                if (traceResult.DidHit)
                {
                    var hitPoint = ConvertVector3ToVector(traceResult.HitPoint);
                    if (Config.PortalCreateOnWallDistance < 1 || CalculateDistance(eyePos, hitPoint) <= Config.PortalCreateOnWallDistance)
                    {
                        //hitPoint.Z -= 70f; // We need to lower the hit point a bit because the portal model's origin is not at the center of the portal, but rather at the bottom of the portal, so we need to adjust the hit point downwards to make the portal appear correctly on the wall.
                        // XY offset from the hitpoint. so model don't get stuck in the wall
                        var pos = hitPoint - (new Vector(forward.X, forward.Y, 0) * 10f); // 10 units in front of the wall to prevent the portal from getting stuck in the wall and being unteleportable.
                        createdPortal = CreatePortal(pos, angle, player.TeamNum == 3 ? Config.CTPortalColor : Config.TPortalColor); // Create the portal at the end position with the calculated angles and the player's team
                        TryGetPropGroundAdjustedOrigin(createdPortal!, pos, out var adjustedPos); // we need to make sure portal isn't half in the ground
                        createdPortal!.Teleport(adjustedPos, angle);
                    }
                }
            }
            else
            {
                var pos = pawn.AbsOrigin! + (new Vector(forward.X, forward.Y, 0) * 60f);
                createdPortal = CreatePortal(pos, angle, player.TeamNum == 3 ? Config.CTPortalColor : Config.TPortalColor); // Create the portal at the end position with the calculated angles and the player's team
            }

            // Now check it's a first or second portal for the player and assign it to the correct property in the PortalInfo class. If it's the first portal, we just create a new PortalInfo object and add it to the player's list. If it's the second portal, we find the first portal that doesn't have a second portal assigned yet and assign this new portal to it.
            if (IsOddNumber(totalPlayerPortalCount)) // If the player has an odd number of active portals, it means they have a first portal without a second portal, so we assign this new portal as the second portal for that PortalInfo object
            {
                var portalInfo = playerPortals[playerId].FirstOrDefault(p => p.Portal1 != null && (p.Portal2 == null || !p.Portal2.IsValid));
                if (portalInfo != null)
                {
                    portalInfo.Portal2 = createdPortal;
                }
            }
            else // If the player has an even number of active portals, it means they either have no portals or all their existing portals are complete pairs, so we create a new PortalInfo object and assign this new portal as the first portal
            {
                var portalInfo = new PortalInfo
                {
                    Team = player.TeamNum,
                    Portal1 = createdPortal,
                    Portal2 = null
                };
                playerPortals[playerId].Add(portalInfo);
            }

            player.RemoveItemByDesignerName("weapon_taser");
            player.GiveNamedItem("weapon_taser"); // We need to remove and give back the taser to prevent the player from having multiple tasers

            return HookResult.Continue;
        });
    }
    public override void Unload(bool hotReload)
    {
        // Remove all portals when the plugin is unloaded
        foreach (var portalInfo in GetAllActivePortalsPairs())
        {
            if (portalInfo.Portal1 != null && portalInfo.Portal1.IsValid)
            {
                portalInfo.Portal1.Remove();
            }
            if (portalInfo.Portal2 != null && portalInfo.Portal2.IsValid)
            {
                portalInfo.Portal2.Remove();
            }
        }
        base.Unload(hotReload);
    }
    private CDynamicProp? CreatePortal(Vector position, QAngle angles, string color)
    {
        var portal = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (portal == null || !portal.IsValid) return null;

        portal.Collision.SolidType = SolidType_t.SOLID_NONE;

        portal.SetModel("models/portal/portal.vmdl");
        portal.Teleport(new Vector(position.X, position.Y, position.Z + 55), angles, Vector.Zero);
        portal.DispatchSpawn();
        portal.AcceptInput("Skin", value: $"{portalColorMapping[color.ToLower()]}"); // Set the portal color based on the team. We use a mapping of color names to skin values to determine which skin to use for the portal based on the color specified in the config

        var bodyComponent = portal.CBodyComponent;
        if (bodyComponent == null)
        {
            return portal;
        }

        // We will scale from 0 to 1 in 1 second, so the portal will have a "growing" effect when spawned
        var entityScale = 0.0f;
        bodyComponent.SceneNode!.GetSkeletonInstance().Scale = 0;
        Utilities.SetStateChanged(portal, "CGameSceneNode", "m_flScale");

        CounterStrikeSharp.API.Modules.Timers.Timer? Timer = null;
        Timer = AddTimer(0.02f, () =>
        {
            if (!portal.IsValid || entityScale >= 1f)
            {
                if (Timer != null) Timer.Kill();
                return;
            }

            entityScale += 0.02f;
            var pos = portal.AbsOrigin!;
            portal.AcceptInput("SetScale", value: $"{entityScale}");
            portal.Teleport(new Vector(pos.X, pos.Y, pos.Z - 1)); // We need to keep teleporting the portal to the same position because changing the scale of the portal also changes its origin, so we need to keep teleporting it to the correct position to prevent it from moving downwards as it grows

        }, TimerFlags.REPEAT);


        return portal;
    }
    private static Vector GetRightVector(QAngle angles)
    {
        var forward = GetForwardVector(angles);
        var worldUp = new Vector(0, 0, 1);
        var right = Cross(forward, worldUp);
        if (LengthSquared(right) <= 0.0001f)
        {
            right = Cross(new Vector(0, 1, 0), forward);
        }

        return Normalize(right);
    }
    private static Vector GetUpVector(QAngle angles)
    {
        var forward = GetForwardVector(angles);
        var right = GetRightVector(angles);
        return Normalize(Cross(right, forward));
    }
    private static float Dot(Vector a, Vector b)
    {
        return (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
    }
    private static Vector Cross(Vector a, Vector b)
    {
        return new Vector(
            (a.Y * b.Z) - (a.Z * b.Y),
            (a.Z * b.X) - (a.X * b.Z),
            (a.X * b.Y) - (a.Y * b.X)
        );
    }
    private static float LengthSquared(Vector v)
    {
        return (v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z);
    }
    private static Vector Normalize(Vector v)
    {
        var lengthSq = LengthSquared(v);
        if (lengthSq <= 0.0001f) return new Vector(0, 0, 0);

        var invLength = 1.0f / MathF.Sqrt(lengthSq);
        return new Vector(v.X * invLength, v.Y * invLength, v.Z * invLength);
    }
    private static float GetYawFromVector(Vector v)
    {
        return MathF.Atan2(v.Y, v.X) * (180f / MathF.PI);
    }
    private static float NormalizeYaw(float yaw)
    {
        while (yaw > 180f) yaw -= 360f;
        while (yaw < -180f) yaw += 360f;
        return yaw;
    }
    private int GetMaxActivePortalsForTeam(int team)
    {
        if (playerPortals.Count == 0) return 0; // If no portals have been placed yet, return 0
        // Count how many portals the team currently has active
        return playerPortals.Values.SelectMany(portals => portals).Where(portal => portal.Team == team).Count();
    }
    private int GetPlayerActivePortalsPairCount(int playerId)
    {
        if (!playerPortals.ContainsKey(playerId)) return 0;
        return playerPortals[playerId].Count(); // Return the number of active portal pair for the player
    }
    private int GetPlayerActivePortalsCount(int playerId)
    {
        if (!playerPortals.ContainsKey(playerId)) return 0;
        var portalInfo = playerPortals[playerId];
        var count = 0;
        foreach (var portal in portalInfo)
        {
            if (portal.Portal1 != null && portal.Portal1.IsValid) count++;
            if (portal.Portal2 != null && portal.Portal2.IsValid) count++;
        }
        return count; // Return the total number of active portals for the player (counting both Portal1 and Portal2)
    }
    private List<PortalInfo> GetAllActivePortalsPairs()
    {
        var activePortals = new List<PortalInfo>();
        activePortals.AddRange(playerPortals.Values.SelectMany(portals => portals)); // Get all PortalInfo objects for all players and combine them into a single list
        return activePortals;
    }
    private bool IsOddNumber(int number)
    {
        return number % 2 != 0;
    }
    private static Vector ConvertVector3ToVector(Vector3 vector)
    {
        return new Vector(vector.X, vector.Y, vector.Z);
    }
    private static Vector3 ConvertVectorToVector3(Vector vector)
    {
        return new Vector3(vector.X, vector.Y, vector.Z);
    }
    private float CalculateDistance(Vector point1, Vector point2)
    {
        float dx = point2.X - point1.X;
        float dy = point2.Y - point1.Y;
        float dz = point2.Z - point1.Z;

        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
    private bool TryGetPropGroundAdjustedOrigin(CDynamicProp prop, Vector desiredOrigin, out Vector adjustedOrigin)
    {
        adjustedOrigin = desiredOrigin;

        if (prop == null || !prop.IsValid || prop.Collision == null)
            return false;

        var mins = prop.Collision.Mins;
        const float groundTraceUp = 16f;
        const float groundTraceDown = 150f;
        var traceStart = new Vector(desiredOrigin.X, desiredOrigin.Y, desiredOrigin.Z + groundTraceUp);
        var traceEnd = new Vector(desiredOrigin.X, desiredOrigin.Y, desiredOrigin.Z - groundTraceDown);
        var options = new TraceOptions(InteractionLayers.MASK_SHOT_FULL, InteractionLayers.MASK_SHOT_FULL, InteractionLayers.Player | InteractionLayers.PlayerClip);

        if (TraceShape(traceStart, traceEnd, prop, options, out var trace) && trace.DidHit && !trace.IsAllSolid)
        {
            var groundZ = trace.HitPointZ;
            var offset = (groundZ - desiredOrigin.Z) - mins.Z;
            adjustedOrigin = new Vector(desiredOrigin.X, desiredOrigin.Y, desiredOrigin.Z + offset + 55f);
            return true;
        }

        adjustedOrigin = new Vector(desiredOrigin.X, desiredOrigin.Y, desiredOrigin.Z - mins.Z);
        return true;
    }
    private bool IsPlayerTouchingPortal(Vector playerPos, CDynamicProp portal)
    {
        var portalPos = portal.AbsOrigin!;
        var angles = portal.AbsRotation!;
        var forward = GetForwardVector(angles);
        var right = GetRightVector(angles);
        var up = GetUpVector(angles);
        var toPlayer = new Vector(playerPos.X - portalPos.X, playerPos.Y - portalPos.Y, playerPos.Z - portalPos.Z);

        var depth = Dot(toPlayer, right);
        var width = Dot(toPlayer, forward);
        var height = Dot(toPlayer, up);

        return MathF.Abs(depth) <= PortalTouchDepthRange
            && MathF.Abs(width) <= PortalTouchWidthRange
            && MathF.Abs(height) <= PortalTouchZRange;
    }
    private bool IsEntityTouchingPortal(Vector entityPos, CDynamicProp portal)
    {
        var portalPos = portal.AbsOrigin!;
        var angles = portal.AbsRotation!;
        var forward = GetForwardVector(angles);
        var right = GetRightVector(angles);
        var up = GetUpVector(angles);
        var toEntity = new Vector(entityPos.X - portalPos.X, entityPos.Y - portalPos.Y, entityPos.Z - portalPos.Z);

        var depth = Dot(toEntity, right);
        var width = Dot(toEntity, forward);
        var height = Dot(toEntity, up);

        return MathF.Abs(depth) <= PortalTouchDepthRange + 5f
            && MathF.Abs(width) <= PortalTouchWidthRange + 5f
            && MathF.Abs(height) <= PortalTouchZRange + 30f;
    }
    private void TeleportPlayerToPortal(CCSPlayerController player, CDynamicProp entryPortal, CDynamicProp exitPortal)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        var entryPos = entryPortal.AbsOrigin!;
        var entryAngles = entryPortal.AbsRotation!;
        var pos = exitPortal.AbsOrigin!;
        var angles = exitPortal.AbsRotation!;
        var entryRight = GetRightVector(entryAngles);
        var entryToPlayer = new Vector(pawn.AbsOrigin!.X - entryPos.X, pawn.AbsOrigin!.Y - entryPos.Y, pawn.AbsOrigin!.Z - entryPos.Z);
        var entryDepth = Dot(entryToPlayer, entryRight);

        var exitRight = GetRightVector(angles);
        var exitNormal = new Vector(-exitRight.X, -exitRight.Y, -exitRight.Z);

        var boostedVelocity = pawn.AbsVelocity + (exitNormal * PortalExitVelocityBoost);
        var zOffset = pawn.AbsOrigin!.Z - entryPos.Z;
        var exitOffsetDir = Normalize(new Vector(exitNormal.X, exitNormal.Y, 0));
        if (LengthSquared(exitOffsetDir) <= 0.0001f)
        {
            exitOffsetDir = exitNormal;
        }

        var exitOffset = exitOffsetDir * PortalExitForwardOffset;
        exitOffset.Z += PortalExitZOffset;

        var entryFrontNormal = new Vector(-entryRight.X, -entryRight.Y, -entryRight.Z);
        var entryFrontYaw = GetYawFromVector(entryFrontNormal);
        var exitFrontYaw = GetYawFromVector(exitNormal);
        var playerYaw = pawn.AbsRotation!.Y;
        var relativeYaw = NormalizeYaw(playerYaw - entryFrontYaw);
        var targetYaw = entryDepth < 0 ? NormalizeYaw(exitFrontYaw + relativeYaw + 180f) : NormalizeYaw(exitFrontYaw + relativeYaw);

        playerJustTeleported[player.Slot] = Server.TickCount;
        pawn.BaseVelocity.X = boostedVelocity.X;
        pawn.BaseVelocity.Y = boostedVelocity.Y;
        pawn.BaseVelocity.Z = boostedVelocity.Z;
        pawn.Teleport(
            new Vector(pos.X + exitOffset.X, pos.Y + exitOffset.Y, pos.Z + zOffset + exitOffset.Z),
            new QAngle(pawn.AbsRotation!.X, targetYaw, 0),
            boostedVelocity
        );
    }
    private void TeleportEntityToPortal(CBaseEntity entity, CDynamicProp entryPortal, CDynamicProp exitPortal)
    {
        var entryPos = entryPortal.AbsOrigin!;
        var entryAngles = entryPortal.AbsRotation!;
        var pos = exitPortal.AbsOrigin!;
        var angles = exitPortal.AbsRotation!;
        var entryRight = GetRightVector(entryAngles);
        var entryToEntity = new Vector(entity.AbsOrigin!.X - entryPos.X, entity.AbsOrigin!.Y - entryPos.Y, entity.AbsOrigin!.Z - entryPos.Z);
        var entryDepth = Dot(entryToEntity, entryRight);

        var exitRight = GetRightVector(angles);
        var exitNormal = new Vector(-exitRight.X, -exitRight.Y, -exitRight.Z);

        var boostedVelocity = entity.AbsVelocity + (exitNormal * (entity.DesignerName.EndsWith("_Projectile") ? 0f : PortalExitVelocityBoost));
        var zOffset = entity.AbsOrigin!.Z - entryPos.Z;
        var exitOffsetDir = Normalize(new Vector(exitNormal.X, exitNormal.Y, 0));
        if (LengthSquared(exitOffsetDir) <= 0.0001f)
        {
            exitOffsetDir = exitNormal;
        }

        var exitOffset = exitOffsetDir * PortalExitForwardOffset;
        exitOffset.Z += PortalExitZOffset;
        entityJustTeleported[entity.Index] = Server.TickCount;
        entity.Teleport(new Vector(pos.X + exitOffset.X, pos.Y + exitOffset.Y, pos.Z + zOffset + exitOffset.Z), new QAngle(angles.X, angles.Y + 90, 0), boostedVelocity);
    }
}
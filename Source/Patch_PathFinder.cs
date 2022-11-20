using HarmonyLib;
using Verse;
using Verse.AI;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using RimWorld;
using static CleanPathfinding.CleanPathfindingUtility;
using static CleanPathfinding.ModSettings_CleanPathfinding;
 
namespace CleanPathfinding
{
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPath), new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode), typeof(PathFinderCostTuning) })]
    static class Patch_PathFinder
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool ran = false;
            int offset = -1;
            foreach (var code in instructions)
            {
                yield return code;
                if (offset == -1 && code.opcode == OpCodes.Ldfld && code.OperandIs(AccessTools.Field(typeof(TerrainDef), nameof(TerrainDef.extraNonDraftedPerceivedPathCost))))
                {
                    offset = 0;
                    continue;
                }
                if (offset > -1 && ++offset == 2)
                {
                    yield return new CodeInstruction(OpCodes.Ldloc_0);
                    yield return new CodeInstruction(OpCodes.Ldloc_S, 12); //topGrid
                    yield return new CodeInstruction(OpCodes.Ldloc_S, 45); //TerrainDef within the grid
                    yield return new CodeInstruction(OpCodes.Ldelem_Ref);
                    yield return new CodeInstruction(OpCodes.Ldloc_S, 48); //Pathcost total
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(PathFinder), nameof(PathFinder.map)));
                    yield return new CodeInstruction(OpCodes.Ldloc_S, 45); //cell location
                    yield return new CodeInstruction(OpCodes.Call, typeof(Patch_PathFinder).GetMethod(nameof(Patch_PathFinder.AdjustCosts)));
                    yield return new CodeInstruction(OpCodes.Stloc_S, 48);

                    ran = true;
                }
            }
            
            if (!ran) Log.Warning("[Clean Pathfinding] Transpiler could not find target. There may be a mod conflict, or RimWorld updated?");
        }

        static public int AdjustCosts(Pawn pawn, TerrainDef def, int cost, Map map, int index)
        {
            //Revert path costs based on rules, also factor light
            Faction faction = pawn?.Faction;
            if (faction != null && pawn.def.race.intelligence >= Intelligence.Humanlike)
            {
                bool revert = false;
                if (!faction.def.isPlayer && faction.HostileTo(Current.gameInt.worldInt.factionManager.ofPlayer)) revert = true;
                //Light factor
                else
                {
                    if (factorLight && GameGlowAtFast(map, index) < 0.3f) cost += 2;
                    if (doorPathing && DoorPathingUtility.compCache.TryGetValue(map.uniqueID, out MapComponent_DoorPathing doorPathingcomp)) cost += doorPathingcomp.doorCostCache[index];
                }

                if (!revert && ((factorCarryingPawn && pawn.carryTracker?.CarriedThing?.def.category == ThingCategory.Pawn) || (factorBleeding && pawn.health.hediffSet.cachedBleedRate > 0.1f))) revert = true;
                

                //Revert if needed
                if (revert && terrainCache.TryGetValue(def.shortHash, out int[] thisTerrain)) cost += thisTerrain[1] * -1;
                if (logging && Verse.Prefs.DevMode) map.debugDrawer.FlashCell(map.cellIndices.IndexToCell(index), cost , cost.ToString());
            }
            
            return cost < 0 ? 0 : cost;
        }

        public static float GameGlowAtFast(Map map, int index)
		{
			float daylight = 0f;
			if (map.roofGrid.roofGrid[index] != null)
			{
				daylight = map.skyManager.curSkyGlowInt;
				if (daylight == 1f) return daylight;
			}
			ColorInt color = map.glowGrid.glowGrid[index];
			if (color.a == 1) return 1f;

			return System.Math.Max(daylight, System.Math.Min(0.5f, (float)(color.r + color.g + color.b) * 0.0047058823529412f)); //n / 3f / 255f * 3.6f pre-computed, since I guess the assembler doesn't optimize this
		}
    }

    [HarmonyPatch (typeof(PathFinder), nameof(PathFinder.DetermineHeuristicStrength))]
    static class Patch_DetermineHeuristicStrength
    {
        static bool Prefix(ref float __result, Pawn pawn, IntVec3 start, LocalTargetInfo dest)
        {
            if (Custom_DistanceCurve == null || (pawn?.RaceProps.Animal ?? false)) return true;
            
            float lengthHorizontal = (start - dest.Cell).LengthHorizontal;
            __result = (float)System.Math.Round(Custom_DistanceCurve.Evaluate(lengthHorizontal));
            return false;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Fixes a vanilla RimWorld bug where plants defined in multiple biomes' wildBiomes lists
/// cause duplicate key errors when those biomes exist on the same map/world.
/// 
/// This bug occurs when:
/// 1. A plant has wildBiomes entries for multiple biomes (e.g., TreeAcacia in both AridShrubland and Desert)
/// 2. Multiple biomes exist on the same map (via biome transitions in Odyssey or mods)
/// 3. Or during world generation when checking biome properties for tile mutators
/// 
/// The vanilla code correctly checks for duplicates when processing biome.wildPlants,
/// but fails to do so when processing plant.wildBiomes, causing dictionary key collisions.
/// 
/// Technically, this would only affect modded games, as the stock biomes/lists are set up in such
/// a way as to avoid duplicatesbut since the code in question is in the vanilla game, I figured
/// I would address it directly.
/// 
/// Author: Kothliim
/// License: Public Domain - Free to use, modify, and distribute
/// </summary>
namespace VanillaBugFix
{
    [StaticConstructorOnStartup]
    public static class PlantCacheDuplicateKeyFix
    {
        static PlantCacheDuplicateKeyFix()
        {
            var harmony = new Harmony("VanillaBugFix.PlantCacheDuplicateKey");
            harmony.PatchAll();
            
            if (Prefs.DevMode)
            {
                Log.Message("[PlantCacheFix] Applied fixes for vanilla plant cache duplicate key bugs.");
            }
        }
    }

    /// <summary>
    /// Fixes WildPlantSpawner.CachePlantCommonalitiesIfShould() to prevent duplicate keys
    /// when processing plant.wildBiomes entries.
    /// </summary>
    [HarmonyPatch(typeof(WildPlantSpawner), "CachePlantCommonalitiesIfShould")]
    internal static class Patch_WildPlantSpawner_CachePlantCommonalitiesIfShould
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var addMethod = AccessTools.Method(typeof(Dictionary<ThingDef, float>), "Add");
            var safeAddMethod = AccessTools.Method(typeof(Patch_WildPlantSpawner_CachePlantCommonalitiesIfShould), 
                nameof(SafeAddToCommonalities));
            
            bool foundWildBiomesSection = false;
            int replacementCount = 0;
            
            for (int i = 0; i < codes.Count; i++)
            {
                var code = codes[i];
                
                // Detect when we're in the wildBiomes processing section
                if (code.operand is FieldInfo field && field.Name == "wildBiomes")
                {
                    foundWildBiomesSection = true;
                }
                
                // Replace unsafe Add() calls with our safe version
                if (foundWildBiomesSection && 
                    code.opcode == OpCodes.Callvirt && 
                    code.operand as MethodInfo == addMethod)
                {
                    yield return new CodeInstruction(OpCodes.Call, safeAddMethod);
                    replacementCount++;
                }
                else
                {
                    yield return code;
                }
            }
            
            if (Prefs.DevMode && replacementCount > 0)
            {
                Log.Message($"[PlantCacheFix] Patched {replacementCount} unsafe dictionary additions in WildPlantSpawner.");
            }
        }
        
        private static void SafeAddToCommonalities(Dictionary<ThingDef, float> dict, ThingDef plant, float commonality)
        {
            if (dict.ContainsKey(plant))
            {
                // Match vanilla behavior: average the commonalities
                dict[plant] = (dict[plant] + commonality) / 2f;
            }
            else
            {
                dict.Add(plant, commonality);
            }
        }
    }

    /// <summary>
    /// Fixes BiomeDef.CachePlantCommonalitiesIfShould() to prevent duplicate keys
    /// when processing plant.wildBiomes entries during world generation.
    /// </summary>
    [HarmonyPatch(typeof(BiomeDef), "CachePlantCommonalitiesIfShould")]
    internal static class Patch_BiomeDef_CachePlantCommonalitiesIfShould
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var addMethod = AccessTools.Method(typeof(Dictionary<ThingDef, float>), "Add");
            var safeAddMethod = AccessTools.Method(typeof(Patch_BiomeDef_CachePlantCommonalitiesIfShould), 
                nameof(SafeAddToCommonalities));
            
            bool foundWildBiomesSection = false;
            int replacementCount = 0;
            
            for (int i = 0; i < codes.Count; i++)
            {
                var code = codes[i];
                
                // Detect when we're in the wildBiomes processing section
                if (code.operand is FieldInfo field && field.Name == "wildBiomes")
                {
                    foundWildBiomesSection = true;
                }
                
                // Replace unsafe Add() calls with our safe version
                if (foundWildBiomesSection && 
                    code.opcode == OpCodes.Callvirt && 
                    code.operand as MethodInfo == addMethod)
                {
                    yield return new CodeInstruction(OpCodes.Call, safeAddMethod);
                    replacementCount++;
                }
                else
                {
                    yield return code;
                }
            }
            
            if (Prefs.DevMode && replacementCount > 0)
            {
                Log.Message($"[PlantCacheFix] Patched {replacementCount} unsafe dictionary additions in BiomeDef.");
            }
        }
        
        private static void SafeAddToCommonalities(Dictionary<ThingDef, float> dict, ThingDef plant, float commonality)
        {
            if (dict.ContainsKey(plant))
            {
                // Use maximum commonality for biome definitions
                dict[plant] = Mathf.Max(dict[plant], commonality);
            }
            else
            {
                dict.Add(plant, commonality);
            }
        }
    }
}

/* 
TECHNICAL DETAILS FOR LUDEON:

The bug exists in two methods that build plant commonality caches:

1. WildPlantSpawner.CachePlantCommonalitiesIfShould() at 0x0036AF18
   - First loop (biome.wildPlants) correctly uses ContainsKey before adding
   - Second loop (plant.wildBiomes) calls Add() without checking, causing exceptions

2. BiomeDef.CachePlantCommonalitiesIfShould() at 0x002BF7B8  
   - First loop (this.wildPlants) correctly uses Add() with no duplicates possible
   - Second loop (plant.wildBiomes) calls Add() without checking for existing keys

The fix is to check ContainsKey before calling Add() in the wildBiomes loops.

REPRODUCTION:
1. Create a map with multiple biomes (e.g., using Odyssey DLC biome transitions)
2. Have any plant with wildBiomes entries for multiple biomes present on the map (from mods)
3. The game will throw "An item with the same key has already been added" exception

This primarily affects:
- Games with mods that add multiple biomes to maps (Geological Landforms, Biome Transitions)
- Games with mods that add plants to multiple biomes via wildBiomes
*/
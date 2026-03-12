using HarmonyLib;
using System.Text;
using System.Reflection;
using UnityModManagerNet;
using Kingmaker.Blueprints;
using Kingmaker.Designers.Mechanics.Buffs;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using FumisCodex;
using Kingmaker.Blueprints.Classes;
using Kingmaker.Blueprints.Classes.Selection;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components.CasterCheckers;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.ElementsSystem;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Utility;
using Kingmaker.UnitLogic.Abilities.Components.AreaEffects;
using System.Diagnostics.Eventing.Reader;
using Kingmaker.UnitLogic.Mechanics.Components;

namespace MobileBlast_Fix;

public static class Main {
    internal static Harmony HarmonyInstance;
    public static bool Enabled;
    public static bool FumiPresent = false;
    internal static LibraryScriptableObject Library;
    internal static UnityModManager.ModEntry.ModLogger Log;

    public static bool Load(UnityModManager.ModEntry modEntry)
    {
        Log = modEntry.Logger;
        HarmonyInstance = new Harmony(modEntry.Info.Id);
        try
        {
            HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
        }
        catch (Exception ex)
        {
            HarmonyInstance.UnpatchAll(HarmonyInstance.Id);
            DebugError(ex);
            throw;
        }
        return true;
    }

    /// <summary>Only prints message, if compiled on DEBUG.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void DebugLog(string msg)
    {
        Log?.Log(msg);
    }

    internal static void DebugError(Exception ex)
    {
        Log?.LogException(ex);
    }

    static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
    {
        Enabled = value;
        return true;
    }

    public static T Get<T>(this LibraryScriptableObject library, String assetId) where T : BlueprintScriptableObject
    {
        return (T)library.BlueprintsByAssetId[assetId];
    }

    public static ContextActionConditionalSaved CreateContextActionConditionalSaved(GameAction[] succeed = null, GameAction[] failed = null)
    {
        var result = Helper.Create<ContextActionConditionalSaved>();
        result.Succeed = Helper.CreateActionList(succeed);
        result.Failed = Helper.CreateActionList(failed);
        return result;
    }

    public static ContextActionSavingThrow CreateActionSavingThrow(this SavingThrowType savingThrow, params GameAction[] actions)
    {
        var c = Helper.Create<ContextActionSavingThrow>();
        c.Type = savingThrow;
        c.Actions = Helper.CreateActionList(actions);
        return c;
    }

    [HarmonyAfter(["FumisCodex"])]
    [HarmonyPatch(typeof(LibraryScriptableObject), "LoadDictionary")]
    static class LibraryScriptableObject_LoadDictionary_Patch
    {
        static bool loaded = false;
        static void Postfix(LibraryScriptableObject __instance)
        {
            if (loaded) return;
            loaded = true;
            Library = __instance;

            try
            {
                FumiPresent = FumisCodex.Main.Enabled && FumisCodex.Main.COTWpresent;
            }
            catch (Exception e)
            {
                DebugLog(e.Message + "\n" + e.StackTrace);
                FumiPresent = false;
            }

            if (FumiPresent && !Settings.StateManager.State.doNotLoad.Contains("Kineticist.createMobileBlast") ) 
            {
                DebugLog("FumiCodex recognized. Applying fix.");

                /*
                There are 3 problems with the original mod:
                The mobileBlast feature was not added to the feature list of infusions.
                
                The mobileBlast abilities for each blast was not added to their respective blast's base variant list.
                
                Only mobileBlast abilities related to fire, (steam, magma, plasma, & fire) missed the proper prerequisite component (AbilityCasterHasFacts), but it didn't interfere with availability.
                Every other ability (cold, water, etc) had the preqrequisite component of the wall infusion instead, which interfered with availability unless you had the wall infusion.
                */

                // Responsible for retrieving and adding Fumi's mobileBlast feature to the feature list.
                var infusion_selection = Library.Get<BlueprintFeatureSelection>("58d6f8e9eea63f6418b107ce64f315ea");
                var mobileBlast_feature = Library.Get<BlueprintFeature>("18765cbb34684878ac3ae9245e4049e4");
                mobileBlast_feature.IsClassFeature = true;
                Helper.AppendAndReplace(ref infusion_selection.AllFeatures, mobileBlast_feature);
                
                var base_forms = Kineticist.base_byname;
                foreach (KeyValuePair<string, List<BlueprintAbility>> item in Kineticist.blast_variants)
                {
                    string key = item.Key;
                    BlueprintAbility value = item.Value.Last();
                    var hasFacts = Helper.CreateAbilityCasterHasFacts(mobileBlast_feature);

                    // Responsible for adding preq of type AbilityCasterHasFacts, which allows abilities to show if player has mobile blast feature
                    if (value?.GetComponent<AbilityCasterHasFacts>() != null) { value.ReplaceComponent<AbilityCasterHasFacts>(hasFacts); }
                    else { value.AddComponent(hasFacts); }

                    // Responsible for adding (saving throw: reflex negates) for ability
                    // saving throw is only applied during each round, not when an enemy first collides with the ability
                    AbilityEffectRunAction parent = value.GetComponent<AbilityEffectRunAction>();
                    ContextActionSpawnAreaEffect spawnParent = parent.Actions.Actions.Get(0) as ContextActionSpawnAreaEffect;
                    if (spawnParent != null)
                    {
                        var secondParent = spawnParent.AreaEffect;

                        // I cannot figure out if a different stat is used to calculate DCs of form infusions for Havocker witches anywhere 
                        // I'm just going to assume that it's the same, (10 + effective witch level + dex mod) = (10 + kineticist level + dex mod)
                        // Otherwise con will be used to calculate the DC, which is incorrect for form infusions such as this one
                        var prereq = secondParent?.GetComponent<CallOfTheWild.NewMechanics.ContextCalculateAbilityParamsBasedOnClasses>();
                        if (prereq != null) 
                        { 
                            prereq.use_kineticist_main_stat = false; 
                            prereq.StatType = StatType.Dexterity; 

                            AbilityAreaEffectRunAction spawnAreaEffectRun = secondParent.GetComponent<AbilityAreaEffectRunAction>();
                            var saving_throw = SavingThrowType.Reflex;

                            var RParent = Helper.CreateActionList(CreateActionSavingThrow(saving_throw, 
                                CreateContextActionConditionalSaved(null, spawnAreaEffectRun.Round.Actions)
                                ));

                            spawnAreaEffectRun.Round = RParent;
                        }
                    }

                    // Responsible for adding Fumi's mobileBlast ability to all variants of elements' blast bases
                    if (base_forms.ContainsKey(key)) { 
                        HelperEA.AddToAbilityVariants(base_forms[key], value);
                    }
                } 
            }
            else
            {
                DebugLog("FumiCodex not recognized. Cannot apply fix.");
            }
        }
    }
}

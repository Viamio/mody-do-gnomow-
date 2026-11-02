// KeepProgressOnLoss - mod do Burglin' Gnomes (BepInEx 5)
//
// Po przegranej (za malo wykonanych zadan) gra normalnie:
//   1. czysci ekwipunek graczy,
//   2. wywoluje GameProgressionManager.ResetGame(), ktore zeruje poziom/dzien,
//      czysci magazyn surowcow (GnomiumDeposit.resourceInventory),
//      niszczy meble i cofa ulepszenia domu (HomeManager.ResetOnGameLost),
//      a na koniec kasuje plik zapisu.
//
// Ten mod zostawia punkt 1 (tracisz przedmioty zabrane na wyprawe),
// a zamiast punktu 2 robi "miekki reset" - tak jak po wygranej, tylko bez nagrody:
//   - dom, ulepszenia, meble i surowce w magazynie zostaja,
//   - licznik oddanego gnomium i zadan wraca do zera, kwota jest liczona od nowa,
//   - gra pokazuje wybor kolejnego domu do okradzenia i zapisuje postep.
//
// Mod uzywa tylko refleksji, wiec nie trzeba kompilowac go z Assembly-CSharp.dll.
// W co-opie wystarczy, ze mod ma host.

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace KeepProgressOnLoss
{
    [BepInPlugin("pl.krystian.keepprogressonloss", "Keep Progress On Loss", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;

        // true tylko wtedy, gdy kod wykonuje sie wewnatrz korutyny konca dnia
        // (GnomiumDeposit.DelayedQuotaHitGameEnd) - czyli przy przegranej.
        [ThreadStatic] internal static bool InLossFlow;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Gdy wlaczone: po przegranej zostaje dom, ulepszenia i surowce w magazynie. Traci sie tylko ekwipunek z wyprawy.");

            Harmony harmony = new Harmony("pl.krystian.keepprogressonloss");
            int patched = 0;

            // 1) Korutyna konca dnia -> ustawiamy flage "jestesmy w przegranej".
            Type deposit = AccessTools.TypeByName("GnomiumDeposit");
            if (deposit != null)
            {
                foreach (Type nested in deposit.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (nested.Name.Contains("DelayedQuotaHitGameEnd"))
                    {
                        MethodInfo moveNext = AccessTools.Method(nested, "MoveNext");
                        if (moveNext != null)
                        {
                            harmony.Patch(moveNext,
                                new HarmonyMethod(typeof(Plugin).GetMethod("LossFlowPrefix", BindingFlags.Static | BindingFlags.NonPublic)),
                                null, null,
                                new HarmonyMethod(typeof(Plugin).GetMethod("LossFlowFinalizer", BindingFlags.Static | BindingFlags.NonPublic)),
                                null);
                            patched++;
                        }
                    }
                }
            }

            // 2) ResetGame -> przy przegranej zastepujemy miekkim resetem.
            Type gpm = AccessTools.TypeByName("GameProgressionManager");
            MethodInfo resetGame = gpm != null ? AccessTools.Method(gpm, "ResetGame") : null;
            if (resetGame != null)
            {
                harmony.Patch(resetGame,
                    new HarmonyMethod(typeof(Plugin).GetMethod("ResetGamePrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }

            // 3) Zabezpieczenie: nie kasuj pliku zapisu w trakcie przegranej.
            Type save = AccessTools.TypeByName("GnomeGameSave");
            MethodInfo deleteSave = save != null ? AccessTools.Method(save, "DeleteSaveData") : null;
            if (deleteSave != null)
            {
                harmony.Patch(deleteSave,
                    new HarmonyMethod(typeof(Plugin).GetMethod("DeleteSavePrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }

            if (patched == 3)
                Log.LogInfo("Keep Progress On Loss zaladowany (3/3 patche).");
            else
                Log.LogWarning("Keep Progress On Loss: zalozono tylko " + patched + "/3 patchy - gra mogla sie zaktualizowac, mod moze nie dzialac.");
        }

        private static void LossFlowPrefix()
        {
            InLossFlow = true;
        }

        private static Exception LossFlowFinalizer(Exception __exception)
        {
            InLossFlow = false;
            return __exception;
        }

        private static bool DeleteSavePrefix()
        {
            if (Enabled.Value && InLossFlow)
            {
                Log.LogInfo("Zablokowano kasowanie zapisu po przegranej.");
                return false;
            }
            return true;
        }

        // __instance = GameProgressionManager
        private static bool ResetGamePrefix(object __instance)
        {
            if (!Enabled.Value || !InLossFlow)
                return true; // normalne zachowanie (np. nowa gra)

            try
            {
                Type t = __instance.GetType();
                Log.LogInfo("Przegrana - miekki reset: dom, ulepszenia i magazyn zostaja.");

                // Zerujemy licznik oddanego gnomium i zadan, liczymy nowa kwote.
                // (to samo robi gra przy resecie, ale bez czyszczenia magazynu i domu)
                PropertyInfo depProp = AccessTools.Property(t, "Deposit");
                object dep = depProp != null ? depProp.GetValue(__instance, null) : null;
                if (dep != null)
                {
                    MethodInfo init = AccessTools.Method(dep.GetType(), "InitGameState");
                    if (init != null) init.Invoke(dep, null);
                }

                // Dalej tak jak po wygranej: wybor nastepnego domu + zapis gry.
                MethodInfo setSel = AccessTools.PropertySetter(t, "SelectedLevelIndex");
                if (setSel != null) setSel.Invoke(__instance, new object[] { -1 });

                FieldInfo lpField = AccessTools.Field(t, "levelProgression");
                object lp = lpField != null ? lpField.GetValue(__instance) : null;
                if (lp != null)
                {
                    MethodInfo next = AccessTools.Method(lp.GetType(), "GetNextLevelOptions");
                    if (next != null) next.Invoke(lp, null);
                }

                MethodInfo saveGame = AccessTools.Method(t, "SaveGame");
                if (saveGame != null) saveGame.Invoke(__instance, null);

                return false; // pomijamy oryginalny ResetGame
            }
            catch (Exception e)
            {
                Log.LogError("Miekki reset sie nie udal, gra zrobi normalny reset: " + e);
                return true;
            }
        }
    }
}

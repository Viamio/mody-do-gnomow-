// Dziennik - statystyki drużyny i dziennik wypraw (Burglin' Gnomes, BepInEx 5)
//
// - Na koniec każdego dnia pokazuje tabelkę: kto ile gnomium oddał (tylko u hosta)
//   i kto ile razy zginął.
// - Każdy dzień zapisuje się do dziennika: BepInEx\config\DziennikWypraw.txt
// - F3 = okno z dziennikiem i statystykami bieżącego dnia.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Dziennik
{
    [BepInPlugin("pl.krystian.dziennik", "Dziennik i statystyki", "1.0.0")]
    public class DziennikPlugin : BaseUnityPlugin
    {
        internal static DziennikPlugin Instance;

        private ConfigEntry<bool> cEnabled;
        private ConfigEntry<bool> cShowSummary;

        private class Stat { public int Gnomium; public int Deaths; }
        private readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();
        private readonly List<string> journal = new List<string>();

        private PropertyInfo pGpmInstance, pTimeActive, pCurrentDay, pDeposit;
        private Type playerType;
        private PropertyInfo pSteamPlayer, pResources, pGnomiumCount;

        private bool lastActive;
        private float nextCheck;
        private bool showWindow;
        private float summaryUntil;
        private string summaryTitle = "";
        private Rect window = new Rect(80, 80, 520, 420);
        private Vector2 scroll;
        private GUIStyle small;

        private void Awake()
        {
            Instance = this;
            cEnabled = Config.Bind("General", "Enabled", true, "Statystyki i dziennik wypraw.");
            cShowSummary = Config.Bind("General", "ShowDaySummary", true, "Pokazuj podsumowanie na koniec dnia.");

            Type gpm = AccessTools.TypeByName("GameProgressionManager");
            if (gpm != null)
            {
                pGpmInstance = AccessTools.Property(gpm, "Instance");
                pTimeActive = AccessTools.Property(gpm, "TimeActive");
                pCurrentDay = AccessTools.Property(gpm, "CurrentDay");
                pDeposit = AccessTools.Property(gpm, "Deposit");
            }
            playerType = AccessTools.TypeByName("PlayerNetworking");
            if (playerType != null)
            {
                pSteamPlayer = AccessTools.Property(playerType, "SteamPlayer");
                pResources = AccessTools.Property(playerType, "Resources");
            }
            Type rs = AccessTools.TypeByName("ResourceStorage");
            if (rs != null) pGnomiumCount = AccessTools.Property(rs, "GnomiumCount");

            Harmony h = new Harmony("pl.krystian.dziennik");
            int ok = 0;
            MethodInfo died = AccessTools.Method(AccessTools.TypeByName("PlayerHealth"), "OnDied");
            if (died != null) { h.Patch(died, null, new HarmonyMethod(typeof(DziennikPlugin).GetMethod("DiedPostfix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null); ok++; }
            MethodInfo dep = AccessTools.Method(AccessTools.TypeByName("GnomiumDeposit"), "Deposit");
            if (dep != null) { h.Patch(dep, new HarmonyMethod(typeof(DziennikPlugin).GetMethod("DepositPrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null); ok++; }

            LoadJournal();
            Logger.LogInfo("Dziennik zaladowany (" + ok + "/2 patche). F3 = dziennik.");
        }

        private static string JournalFile() { return Path.Combine(Paths.ConfigPath, "DziennikWypraw.txt"); }

        private void LoadJournal()
        {
            journal.Clear();
            try
            {
                if (File.Exists(JournalFile()))
                    journal.AddRange(File.ReadAllLines(JournalFile(), Encoding.UTF8));
            }
            catch (Exception) { }
        }

        // ---------- zbieranie danych ----------

        internal string PlayerName(object player)
        {
            try
            {
                object sp = pSteamPlayer != null ? pSteamPlayer.GetValue(player, null) : null;
                if (sp != null)
                {
                    MethodInfo tg = sp.GetType().GetMethod("TryGetName");
                    if (tg != null)
                    {
                        object[] args = new object[] { null };
                        if ((bool)tg.Invoke(sp, args) && args[0] is string && ((string)args[0]).Length > 0) return (string)args[0];
                    }
                }
            }
            catch (Exception) { }
            Component c = player as Component;
            return c != null ? c.gameObject.name : "Gracz";
        }

        private Stat Get(string name)
        {
            Stat s;
            if (!stats.TryGetValue(name, out s)) { s = new Stat(); stats[name] = s; }
            return s;
        }

        private static void DiedPostfix(object __instance)
        {
            if (Instance == null || !Instance.cEnabled.Value || Instance.playerType == null) return;
            try
            {
                Component c = __instance as Component;
                if (c == null) return;
                Component p = c.GetComponent(Instance.playerType);
                if (p == null) p = c.GetComponentInParent(Instance.playerType);
                if (p == null) return;
                Instance.Get(Instance.PlayerName(p)).Deaths++;
            }
            catch (Exception) { }
        }

        private static void DepositPrefix(object player)
        {
            if (Instance == null || !Instance.cEnabled.Value || player == null) return;
            try
            {
                object res = Instance.pResources != null ? Instance.pResources.GetValue(player, null) : null;
                int amount = res != null && Instance.pGnomiumCount != null ? (int)Instance.pGnomiumCount.GetValue(res, null) : 0;
                if (amount > 0) Instance.Get(Instance.PlayerName(player)).Gnomium += amount;
            }
            catch (Exception) { }
        }

        // ---------- początek i koniec dnia ----------

        private void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.25f;

            object gpm = null;
            bool active = false;
            try
            {
                gpm = pGpmInstance != null ? pGpmInstance.GetValue(null, null) : null;
                if (gpm != null && (gpm as UnityEngine.Object) != null && pTimeActive != null)
                    active = (bool)pTimeActive.GetValue(gpm, null);
                else gpm = null;
            }
            catch (Exception) { gpm = null; }

            if (active && !lastActive) stats.Clear();          // nowy dzień
            if (!active && lastActive && gpm != null && cEnabled.Value) EndOfDay(gpm);
            lastActive = active;
        }

        private static int ReadInt(object obj, string prop)
        {
            try
            {
                PropertyInfo p = AccessTools.Property(obj.GetType(), prop);
                if (p == null) return -1;
                return Convert.ToInt32(p.GetValue(obj, null));
            }
            catch (Exception) { return -1; }
        }

        private void EndOfDay(object gpm)
        {
            int day = -1, tasks = -1, minTasks = -1, gnomium = -1, quota = -1;
            try { if (pCurrentDay != null) day = Convert.ToInt32(pCurrentDay.GetValue(gpm, null)); } catch (Exception) { }
            object dep = null;
            try { dep = pDeposit != null ? pDeposit.GetValue(gpm, null) : null; } catch (Exception) { }
            if (dep != null)
            {
                tasks = ReadInt(dep, "CompletedTasks");
                minTasks = ReadInt(dep, "MinTasksCompleted");
                gnomium = ReadInt(dep, "DepositedGnomium");
                quota = ReadInt(dep, "Quota");
            }
            bool won = tasks >= 0 && minTasks >= 0 && tasks >= minTasks;
            string result = (tasks < 0 || minTasks < 0) ? "?" : (won ? "WYGRANA" : "PRZEGRANA");

            int deaths = 0; string best = "-"; int bestG = 0;
            foreach (KeyValuePair<string, Stat> kv in stats)
            {
                deaths += kv.Value.Deaths;
                if (kv.Value.Gnomium > bestG) { bestG = kv.Value.Gnomium; best = kv.Key; }
            }

            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " | Dzien " + day + " | " + result +
                          " | zadania " + tasks + "/" + minTasks + " | gnomium " + gnomium + " (kwota " + quota + ")" +
                          " | smierci " + deaths + (bestG > 0 ? " | najlepszy zlodziej: " + best + " (" + bestG + ")" : "");
            journal.Add(line);
            try { File.AppendAllText(JournalFile(), line + Environment.NewLine, new UTF8Encoding(false)); } catch (Exception) { }

            summaryTitle = "Koniec dnia " + day + ": " + result;
            if (cShowSummary.Value) summaryUntil = Time.unscaledTime + 20f;
        }

        // ---------- wyświetlanie ----------

        public static void Open() { if (Instance != null) Instance.showWindow = true; }

        private void OnGUI()
        {
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F3 && cEnabled.Value)
            {
                ev.Use();
                showWindow = !showWindow;
            }
            if (small == null) { small = new GUIStyle(GUI.skin.label); small.fontSize = 12; small.wordWrap = true; }

            if (Time.unscaledTime < summaryUntil && !showWindow)
            {
                float h = 70f + 22f * Math.Max(1, stats.Count);
                GUILayout.BeginArea(new Rect(Screen.width / 2f - 200f, 60f, 400f, h), GUI.skin.box);
                GUILayout.Label(summaryTitle);
                DrawStatsTable();
                GUILayout.Label("F3 = dziennik wypraw", small);
                GUILayout.EndArea();
            }

            if (showWindow)
                window = GUI.Window(0x647A6E6B, window, DrawWindow, "Dziennik wypraw (F3 - zamknij)");
        }

        private void DrawStatsTable()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Gracz", GUILayout.Width(200));
            GUILayout.Label("Gnomium", GUILayout.Width(80));
            GUILayout.Label("Śmierci", GUILayout.Width(80));
            GUILayout.EndHorizontal();
            if (stats.Count == 0) GUILayout.Label("(brak danych z tego dnia)", small);
            foreach (KeyValuePair<string, Stat> kv in stats)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Key, GUILayout.Width(200));
                GUILayout.Label(kv.Value.Gnomium.ToString(), GUILayout.Width(80));
                GUILayout.Label(kv.Value.Deaths.ToString(), GUILayout.Width(80));
                GUILayout.EndHorizontal();
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("Bieżący / ostatni dzień:");
            DrawStatsTable();
            GUILayout.Label("Gnomium liczy tylko host (u pozostałych graczy będzie 0).", small);
            GUILayout.Space(6);
            GUILayout.Label("Historia (najnowsze na górze):");
            scroll = GUILayout.BeginScrollView(scroll);
            if (journal.Count == 0) GUILayout.Label("Dziennik jest pusty - zagraj pierwszy dzień.", small);
            for (int i = journal.Count - 1; i >= 0 && i >= journal.Count - 100; i--)
                GUILayout.Label(journal[i], small);
            GUILayout.EndScrollView();
            if (GUILayout.Button("Zamknij")) showWindow = false;
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private void LateUpdate()
        {
            if (showWindow) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }
    }
}

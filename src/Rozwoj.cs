// Rozwoj - klasy gnomów, Żołędzie, drzewko umiejętności, plecak, osiągnięcia (Burglin' Gnomes, BepInEx 5)
//
// F4 = okno rozwoju.
// - Na koniec każdego dnia dostajesz Żołędzie (za zadania, wygraną i oddane gnomium).
// - KLASA (zmiana tylko w bazie): Złodziej, Siłacz, Zwiadowca, Saper.
// - UMIEJĘTNOŚCI (każdy kupuje dla siebie): Szybkie nóżki, Sprężyna, Kondycja, Krzepa, Hak mistrza.
// - DRUŻYNA (kupuje host, działa na wszystkich): Plecak (+1 miejsce w ekwipunku), Ciche kroki (mniej hałasu).
// - OSIĄGNIĘCIA: tytuły + premie w Żołędziach.
// Postęp zapisuje się w BepInEx\config\RozwojGnoma.txt (u każdego gracza osobno).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Rozwoj
{
    [BepInPlugin("pl.krystian.rozwoj", "Rozwój gnoma", "1.0.0")]
    public class RozwojPlugin : BaseUnityPlugin
    {
        internal static RozwojPlugin Instance;
        private ConfigEntry<bool> cEnabled;

        // ---------- dane gracza ----------
        private readonly Dictionary<string, int> data = new Dictionary<string, int>();
        private string className = "Brak";

        private class Skill
        {
            public string Id, Name, Desc; public int Max; public bool Team;
            public Skill(string id, string name, string desc, int max, bool team) { Id = id; Name = name; Desc = desc; Max = max; Team = team; }
        }
        private static readonly Skill[] Skills = {
            new Skill("nozki",   "Szybkie nóżki", "+6% prędkości biegu na poziom", 5, false),
            new Skill("sprezyna","Sprężyna",      "+8% wysokości skoku na poziom", 5, false),
            new Skill("kondycja","Kondycja",      "-10% zużycia staminy na poziom", 5, false),
            new Skill("krzepa",  "Krzepa",        "+10% siły pchania/noszenia na poziom", 5, false),
            new Skill("hak",     "Hak mistrza",   "+15% zasięgu i szybkości haka na poziom", 5, false),
            new Skill("plecak",  "Plecak (drużyna)", "+1 miejsce w ekwipunku dla wszystkich", 3, true),
            new Skill("cisza",   "Ciche kroki (drużyna)", "-10% hałasu dla wszystkich", 3, true),
        };

        private static readonly string[] Classes = { "Brak", "Złodziej", "Siłacz", "Zwiadowca", "Saper" };
        private static readonly string[] ClassDesc = {
            "Bez premii.",
            "+10% prędkości, mniej zużywa staminy przy biegu (-10%).",
            "+30% siły pchania i noszenia ciężkich rzeczy.",
            "+15% wysokości skoku, -20% zużycia staminy.",
            "-50% obrażeń od upadku, +30% zasięgu i szybkości haka."
        };

        private class Achievement
        {
            public string Id, Name, Desc;
            public Achievement(string id, string name, string desc) { Id = id; Name = name; Desc = desc; }
        }
        private static readonly Achievement[] Achievements = {
            new Achievement("a_first",  "Pierwsza robota", "Zagraj pierwszy dzień"),
            new Achievement("a_win1",   "Udany skok",      "Wygraj dzień"),
            new Achievement("a_win10",  "Zawodowiec",      "Wygraj 10 dni"),
            new Achievement("a_play30", "Weteran",         "Zagraj 30 dni"),
            new Achievement("a_perfect","Perfekcjonista",  "Wykonaj o 2 zadania więcej niż trzeba"),
            new Achievement("a_rich",   "Bogacz",          "Zdobądź łącznie 200 Żołędzi"),
            new Achievement("a_day10",  "Długodystansowiec","Dojdź do 10. dnia"),
            new Achievement("a_class",  "Kim jestem?",     "Wybierz klasę"),
        };

        // ---------- gra (refleksja) ----------
        private PropertyInfo pGpmInstance, pTimeActive, pCurrentDay, pDeposit;
        private Type normalMoveType, playerType, hookType;
        private FieldInfo fPlanar, fVertical, fRunStamina, fJumpStamina, fForceMul, fMaxForce, fFallDmg;
        private FieldInfo fBaseSpeed, fBoostSpeed, fJumpSpeed, fJumpApex;
        private FieldInfo fHookRange, fHookSpeed;
        private PropertyInfo pIsOwner, pInventory;

        private readonly Dictionary<object, float[]> origMove = new Dictionary<object, float[]>();
        private readonly Dictionary<object, float[]> origHook = new Dictionary<object, float[]>();
        private readonly Dictionary<object, int> origInv = new Dictionary<object, int>();

        private bool lastActive;
        private float nextCheck, nextApply;

        // ---------- UI ----------
        private bool showWindow;
        private int tab;
        private Rect window = new Rect(100, 70, 600, 500);
        private Vector2 scroll;
        private GUIStyle small, bold;
        private string toast = ""; private float toastUntil;

        private void Awake()
        {
            Instance = this;
            cEnabled = Config.Bind("General", "Enabled", true, "Klasy, umiejetnosci i Zoledzie.");
            Load();

            Type gpm = AccessTools.TypeByName("GameProgressionManager");
            if (gpm != null)
            {
                pGpmInstance = AccessTools.Property(gpm, "Instance");
                pTimeActive = AccessTools.Property(gpm, "TimeActive");
                pCurrentDay = AccessTools.Property(gpm, "CurrentDay");
                pDeposit = AccessTools.Property(gpm, "Deposit");
            }
            normalMoveType = AccessTools.TypeByName("NormalMovement");
            playerType = AccessTools.TypeByName("PlayerNetworking");
            hookType = AccessTools.TypeByName("GrapplingHook");
            if (normalMoveType != null)
            {
                fPlanar = AccessTools.Field(normalMoveType, "planarMovementParameters");
                fVertical = AccessTools.Field(normalMoveType, "verticalMovementParameters");
                fRunStamina = AccessTools.Field(normalMoveType, "runningStaminaUsage");
                fJumpStamina = AccessTools.Field(normalMoveType, "jumpStamina");
                fForceMul = AccessTools.Field(normalMoveType, "forceMultiplier");
                fMaxForce = AccessTools.Field(normalMoveType, "maxForce");
                fFallDmg = AccessTools.Field(normalMoveType, "fallDamageSmall");
                if (fPlanar != null) { fBaseSpeed = AccessTools.Field(fPlanar.FieldType, "baseSpeedLimit"); fBoostSpeed = AccessTools.Field(fPlanar.FieldType, "boostSpeedLimit"); }
                if (fVertical != null) { fJumpSpeed = AccessTools.Field(fVertical.FieldType, "jumpSpeed"); fJumpApex = AccessTools.Field(fVertical.FieldType, "jumpApexHeight"); }
            }
            if (hookType != null) { fHookRange = AccessTools.Field(hookType, "hookMaxRange"); fHookSpeed = AccessTools.Field(hookType, "hookSpeed"); }
            if (playerType != null) { pIsOwner = playerType.GetProperty("IsOwner"); pInventory = AccessTools.Property(playerType, "Inventory"); }

            try
            {
                Harmony h = new Harmony("pl.krystian.rozwoj");
                MethodInfo noise = AccessTools.Method(AccessTools.TypeByName("RoomManager"), "CreateNoiseEvent");
                if (noise != null) h.Patch(noise, new HarmonyMethod(typeof(RozwojPlugin).GetMethod("NoisePrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null);
            }
            catch (Exception e) { Logger.LogWarning("Patch halasu: " + e.Message); }

            Logger.LogInfo("Rozwoj gnoma zaladowany. F4 = okno. Ruch: " + (fBaseSpeed != null) + ", hak: " + (fHookRange != null));
        }

        // ================= zapis =================

        private static string DataFile() { return Path.Combine(Paths.ConfigPath, "RozwojGnoma.txt"); }

        private int Get(string k) { int v; return data.TryGetValue(k, out v) ? v : 0; }
        private void Set(string k, int v) { data[k] = v; }

        private void Load()
        {
            data.Clear();
            try
            {
                if (!File.Exists(DataFile())) return;
                foreach (string line in File.ReadAllLines(DataFile(), Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "klasa") { className = v; continue; }
                    int n; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) data[k] = n;
                }
            }
            catch (Exception) { }
            if (Array.IndexOf(Classes, className) < 0) className = "Brak";
        }

        private void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("klasa=" + className);
                foreach (KeyValuePair<string, int> kv in data) lines.Add(kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                File.WriteAllLines(DataFile(), lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception e) { Logger.LogWarning("Zapis rozwoju: " + e.Message); }
        }

        // ================= dzień =================

        private object Gpm()
        {
            try
            {
                object g = pGpmInstance != null ? pGpmInstance.GetValue(null, null) : null;
                return (g != null && (g as UnityEngine.Object) != null) ? g : null;
            }
            catch (Exception) { return null; }
        }

        private static int ReadProp(object o, string name)
        {
            try
            {
                PropertyInfo p = AccessTools.Property(o.GetType(), name);
                return p != null ? Convert.ToInt32(p.GetValue(o, null)) : -1;
            }
            catch (Exception) { return -1; }
        }

        internal static bool IsHost()
        {
            try
            {
                Type nmT = Type.GetType("Unity.Netcode.NetworkManager, Unity.Netcode.Runtime");
                object nm = nmT != null ? nmT.GetProperty("Singleton").GetValue(null, null) : null;
                return nm != null && (bool)nm.GetType().GetProperty("IsServer").GetValue(nm, null);
            }
            catch (Exception) { return false; }
        }

        private bool InBase()
        {
            object g = Gpm();
            if (g == null) return true;   // menu główne / lobby
            try { return !(bool)pTimeActive.GetValue(g, null); } catch (Exception) { return true; }
        }

        private void Update()
        {
            if (!cEnabled.Value) return;
            if (Time.unscaledTime >= nextCheck)
            {
                nextCheck = Time.unscaledTime + 0.3f;
                object g = Gpm();
                bool active = false;
                try { if (g != null && pTimeActive != null) active = (bool)pTimeActive.GetValue(g, null); } catch (Exception) { }
                if (!active && lastActive && g != null) DayEnded(g);
                lastActive = active;
            }
            if (Time.unscaledTime >= nextApply)
            {
                nextApply = Time.unscaledTime + 1.5f;
                ApplyPersonal();
                if (IsHost()) ApplyTeam();
            }
        }

        private void DayEnded(object gpm)
        {
            int day = -1;
            try { day = Convert.ToInt32(pCurrentDay.GetValue(gpm, null)); } catch (Exception) { }
            object dep = null;
            try { dep = pDeposit != null ? pDeposit.GetValue(gpm, null) : null; } catch (Exception) { }
            int tasks = dep != null ? ReadProp(dep, "CompletedTasks") : -1;
            int min = dep != null ? ReadProp(dep, "MinTasksCompleted") : -1;
            int gnomium = dep != null ? ReadProp(dep, "DepositedGnomium") : -1;
            bool won = tasks >= 0 && min >= 0 && tasks >= min;

            int earned = 3 + Math.Max(0, tasks) * 2 + (won ? 5 : 0) + Math.Max(0, gnomium) / 20;
            AddAcorns(earned);
            Set("dni", Get("dni") + 1);
            if (won) Set("wygrane", Get("wygrane") + 1);
            if (day > Get("maxdzien")) Set("maxdzien", day);
            if (won && tasks >= min + 2) Set("perfekcyjne", Get("perfekcyjne") + 1);
            CheckAchievements();
            Save();
            ShowToast("+" + earned + " Żołędzi (masz " + Get("zoledzie") + ")  –  F4 = rozwój", 8f);
        }

        private void AddAcorns(int n)
        {
            Set("zoledzie", Get("zoledzie") + n);
            Set("zoledzie_lacznie", Get("zoledzie_lacznie") + n);
        }

        private void CheckAchievements()
        {
            Unlock("a_first", Get("dni") >= 1);
            Unlock("a_win1", Get("wygrane") >= 1);
            Unlock("a_win10", Get("wygrane") >= 10);
            Unlock("a_play30", Get("dni") >= 30);
            Unlock("a_perfect", Get("perfekcyjne") >= 1);
            Unlock("a_rich", Get("zoledzie_lacznie") >= 200);
            Unlock("a_day10", Get("maxdzien") >= 10);
            Unlock("a_class", className != "Brak");
        }

        private void Unlock(string id, bool cond)
        {
            if (!cond || Get(id) == 1) return;
            Set(id, 1);
            AddAcorns(10);
            foreach (Achievement a in Achievements)
                if (a.Id == id) ShowToast("Osiągnięcie: " + a.Name + "  (+10 Żołędzi)", 6f);
        }

        // ================= efekty: gracz =================

        private float SpeedMul() { return 1f + 0.06f * Get("nozki") + (className == "Złodziej" ? 0.10f : 0f); }
        private float JumpMul() { return 1f + 0.08f * Get("sprezyna") + (className == "Zwiadowca" ? 0.15f : 0f); }
        private float StaminaMul()
        {
            float m = 1f - 0.10f * Get("kondycja");
            if (className == "Zwiadowca") m *= 0.8f;
            if (className == "Złodziej") m *= 0.9f;
            return Mathf.Max(0.2f, m);
        }
        private float ForceMul() { return 1f + 0.10f * Get("krzepa") + (className == "Siłacz" ? 0.30f : 0f); }
        private float FallMul() { return className == "Saper" ? 0.5f : 1f; }
        private float HookMul() { return 1f + 0.15f * Get("hak") + (className == "Saper" ? 0.30f : 0f); }

        private bool IsLocalPlayer(Component c)
        {
            if (playerType == null || pIsOwner == null) return true;
            Component p = c.GetComponentInParent(playerType);
            if (p == null) p = c.GetComponent(playerType);
            if (p == null) return false;
            try { return (bool)pIsOwner.GetValue(p, null); } catch (Exception) { return false; }
        }

        private static float GetF(FieldInfo f, object o) { return (f != null && o != null) ? (float)f.GetValue(o) : 0f; }
        private static void SetF(FieldInfo f, object o, float v) { if (f != null && o != null) f.SetValue(o, v); }

        private void ApplyPersonal()
        {
            if (normalMoveType != null)
            {
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(normalMoveType, FindObjectsSortMode.None))
                {
                    Component nm = o as Component;
                    if (nm == null || !IsLocalPlayer(nm)) continue;
                    try
                    {
                        object planar = fPlanar != null ? fPlanar.GetValue(nm) : null;
                        object vert = fVertical != null ? fVertical.GetValue(nm) : null;
                        float[] org;
                        if (!origMove.TryGetValue(nm, out org))
                        {
                            org = new float[] {
                                GetF(fBaseSpeed, planar), GetF(fBoostSpeed, planar), GetF(fJumpSpeed, vert), GetF(fJumpApex, vert),
                                GetF(fRunStamina, nm), GetF(fJumpStamina, nm), GetF(fForceMul, nm), GetF(fMaxForce, nm), GetF(fFallDmg, nm) };
                            origMove[nm] = org;
                        }
                        float s = SpeedMul(), j = JumpMul(), st = StaminaMul(), f = ForceMul();
                        SetF(fBaseSpeed, planar, org[0] * s);
                        SetF(fBoostSpeed, planar, org[1] * s);
                        SetF(fJumpSpeed, vert, org[2] * j);
                        SetF(fJumpApex, vert, org[3] * j * j);
                        SetF(fRunStamina, nm, org[4] * st);
                        SetF(fJumpStamina, nm, org[5] * st);
                        SetF(fForceMul, nm, org[6] * f);
                        SetF(fMaxForce, nm, org[7] * f);
                        SetF(fFallDmg, nm, org[8] * FallMul());
                    }
                    catch (Exception) { }
                }
            }
            if (hookType != null)
            {
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(hookType, FindObjectsSortMode.None))
                {
                    try
                    {
                        float[] org;
                        if (!origHook.TryGetValue(o, out org)) { org = new float[] { GetF(fHookRange, o), GetF(fHookSpeed, o) }; origHook[o] = org; }
                        float h = HookMul();
                        SetF(fHookRange, o, org[0] * h);
                        SetF(fHookSpeed, o, org[1] * h);
                    }
                    catch (Exception) { }
                }
            }
        }

        // ================= efekty: drużyna (host) =================

        private void ApplyTeam()
        {
            if (playerType == null || pInventory == null) return;
            int extra = Get("plecak");
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(playerType, FindObjectsSortMode.None))
            {
                try
                {
                    object inv = pInventory.GetValue(o, null);
                    if (inv == null) continue;
                    int cur = ReadInvSize(inv);
                    if (cur <= 0) continue;
                    int org;
                    if (!origInv.TryGetValue(inv, out org)) { org = cur; origInv[inv] = org; }
                    int want = org + extra;
                    if (cur != want)
                    {
                        MethodInfo m = AccessTools.Method(inv.GetType(), "ResizeInventory");
                        if (m != null) m.Invoke(inv, new object[] { want });
                    }
                }
                catch (Exception) { }
            }
        }

        private static int ReadInvSize(object inv)
        {
            try
            {
                FieldInfo f = AccessTools.Field(inv.GetType(), "n_inventorySize");
                object nv = f != null ? f.GetValue(inv) : null;
                if (nv == null) return -1;
                return Convert.ToInt32(nv.GetType().GetProperty("Value").GetValue(nv, null));
            }
            catch (Exception) { return -1; }
        }

        // Ciche kroki: host ścisza wszystkie hałasy
        private static void NoisePrefix(object[] __args)
        {
            if (Instance == null || !Instance.cEnabled.Value || __args == null || __args.Length < 2) return;
            int lvl = Instance.Get("cisza");
            if (lvl <= 0) return;
            try
            {
                int loud = Convert.ToInt32(__args[1]);
                __args[1] = Mathf.Max(0, Mathf.RoundToInt(loud * (1f - 0.10f * lvl)));
            }
            catch (Exception) { }
        }

        // ================= UI =================

        private static int Cost(int nextLevel) { return 5 * nextLevel; }

        private void ShowToast(string t, float s) { toast = t; toastUntil = Time.unscaledTime + s; }

        private void OnGUI()
        {
            if (!cEnabled.Value) return;
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F4) { ev.Use(); showWindow = !showWindow; }

            if (small == null)
            {
                small = new GUIStyle(GUI.skin.label); small.fontSize = 12; small.wordWrap = true;
                bold = new GUIStyle(GUI.skin.label); bold.fontSize = 14;
            }
            if (Time.unscaledTime < toastUntil && toast.Length > 0)
                GUI.Box(new Rect(Screen.width / 2f - 260f, Screen.height - 120f, 520f, 36f), toast);

            if (showWindow) window = GUI.Window(0x726F7A77, window, DrawWindow, "Rozwój gnoma (F4 - zamknij)");
        }

        private void LateUpdate()
        {
            if (showWindow) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("Żołędzie: " + Get("zoledzie") + "      Klasa: " + className + "      Dni: " + Get("dni") + " (wygrane " + Get("wygrane") + ")", bold);
            tab = GUILayout.Toolbar(tab, new string[] { "Klasa", "Umiejętności", "Drużyna", "Osiągnięcia" });
            scroll = GUILayout.BeginScrollView(scroll);

            if (tab == 0)
            {
                bool canChange = InBase();
                if (!canChange) GUILayout.Label("Klasę można zmienić tylko w bazie.", small);
                for (int i = 0; i < Classes.Length; i++)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label((className == Classes[i] ? "► " : "") + Classes[i], bold, GUILayout.Width(160));
                    GUI.enabled = canChange && className != Classes[i];
                    if (GUILayout.Button("Wybierz", GUILayout.Width(90)))
                    {
                        className = Classes[i]; CheckAchievements(); Save();
                        nextApply = 0f;
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    GUILayout.Label(ClassDesc[i], small);
                    GUILayout.EndVertical();
                }
            }
            else if (tab == 1 || tab == 2)
            {
                bool team = tab == 2;
                if (team) GUILayout.Label("Ulepszenia drużyny kupuje i wykorzystuje HOST – działają na wszystkich graczy.", small);
                bool host = IsHost();
                foreach (Skill s in Skills)
                {
                    if (s.Team != team) continue;
                    int lvl = Get(s.Id);
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(s.Name + "  [" + lvl + "/" + s.Max + "]", bold, GUILayout.Width(260));
                    if (lvl < s.Max)
                    {
                        int cost = Cost(lvl + 1);
                        GUI.enabled = Get("zoledzie") >= cost && (!team || host);
                        if (GUILayout.Button("Kup (" + cost + ")", GUILayout.Width(110)))
                        {
                            Set("zoledzie", Get("zoledzie") - cost);
                            Set(s.Id, lvl + 1);
                            Save();
                            nextApply = 0f;
                        }
                        GUI.enabled = true;
                    }
                    else GUILayout.Label("MAKS", GUILayout.Width(110));
                    GUILayout.EndHorizontal();
                    GUILayout.Label(s.Desc, small);
                    GUILayout.EndVertical();
                }
                if (team && !host) GUILayout.Label("Nie jesteś hostem – te ulepszenia kupuje gospodarz gry.", small);
            }
            else
            {
                foreach (Achievement a in Achievements)
                {
                    bool got = Get(a.Id) == 1;
                    GUILayout.Label((got ? "[x] " : "[  ] ") + a.Name + " – " + a.Desc, got ? bold : small);
                }
                GUILayout.Space(6);
                GUILayout.Label("Każde osiągnięcie daje +10 Żołędzi.", small);
            }

            GUILayout.EndScrollView();
            if (GUILayout.Button("Zamknij")) showWindow = false;
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

    }
}

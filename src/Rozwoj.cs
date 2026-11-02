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
    [BepInPlugin("pl.krystian.rozwoj", "Rozwój gnoma", "1.1.0")]
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

        // Wywoływane z Tablicy gnoma (i innych modów) przez refleksję.
        public static void OpenTab(int t)
        {
            if (Instance == null) return;
            Instance.tab = Mathf.Clamp(t, 0, 3);
            Instance.showWindow = true;
            Instance.centerWindow = true;
        }
        public static bool IsOpen() { return Instance != null && Instance.showWindow; }

        // ---------- wygląd ----------
        private bool centerWindow = true;
        private GUIStyle sPanel, sTitle, sHeader, sText, sTextDim, sCard, sCardSel, sBtn, sBtnBig, sTab, sTabSel, sPips, sToast;
        private static readonly Color cBg = new Color(0.14f, 0.10f, 0.07f, 0.97f);
        private static readonly Color cCard = new Color(0.25f, 0.18f, 0.12f, 1f);
        private static readonly Color cCardSel = new Color(0.22f, 0.36f, 0.18f, 1f);
        private static readonly Color cBtn = new Color(0.55f, 0.38f, 0.16f, 1f);
        private static readonly Color cBtnHover = new Color(0.72f, 0.52f, 0.22f, 1f);
        private static readonly Color cGold = new Color(1f, 0.82f, 0.38f, 1f);
        private static readonly Color cCream = new Color(0.97f, 0.93f, 0.84f, 1f);
        private static readonly Color[] ClassColors = {
            new Color(0.45f, 0.45f, 0.45f), new Color(0.50f, 0.30f, 0.62f), new Color(0.72f, 0.24f, 0.20f),
            new Color(0.28f, 0.58f, 0.30f), new Color(0.85f, 0.52f, 0.15f) };

        private static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private static GUIStyle Box(Color bg, int pad)
        {
            GUIStyle g = new GUIStyle();
            g.normal.background = Tex(bg);
            g.padding = new RectOffset(pad, pad, pad, pad);
            g.margin = new RectOffset(4, 4, 4, 4);
            return g;
        }

        private static GUIStyle Label(int size, Color col, bool boldText, TextAnchor align)
        {
            GUIStyle g = new GUIStyle();
            g.fontSize = size;
            g.normal.textColor = col;
            g.wordWrap = true;
            g.alignment = align;
            g.richText = true;
            if (boldText) g.fontStyle = FontStyle.Bold;
            g.padding = new RectOffset(2, 2, 2, 2);
            return g;
        }

        private static GUIStyle Button(int size, Color normal, Color hover)
        {
            GUIStyle g = new GUIStyle();
            g.fontSize = size;
            g.fontStyle = FontStyle.Bold;
            g.alignment = TextAnchor.MiddleCenter;
            g.normal.background = Tex(normal); g.normal.textColor = cCream;
            g.hover.background = Tex(hover); g.hover.textColor = Color.white;
            g.active.background = Tex(hover * 0.85f); g.active.textColor = Color.white;
            g.padding = new RectOffset(10, 10, 8, 8);
            g.margin = new RectOffset(4, 4, 4, 4);
            return g;
        }

        private void EnsureStyles()
        {
            if (sPanel != null) return;
            sPanel = Box(cBg, 14);
            sTitle = Label(26, cGold, true, TextAnchor.MiddleLeft);
            sHeader = Label(18, cCream, true, TextAnchor.MiddleLeft);
            sText = Label(14, cCream, false, TextAnchor.UpperLeft);
            sTextDim = Label(13, new Color(0.8f, 0.74f, 0.62f), false, TextAnchor.UpperLeft);
            sPips = Label(18, cGold, false, TextAnchor.MiddleLeft);
            sCard = Box(cCard, 10);
            sCardSel = Box(cCardSel, 10);
            sBtn = Button(14, cBtn, cBtnHover);
            sBtnBig = Button(17, new Color(0.30f, 0.52f, 0.24f), new Color(0.40f, 0.66f, 0.32f));
            sTab = Button(15, new Color(0.22f, 0.16f, 0.11f), new Color(0.38f, 0.27f, 0.16f));
            sTabSel = Button(15, cBtn, cBtnHover);
            sToast = Box(new Color(0.12f, 0.09f, 0.06f, 0.92f), 10);
            sToast.fontSize = 16; sToast.alignment = TextAnchor.MiddleCenter; sToast.normal.textColor = cGold; sToast.fontStyle = FontStyle.Bold;
        }

        private void OnGUI()
        {
            if (!cEnabled.Value) return;
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F4) { ev.Use(); showWindow = !showWindow; centerWindow = true; }
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape && showWindow) { ev.Use(); showWindow = false; }

            EnsureStyles();
            if (Time.unscaledTime < toastUntil && toast.Length > 0)
                GUI.Box(new Rect(Screen.width / 2f - 300f, Screen.height - 130f, 600f, 44f), toast, sToast);

            if (!showWindow) return;
            float w = Mathf.Min(900f, Screen.width - 40f), h = Mathf.Min(600f, Screen.height - 40f);
            if (centerWindow) { window = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h); centerWindow = false; }
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            window = GUI.Window(0x726F7A77, window, DrawWindow, GUIContent.none, sPanel);
        }

        private void LateUpdate()
        {
            if (showWindow) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        private static string Pips(int lvl, int max)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < max; i++) b.Append(i < lvl ? "● " : "○ ");
            return b.ToString();
        }

        private void DrawWindow(int id)
        {
            // nagłówek
            GUILayout.BeginHorizontal();
            GUILayout.Label("ROZWÓJ GNOMA", sTitle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("<color=#FFD166>" + Get("zoledzie") + "</color> Żołędzi", sHeader, GUILayout.Width(170));
            if (GUILayout.Button("X", sBtn, GUILayout.Width(40), GUILayout.Height(34))) showWindow = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Klasa: <b>" + className + "</b>    •    Dni: " + Get("dni") + "    •    Wygrane: " + Get("wygrane"), sTextDim);
            GUILayout.Space(6);

            // zakładki
            string[] tabs = { "Klasa", "Umiejętności", "Drużyna", "Osiągnięcia" };
            GUILayout.BeginHorizontal();
            for (int i = 0; i < tabs.Length; i++)
                if (GUILayout.Button(tabs[i], i == tab ? sTabSel : sTab, GUILayout.Height(38))) { tab = i; scroll = Vector2.zero; }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            scroll = GUILayout.BeginScrollView(scroll, GUIStyle.none, GUI.skin.verticalScrollbar);
            if (tab == 0) DrawClasses();
            else if (tab == 1) DrawSkills(false);
            else if (tab == 2) DrawSkills(true);
            else DrawAchievements();
            GUILayout.EndScrollView();

            GUILayout.Label("Esc / F4 – zamknij    •    Okno możesz przesuwać, łapiąc za górę", sTextDim);
            GUI.DragWindow(new Rect(0, 0, 10000, 60));
        }

        private void DrawClasses()
        {
            bool canChange = InBase();
            if (!canChange) GUILayout.Label("<color=#FF9966>Klasę można zmienić tylko w bazie.</color>", sText);
            GUILayout.BeginHorizontal();
            for (int i = 1; i < Classes.Length; i++)
            {
                bool sel = className == Classes[i];
                GUILayout.BeginVertical(sel ? sCardSel : sCard, GUILayout.MinHeight(300));
                GUI.color = ClassColors[i];
                GUILayout.Box(GUIContent.none, sCard, GUILayout.Height(10), GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
                GUILayout.Label(Classes[i].ToUpper(), sHeader);
                GUILayout.Space(4);
                GUILayout.Label(ClassDesc[i], sText);
                GUILayout.FlexibleSpace();
                GUI.enabled = canChange && !sel;
                if (GUILayout.Button(sel ? "WYBRANA" : "WYBIERZ", sel ? sBtn : sBtnBig, GUILayout.Height(42)))
                {
                    className = Classes[i]; CheckAchievements(); Save(); nextApply = 0f;
                    ShowToast("Jesteś teraz: " + className, 4f);
                }
                GUI.enabled = true;
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            GUI.enabled = canChange && className != "Brak";
            if (GUILayout.Button("Bez klasy", sBtn, GUILayout.Width(160))) { className = "Brak"; Save(); nextApply = 0f; }
            GUI.enabled = true;
        }

        private void DrawSkills(bool team)
        {
            bool host = IsHost();
            if (team)
                GUILayout.Label(host ? "Ulepszenia drużyny kupujesz Ty (host) – działają na wszystkich graczy."
                                     : "<color=#FF9966>Ulepszenia drużyny kupuje host gry.</color>", sText);
            foreach (Skill s in Skills)
            {
                if (s.Team != team) continue;
                int lvl = Get(s.Id);
                GUILayout.BeginHorizontal(sCard);
                GUILayout.BeginVertical();
                GUILayout.Label(s.Name, sHeader);
                GUILayout.Label(s.Desc, sTextDim);
                GUILayout.Label(Pips(lvl, s.Max), sPips);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (lvl < s.Max)
                {
                    int cost = Cost(lvl + 1);
                    GUI.enabled = Get("zoledzie") >= cost && (!team || host);
                    if (GUILayout.Button("KUP\n" + cost + " Żołędzi", sBtnBig, GUILayout.Width(150), GUILayout.Height(64)))
                    {
                        Set("zoledzie", Get("zoledzie") - cost);
                        Set(s.Id, lvl + 1);
                        Save();
                        nextApply = 0f;
                        ShowToast(s.Name + " – poziom " + (lvl + 1), 3f);
                    }
                    GUI.enabled = true;
                }
                else GUILayout.Label("<color=#FFD166>MAKS</color>", sHeader, GUILayout.Width(150));
                GUILayout.EndHorizontal();
            }
        }

        private void DrawAchievements()
        {
            int got = 0;
            foreach (Achievement a in Achievements) if (Get(a.Id) == 1) got++;
            GUILayout.Label("Zdobyte: " + got + " / " + Achievements.Length + "   (każde daje +10 Żołędzi)", sText);
            for (int i = 0; i < Achievements.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = i; k < i + 2 && k < Achievements.Length; k++)
                {
                    Achievement a = Achievements[k];
                    bool have = Get(a.Id) == 1;
                    GUILayout.BeginVertical(have ? sCardSel : sCard, GUILayout.Width(Mathf.Max(200f, window.width / 2f - 40f)));
                    GUILayout.Label((have ? "<color=#FFD166>★</color> " : "<color=#776655>☆</color> ") + a.Name, sHeader);
                    GUILayout.Label(a.Desc, have ? sText : sTextDim);
                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
            }
        }
    }
}

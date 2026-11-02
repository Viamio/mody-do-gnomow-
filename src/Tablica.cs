// Tablica - tablica gnoma w bazie (Burglin' Gnomes, BepInEx 5)
//
// W bazie stoi drewniana tablica. Podejdź, spójrz na nią i naciśnij E – otworzy się menu:
// Klasa, Umiejętności, Drużyna, Osiągnięcia, Dziennik, Radio, Mody.
// Pierwszy raz tablica staje przed Tobą; w menu możesz ją przestawić i zmienić jej rozmiar.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace Tablica
{
    [BepInPlugin("pl.krystian.tablica", "Tablica gnoma", "1.0.0")]
    public class TablicaPlugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> cEnabled;
        private ConfigEntry<string> cOffset;
        private ConfigEntry<float> cScale;
        private ConfigEntry<float> cYaw;

        private PropertyInfo pGpmInstance, pTimeActive, pCurrentHome;
        private Type homeManagerType;
        private Component homeManager;
        private float nextHomeSearch, nextState;
        private bool inBase;

        private GameObject board;
        private Collider boardCollider;
        private bool lookingAt;
        private bool menuOpen;
        private Rect window;
        private bool centerWindow = true;

        // wygląd
        private GUIStyle sPanel, sTitle, sTile, sTileTitle, sTileText, sBtn, sPrompt, sDim;
        private static readonly Color cBg = new Color(0.14f, 0.10f, 0.07f, 0.97f);
        private static readonly Color cGold = new Color(1f, 0.82f, 0.38f, 1f);
        private static readonly Color cCream = new Color(0.97f, 0.93f, 0.84f, 1f);

        private void Awake()
        {
            cEnabled = Config.Bind("General", "Enabled", true, "Pokazuj tablice gnoma w bazie.");
            cOffset = Config.Bind("General", "Offset", "", "Pozycja tablicy wzgledem domu (ustawiana z menu tablicy).");
            cScale = Config.Bind("General", "Scale", 1f, "Rozmiar tablicy.");
            cYaw = Config.Bind("General", "Rotation", 0f, "Obrot tablicy (stopnie, wzgledem domu).");

            Type gpm = FindType("GameProgressionManager");
            if (gpm != null)
            {
                pGpmInstance = gpm.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                pTimeActive = gpm.GetProperty("TimeActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            homeManagerType = FindType("HomeManager");
            if (homeManagerType != null)
                pCurrentHome = homeManagerType.GetProperty("CurrentHome", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Logger.LogInfo("Tablica gnoma zaladowana. Podejdz do tablicy w bazie i nacisnij E.");
        }

        // ================= stan gry =================

        private bool IsInBase()
        {
            try
            {
                object g = pGpmInstance != null ? pGpmInstance.GetValue(null, null) : null;
                if (g == null || (g as UnityEngine.Object) == null) return false;
                return pTimeActive == null || !(bool)pTimeActive.GetValue(g, null);
            }
            catch (Exception) { return false; }
        }

        private Transform HomeTransform()
        {
            if (homeManagerType == null) return null;
            if (homeManager == null && Time.unscaledTime >= nextHomeSearch)
            {
                nextHomeSearch = Time.unscaledTime + 2f;
                homeManager = UnityEngine.Object.FindAnyObjectByType(homeManagerType) as Component;
            }
            if (homeManager == null) return null;
            try
            {
                Component home = pCurrentHome != null ? pCurrentHome.GetValue(homeManager, null) as Component : null;
                return home != null ? home.transform : homeManager.transform;
            }
            catch (Exception) { return homeManager.transform; }
        }

        // ================= tablica w świecie =================

        private static Material MakeMat(Color c)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (sh == null) sh = Shader.Find("Standard");
            Material m = sh != null ? new Material(sh) : new Material(Shader.Find("Sprites/Default"));
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            m.color = c;
            return m;
        }

        private GameObject Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, bool keepCollider)
        {
            GameObject g = GameObject.CreatePrimitive(type);
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos;
            g.transform.localScale = scale;
            g.GetComponent<Renderer>().sharedMaterial = mat;
            if (!keepCollider) Destroy(g.GetComponent<Collider>());
            return g;
        }

        private void BuildBoard()
        {
            board = new GameObject("TablicaGnoma");
            DontDestroyOnLoad(board);
            Material wood = MakeMat(new Color(0.45f, 0.29f, 0.15f));
            Material dark = MakeMat(new Color(0.28f, 0.17f, 0.08f));
            Material paper = MakeMat(new Color(0.93f, 0.88f, 0.74f));
            Material gold = MakeMat(new Color(0.95f, 0.75f, 0.25f));

            Transform t = board.transform;
            Part(t, PrimitiveType.Cube, new Vector3(-0.42f, 0.45f, 0f), new Vector3(0.06f, 0.9f, 0.06f), dark, false);   // nogi
            Part(t, PrimitiveType.Cube, new Vector3(0.42f, 0.45f, 0f), new Vector3(0.06f, 0.9f, 0.06f), dark, false);
            GameObject plank = Part(t, PrimitiveType.Cube, new Vector3(0f, 0.75f, 0f), new Vector3(0.95f, 0.6f, 0.05f), wood, true);
            boardCollider = plank.GetComponent<Collider>();
            Part(t, PrimitiveType.Cube, new Vector3(0f, 1.08f, 0f), new Vector3(1.02f, 0.07f, 0.08f), dark, false);      // daszek
            // kartki
            Part(t, PrimitiveType.Cube, new Vector3(-0.25f, 0.70f, -0.03f), new Vector3(0.28f, 0.32f, 0.01f), paper, false);
            Part(t, PrimitiveType.Cube, new Vector3(0.10f, 0.66f, -0.03f), new Vector3(0.24f, 0.26f, 0.01f), paper, false);
            Part(t, PrimitiveType.Cube, new Vector3(0.32f, 0.78f, -0.03f), new Vector3(0.16f, 0.2f, 0.01f), paper, false);
            Part(t, PrimitiveType.Sphere, new Vector3(-0.25f, 0.85f, -0.04f), Vector3.one * 0.03f, gold, false);          // pinezki
            Part(t, PrimitiveType.Sphere, new Vector3(0.10f, 0.78f, -0.04f), Vector3.one * 0.03f, gold, false);
            Part(t, PrimitiveType.Sphere, new Vector3(0.32f, 0.87f, -0.04f), Vector3.one * 0.03f, gold, false);

            // napis
            try
            {
                GameObject txt = new GameObject("Napis");
                txt.transform.SetParent(t, false);
                txt.transform.localPosition = new Vector3(0f, 0.98f, -0.04f);
                TextMesh tm = txt.AddComponent<TextMesh>();
                Font f = null;
                try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
                if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
                if (f != null) { tm.font = f; txt.GetComponent<MeshRenderer>().sharedMaterial = f.material; }
                tm.text = "TABLICA GNOMA";
                tm.anchor = TextAnchor.MiddleCenter;
                tm.characterSize = 0.012f;
                tm.fontSize = 64;
                tm.color = new Color(1f, 0.85f, 0.4f);
                txt.transform.localRotation = Quaternion.identity;
            }
            catch (Exception e) { Logger.LogWarning("Napis na tablicy: " + e.Message); }
            board.SetActive(false);
        }

        private bool PlaceInFront(Transform home)
        {
            Camera cam = Camera.main;
            if (cam == null || home == null) return false;
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 p = cam.transform.position + fwd * 1.5f * Mathf.Max(0.3f, cScale.Value);
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore)
                && (boardCollider == null || hit.collider == null || !hit.collider.transform.IsChildOf(board.transform)))
                p = hit.point;
            Vector3 local = home.InverseTransformPoint(p);
            cOffset.Value = local.x.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                            local.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                            local.z.ToString("0.###", CultureInfo.InvariantCulture);
            // tablica przodem do gracza
            Quaternion face = Quaternion.LookRotation(fwd);
            cYaw.Value = (Quaternion.Inverse(home.rotation) * face).eulerAngles.y;
            return true;
        }

        private static bool TryParseVector(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(s)) return false;
            string[] p = s.Split(',');
            float x, y, z;
            if (p.Length != 3 ||
                !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        private void Update()
        {
            if (Time.unscaledTime >= nextState) { nextState = Time.unscaledTime + 0.5f; inBase = IsInBase(); }

            Transform home = (cEnabled.Value && inBase) ? HomeTransform() : null;
            if (home == null)
            {
                if (board != null && board.activeSelf) board.SetActive(false);
                lookingAt = false;
                if (menuOpen && !inBase) menuOpen = false;
                return;
            }
            if (board == null) BuildBoard();

            Vector3 off;
            if (!TryParseVector(cOffset.Value, out off))
            {
                if (!PlaceInFront(home)) return;
                TryParseVector(cOffset.Value, out off);
            }
            if (!board.activeSelf) board.SetActive(true);
            board.transform.position = home.TransformPoint(off);
            board.transform.rotation = home.rotation * Quaternion.Euler(0f, cYaw.Value, 0f);
            board.transform.localScale = Vector3.one * Mathf.Clamp(cScale.Value, 0.1f, 10f);

            // czy gracz patrzy na tablicę z bliska
            lookingAt = false;
            Camera cam = Camera.main;
            if (cam != null && boardCollider != null)
            {
                float reach = 2.5f * Mathf.Max(0.3f, cScale.Value);
                Ray r = new Ray(cam.transform.position, cam.transform.forward);
                RaycastHit hit;
                if (boardCollider.Raycast(r, out hit, reach)) lookingAt = true;
                else
                {
                    Vector3 d = boardCollider.bounds.center - cam.transform.position;
                    if (d.magnitude < reach && Vector3.Angle(cam.transform.forward, d) < 20f) lookingAt = true;
                }
            }
        }

        private void LateUpdate()
        {
            if (menuOpen) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        // ================= wywołania innych modów =================

        private static Type PluginType(string guid)
        {
            PluginInfo pi;
            if (Chainloader.PluginInfos.TryGetValue(guid, out pi) && pi != null && pi.Instance != null) return pi.Instance.GetType();
            return null;
        }

        private static bool Has(string guid) { return PluginType(guid) != null; }

        private static object CallStatic(string guid, string method, params object[] args)
        {
            try
            {
                Type t = PluginType(guid);
                if (t == null) return null;
                MethodInfo m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                return m != null ? m.Invoke(null, args) : null;
            }
            catch (Exception) { return null; }
        }

        // ================= menu =================

        private static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); t.hideFlags = HideFlags.HideAndDontSave; return t;
        }

        private static GUIStyle Lbl(int size, Color col, bool b, TextAnchor a)
        {
            GUIStyle g = new GUIStyle(); g.fontSize = size; g.normal.textColor = col; g.wordWrap = true; g.richText = true;
            g.alignment = a; if (b) g.fontStyle = FontStyle.Bold; g.padding = new RectOffset(2, 2, 2, 2); return g;
        }

        private void EnsureStyles()
        {
            if (sPanel != null) return;
            sPanel = new GUIStyle(); sPanel.normal.background = Tex(cBg); sPanel.padding = new RectOffset(16, 16, 14, 14);
            sTitle = Lbl(28, cGold, true, TextAnchor.MiddleLeft);
            sDim = Lbl(13, new Color(0.8f, 0.74f, 0.62f), false, TextAnchor.MiddleLeft);
            sTileTitle = Lbl(20, cCream, true, TextAnchor.UpperLeft);
            sTileText = Lbl(13, new Color(0.85f, 0.79f, 0.66f), false, TextAnchor.UpperLeft);

            sTile = new GUIStyle();
            sTile.normal.background = Tex(new Color(0.27f, 0.19f, 0.12f));
            sTile.hover.background = Tex(new Color(0.40f, 0.28f, 0.16f));
            sTile.active.background = Tex(new Color(0.48f, 0.34f, 0.18f));
            sTile.margin = new RectOffset(6, 6, 6, 6);

            sBtn = new GUIStyle(); sBtn.fontSize = 14; sBtn.fontStyle = FontStyle.Bold; sBtn.alignment = TextAnchor.MiddleCenter;
            sBtn.normal.background = Tex(new Color(0.55f, 0.38f, 0.16f)); sBtn.normal.textColor = cCream;
            sBtn.hover.background = Tex(new Color(0.72f, 0.52f, 0.22f)); sBtn.hover.textColor = Color.white;
            sBtn.padding = new RectOffset(10, 10, 8, 8); sBtn.margin = new RectOffset(4, 4, 4, 4);

            sPrompt = new GUIStyle(); sPrompt.normal.background = Tex(new Color(0.1f, 0.07f, 0.05f, 0.85f));
            sPrompt.normal.textColor = cGold; sPrompt.fontSize = 18; sPrompt.fontStyle = FontStyle.Bold;
            sPrompt.alignment = TextAnchor.MiddleCenter;
        }

        private void OnGUI()
        {
            if (!cEnabled.Value) return;
            EnsureStyles();
            Event ev = Event.current;

            if (!menuOpen && lookingAt && board != null && board.activeSelf)
            {
                GUI.Box(new Rect(Screen.width / 2f - 150f, Screen.height / 2f + 60f, 300f, 40f), "[E]  Tablica gnoma", sPrompt);
                if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.E) { ev.Use(); menuOpen = true; centerWindow = true; }
            }
            if (!menuOpen) return;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape) { ev.Use(); menuOpen = false; return; }

            float w = Mathf.Min(860f, Screen.width - 40f), h = Mathf.Min(560f, Screen.height - 40f);
            if (centerWindow) { window = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h); centerWindow = false; }
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            window = GUI.Window(0x7461626C, window, DrawMenu, GUIContent.none, sPanel);
        }

        private bool Tile(string title, string text, bool enabled, float width, float height)
        {
            GUI.enabled = enabled;
            Rect r = GUILayoutUtility.GetRect(width, height, sTile, GUILayout.Width(width), GUILayout.Height(height));
            bool clicked = GUI.Button(r, GUIContent.none, sTile);
            GUI.Label(new Rect(r.x + 14, r.y + 12, r.width - 28, 30), title, sTileTitle);
            GUI.Label(new Rect(r.x + 14, r.y + 46, r.width - 28, r.height - 56), enabled ? text : "<i>Mod nie jest wczytany</i>", sTileText);
            GUI.enabled = true;
            return clicked && enabled;
        }

        private void DrawMenu(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("TABLICA GNOMA", sTitle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", sBtn, GUILayout.Width(40), GUILayout.Height(34))) menuOpen = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Wybierz, co chcesz zrobić. Esc – zamknij.", sDim);
            GUILayout.Space(8);

            float tw = (window.width - 32f - 36f) / 3f, th = 120f;
            bool rozwoj = Has("pl.krystian.rozwoj"), dziennik = Has("pl.krystian.dziennik"), radio = Has("pl.krystian.basemusic");

            GUILayout.BeginHorizontal();
            if (Tile("Klasa", "Złodziej, Siłacz, Zwiadowca czy Saper? Wybierz styl gry.", rozwoj, tw, th)) Open("pl.krystian.rozwoj", 0);
            if (Tile("Umiejętności", "Wydaj Żołędzie na szybkość, skok, kondycję, siłę i hak.", rozwoj, tw, th)) Open("pl.krystian.rozwoj", 1);
            if (Tile("Drużyna", "Plecak i ciche kroki dla całej ekipy (kupuje host).", rozwoj, tw, th)) Open("pl.krystian.rozwoj", 2);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (Tile("Osiągnięcia", "Tytuły i nagrody za Wasze wyczyny.", rozwoj, tw, th)) Open("pl.krystian.rozwoj", 3);
            if (Tile("Dziennik i statystyki", "Historia dni, wyniki, kto ile ukradł i ile razy zginął.", dziennik, tw, th))
            { menuOpen = false; CallStatic("pl.krystian.dziennik", "Open"); }
            string st = radio ? (CallStatic("pl.krystian.basemusic", "Status") as string ?? "") : "";
            if (Tile("Radio", "Teraz: " + st + "\nKliknij: następna stacja / utwór.", radio, tw, th))
                CallStatic("pl.krystian.basemusic", "NextStation");
            GUILayout.EndHorizontal();

            if (radio)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Radio:", sDim, GUILayout.Width(60));
                if (GUILayout.Button("Pauza / graj", sBtn)) CallStatic("pl.krystian.basemusic", "TogglePause");
                if (GUILayout.Button("Radio / pliki", sBtn)) CallStatic("pl.krystian.basemusic", "ToggleSource");
                if (GUILayout.Button("Postaw radio przy mnie", sBtn)) { CallStatic("pl.krystian.basemusic", "PlaceRadioHere"); menuOpen = false; }
                GUILayout.EndHorizontal();
            }

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (Has("pl.krystian.modmenu") && GUILayout.Button("Wszystkie mody (F1)", sBtn)) { menuOpen = false; CallStatic("pl.krystian.modmenu", "Open"); }
            if (GUILayout.Button("Przestaw tablicę tutaj", sBtn)) { menuOpen = false; PlaceInFront(HomeTransform()); }
            if (GUILayout.Button("Tablica mniejsza", sBtn)) cScale.Value = Mathf.Max(0.2f, cScale.Value * 0.8f);
            if (GUILayout.Button("Tablica większa", sBtn)) cScale.Value = Mathf.Min(8f, cScale.Value * 1.25f);
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 50));
        }

        private void Open(string guid, int tab)
        {
            menuOpen = false;
            CallStatic(guid, "OpenTab", tab);
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(name, false); if (t != null) return t; } catch (Exception) { }
            }
            return null;
        }
    }
}

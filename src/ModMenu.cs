// ModMenu - prosta zakładka "MODY" do Burglin' Gnomes (BepInEx 5)
//
// - W menu głównym (scena MainMenu) w lewym górnym rogu pojawia się przycisk "MODY".
// - W każdej chwili (też w trakcie gry) panel otwiera/zamyka klawisz F1.
// - Lista pokazuje wszystkie mody BepInEx z folderu BepInEx\plugins.
//     * "Działa teraz"  – przełącza ustawienie Enabled moda od razu
//                         (tylko mody, które mają takie ustawienie, np. Keep Progress On Loss).
//     * "Ładuj przy starcie" – włącza/wyłącza plik moda; zmiana zadziała po ponownym
//                         uruchomieniu gry (plik .dll zmienia nazwę na .dll.off przy wyjściu z gry).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ModMenu
{
    [BepInPlugin("pl.krystian.modmenu", "Mod Menu", "1.2.0")]
    public class ModMenuPlugin : BaseUnityPlugin
    {
        private class ModRow
        {
            public string Name;
            public string Version;
            public string DllPath;      // aktualna ścieżka pliku (.dll albo .dll.off)
            public bool LoadedNow;      // czy mod jest wczytany w tej sesji
            public bool LoadOnStart;    // stan docelowy po restarcie
            public bool OriginalLoadOnStart;
            public ConfigEntry<bool> EnabledEntry; // ustawienie "Enabled" (jeśli mod je ma)
            public string Key;          // nazwa pliku moda (do zapisu własnej nazwy)
        }

        // Własne nazwy modów: BepInEx\config\ModMenu.names.txt  (plik.dll<TAB>nazwa)
        private readonly Dictionary<string, string> customNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private ModRow editing;
        private string editText = "";

        // Aktualizacje z GitHuba
        private const string RepoName = "Viamio/mody-do-gnomow-";
        private string localVersion = "";
        private volatile string remoteVersion;   // null = jeszcze nie wiadomo / brak internetu
        private volatile bool versionChecked;
        private bool confirmUpdate;

        private readonly List<ModRow> rows = new List<ModRow>();
        private bool open;
        private Rect window = new Rect(60, 60, 560, 460);
        private Vector2 scroll;
        private bool built;
        private bool cursorWasVisible;
        private CursorLockMode cursorWasLock;
        private GUIStyle smallStyle;

        private void Awake()
        {
            Application.quitting += ApplyPendingChanges;
            LoadNames();
            CheckForUpdate();
            Logger.LogInfo("Mod Menu zaladowany. F1 = lista modow.");
        }

        private void BuildList()
        {
            rows.Clear();
            foreach (KeyValuePair<string, PluginInfo> kv in Chainloader.PluginInfos)
            {
                PluginInfo pi = kv.Value;
                if (pi == null || pi.Metadata == null) continue;
                if (pi.Metadata.GUID == "pl.krystian.modmenu") continue;

                ModRow r = new ModRow();
                r.Name = pi.Metadata.Name;
                r.Version = pi.Metadata.Version != null ? pi.Metadata.Version.ToString() : "";
                r.DllPath = pi.Location;
                r.Key = string.IsNullOrEmpty(pi.Location) ? pi.Metadata.GUID : Path.GetFileName(pi.Location);
                r.LoadedNow = true;
                r.LoadOnStart = true;
                r.OriginalLoadOnStart = true;
                r.EnabledEntry = FindEnabledEntry(pi.Instance);
                rows.Add(r);
            }

            // Mody wyłączone wcześniej (pliki .dll.off)
            try
            {
                string dir = Paths.PluginPath;
                if (Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir, "*.dll.off", SearchOption.AllDirectories))
                    {
                        ModRow r = new ModRow();
                        string file = Path.GetFileName(f);
                        r.Name = file.Substring(0, file.Length - ".dll.off".Length);
                        r.Version = "";
                        r.DllPath = f;
                        r.Key = file.Substring(0, file.Length - ".off".Length);
                        r.LoadedNow = false;
                        r.LoadOnStart = false;
                        r.OriginalLoadOnStart = false;
                        rows.Add(r);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Nie udalo sie przejrzec folderu plugins: " + e.Message);
            }

            rows.Sort(delegate (ModRow a, ModRow b) { return string.Compare(DisplayName(a), DisplayName(b), StringComparison.OrdinalIgnoreCase); });
            built = true;
        }

        private static ConfigEntry<bool> FindEnabledEntry(BaseUnityPlugin plugin)
        {
            if (plugin == null || plugin.Config == null) return null;
            try
            {
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in plugin.Config)
                {
                    if (kv.Value is ConfigEntry<bool>)
                    {
                        string key = kv.Key.Key.ToLowerInvariant();
                        if (key == "enabled" || key == "enable" || key == "włączony")
                            return (ConfigEntry<bool>)kv.Value;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private bool IsMainMenu()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).name == "MainMenu") return true;
            }
            return false;
        }

        private void SetOpen(bool value)
        {
            if (value == open) return;
            open = value;
            if (open)
            {
                if (!built) BuildList();
                cursorWasVisible = Cursor.visible;
                cursorWasLock = Cursor.lockState;
            }
            else
            {
                Cursor.visible = cursorWasVisible;
                Cursor.lockState = cursorWasLock;
            }
        }

        private void LateUpdate()
        {
            if (open)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private void OnGUI()
        {
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F1)
            {
                SetOpen(!open);
                ev.Use();
            }

            if (!open && IsMainMenu())
            {
                if (GUI.Button(new Rect(20, 20, 110, 36), "MODY"))
                    SetOpen(true);
            }

            if (open)
            {
                if (smallStyle == null)
                {
                    smallStyle = new GUIStyle(GUI.skin.label);
                    smallStyle.fontSize = 11;
                    smallStyle.wordWrap = true;
                }
                GUI.depth = -1000;
                window = GUI.Window(0x6D6F6479, window, DrawWindow, "Mody (F1 - zamknij)");
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(4);
            GUILayout.Label("\"Działa teraz\" zmienia się od razu. \"Ładuj przy starcie\" zadziała po ponownym uruchomieniu gry.", smallStyle);
            GUILayout.Space(4);

            scroll = GUILayout.BeginScrollView(scroll);
            if (rows.Count == 0)
                GUILayout.Label("Brak modów w BepInEx\\plugins.");

            bool anyPending = false;
            foreach (ModRow r in rows)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                if (editing == r)
                {
                    GUILayout.BeginHorizontal();
                    GUI.SetNextControlName("ModMenuRename");
                    editText = GUILayout.TextField(editText, 60, GUILayout.MinWidth(220));
                    if (GUILayout.Button("Zapisz", GUILayout.Width(70))) { SetCustomName(r, editText); editing = null; }
                    if (GUILayout.Button("Anuluj", GUILayout.Width(70))) editing = null;
                    if (GUILayout.Button("Domyślna", GUILayout.Width(80))) { SetCustomName(r, ""); editing = null; }
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Oryginalna nazwa: " + r.Name, smallStyle);
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(DisplayName(r) + (r.Version.Length > 0 ? "  v" + r.Version : "")
                        + (r.LoadedNow ? "" : "   (wyłączony)"));
                    if (GUILayout.Button("Zmień nazwę", GUILayout.Width(110)))
                    {
                        editing = r;
                        editText = DisplayName(r);
                    }
                    GUILayout.EndHorizontal();
                    if (DisplayName(r) != r.Name)
                        GUILayout.Label("(" + r.Name + ")", smallStyle);
                }

                GUILayout.BeginHorizontal();
                if (r.EnabledEntry != null && r.LoadedNow)
                {
                    bool cur = r.EnabledEntry.Value;
                    bool nv = GUILayout.Toggle(cur, " Działa teraz", GUILayout.Width(170));
                    if (nv != cur)
                    {
                        r.EnabledEntry.Value = nv; // BepInEx sam zapisze plik .cfg
                    }
                }
                else
                {
                    GUILayout.Label(r.LoadedNow ? "(brak przełącznika na żywo)" : "", smallStyle, GUILayout.Width(170));
                }

                r.LoadOnStart = GUILayout.Toggle(r.LoadOnStart, " Ładuj przy starcie");
                GUILayout.EndHorizontal();

                if (r.LoadOnStart != r.OriginalLoadOnStart)
                {
                    anyPending = true;
                    GUILayout.Label(r.LoadOnStart ? "-> zostanie WŁĄCZONY po restarcie gry"
                                                  : "-> zostanie WYŁĄCZONY po restarcie gry", smallStyle);
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();

            if (anyPending)
                GUILayout.Label("Zmiany plików zostaną zapisane, gdy zamkniesz grę.", smallStyle);

            DrawUpdateSection();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Odśwież")) BuildList();
            if (GUILayout.Button("Zamknij")) SetOpen(false);
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        // ---------- aktualizacja ----------

        private void CheckForUpdate()
        {
            try
            {
                string f = Path.Combine(Paths.PluginPath, "gnome-mods.version");
                if (File.Exists(f)) localVersion = File.ReadAllText(f).Trim();
            }
            catch (Exception) { }

            Thread t = new Thread(delegate ()
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (Exception) { }
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "gnome-mods");
                        remoteVersion = wc.DownloadString("https://raw.githubusercontent.com/" + RepoName + "/main/VERSION?t=" + DateTime.UtcNow.Ticks).Trim();
                    }
                }
                catch (Exception) { remoteVersion = null; }
                versionChecked = true;
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DrawUpdateSection()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            string info;
            bool newer = false;
            if (!versionChecked) info = "Sprawdzam aktualizacje...";
            else if (remoteVersion == null) info = "Nie udało się sprawdzić aktualizacji (brak internetu?).";
            else if (localVersion.Length == 0) info = "Najnowsza wersja: " + remoteVersion;
            else if (remoteVersion != localVersion) { info = "Dostępna nowa wersja: " + remoteVersion + "  (masz " + localVersion + ")"; newer = true; }
            else info = "Masz najnowszą wersję (" + localVersion + ").";
            GUILayout.Label(info, newer ? GUI.skin.box : smallStyle);

            if (!confirmUpdate)
            {
                if (GUILayout.Button(newer ? "AKTUALIZUJ MODY" : "Aktualizuj mody"))
                    confirmUpdate = true;
            }
            else
            {
                GUILayout.Label("Gra się zamknie, mody się zaktualizują i gra uruchomi się ponownie. Kontynuować?", smallStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Tak, aktualizuj")) StartUpdate();
                if (GUILayout.Button("Nie")) confirmUpdate = false;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private void StartUpdate()
        {
            try
            {
                string game = Paths.GameRootPath.Replace("'", "''");
                string url = "https://raw.githubusercontent.com/" + RepoName + "/main/scripts/aktualizuj.ps1";
                string ps =
                    "[Net.ServicePointManager]::SecurityProtocol='Tls12';" +
                    "$p = Join-Path $env:TEMP 'gnome_mods_aktualizuj.ps1';" +
                    "try { Invoke-WebRequest -UseBasicParsing '" + url + "' -OutFile $p } catch { Write-Host ('[BLAD] Brak polaczenia z GitHubem: ' + $_.Exception.Message) -ForegroundColor Red; Read-Host 'Enter = zamknij'; exit 1 };" +
                    "& $p -StartDir '" + game + "' -WaitForGame -Relaunch;" +
                    "if ($LASTEXITCODE -ne 0) { Read-Host 'Enter = zamknij' }";

                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
                Logger.LogInfo("Uruchomiono aktualizator - zamykam gre.");
                Application.Quit();
            }
            catch (Exception e)
            {
                confirmUpdate = false;
                Logger.LogError("Nie udalo sie uruchomic aktualizatora: " + e);
            }
        }

        private string DisplayName(ModRow r)
        {
            string n;
            if (r.Key != null && customNames.TryGetValue(r.Key, out n) && n.Length > 0) return n;
            return r.Name;
        }

        private static string NamesFile()
        {
            return Path.Combine(Paths.ConfigPath, "ModMenu.names.txt");
        }

        private void LoadNames()
        {
            customNames.Clear();
            try
            {
                string f = NamesFile();
                if (!File.Exists(f)) return;
                foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string name = line.Substring(tab + 1).Trim();
                    if (name.Length > 0) customNames[line.Substring(0, tab)] = name;
                }
            }
            catch (Exception e) { Logger.LogWarning("Nie udalo sie wczytac nazw modow: " + e.Message); }
        }

        private void SetCustomName(ModRow r, string name)
        {
            if (r.Key == null) return;
            name = (name ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
            if (name.Length == 0 || name == r.Name) customNames.Remove(r.Key);
            else customNames[r.Key] = name;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in customNames) lines.Add(kv.Key + "\t" + kv.Value);
                File.WriteAllLines(NamesFile(), lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception e) { Logger.LogWarning("Nie udalo sie zapisac nazw modow: " + e.Message); }
        }

        // Pliki wczytanych modów są zablokowane, dopóki gra działa,
        // więc zmiany nazw robi krótki skrypt uruchomiony po zamknięciu gry.
        private void ApplyPendingChanges()
        {
            try
            {
                StringBuilder cmd = new StringBuilder();
                cmd.Append("ping -n 6 127.0.0.1 >nul");
                int count = 0;
                foreach (ModRow r in rows)
                {
                    if (r.LoadOnStart == r.OriginalLoadOnStart || string.IsNullOrEmpty(r.DllPath)) continue;
                    string target;
                    if (r.LoadOnStart)
                    {
                        if (!r.DllPath.EndsWith(".off", StringComparison.OrdinalIgnoreCase)) continue;
                        target = r.DllPath.Substring(0, r.DllPath.Length - 4);
                    }
                    else
                    {
                        target = r.DllPath + ".off";
                    }
                    cmd.Append(" & move /Y \"" + r.DllPath + "\" \"" + target + "\"");
                    count++;
                }
                if (count == 0) return;

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + cmd.ToString());
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                Logger.LogInfo("Zaplanowano zmiany " + count + " modow po zamknieciu gry.");
            }
            catch (Exception e)
            {
                Logger.LogError("Nie udalo sie zapisac zmian modow: " + e);
            }
        }
    }
}

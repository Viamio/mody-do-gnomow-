// BaseMusic - muzyka i radio w bazie (Burglin' Gnomes, BepInEx 5)
//
// Dwa źródła (F8 przełącza):
//   * RADIO  - radio internetowe (lista stacji w configu). W bazie stoi radio z gry,
//              dźwięk dochodzi z niego: im bliżej, tym głośniej. F6 = następna stacja.
//   * PLIKI  - Twoje pliki .mp3/.ogg/.wav z folderu "Muzyka" (obok Gnomium.exe). F6 = następny utwór.
// F7 = pauza / wznów, F9 = postaw radio przed sobą (zapamiętuje miejsce).
// Muzyka gra tylko w bazie i cichnie na wyprawie. Każdy gracz z modem słyszy radio u siebie;
// jeśli macie tę samą stację, słyszycie to samo.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace BaseMusic
{
    [BepInPlugin("pl.krystian.basemusic", "Muzyka i radio w bazie", "1.1.0")]
    public class BaseMusicPlugin : BaseUnityPlugin
    {
        // ---------- ustawienia ----------
        private ConfigEntry<bool> cEnabled;
        private ConfigEntry<string> cSource;
        private ConfigEntry<float> cVolume;
        private ConfigEntry<float> cFade;
        private ConfigEntry<bool> cShuffle;
        private ConfigEntry<bool> cMainMenu;
        private ConfigEntry<string> cStations;
        private ConfigEntry<int> cStationIndex;
        private ConfigEntry<bool> cRadioModel;
        private ConfigEntry<bool> cPositional;
        private ConfigEntry<float> cRange;
        private ConfigEntry<float> cModelScale;
        private ConfigEntry<string> cRadioOffset;

        private const string DefaultStations =
            "RMF FM|http://195.150.20.242:8000/rmf_fm;" +
            "SomaFM Groove Salad|https://ice1.somafm.com/groovesalad-128-mp3;" +
            "SomaFM Indie Pop|https://ice1.somafm.com/indiepop-128-mp3;" +
            "Radio Paradise|https://stream.radioparadise.com/mp3-192";

        // ---------- stan ----------
        private bool inBase;
        private bool userPaused;
        private float nextStateCheck;
        private string toast = "";
        private float toastUntil;
        private GUIStyle toastStyle;

        // pliki
        private string musicDir;
        private readonly List<string> playlist = new List<string>();
        private int fileIndex = -1;
        private AudioSource source;
        private bool loading;
        private float nextRescan;

        // radio
        private readonly List<KeyValuePair<string, string>> stations = new List<KeyValuePair<string, string>>();
        private Process radioProc;
        private bool radioOpen;
        private float radioVolume;        // aktualna (po wyciszaniu)
        private float sentVolume = -1f, sentBalance = 99f;
        private float nextSend;
        private GameObject radioModel;
        private bool modelSearched;

        // gra (refleksja)
        private PropertyInfo pGpmInstance, pTimeActive, pCurrentHome;
        private Type homeManagerType;
        private Component homeManager;
        private float nextHomeSearch;

        private static readonly string[] Extensions = { ".mp3", ".ogg", ".wav" };

        private void Awake()
        {
            cEnabled = Config.Bind("General", "Enabled", true, "Wlacza muzyke/radio w bazie.");
            cSource = Config.Bind("General", "Source", "Radio", "Zrodlo: Radio albo Pliki (F8 przelacza).");
            cVolume = Config.Bind("General", "Volume", 0.5f, "Glosnosc 0-1.");
            cFade = Config.Bind("General", "FadeSeconds", 2f, "Czas wyciszania/podglasniania (s).");
            cShuffle = Config.Bind("Pliki", "Shuffle", true, "Losowa kolejnosc utworow z folderu Muzyka.");
            cMainMenu = Config.Bind("General", "PlayInMainMenu", false, "Graj tez w menu glownym.");
            cStations = Config.Bind("Radio", "Stations", DefaultStations, "Stacje: Nazwa|adres;Nazwa|adres;... (adres strumienia mp3/aac).");
            cStationIndex = Config.Bind("Radio", "StationIndex", 0, "Ktora stacja gra (od 0). F6 zmienia.");
            cRadioModel = Config.Bind("Radio", "ShowRadioModel", true, "Pokazuj radio z gry w bazie.");
            cPositional = Config.Bind("Radio", "PositionalSound", true, "Dzwiek dochodzi z radia (blizej = glosniej).");
            cRange = Config.Bind("Radio", "HearingRange", 25f, "Z jakiej odleglosci slychac radio (m).");
            cModelScale = Config.Bind("Radio", "ModelScale", 1f, "Skala modelu radia.");
            cRadioOffset = Config.Bind("Radio", "RadioOffset", "", "Pozycja radia wzgledem domu (ustawiana klawiszem F9).");

            musicDir = Path.Combine(Paths.GameRootPath, "Muzyka");
            try { if (!Directory.Exists(musicDir)) Directory.CreateDirectory(musicDir); } catch (Exception) { }

            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.ignoreListenerPause = true;
            source.volume = 0f;

            Type gpm = FindType("GameProgressionManager");
            if (gpm != null)
            {
                pGpmInstance = gpm.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                pTimeActive = gpm.GetProperty("TimeActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            homeManagerType = FindType("HomeManager");
            if (homeManagerType != null)
                pCurrentHome = homeManagerType.GetProperty("CurrentHome", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            ParseStations();
            RescanFiles();
            Application.quitting += StopRadioProcess;
            Logger.LogInfo("Muzyka i radio w bazie: " + stations.Count + " stacji, " + playlist.Count + " plikow. F6/F7/F8/F9.");
        }

        private void OnDestroy() { StopRadioProcess(); }

        private bool RadioMode { get { return !string.Equals(cSource.Value, "Pliki", StringComparison.OrdinalIgnoreCase); } }

        // ================= stan gry =================

        private bool IsInBase()
        {
            try
            {
                object gpm = pGpmInstance != null ? pGpmInstance.GetValue(null, null) : null;
                if (gpm == null || (gpm as UnityEngine.Object) == null)
                {
                    if (!cMainMenu.Value) return false;
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                        if (SceneManager.GetSceneAt(i).name == "MainMenu") return true;
                    return false;
                }
                if (pTimeActive == null) return true;
                return !(bool)pTimeActive.GetValue(gpm, null);
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

        private void Update()
        {
            if (Time.unscaledTime >= nextStateCheck)
            {
                nextStateCheck = Time.unscaledTime + 0.5f;
                inBase = IsInBase();
            }

            bool want = cEnabled.Value && inBase && !userPaused;
            float step = Time.unscaledDeltaTime / Mathf.Max(0.1f, cFade.Value);

            if (RadioMode)
            {
                FadeFiles(false, step);
                UpdateRadio(want, step);
            }
            else
            {
                UpdateRadio(false, step);
                FadeFiles(want, step);
            }
            UpdateModel();
        }

        // ================= PLIKI =================

        private void RescanFiles()
        {
            playlist.Clear();
            try
            {
                foreach (string f in Directory.GetFiles(musicDir))
                    if (Array.IndexOf(Extensions, Path.GetExtension(f).ToLowerInvariant()) >= 0) playlist.Add(f);
            }
            catch (Exception) { }
            playlist.Sort(StringComparer.OrdinalIgnoreCase);
            if (cShuffle.Value)
            {
                System.Random rnd = new System.Random();
                for (int i = playlist.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    string t = playlist[i]; playlist[i] = playlist[j]; playlist[j] = t;
                }
            }
            fileIndex = -1;
        }

        private void FadeFiles(bool want, float step)
        {
            float target = want ? Mathf.Clamp01(cVolume.Value) : 0f;
            source.volume = Mathf.MoveTowards(source.volume, target, step * Mathf.Max(0.01f, cVolume.Value));
            if (!want)
            {
                if (source.isPlaying && source.volume <= 0.001f) source.Pause();
                return;
            }
            if (source.clip != null && !source.isPlaying && source.time > 0f && source.time < source.clip.length - 0.1f)
            {
                source.UnPause();
                return;
            }
            if (!source.isPlaying && !loading)
            {
                if (playlist.Count == 0)
                {
                    if (Time.unscaledTime >= nextRescan) { nextRescan = Time.unscaledTime + 10f; RescanFiles(); }
                    return;
                }
                NextFile();
            }
        }

        private void NextFile()
        {
            if (playlist.Count == 0) { RescanFiles(); if (playlist.Count == 0) { ShowToast("Brak plikow w folderze Muzyka"); return; } }
            fileIndex++;
            if (fileIndex >= playlist.Count) { RescanFiles(); fileIndex = 0; if (playlist.Count == 0) return; }
            StartCoroutine(LoadAndPlay(playlist[fileIndex]));
        }

        private IEnumerator LoadAndPlay(string path)
        {
            loading = true;
            AudioType type = AudioType.MPEG;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ogg") type = AudioType.OGGVORBIS;
            else if (ext == ".wav") type = AudioType.WAV;

            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type))
            {
                yield return req.SendWebRequest();
                AudioClip clip = null;
                if (string.IsNullOrEmpty(req.error)) clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null)
                {
                    Logger.LogWarning("Nie udalo sie wczytac " + Path.GetFileName(path) + ": " + req.error);
                    playlist.Remove(path);
                    fileIndex--;
                    loading = false;
                    yield break;
                }
                AudioClip old = source.clip;
                source.Stop();
                source.clip = clip;
                source.time = 0f;
                source.Play();
                if (old != null) Destroy(old);
                ShowToast("♪ " + Path.GetFileNameWithoutExtension(path));
            }
            loading = false;
        }

        // ================= RADIO =================

        private void ParseStations()
        {
            stations.Clear();
            foreach (string part in (cStations.Value ?? "").Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int bar = p.IndexOf('|');
                string name = bar > 0 ? p.Substring(0, bar).Trim() : p;
                string url = bar > 0 ? p.Substring(bar + 1).Trim() : p;
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    stations.Add(new KeyValuePair<string, string>(name, url));
            }
            if (stations.Count == 0) Logger.LogWarning("Brak poprawnych stacji w konfiguracji.");
        }

        private KeyValuePair<string, string> CurrentStation()
        {
            int i = cStationIndex.Value;
            if (i < 0 || i >= stations.Count) i = 0;
            return stations[i];
        }

        private void UpdateRadio(bool want, float step)
        {
            float maxVol = Mathf.Clamp01(cVolume.Value);
            radioVolume = Mathf.MoveTowards(radioVolume, want ? maxVol : 0f, step * Mathf.Max(0.01f, maxVol));

            if (want && !radioOpen && stations.Count > 0) OpenStation();
            if (!want && radioOpen && radioVolume <= 0.001f) { Send("stop"); radioOpen = false; }
            if (!radioOpen) return;

            // głośność i balans zależne od odległości od radia
            float vol = radioVolume;
            float bal = 0f;
            if (cPositional.Value && radioModel != null && radioModel.activeSelf)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 d = radioModel.transform.position - cam.transform.position;
                    float dist = d.magnitude;
                    float range = Mathf.Max(2f, cRange.Value);
                    float near = range * 0.08f;
                    float att = 1f - Mathf.Clamp01((dist - near) / (range - near));
                    vol *= att * att;
                    if (dist > 0.01f) bal = Mathf.Clamp(Vector3.Dot(cam.transform.right, d / dist), -1f, 1f) * 0.7f;
                }
            }

            if (Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + 0.1f;
                if (Mathf.Abs(vol - sentVolume) > 0.005f) { Send("vol " + vol.ToString("0.000", CultureInfo.InvariantCulture)); sentVolume = vol; }
                if (Mathf.Abs(bal - sentBalance) > 0.03f) { Send("bal " + bal.ToString("0.00", CultureInfo.InvariantCulture)); sentBalance = bal; }
            }
        }

        private void OpenStation()
        {
            if (!EnsureRadioProcess()) return;
            KeyValuePair<string, string> st = CurrentStation();
            sentVolume = -1f; sentBalance = 99f;
            Send("vol 0");
            Send("open " + st.Value);
            radioOpen = true;
            ShowToast("Radio: " + st.Key + "  (F6 = nastepna stacja)");
            Logger.LogInfo("Radio: " + st.Key + " (" + st.Value + ")");
        }

        private const string PlayerScript =
            "Add-Type -AssemblyName PresentationCore\r\n" +
            "$mp = New-Object System.Windows.Media.MediaPlayer\r\n" +
            "$mp.Volume = 0\r\n" +
            "$inv = [Globalization.CultureInfo]::InvariantCulture\r\n" +
            "$run = $true\r\n" +
            "while ($run) {\r\n" +
            "  $line = [Console]::In.ReadLine()\r\n" +
            "  if ($line -eq $null) { break }\r\n" +
            "  $sp = $line.IndexOf(' ')\r\n" +
            "  if ($sp -lt 0) { $cmd = $line; $arg = '' } else { $cmd = $line.Substring(0, $sp); $arg = $line.Substring($sp + 1) }\r\n" +
            "  try {\r\n" +
            "    if ($cmd -eq 'open') { $mp.Stop(); $mp.Close(); $mp.Open([Uri]$arg); $mp.Play() }\r\n" +
            "    elseif ($cmd -eq 'vol') { $mp.Volume = [double]::Parse($arg, $inv) }\r\n" +
            "    elseif ($cmd -eq 'bal') { $mp.Balance = [double]::Parse($arg, $inv) }\r\n" +
            "    elseif ($cmd -eq 'stop') { $mp.Stop(); $mp.Close() }\r\n" +
            "    elseif ($cmd -eq 'quit') { $run = $false }\r\n" +
            "  } catch { }\r\n" +
            "}\r\n" +
            "$mp.Stop(); $mp.Close()\r\n";

        private bool EnsureRadioProcess()
        {
            if (radioProc != null && !radioProc.HasExited) return true;
            try
            {
                string script = Path.Combine(Paths.CachePath, "gnome_radio_player.ps1");
                Directory.CreateDirectory(Paths.CachePath);
                File.WriteAllText(script, PlayerScript, Encoding.ASCII);

                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardInput = true;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                radioProc = Process.Start(psi);
                radioProc.StandardInput.AutoFlush = true;
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError("Nie udalo sie uruchomic odtwarzacza radia: " + e.Message);
                ShowToast("Radio nie dziala - zobacz LogOutput.log");
                cSource.Value = "Pliki";
                return false;
            }
        }

        private void Send(string line)
        {
            try
            {
                if (radioProc != null && !radioProc.HasExited) radioProc.StandardInput.WriteLine(line);
            }
            catch (Exception) { }
        }

        private void StopRadioProcess()
        {
            try
            {
                if (radioProc != null && !radioProc.HasExited)
                {
                    Send("quit");
                    if (!radioProc.WaitForExit(500)) radioProc.Kill();
                }
            }
            catch (Exception) { }
            radioProc = null;
            radioOpen = false;
        }

        // ================= MODEL RADIA =================

        private void UpdateModel()
        {
            bool show = cEnabled.Value && cRadioModel.Value && RadioMode && inBase;
            Transform home = show ? HomeTransform() : null;
            if (!show || home == null)
            {
                if (radioModel != null && radioModel.activeSelf) radioModel.SetActive(false);
                return;
            }
            if (radioModel == null)
            {
                if (modelSearched) return;
                modelSearched = true;
                radioModel = BuildRadioModel();
                if (radioModel == null) return;
            }
            if (!radioModel.activeSelf) radioModel.SetActive(true);

            Vector3 off;
            if (!TryParseVector(cRadioOffset.Value, out off))
            {
                // pierwsze wejście: postaw radio przed graczem
                if (!PlaceInFrontOfCamera(home)) return;
                TryParseVector(cRadioOffset.Value, out off);
            }
            radioModel.transform.position = home.TransformPoint(off);
            radioModel.transform.rotation = home.rotation;
            radioModel.transform.localScale = Vector3.one * Mathf.Max(0.05f, cModelScale.Value);
        }

        private bool PlaceInFrontOfCamera(Transform home)
        {
            Camera cam = Camera.main;
            if (cam == null || home == null) return false;
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            Vector3 p = cam.transform.position + fwd.normalized * 1.5f;
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore)) p = hit.point;
            Vector3 local = home.InverseTransformPoint(p);
            cRadioOffset.Value = local.x.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                                 local.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                                 local.z.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryParseVector(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(s)) return false;
            string[] p = s.Split(',');
            if (p.Length != 3) return false;
            float x, y, z;
            if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        // Kopiuje sam wygląd (siatki i materiały) radia z gry - bez fizyki i sieci.
        private GameObject BuildRadioModel()
        {
            GameObject prefab = FindRadioPrefab();
            if (prefab == null) { Logger.LogWarning("Nie znaleziono modelu radia w grze - radio gra bez modelu."); return null; }

            GameObject root = new GameObject("BaseMusic_Radio");
            UnityEngine.Object.DontDestroyOnLoad(root);
            Transform pr = prefab.transform;
            foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null) continue;
                GameObject part = new GameObject(mf.name);
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = pr.InverseTransformPoint(mf.transform.position);
                part.transform.localRotation = Quaternion.Inverse(pr.rotation) * mf.transform.rotation;
                Vector3 rs = pr.lossyScale, ms = mf.transform.lossyScale;
                part.transform.localScale = new Vector3(ms.x / Mathf.Max(1e-4f, rs.x), ms.y / Mathf.Max(1e-4f, rs.y), ms.z / Mathf.Max(1e-4f, rs.z));
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
            }
            root.SetActive(false);
            Logger.LogInfo("Model radia: " + prefab.name);
            return root;
        }

        private GameObject FindRadioPrefab()
        {
            try
            {
                Type stealT = FindType("StealableObject");
                FieldInfo fType = stealT != null ? stealT.GetField("itemType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (stealT == null || fType == null) return null;

                // 1) prefaby sieciowe gry
                Type nmT = Type.GetType("Unity.Netcode.NetworkManager, Unity.Netcode.Runtime");
                object nm = nmT != null ? nmT.GetProperty("Singleton").GetValue(null, null) : null;
                if (nm != null)
                {
                    object cfg = nm.GetType().GetField("NetworkConfig").GetValue(nm);
                    object prefabs = cfg.GetType().GetField("Prefabs").GetValue(cfg);
                    IEnumerable list = prefabs.GetType().GetProperty("Prefabs").GetValue(prefabs, null) as IEnumerable;
                    foreach (object np in list)
                    {
                        GameObject go = np.GetType().GetField("Prefab").GetValue(np) as GameObject;
                        if (go == null) continue;
                        Component s = go.GetComponent(stealT);
                        if (s != null && Convert.ToString(fType.GetValue(s)) == "Radio") return go;
                    }
                }
                // 2) wszystko, co jest wczytane w pamięci
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(stealT))
                {
                    Component s = o as Component;
                    if (s != null && Convert.ToString(fType.GetValue(s)) == "Radio") return s.gameObject;
                }
            }
            catch (Exception e) { Logger.LogWarning("Szukanie modelu radia: " + e.Message); }
            return null;
        }

        // ================= klawisze i napisy =================

        private void ShowToast(string text) { toast = text; toastUntil = Time.unscaledTime + 4f; }

        private void OnGUI()
        {
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && cEnabled.Value && inBase)
            {
                if (ev.keyCode == KeyCode.F6)
                {
                    ev.Use();
                    userPaused = false;
                    if (RadioMode)
                    {
                        if (stations.Count > 0)
                        {
                            cStationIndex.Value = (cStationIndex.Value + 1) % stations.Count;
                            if (radioOpen) OpenStation();
                        }
                    }
                    else
                    {
                        source.Stop();
                        if (!loading) NextFile();
                    }
                }
                else if (ev.keyCode == KeyCode.F7)
                {
                    ev.Use();
                    userPaused = !userPaused;
                    ShowToast(userPaused ? "Muzyka: pauza (F7)" : "Muzyka: gra dalej");
                }
                else if (ev.keyCode == KeyCode.F8)
                {
                    ev.Use();
                    cSource.Value = RadioMode ? "Pliki" : "Radio";
                    ShowToast(RadioMode ? "Zrodlo: RADIO" : "Zrodlo: PLIKI z folderu Muzyka");
                }
                else if (ev.keyCode == KeyCode.F9 && RadioMode)
                {
                    ev.Use();
                    if (PlaceInFrontOfCamera(HomeTransform())) ShowToast("Radio postawione tutaj");
                }
            }

            if (Time.unscaledTime < toastUntil && toast.Length > 0)
            {
                if (toastStyle == null)
                {
                    toastStyle = new GUIStyle(GUI.skin.box);
                    toastStyle.fontSize = 16;
                    // (wyrownanie domyslne - bez TextRenderingModule)
                }
                float w = Mathf.Min(520f, 40f + toast.Length * 9f);
                GUI.Box(new Rect(Screen.width - w - 20f, 20f, w, 36f), toast, toastStyle);
            }
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

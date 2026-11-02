// BaseMusic - własna muzyka w bazie (Burglin' Gnomes, BepInEx 5)
//
// - Wrzuć pliki .mp3 / .ogg / .wav do folderu "Muzyka" w folderze gry (obok Gnomium.exe).
// - Muzyka gra, gdy jesteś w bazie, i płynnie cichnie, gdy zaczyna się wyprawa.
// - F6 = następny utwór, F7 = pauza / wznów.
// - Każdy gracz słyszy swoją muzykę ze swojego folderu.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace BaseMusic
{
    [BepInPlugin("pl.krystian.basemusic", "Muzyka w bazie", "1.0.0")]
    public class BaseMusicPlugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> enabled;
        private ConfigEntry<float> volume;
        private ConfigEntry<bool> shuffle;
        private ConfigEntry<float> fadeTime;
        private ConfigEntry<bool> playInMainMenu;

        private string musicDir;
        private readonly List<string> playlist = new List<string>();
        private int index = -1;
        private AudioSource source;
        private bool loading;
        private bool userPaused;
        private bool inBase;
        private float nextStateCheck;
        private float nextRescan;

        private string toast = "";
        private float toastUntil;
        private GUIStyle toastStyle;

        // GameProgressionManager (przez refleksję)
        private PropertyInfo pInstance, pTimeActive;

        private static readonly string[] Extensions = { ".mp3", ".ogg", ".wav" };

        private void Awake()
        {
            enabled = Config.Bind("General", "Enabled", true, "Wlacza muzyke w bazie.");
            volume = Config.Bind("General", "Volume", 0.5f, "Glosnosc 0-1.");
            shuffle = Config.Bind("General", "Shuffle", true, "Losowa kolejnosc utworow.");
            fadeTime = Config.Bind("General", "FadeSeconds", 2f, "Czas wyciszania/podglasniania (s).");
            playInMainMenu = Config.Bind("General", "PlayInMainMenu", false, "Graj tez w menu glownym.");

            musicDir = Path.Combine(Paths.GameRootPath, "Muzyka");
            try { if (!Directory.Exists(musicDir)) Directory.CreateDirectory(musicDir); } catch (Exception) { }

            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.ignoreListenerPause = true;
            source.volume = 0f;

            Type gpm = AccessTools.TypeByName("GameProgressionManager");
            if (gpm != null)
            {
                pInstance = gpm.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                pTimeActive = gpm.GetProperty("TimeActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            Rescan();
            Logger.LogInfo("Muzyka w bazie zaladowana: " + playlist.Count + " utworow w " + musicDir + ". F6 = nastepny, F7 = pauza.");
        }

        private void Rescan()
        {
            playlist.Clear();
            try
            {
                foreach (string f in Directory.GetFiles(musicDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (Array.IndexOf(Extensions, ext) >= 0) playlist.Add(f);
                }
            }
            catch (Exception) { }
            playlist.Sort(StringComparer.OrdinalIgnoreCase);
            if (shuffle.Value)
            {
                System.Random rnd = new System.Random();
                for (int i = playlist.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    string t = playlist[i]; playlist[i] = playlist[j]; playlist[j] = t;
                }
            }
            index = -1;
        }

        private bool IsInBase()
        {
            try
            {
                object gpm = pInstance != null ? pInstance.GetValue(null, null) : null;
                UnityEngine.Object uo = gpm as UnityEngine.Object;
                if (gpm == null || uo == null)
                {
                    if (!playInMainMenu.Value) return false;
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                        if (SceneManager.GetSceneAt(i).name == "MainMenu") return true;
                    return false;
                }
                if (pTimeActive == null) return true;
                return !(bool)pTimeActive.GetValue(gpm, null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Update()
        {
            if (Time.unscaledTime >= nextStateCheck)
            {
                nextStateCheck = Time.unscaledTime + 0.5f;
                inBase = IsInBase();
            }

            bool shouldPlay = enabled.Value && inBase && !userPaused;
            float target = shouldPlay ? Mathf.Clamp01(volume.Value) : 0f;
            float step = Time.unscaledDeltaTime * Mathf.Max(0.01f, volume.Value) / Mathf.Max(0.1f, fadeTime.Value);
            source.volume = Mathf.MoveTowards(source.volume, target, step);

            if (!shouldPlay)
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
                    if (Time.unscaledTime >= nextRescan) { nextRescan = Time.unscaledTime + 10f; Rescan(); }
                    return;
                }
                PlayNext();
            }
        }

        private void PlayNext()
        {
            if (playlist.Count == 0) { Rescan(); if (playlist.Count == 0) { ShowToast("Brak muzyki - wrzuc pliki do folderu Muzyka"); return; } }
            index++;
            if (index >= playlist.Count) { Rescan(); index = 0; if (playlist.Count == 0) return; }
            StartCoroutine(LoadAndPlay(playlist[index]));
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
                if (!string.IsNullOrEmpty(req.error))
                {
                    Logger.LogWarning("Nie udalo sie wczytac " + Path.GetFileName(path) + ": " + req.error);
                    playlist.Remove(path);
                    index--;
                    loading = false;
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null)
                {
                    playlist.Remove(path);
                    index--;
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

        private void ShowToast(string text)
        {
            toast = text;
            toastUntil = Time.unscaledTime + 4f;
        }

        private void OnGUI()
        {
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && enabled.Value)
            {
                if (ev.keyCode == KeyCode.F6 && inBase)
                {
                    ev.Use();
                    userPaused = false;
                    source.Stop();
                    if (!loading) PlayNext();
                }
                else if (ev.keyCode == KeyCode.F7 && inBase)
                {
                    ev.Use();
                    userPaused = !userPaused;
                    ShowToast(userPaused ? "Muzyka: pauza (F7)" : "Muzyka: gra dalej");
                }
            }

            if (Time.unscaledTime < toastUntil && toast.Length > 0)
            {
                if (toastStyle == null)
                {
                    toastStyle = new GUIStyle(GUI.skin.box);
                    toastStyle.fontSize = 16;
                    toastStyle.alignment = TextAnchor.MiddleCenter;
                }
                float w = Mathf.Min(520f, 40f + toast.Length * 9f);
                GUI.Box(new Rect(Screen.width - w - 20f, 20f, w, 36f), toast, toastStyle);
            }
        }
    }

    // mini-zamiennik HarmonyLib.AccessTools, żeby nie zależeć od Harmony
    internal static class AccessTools
    {
        public static Type TypeByName(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(name, false);
                    if (t != null) return t;
                }
                catch (Exception) { }
            }
            return null;
        }
    }
}

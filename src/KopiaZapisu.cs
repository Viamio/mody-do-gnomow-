// KopiaZapisu - automatyczna kopia zapisu gry przed każdą wyprawą (Burglin' Gnomes, BepInEx 5)
//
// Gdy zaczyna się dzień (wyprawa), host kopiuje pliki zapisu (SaveGameX.bgsave) do:
//   %USERPROFILE%\AppData\LocalLow\<gra>\SaveGames\Kopie\<data_godzina>\
// Trzyma ostatnie N kopii (domyślnie 15). Żeby przywrócić kopię: zamknij grę i skopiuj
// plik .bgsave z wybranej kopii z powrotem do folderu SaveGames.

using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace KopiaZapisu
{
    [BepInPlugin("pl.krystian.kopiazapisu", "Kopia zapisu", "1.0.0")]
    public class KopiaZapisuPlugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> cEnabled;
        private ConfigEntry<int> cMax;

        private PropertyInfo pGpmInstance, pTimeActive;
        private bool lastActive;
        private float nextCheck;

        private void Awake()
        {
            cEnabled = Config.Bind("General", "Enabled", true, "Robi kopie zapisu przed kazda wyprawa (tylko host).");
            cMax = Config.Bind("General", "MaxBackups", 15, "Ile ostatnich kopii trzymac.");

            Type gpm = FindType("GameProgressionManager");
            if (gpm != null)
            {
                pGpmInstance = gpm.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                pTimeActive = gpm.GetProperty("TimeActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            Logger.LogInfo("Kopia zapisu: " + SaveDir());
        }

        private static string SaveDir() { return Application.persistentDataPath + "/SaveGames"; }

        private void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;

            bool active = false;
            try
            {
                object gpm = pGpmInstance != null ? pGpmInstance.GetValue(null, null) : null;
                if (gpm != null && (gpm as UnityEngine.Object) != null && pTimeActive != null)
                    active = (bool)pTimeActive.GetValue(gpm, null);
            }
            catch (Exception) { }

            if (active && !lastActive && cEnabled.Value && IsHost()) MakeBackup();
            lastActive = active;
        }

        private static bool IsHost()
        {
            try
            {
                Type nmT = Type.GetType("Unity.Netcode.NetworkManager, Unity.Netcode.Runtime");
                object nm = nmT != null ? nmT.GetProperty("Singleton").GetValue(null, null) : null;
                return nm != null && (bool)nm.GetType().GetProperty("IsServer").GetValue(nm, null);
            }
            catch (Exception) { return false; }
        }

        private void MakeBackup()
        {
            try
            {
                string dir = SaveDir();
                if (!Directory.Exists(dir)) return;
                string[] saves = Directory.GetFiles(dir, "*.bgsave");
                if (saves.Length == 0) return;

                string root = Path.Combine(dir, "Kopie");
                string target = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(target);
                foreach (string f in saves) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
                Logger.LogInfo("Zrobiono kopie zapisu: " + target);

                string[] all = Directory.GetDirectories(root);
                Array.Sort(all, StringComparer.Ordinal);
                int keep = Math.Max(1, cMax.Value);
                for (int i = 0; i < all.Length - keep; i++)
                {
                    try { Directory.Delete(all[i], true); } catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Nie udalo sie zrobic kopii zapisu: " + e.Message);
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

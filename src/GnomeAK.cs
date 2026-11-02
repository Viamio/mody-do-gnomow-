// GnomeAK - "AK-47" do Burglin' Gnomes (BepInEx 5)
//
// - Na początku każdego dnia (start rozgrywki) host dostaje broń z gry (pistolet,
//   a jeśli go nie ma - strzelbę) przed sobą. F2 = dodatkowa broń w dowolnej chwili (tylko host).
// - Broń z moda działa jak AK: 30 naboi w magazynku, samo przeładowanie po 2.5 s,
//   słabszy odrzut, rozrzut rośnie przy długiej serii i spada po puszczeniu spustu (jak w CS).
// - Ogień ciągły: przytrzymaj przycisk, którym strzelasz - broń strzela ~600 razy/min.
//   Działa dla każdego gracza, który ma ten mod (u znajomych bez moda - pojedyncze strzały).
//
// Wszystko przez refleksję, żeby nie zależeć od wersji bibliotek gry.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GnomeAK
{
    [BepInPlugin("pl.krystian.gnomeak", "Gnome AK-47", "1.0.0")]
    public class AKPlugin : BaseUnityPlugin
    {
        internal static AKPlugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> GiveAtStart;
        internal static ConfigEntry<int> MagazineSize;
        internal static ConfigEntry<float> ReloadTime;
        internal static ConfigEntry<float> RoundsPerMinute;
        internal static ConfigEntry<float> KickMultiplier;
        internal static ConfigEntry<float> BaseSpread;
        internal static ConfigEntry<float> SpreadPerShot;
        internal static ConfigEntry<float> MaxSpread;
        internal static ConfigEntry<float> SpreadRecovery;

        // typy i pola gry
        internal static Type GunType;
        private static FieldInfo fSpread, fKick, fBulletCount;
        private static MethodInfo mPlayerShoot, mSetBulletCount;
        private static PropertyInfo pBulletCount;

        // broń "AK" (tylko na hoście)
        private class AkState { public float Heat; public float LastShot; public bool Reloading; }
        private static readonly Dictionary<object, AkState> akGuns = new Dictionary<object, AkState>();

        // ogień ciągły (lokalnie)
        private Component autoGun;
        private object heldControl;
        private PropertyInfo heldIsPressed;
        private bool heldLegacyMouse;
        private float nextShot;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Wlacza caly mod AK.");
            GiveAtStart = Config.Bind("General", "GiveAtStart", true, "Host dostaje bron na poczatku kazdego dnia.");
            MagazineSize = Config.Bind("AK", "MagazineSize", 30, "Naboje w magazynku.");
            ReloadTime = Config.Bind("AK", "ReloadTime", 2.5f, "Czas przeladowania (s).");
            RoundsPerMinute = Config.Bind("AK", "RoundsPerMinute", 600f, "Szybkostrzelnosc ognia ciaglego.");
            KickMultiplier = Config.Bind("AK", "KickMultiplier", 0.35f, "Mnoznik odrzutu fizycznego broni (1 = jak w grze).");
            BaseSpread = Config.Bind("AK", "BaseSpread", 0.3f, "Rozrzut pierwszego strzalu (stopnie).");
            SpreadPerShot = Config.Bind("AK", "SpreadPerShot", 0.9f, "O ile rosnie rozrzut z kazdym strzalem serii.");
            MaxSpread = Config.Bind("AK", "MaxSpread", 7f, "Maksymalny rozrzut (stopnie).");
            SpreadRecovery = Config.Bind("AK", "SpreadRecovery", 9f, "Jak szybko rozrzut wraca do normy (stopnie/s).");

            GunType = AccessTools.TypeByName("Gun");
            if (GunType == null) { Log.LogError("Nie znaleziono klasy Gun - mod nie zadziala."); return; }
            fSpread = OfType(AccessTools.Field(GunType, "spread"), typeof(float));
            fKick = OfType(AccessTools.Field(GunType, "kickStrength"), typeof(float));
            fBulletCount = OfType(AccessTools.Field(GunType, "bulletCount"), typeof(int));
            mPlayerShoot = AccessTools.Method(GunType, "PlayerShoot");
            mSetBulletCount = AccessTools.Method(GunType, "SetBulletCount");
            pBulletCount = AccessTools.Property(GunType, "BulletCount");

            Harmony h = new Harmony("pl.krystian.gnomeak");
            int ok = 0;
            ok += Patch(h, AccessTools.Method(GunType, "DoShoot"), "DoShootPrefix", "DoShootPostfix");
            ok += Patch(h, AccessTools.Method(AccessTools.TypeByName("PlayerInteractListener"), "Interact"), null, "InteractPostfix");
            ok += Patch(h, AccessTools.Method(AccessTools.TypeByName("GameProgressionManager"), "OnGameStarted"), null, "GameStartedPostfix");
            Log.LogInfo("Gnome AK-47 zaladowany (" + ok + "/3 patche). F2 = bron (host).");
        }

        private static FieldInfo OfType(FieldInfo f, Type t)
        {
            if (f == null) return null;
            if (f.FieldType != t) { Log.LogWarning("Pole " + f.Name + " ma inny typ (" + f.FieldType.Name + ") - pomijam."); return null; }
            return f;
        }

        private static int Patch(Harmony h, MethodInfo target, string prefix, string postfix)
        {
            if (target == null) return 0;
            HarmonyMethod pre = prefix != null ? new HarmonyMethod(typeof(AKPlugin).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)) : null;
            HarmonyMethod post = postfix != null ? new HarmonyMethod(typeof(AKPlugin).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)) : null;
            h.Patch(target, pre, post, null, null, null);
            return 1;
        }

        // ---------- rozrzut i przeładowanie (serwer/host) ----------

        private static void DoShootPrefix(object __instance)
        {
            if (!Enabled.Value) return;
            AkState st;
            if (!akGuns.TryGetValue(__instance, out st)) return;
            float now = Time.time;
            st.Heat = Mathf.Max(0f, st.Heat - (now - st.LastShot) * SpreadRecovery.Value);
            if (fSpread != null) fSpread.SetValue(__instance, Mathf.Min(MaxSpread.Value, BaseSpread.Value + st.Heat));
            st.Heat = Mathf.Min(MaxSpread.Value, st.Heat + SpreadPerShot.Value);
            st.LastShot = now;
        }

        private static void DoShootPostfix(object __instance)
        {
            if (!Enabled.Value) return;
            AkState st;
            if (!akGuns.TryGetValue(__instance, out st) || st.Reloading) return;
            if (GetBullets(__instance) <= 0 && Instance != null)
            {
                st.Reloading = true;
                Instance.StartCoroutine(Instance.Reload(__instance, st));
            }
        }

        private IEnumerator Reload(object gun, AkState st)
        {
            yield return new WaitForSeconds(ReloadTime.Value);
            st.Reloading = false;
            UnityEngine.Object uo = gun as UnityEngine.Object;
            if (uo == null || mSetBulletCount == null) yield break;
            try { mSetBulletCount.Invoke(gun, new object[] { MagazineSize.Value }); }
            catch (Exception e) { Log.LogWarning("Przeladowanie nie wyszlo: " + e.Message); }
        }

        private static int GetBullets(object gun)
        {
            try { return pBulletCount != null ? (int)pBulletCount.GetValue(gun, null) : 1; }
            catch (Exception) { return 1; }
        }

        // ---------- ogień ciągły (lokalny gracz) ----------

        private static void InteractPostfix(object __instance)
        {
            if (!Enabled.Value || Instance == null || GunType == null) return;
            Component c = __instance as Component;
            if (c == null) return;
            Component gun = c.GetComponent(GunType);
            if (gun == null) gun = c.GetComponentInParent(GunType);
            if (gun == null) return;
            Instance.StartAutoFire(gun);
        }

        private void StartAutoFire(Component gun)
        {
            autoGun = gun;
            heldControl = FindPressedControl(out heldIsPressed);
            heldLegacyMouse = false;
            if (heldControl == null)
            {
                try { heldLegacyMouse = Input.GetMouseButton(0); } catch (Exception) { }
            }
            nextShot = Time.time + 60f / Mathf.Max(60f, RoundsPerMinute.Value);
        }

        private bool StillHeld()
        {
            try
            {
                if (heldControl != null && heldIsPressed != null) return (bool)heldIsPressed.GetValue(heldControl, null);
                if (heldLegacyMouse) return Input.GetMouseButton(0);
            }
            catch (Exception) { }
            return false;
        }

        private void Update()
        {
            if (autoGun == null) return;
            if (!Enabled.Value || !StillHeld() || GetBullets(autoGun) <= 0)
            {
                autoGun = null; heldControl = null;
                return;
            }
            if (Time.time >= nextShot)
            {
                nextShot = Time.time + 60f / Mathf.Max(60f, RoundsPerMinute.Value);
                try { mPlayerShoot.Invoke(autoGun, null); }
                catch (Exception e) { Log.LogWarning("Strzal nie wyszedl: " + e.Message); autoGun = null; }
            }
        }

        // Szuka wciśniętego przycisku (mysz / klawiatura) w nowym Input System.
        private static object FindPressedControl(out PropertyInfo isPressed)
        {
            isPressed = null;
            try
            {
                Type mouseT = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
                if (mouseT != null)
                {
                    object mouse = mouseT.GetProperty("current").GetValue(null, null);
                    if (mouse != null)
                    {
                        foreach (string b in new string[] { "leftButton", "rightButton", "middleButton" })
                        {
                            object ctrl = mouseT.GetProperty(b).GetValue(mouse, null);
                            PropertyInfo p = ctrl.GetType().GetProperty("isPressed");
                            if (p != null && (bool)p.GetValue(ctrl, null)) { isPressed = p; return ctrl; }
                        }
                    }
                }
                Type kbT = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (kbT != null)
                {
                    object kb = kbT.GetProperty("current").GetValue(null, null);
                    if (kb != null)
                    {
                        IEnumerable keys = kbT.GetProperty("allKeys").GetValue(kb, null) as IEnumerable;
                        if (keys != null)
                        {
                            foreach (object k in keys)
                            {
                                if (k == null) continue;
                                PropertyInfo p = k.GetType().GetProperty("isPressed");
                                if (p != null && (bool)p.GetValue(k, null)) { isPressed = p; return k; }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // ---------- dawanie broni (host) ----------

        private static void GameStartedPostfix()
        {
            if (Enabled.Value && GiveAtStart.Value && Instance != null)
                Instance.StartCoroutine(Instance.GiveLater(4f));
        }

        private IEnumerator GiveLater(float delay)
        {
            yield return new WaitForSeconds(delay);
            SpawnAK();
        }

        private void OnGUI()
        {
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F2 && Enabled.Value)
            {
                ev.Use();
                SpawnAK();
            }
        }

        private static object GetNetworkManager()
        {
            Type nmT = Type.GetType("Unity.Netcode.NetworkManager, Unity.Netcode.Runtime");
            if (nmT == null) return null;
            return nmT.GetProperty("Singleton").GetValue(null, null);
        }

        private void SpawnAK()
        {
            try
            {
                object nm = GetNetworkManager();
                if (nm == null) { Log.LogWarning("Brak NetworkManagera - nie jestes w grze."); return; }
                bool isServer = (bool)nm.GetType().GetProperty("IsServer").GetValue(nm, null);
                if (!isServer) { Log.LogInfo("Bron moze dac tylko host."); return; }

                GameObject prefab = FindGunPrefab(nm);
                if (prefab == null) { Log.LogWarning("Nie znaleziono broni w prefabach gry."); return; }

                Camera cam = Camera.main;
                Vector3 pos = cam != null ? cam.transform.position + cam.transform.forward * 1.2f + Vector3.up * 0.3f : Vector3.zero;
                Quaternion rot = cam != null ? Quaternion.LookRotation(cam.transform.forward) : Quaternion.identity;

                GameObject go = (GameObject)UnityEngine.Object.Instantiate(prefab, pos, rot);
                Component gun = go.GetComponent(GunType);
                if (gun == null) gun = go.GetComponentInChildren(GunType);

                if (gun != null)
                {
                    // ustawiane przed Spawn - OnNetworkSpawn przepisze bulletCount do licznika naboi
                    if (fBulletCount != null) fBulletCount.SetValue(gun, MagazineSize.Value);
                    if (fKick != null) fKick.SetValue(gun, (float)fKick.GetValue(gun) * KickMultiplier.Value);
                    if (fSpread != null) fSpread.SetValue(gun, BaseSpread.Value);
                    AkState st = new AkState();
                    st.LastShot = Time.time;
                    akGuns[gun] = st;
                }

                Type noT = Type.GetType("Unity.Netcode.NetworkObject, Unity.Netcode.Runtime");
                Component no = go.GetComponent(noT);
                MethodInfo spawn = noT.GetMethod("Spawn", new Type[] { typeof(bool) });
                spawn.Invoke(no, new object[] { true });
                Log.LogInfo("Dano AK (" + prefab.name + ").");
            }
            catch (Exception e)
            {
                Log.LogError("Nie udalo sie dac broni: " + e);
            }
        }

        private static GameObject FindGunPrefab(object nm)
        {
            object cfg = nm.GetType().GetField("NetworkConfig").GetValue(nm);
            object prefabs = cfg.GetType().GetField("Prefabs").GetValue(cfg);
            IEnumerable list = prefabs.GetType().GetProperty("Prefabs").GetValue(prefabs, null) as IEnumerable;
            FieldInfo fPellet = OfType(AccessTools.Field(GunType, "pelletCount"), typeof(int));
            GameObject best = null;
            foreach (object np in list)
            {
                GameObject go = np.GetType().GetField("Prefab").GetValue(np) as GameObject;
                if (go == null) continue;
                Component g = go.GetComponent(GunType);
                if (g == null) continue;
                int pellets = fPellet != null ? (int)fPellet.GetValue(g) : 1;
                if (pellets <= 1) return go;      // pistolet - najlepszy na AK
                if (best == null) best = go;      // inaczej strzelba
            }
            return best;
        }
    }
}

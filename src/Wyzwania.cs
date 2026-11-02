// Wyzwania - modyfikatory dnia, wściekły dziadek, alarm, skarb dnia (Burglin' Gnomes, BepInEx 5)
//
// MODYFIKATOR DNIA (losowany z numeru dnia - u wszystkich graczy z modem ten sam):
//   Zwykły dzień, Ciemność, Mgła, Dziadek z kawą (szybszy), Zaspany dziadek (wolniejszy),
//   Niska grawitacja, Złota gorączka (łup wart więcej).
// WŚCIEKŁY DZIADEK (host): im więcej hałasu i złości, tym szybciej biega.
// ALARM (host): seria głośnych hałasów w krótkim czasie włącza alarm - dziadek przybiega na miejsce
//   hałasu i przez 20 s jest szybszy.
// SKARB DNIA: jeden losowy przedmiot w domu świeci na złoto i jest wart 5x więcej (wartość liczy host).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Wyzwania
{
    [BepInPlugin("pl.krystian.wyzwania", "Wyzwania dnia", "1.0.0")]
    public class WyzwaniaPlugin : BaseUnityPlugin
    {
        internal static WyzwaniaPlugin Instance;

        private ConfigEntry<bool> cEnabled, cModifiers, cAngry, cAlarm, cTreasure;
        private ConfigEntry<int> cNormalChance;
        private ConfigEntry<float> cAngrySpeed, cTreasureMult;

        private enum Mod { Zwykly, Ciemnosc, Mgla, Kawa, Zaspany, Grawitacja, Zloto }
        private static readonly string[] ModNames = {
            "Zwykły dzień", "Ciemność – w domu zgasły światła", "Gęsta mgła",
            "Dziadek z kawą – jest szybszy!", "Zaspany dziadek – jest wolniejszy",
            "Niska grawitacja", "Złota gorączka – łup wart 50% więcej" };

        private Mod today = Mod.Zwykly;
        private bool dayActive;
        private float nextCheck;

        // gra
        private PropertyInfo pGpmInstance, pTimeActive, pCurrentDay, pCurrentLevel;
        private Type humanType, aiBaseType, stealType;
        private FieldInfo fMaxSpeed, fReward;
        private PropertyInfo pAnger;

        // przywracanie
        private readonly Dictionary<Light, float> lightOrig = new Dictionary<Light, float>();
        private float ambientOrig = -1f;
        private bool fogOrig; private float fogDensityOrig; private Color fogColorOrig; private FogMode fogModeOrig;
        private bool fogSaved;
        private Vector3 gravityOrig; private bool gravitySaved;
        private readonly Dictionary<Component, float> speedOrig = new Dictionary<Component, float>();
        private readonly Dictionary<object, bool> rewardScaled = new Dictionary<object, bool>();

        // złość i alarm
        internal float noiseRage;
        private readonly List<float> loudTimes = new List<float>();
        private float alarmUntil;
        internal static bool creatingNoise;

        // skarb
        private Component treasure;
        private Light treasureLight;
        private string treasureName = "";

        // napisy
        private string banner = "";
        private float bannerUntil;
        private GUIStyle bannerStyle;

        private void Awake()
        {
            Instance = this;
            cEnabled = Config.Bind("General", "Enabled", true, "Wlacza wszystkie wyzwania.");
            cModifiers = Config.Bind("Modyfikatory", "Enabled", true, "Losowy modyfikator kazdego dnia.");
            cNormalChance = Config.Bind("Modyfikatory", "NormalDayChance", 25, "Szansa (%) na zwykly dzien bez modyfikatora.");
            cAngry = Config.Bind("WsciekłyDziadek", "Enabled", true, "Dziadek przyspiesza od halasu i zlosci (host).");
            cAngrySpeed = Config.Bind("WsciekłyDziadek", "MaxSpeedBonus", 0.6f, "Maksymalne przyspieszenie (0.6 = +60%).");
            cAlarm = Config.Bind("Alarm", "Enabled", true, "Seria halasow wlacza alarm (host).");
            cTreasure = Config.Bind("SkarbDnia", "Enabled", true, "Jeden przedmiot dziennie jest skarbem.");
            cTreasureMult = Config.Bind("SkarbDnia", "ValueMultiplier", 5f, "Ile razy cenniejszy jest skarb.");

            Type gpm = AccessTools.TypeByName("GameProgressionManager");
            if (gpm != null)
            {
                pGpmInstance = AccessTools.Property(gpm, "Instance");
                pTimeActive = AccessTools.Property(gpm, "TimeActive");
                pCurrentDay = AccessTools.Property(gpm, "CurrentDay");
                pCurrentLevel = AccessTools.Property(gpm, "CurrentLevel");
            }
            humanType = AccessTools.TypeByName("HumanAILink");
            if (humanType != null) pAnger = AccessTools.Property(humanType, "AngerPercentage");
            aiBaseType = Type.GetType("Pathfinding.AIBase, AstarPathfindingProject");
            if (aiBaseType != null) fMaxSpeed = AccessTools.Field(aiBaseType, "maxSpeed");
            stealType = AccessTools.TypeByName("StealableObject");
            if (stealType != null) fReward = AccessTools.Field(stealType, "reward");

            Harmony h = new Harmony("pl.krystian.wyzwania");
            MethodInfo noise = AccessTools.Method(AccessTools.TypeByName("RoomManager"), "CreateNoiseEvent");
            try
            {
                if (noise != null)
                    h.Patch(noise, null, new HarmonyMethod(typeof(WyzwaniaPlugin).GetMethod("NoisePostfix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
            }
            catch (Exception e) { Logger.LogWarning("Patch halasu nie wyszedl: " + e.Message); noise = null; }
            Logger.LogInfo("Wyzwania zaladowane. Halas: " + (noise != null) + ", dziadek: " + (humanType != null) + ", AI: " + (fMaxSpeed != null));
        }

        // ================= cykl dnia =================

        private object Gpm()
        {
            try
            {
                object g = pGpmInstance != null ? pGpmInstance.GetValue(null, null) : null;
                return (g != null && (g as UnityEngine.Object) != null) ? g : null;
            }
            catch (Exception) { return null; }
        }

        private static int ReadInt(PropertyInfo p, object o)
        {
            try { return p != null ? Convert.ToInt32(p.GetValue(o, null)) : 0; } catch (Exception) { return 0; }
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

        private void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;

            object gpm = Gpm();
            bool active = false;
            try { if (gpm != null && pTimeActive != null) active = (bool)pTimeActive.GetValue(gpm, null); } catch (Exception) { }

            if (active && !dayActive) StartCoroutine(DayStarted(gpm));
            if (!active && dayActive) DayEnded();
            dayActive = active;

            if (!active || !cEnabled.Value) return;

            noiseRage = Mathf.Max(0f, noiseRage - 0.04f);   // złość z hałasu powoli opada
            if (IsHost()) UpdateHumans();
            if (today == Mod.Ciemnosc) ApplyDarkness();      // nowe lampy po wczytaniu pokoi
        }

        private IEnumerator DayStarted(object gpm)
        {
            yield return new WaitForSeconds(3f);
            if (!cEnabled.Value || !dayActive) yield break;

            int day = ReadInt(pCurrentDay, gpm), level = ReadInt(pCurrentLevel, gpm);
            System.Random rnd = new System.Random(day * 7919 + level * 131 + 17);   // ten sam u wszystkich
            today = Mod.Zwykly;
            if (cModifiers.Value && rnd.Next(100) >= Mathf.Clamp(cNormalChance.Value, 0, 100))
                today = (Mod)(1 + rnd.Next(ModNames.Length - 1));

            switch (today)
            {
                case Mod.Ciemnosc: ApplyDarkness(); break;
                case Mod.Mgla: ApplyFog(); break;
                case Mod.Grawitacja:
                    if (!gravitySaved) { gravityOrig = Physics.gravity; gravitySaved = true; }
                    Physics.gravity = gravityOrig * 0.6f; break;
            }
            string msg = "Dzień " + day + ": " + ModNames[(int)today];

            if (cTreasure.Value)
            {
                yield return new WaitForSeconds(2f);
                PickTreasure(rnd);
                if (treasure != null) msg += "\nSkarb dnia: " + treasureName + " (świeci na złoto)";
            }
            if (today == Mod.Zloto && IsHost()) ScaleAllRewards(1.5f);
            ShowBanner(msg, 8f);
        }

        private void DayEnded()
        {
            // przywracanie wszystkiego
            foreach (KeyValuePair<Light, float> kv in lightOrig) if (kv.Key != null) kv.Key.intensity = kv.Value;
            lightOrig.Clear();
            if (ambientOrig >= 0f) { RenderSettings.ambientIntensity = ambientOrig; ambientOrig = -1f; }
            if (fogSaved)
            {
                RenderSettings.fog = fogOrig; RenderSettings.fogDensity = fogDensityOrig;
                RenderSettings.fogColor = fogColorOrig; RenderSettings.fogMode = fogModeOrig; fogSaved = false;
            }
            if (gravitySaved) { Physics.gravity = gravityOrig; gravitySaved = false; }
            foreach (KeyValuePair<Component, float> kv in speedOrig)
                if (kv.Key != null && fMaxSpeed != null) fMaxSpeed.SetValue(kv.Key, kv.Value);
            speedOrig.Clear();
            rewardScaled.Clear();
            if (treasureLight != null) Destroy(treasureLight.gameObject);
            treasure = null; treasureLight = null;
            today = Mod.Zwykly; noiseRage = 0f; alarmUntil = 0f; loudTimes.Clear();
        }

        // ================= modyfikatory =================

        private void ApplyDarkness()
        {
            if (ambientOrig < 0f) { ambientOrig = RenderSettings.ambientIntensity; RenderSettings.ambientIntensity = ambientOrig * 0.35f; }
            foreach (Light l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l == null || lightOrig.ContainsKey(l) || l == treasureLight) continue;
                if (l.type == LightType.Directional) continue;   // słońce zostaje, gasną lampy
                lightOrig[l] = l.intensity;
                l.intensity = l.intensity * 0.15f;
            }
        }

        private void ApplyFog()
        {
            if (!fogSaved)
            {
                fogOrig = RenderSettings.fog; fogDensityOrig = RenderSettings.fogDensity;
                fogColorOrig = RenderSettings.fogColor; fogModeOrig = RenderSettings.fogMode; fogSaved = true;
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.12f;
            RenderSettings.fogColor = new Color(0.62f, 0.65f, 0.68f);
        }

        private void ScaleAllRewards(float mult)
        {
            if (stealType == null) return;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(stealType, FindObjectsSortMode.None))
                ScaleReward(o, mult);
        }

        // reward to ResourceAmounts - mnożymy każde pole liczbowe
        private void ScaleReward(object stealable, float mult)
        {
            if (fReward == null || stealable == null) return;
            try
            {
                object amounts = fReward.GetValue(stealable);
                if (amounts == null) return;
                foreach (FieldInfo f in amounts.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType == typeof(int)) f.SetValue(amounts, Mathf.CeilToInt((int)f.GetValue(amounts) * mult));
                    else if (f.FieldType == typeof(float)) f.SetValue(amounts, (float)f.GetValue(amounts) * mult);
                }
            }
            catch (Exception e) { Logger.LogWarning("Skalowanie lupu: " + e.Message); }
        }

        // ================= dziadek: prędkość, złość, alarm =================

        private void UpdateHumans()
        {
            if (humanType == null || aiBaseType == null || fMaxSpeed == null) return;
            float dayMult = today == Mod.Kawa ? 1.3f : (today == Mod.Zaspany ? 0.75f : 1f);
            bool alarm = Time.unscaledTime < alarmUntil;

            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(humanType, FindObjectsSortMode.None))
            {
                Component h = o as Component;
                if (h == null) continue;
                Component ai = h.GetComponent(aiBaseType);
                if (ai == null) ai = h.GetComponentInChildren(aiBaseType);
                if (ai == null) ai = h.GetComponentInParent(aiBaseType);
                if (ai == null) continue;
                if (!speedOrig.ContainsKey(ai)) speedOrig[ai] = (float)fMaxSpeed.GetValue(ai);

                float rage = noiseRage;
                if (pAnger != null)
                {
                    try { rage = Mathf.Max(rage, Mathf.Clamp01(Convert.ToSingle(pAnger.GetValue(h, null)))); } catch (Exception) { }
                }
                float angryMult = cAngry.Value ? 1f + Mathf.Clamp01(rage) * Mathf.Max(0f, cAngrySpeed.Value) : 1f;
                float alarmMult = alarm ? 1.4f : 1f;
                fMaxSpeed.SetValue(ai, speedOrig[ai] * dayMult * angryMult * alarmMult);
            }
        }

        // CreateNoiseEvent(Vector3 position, int loudness, AINoiseEventParameters p)
        private static void NoisePostfix(object __instance, object[] __args)
        {
            if (Instance == null || creatingNoise || !Instance.cEnabled.Value || !Instance.dayActive) return;
            try
            {
                if (__args == null || __args.Length < 3) return;
                Instance.OnNoise(__instance, (Vector3)__args[0], Convert.ToSingle(__args[1]), __args[2]);
            }
            catch (Exception) { }
        }

        private void OnNoise(object roomManager, Vector3 position, float loudness, object prms)
        {
            if (!IsHost()) return;
            noiseRage = Mathf.Clamp01(noiseRage + Mathf.Clamp(loudness, 0f, 10f) * 0.03f);
            if (noiseRage > 0.8f && Time.unscaledTime > bannerUntil) ShowBanner("Dziadek jest wściekły!", 3f);

            if (!cAlarm.Value || Time.unscaledTime < alarmUntil) return;
            float now = Time.unscaledTime;
            loudTimes.Add(now);
            loudTimes.RemoveAll(delegate (float t) { return now - t > 3f; });
            if (loudTimes.Count >= 5)
            {
                loudTimes.Clear();
                alarmUntil = now + 20f;
                ShowBanner("ALARM! Dziadek biegnie sprawdzić hałas!", 5f);
                // dodatkowy, bardzo głośny hałas w tym miejscu - przyciąga dziadka
                try
                {
                    MethodInfo m = AccessTools.Method(roomManager.GetType(), "CreateNoiseEvent");
                    creatingNoise = true;
                    m.Invoke(roomManager, new object[] { position, Mathf.Max(1, Mathf.RoundToInt(loudness)) * 4, prms });
                }
                catch (Exception) { }
                finally { creatingNoise = false; }
            }
        }

        // ================= skarb dnia =================

        private void PickTreasure(System.Random rnd)
        {
            if (stealType == null) return;
            List<Component> list = new List<Component>();
            PropertyInfo pId = null;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(stealType, FindObjectsSortMode.None))
            {
                Component c = o as Component;
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                if (pId == null) pId = c.GetType().GetProperty("NetworkObjectId");
                list.Add(c);
            }
            if (list.Count == 0) return;
            PropertyInfo idProp = pId;
            list.Sort(delegate (Component a, Component b)
            {
                ulong ia = 0, ib = 0;
                try { ia = Convert.ToUInt64(idProp.GetValue(a, null)); ib = Convert.ToUInt64(idProp.GetValue(b, null)); } catch (Exception) { }
                return ia.CompareTo(ib);
            });
            treasure = list[rnd.Next(list.Count)];
            treasureName = CleanName(treasure.gameObject.name);

            // złota poświata - widoczna u każdego gracza z modem
            GameObject lg = new GameObject("SkarbDnia_Light");
            lg.transform.SetParent(treasure.transform, false);
            treasureLight = lg.AddComponent<Light>();
            treasureLight.type = LightType.Point;
            treasureLight.color = new Color(1f, 0.78f, 0.2f);
            treasureLight.intensity = 3f;
            treasureLight.range = 1.5f;
            foreach (Renderer r in treasure.GetComponentsInChildren<Renderer>())
            {
                try
                {
                    foreach (Material m in r.materials)
                    {
                        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.Lerp(m.GetColor("_BaseColor"), new Color(1f, 0.8f, 0.1f), 0.7f));
                        if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", new Color(0.6f, 0.45f, 0.05f)); }
                    }
                }
                catch (Exception) { }
            }
            if (IsHost()) ScaleReward(treasure, Mathf.Max(1f, cTreasureMult.Value));
            Logger.LogInfo("Skarb dnia: " + treasureName);
        }

        private static string CleanName(string n)
        {
            n = n.Replace("(Clone)", "").Replace("_", " ").Trim();
            return n.Length > 0 ? n : "przedmiot";
        }

        // ================= napisy =================

        private void ShowBanner(string text, float seconds) { banner = text; bannerUntil = Time.unscaledTime + seconds; }

        private void OnGUI()
        {
            if (Time.unscaledTime >= bannerUntil || banner.Length == 0) return;
            if (bannerStyle == null)
            {
                bannerStyle = new GUIStyle(GUI.skin.box);
                bannerStyle.fontSize = 20;
                bannerStyle.alignment = TextAnchor.MiddleCenter;
                bannerStyle.wordWrap = true;
            }
            GUI.Box(new Rect(Screen.width / 2f - 300f, 90f, 600f, banner.Contains("\n") ? 80f : 48f), banner, bannerStyle);
        }
    }
}

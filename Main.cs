using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Newtonsoft.Json;
using UnityEngine;
using UnityModManagerNet;

namespace VVHairColors
{
    /// <summary>
    /// Gives any companion any hair color, including colors WOTR does not ship (green, blue, etc).
    /// Works by generating a gradient ramp texture at runtime and injecting it into the unit's hair
    /// EquipmentEntity, then selecting it on the live character doll. Choices persist to disk and are
    /// re-applied on area load (the doll is rebuilt each time an area loads).
    /// </summary>
    public static class Main
    {
        public static UnityModManager.ModEntry.ModLogger Log;
        private static string _savePath;

        // unitId -> chosen color. Serialized to colors.json in the mod folder.
        private static Dictionary<string, float[]> _colors = new Dictionary<string, float[]>();

        // The color the sliders are currently editing.
        private static float _r = 0.15f, _g = 0.75f, _b = 0.20f;

        // Set true on area load; OnUpdate keeps retrying until each stored doll is recolored.
        private static bool _needReapply;
        private static readonly HashSet<string> _appliedThisArea = new HashSet<string>();

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Log = modEntry.Logger;
            _savePath = Path.Combine(modEntry.Path, "colors.json");
            LoadColors();

            modEntry.OnGUI = OnGUI;
            modEntry.OnUpdate = (_, dt) => OnUpdate();
            EventBus.Subscribe(new AreaWatcher());

            Log.Log("[VVHairColors] loaded. Open the UMM menu in-game to recolor companions.");
            return true;
        }

        // ---------------- in-game settings panel (UMM menu) ----------------
        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            var party = Game.Instance?.Player?.Party;
            if (party == null || party.Count == 0)
            {
                GUILayout.Label("Load a save first, then reopen this menu.");
                return;
            }

            GUILayout.Label("Pick a color, then click a companion to apply it:");
            GUILayout.Space(6);

            Slider("R", ref _r);
            Slider("G", ref _g);
            Slider("B", ref _b);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Preview:", GUILayout.Width(70));
            var swatch = GUILayoutUtility.GetRect(120, 20);
            var prev = GUI.color;
            GUI.color = new Color(_r, _g, _b);
            GUI.DrawTexture(swatch, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Label("  #" + Hex(new Color(_r, _g, _b)));
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("Presets:");
            GUILayout.BeginHorizontal();
            Preset("Green", 0.15f, 0.75f, 0.20f);
            Preset("Blue", 0.15f, 0.35f, 0.95f);
            Preset("Purple", 0.55f, 0.20f, 0.85f);
            Preset("Pink", 0.95f, 0.30f, 0.65f);
            Preset("Silver", 0.80f, 0.82f, 0.88f);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            foreach (var unit in party)
            {
                if (unit == null) continue;
                GUILayout.BeginHorizontal();
                var current = _colors.TryGetValue(unit.UniqueId, out var c) ? "  (now #" + Hex(new Color(c[0], c[1], c[2])) + ")" : "";
                if (GUILayout.Button(unit.CharacterName + current, GUILayout.Width(280)))
                {
                    _colors[unit.UniqueId] = new[] { _r, _g, _b };
                    SaveColors();
                    if (!Apply(unit, new Color(_r, _g, _b)))
                        Log.Log("[VVHairColors] queued recolor for " + unit.CharacterName + " (doll not ready yet).");
                }
                if (_colors.ContainsKey(unit.UniqueId) && GUILayout.Button("Reset", GUILayout.Width(70)))
                {
                    _colors.Remove(unit.UniqueId);
                    SaveColors();
                    Log.Log("[VVHairColors] cleared saved color for " + unit.CharacterName + " (reload area to see vanilla hair).");
                }
                GUILayout.EndHorizontal();
            }
        }

        private static void Slider(string label, ref float v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + "  " + Mathf.RoundToInt(v * 255), GUILayout.Width(70));
            v = GUILayout.HorizontalSlider(v, 0f, 1f, GUILayout.Width(240));
            GUILayout.EndHorizontal();
        }

        private static void Preset(string name, float r, float g, float b)
        {
            if (GUILayout.Button(name, GUILayout.Width(70))) { _r = r; _g = g; _b = b; }
        }

        // ---------------- apply / persistence ----------------
        private static void OnUpdate()
        {
            if (!_needReapply) return;
            var party = Game.Instance?.Player?.Party;
            if (party == null) return;

            bool allDone = true;
            foreach (var unit in party)
            {
                if (unit == null) continue;
                if (_appliedThisArea.Contains(unit.UniqueId)) continue;
                if (!_colors.TryGetValue(unit.UniqueId, out var c)) { _appliedThisArea.Add(unit.UniqueId); continue; }
                if (Apply(unit, new Color(c[0], c[1], c[2]))) _appliedThisArea.Add(unit.UniqueId);
                else allDone = false; // doll not built yet; try again next frame
            }
            if (allDone) _needReapply = false;
        }

        internal static void OnAreaLoaded()
        {
            _appliedThisArea.Clear();
            _needReapply = _colors.Count > 0;
        }

        /// <summary>Inject a gradient ramp of <paramref name="color"/> and select it on the unit's hair.</summary>
        private static bool Apply(UnitEntityData unit, Color color)
        {
            try
            {
                var avatar = unit?.View?.CharacterAvatar;
                if (avatar == null) return false;
                var ees = avatar.EquipmentEntities;
                if (ees == null || ees.Count == 0) return false;

                var hair = ees.FirstOrDefault(e => e != null && e.name != null
                    && e.name.IndexOf("Hair", StringComparison.OrdinalIgnoreCase) >= 0
                    && e.name.IndexOf("Facial", StringComparison.OrdinalIgnoreCase) < 0
                    && e.name.IndexOf("Eyebrow", StringComparison.OrdinalIgnoreCase) < 0);
                if (hair == null) return false;

                var ramps = hair.PrimaryRamps as IList;
                if (ramps == null) return false;

                string rampName = "VVHair_" + Hex(color);
                int index = -1;
                for (int i = 0; i < ramps.Count; i++)
                    if (ramps[i] is Texture t && t.name == rampName) { index = i; break; }

                if (index < 0)
                {
                    var sample = ramps.Count > 0 ? ramps[0] as Texture : null;
                    int w = sample != null ? sample.width : 128;
                    int h = sample != null ? sample.height : 8;
                    ramps.Add(MakeRamp(w, h, color, rampName));
                    index = ramps.Count - 1;
                }

                avatar.SetPrimaryRampIndex(hair, index, true);
                Log.Log("[VVHairColors] " + unit.CharacterName + " hair -> #" + Hex(color) + " (ramp index " + index + ").");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[VVHairColors] apply failed for " + unit?.CharacterName + ": " + e);
                return true; // don't spin forever on a hard error
            }
        }

        // Dark -> mid -> full-color gradient the hair shader reads by luminance.
        private static Texture2D MakeRamp(int w, int h, Color color, string name)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp };
            var dark = new Color(color.r * 0.18f, color.g * 0.18f, color.b * 0.18f, 1f);
            var mid = new Color(color.r * 0.55f, color.g * 0.55f, color.b * 0.55f, 1f);
            var bright = Color.Lerp(color, Color.white, 0.12f); bright.a = 1f;
            for (int x = 0; x < w; x++)
            {
                float u = w > 1 ? x / (float)(w - 1) : 0f;
                Color col = u < 0.5f ? Color.Lerp(dark, mid, u * 2f) : Color.Lerp(mid, bright, (u - 0.5f) * 2f);
                for (int y = 0; y < h; y++) tex.SetPixel(x, y, col);
            }
            tex.Apply();
            return tex;
        }

        private static string Hex(Color c) =>
            Mathf.RoundToInt(c.r * 255).ToString("X2") +
            Mathf.RoundToInt(c.g * 255).ToString("X2") +
            Mathf.RoundToInt(c.b * 255).ToString("X2");

        private static void LoadColors()
        {
            try
            {
                if (File.Exists(_savePath))
                    _colors = JsonConvert.DeserializeObject<Dictionary<string, float[]>>(File.ReadAllText(_savePath))
                              ?? new Dictionary<string, float[]>();
            }
            catch (Exception e) { Log.Error("[VVHairColors] load colors failed: " + e); }
        }

        private static void SaveColors()
        {
            try { File.WriteAllText(_savePath, JsonConvert.SerializeObject(_colors)); }
            catch (Exception e) { Log.Error("[VVHairColors] save colors failed: " + e); }
        }
    }

    internal sealed class AreaWatcher : IAreaHandler
    {
        public void OnAreaBeginUnloading() { }
        public void OnAreaDidLoad() { Main.OnAreaLoaded(); }
    }
}

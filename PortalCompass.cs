// Portal Compass (was Portal Picker): a search-first destination picker for XPortal's portal panel.
// Copyright (C) 2026 Joost Liebregts. Builds on XPortal by SpikeHimself (https://github.com/SpikeHimself/XPortal).
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// It is distributed WITHOUT ANY WARRANTY; see <https://www.gnu.org/licenses/> for the full text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Finn.PortalCompass;

// Wireframe board D ("All in one, with tabs") on top of XPortal. XPortal keeps doing all linking, saving and syncing:
// its panel stays alive with its own widgets hidden, the picker takes their place inside the same wood panel, and every
// action goes through XPortal's own controls (dropdown value -> its OnDropdownValueChanged; Link/Ping/Cancel -> its buttons).
[BepInPlugin(Id, "Portal Compass", Version)]
[BepInDependency(XPortalGuid, BepInDependency.DependencyFlags.HardDependency)]
[BepInProcess("valheim.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "com.joostliebregts.portalpicker";   // kept from the Portal Picker days: config, pins and settings stay
    public const string Version = "1.0.0";
    public const string XPortalGuid = "yay.spikehimself.xportal";

    const float W = 980f, H = 720f, Pad = 20f, ContentTop = 156f, BottomBar = 72f, RowH = 40f, CompactH = 32f, FontScale = 1.15f;
    const int MaxPins = 6, RecentTiles = 4, UsedMax = 50;

    // Colours (plain; the Hearthwoven look comes later)
    // "Ember on soot": a warm near-black ground, three levels of text, one accent (ember) only for state and action. No white
    // surfaces. Contrast (WCAG): body text 11-14:1, meta 6.7-8:1, hints and placeholder >= 5.4:1, dark text on ember 8.9:1.
    static Color Hex(int rgb, float a = 1f) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    static readonly Color
        Ink = Hex(0xF2EBDD), Dim = Hex(0xBFB3A0), Faint = Hex(0x9C917F),          // text: primary, meta, hints
        Orange = Hex(0xF0A43A), PinOn = Hex(0xF0A43A), PinOff = Hex(0x7A705F),     // ember accent; an unpinned pin
        Backdrop = Hex(0x16130F, 0.98f), Box = Hex(0x100E0B),                      // ground inside the wood frame; the list well
        RowCol = Hex(0x221D17), SelCol = Hex(0x3A2A14),                            // rows; the selected row, tab or chip
        PinBox = Hex(0x1C1712), TileCol = Hex(0x2E2418),                           // the pinned shelf; a tile
        Field = Hex(0x221D17), Border = Hex(0x4A3F31),                             // inputs and secondary buttons get a 1 px border
        BtnCol = Hex(0x2A241C), TabOff = Hex(0x1F1A15), DarkBtn = Hex(0x16130F);

    static Sprite roundSprite;
    static BepInEx.Configuration.ConfigEntry<float> uiScale;
    static BepInEx.Configuration.ConfigEntry<int> scrollRows;
    static Sprite pinSprite, dotSprite, ringSprite;

    // Letter rail (rolodex): letters shown, the one under the pointer
    readonly List<(string l, Text t)> railLetters = new();
    string railHover;

    // Map tab
    RectTransform mapArea, mapListContent, mapListViewport, mapFrame, dotLayer;
    RawImage mapImage; Material mapMat; MapView mapView;
    GameObject mapOff, mapLabel; Text mapLabelText;
    readonly List<(Entry e, RectTransform rt, Image img)> dots = new();
    readonly HashSet<Entry> unexplored = new();   // portals in the fog: no dot, unless it is the selected one
    RectTransform hereDot;
    Vector2 mapCenter; float mapZoom = 0.1f; bool mapFitted, mapJustFitted, mapZoomDirty;
    const float DefaultZoom = 0.12f;   // about 3 km top to bottom on a standard world
    RectTransform selRing, hoverRing;
    CanvasGroup youTag;
    GameObject ctxMenu, ctxBlocker;
    Image pinButtonIcon;   // right-click menu on a portal
    // Other players on the map: only those who share their position (Valheim's own rule, GetOtherPublicPlayers)
    readonly List<(RectTransform rt, CanvasGroup tag, Text text)> playerDots = new();
    readonly List<ZNet.PlayerInfo> tmpPlayers = new();
    float playerRefresh;
    static readonly Color FriendBlue = Hex(0x9FD3FF);
    readonly List<CanvasGroup> nameTags = new();   // small names under the dots, parallel to dots, shown when zoomed in
    const float TagsFrom = 0.08f, TagsFull = 0.06f;   // zoom where the names start to fade in / are fully shown (smaller = closer)
    static readonly Color HoverGlow = Hex(0xFFE3A3);
    static MethodInfo mIsExplored;
    const float MinZoom = 0.01f;   // Valheim's own m_minZoom

    static Plugin instance;

    // XPortal internals (1.2.26), resolved once by name.
    static FieldInfo fMainPanel, fDropdown, fMapping, fThisPortal, fNameInput, fDefaultToggle, fPingButton;
    static object portalsManager; static MethodInfo mGetById;
    static PropertyInfo pPanelInstance;
    int adoptChecks = 30;   // after a live reload: take over a panel that is already open
    static object xpConfig;

    // XPortal's live objects for the open panel
    GameObject mainPanel; Dropdown dropdown; InputField xpName; Toggle xpDefault; Button xpOk, xpCancel, xpPing;
    // Our own top-level canvas: its scale factor makes Unity draw the text at the screen's real resolution (a transform
    // scale on Jotunn's 1:1 canvas only stretched 1080p-sized glyphs, which looked hazy on 4K).
    GameObject canvasGo, builtFor; CanvasScaler scaler; Image mainImg; bool hidMainImg;
    readonly List<GameObject> xpWidgets = new();
    XpPortal thisPortal;

    // Our UI
    GameObject root; Font font;
    InputField search, nameField;
    Text countLabel, selName, selInfo, pinLabel;
    Image defaultBox;
    readonly List<(string id, Image img, Text t)> tabs = new();
    RectTransform quickArea, listArea, listContent, listViewport, railArea;
    ScrollRect listScroll;
    GameObject chipsRow, pingButton, clearButton;
    readonly List<(string id, Image img, Text t)> chips = new();

    // State
    sealed class Entry { public int Index; public string Key, Name; public float Meters; public Vector3 Pos; }
    // Row and Col (centre, 0..1) place each selectable on screen, so Up/Down move to the nearest item in the row above/below.
    sealed class Nav { public Entry Entry; public Image Back; public GameObject Bar; public Color Normal; public int Row; public float Col = 0.5f; }
    readonly List<Entry> entries = new();
    readonly List<Nav> nav = new();
    int sel = -1, focusIn = -1, navRow;
    string tab = "quick", mode = "az", lastSearch = "";
    bool defaultOn, noDistance, caretEnd;
    Store.Entry store;
    Entry beforeKey;

    bool Active => root && root.activeInHierarchy;

    // ---------- startup: find XPortal's internals, patch narrowly ----------

    Harmony harmony;

    // A live reload while XPortal's panel is open: the new copy takes it over in its first frames.
    void AdoptOpenPanel()
    {
        if (adoptChecks <= 0) return;
        adoptChecks--;
        try
        {
            var panel = pPanelInstance?.GetValue(null);
            var go = panel == null ? null : fMainPanel.GetValue(panel) as GameObject;
            if (go && go.activeInHierarchy) { adoptChecks = 0; Open(panel); }
        }
        catch (Exception e) { adoptChecks = 0; Logger.LogWarning($"Could not take over the open panel: {e.Message}"); }
    }

    // Live reload (ScriptEngine) or game exit: put XPortal's panel back, remove our patches and our canvas.
    private void OnDestroy()
    {
        try { if (Active) Close(); } catch { }
        try { harmony?.UnpatchSelf(); } catch { }
        if (canvasGo) Destroy(canvasGo);
        if (mapMat) Destroy(mapMat);
        if (instance == this) instance = null;
    }

    private void Awake()
    {
        instance = this;
        uiScale = Config.Bind("Panel", "Size", 1f, new BepInEx.Configuration.ConfigDescription(
            "Size of the portal panel. 1 = as big as Valheim's own menus at your resolution and GUI scale (the same on a 1080p and a 4K screen).",
            new BepInEx.Configuration.AcceptableValueRange<float>(0.6f, 1.6f)));
        scrollRows = Config.Bind("Panel", "Rows per scroll step", 4, new BepInEx.Configuration.ConfigDescription(
            "How many rows of the list one notch of the mouse wheel scrolls.", new BepInEx.Configuration.AcceptableValueRange<int>(1, 12)));
        if (!TryInit(final: false)) initWait = 120f;   // XPortal not up yet (loaded early, e.g. by ScriptEngine at start): retry in Update
    }

    float initWait, initRetry;
    bool initDone;

    // Find XPortal's internals and patch. Only once XPortal itself has started (its config and managers exist); before that,
    // returns false and Update tries again, so this copy can be loaded early (ScriptEngine LoadOnStart).
    bool TryInit(bool final)
    {
        if (initDone) return true;
        if (!(BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(XPortalGuid, out var xp) && xp.Instance))
        {
            if (final) Logger.LogError("XPortal never started. Portal Compass stays off.");
            return false;
        }
        var panelType = AccessTools.TypeByName("XPortal.UI.PortalConfigurationPanel");
        var managerType = AccessTools.TypeByName("XPortal.KnownPortalsManager");
        pPanelInstance = panelType == null ? null : AccessTools.Property(panelType, "Instance");
        var configure = panelType == null ? null : AccessTools.Method(panelType, "ConfigurePortal");
        var handleInput = panelType == null ? null : AccessTools.Method(panelType, "HandleInput");
        fMainPanel = AccessTools.Field(panelType, "mainPanel");
        fDropdown = AccessTools.Field(panelType, "targetPortalDropdown");
        fMapping = AccessTools.Field(panelType, "dropdownIndexToZDOIDMapping");
        fThisPortal = AccessTools.Field(panelType, "thisPortal");
        fNameInput = AccessTools.Field(panelType, "portalNameInputField");
        fDefaultToggle = AccessTools.Field(panelType, "defaultPortalToggle");
        fPingButton = AccessTools.Field(panelType, "pingMapButtonObject");
        portalsManager = managerType == null ? null : AccessTools.Property(managerType, "Instance")?.GetValue(null);
        mGetById = managerType == null ? null : AccessTools.Method(managerType, "GetKnownPortalById");
        try { xpConfig = AccessTools.Property(AccessTools.TypeByName("XPortal.XPortalConfig"), "Instance")?.GetValue(null); } catch { }

        if (configure == null || handleInput == null || fMainPanel == null || fDropdown == null || fMapping == null || fThisPortal == null
            || fNameInput == null || fDefaultToggle == null || fPingButton == null || portalsManager == null || mGetById == null)
        {
            Logger.LogError("XPortal internals not found (expected XPortal 1.2.26). Portal Compass stays off; XPortal's own panel works as before.");
            initDone = true;   // nothing to retry: XPortal is there, its internals aren't
            return true;
        }
        // A fresh Harmony id per load: under ScriptEngine (live reload) the new copy loads before the old one is destroyed,
        // and each copy must unpatch only its own patches.
        harmony = new Harmony(Id + "." + Guid.NewGuid().ToString("N"));
        harmony.Patch(configure, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterConfigure)));
        harmony.Patch(handleInput, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeHandleInput)));
        var teleport = AccessTools.Method(typeof(TeleportWorld), "Teleport", new[] { typeof(Player) });
        if (teleport != null) harmony.Patch(teleport, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterTeleport)));
        else Logger.LogWarning("TeleportWorld.Teleport(Player) not found: 'last used' will not be recorded.");
        Logger.LogInfo($"Portal Compass {Version} on top of XPortal.");
        initDone = true;
        return true;
    }

    // XPortal opened its panel for a portal (ConfigurePortal = populate + show): take over its content.
    static void AfterConfigure(object __instance)
    {
        if (!instance) return;   // between a live reload's unload and load
        try { instance.Open(__instance); }
        catch (Exception e)
        {
            instance.Logger.LogError($"Picker failed to open, falling back to XPortal's own panel: {e}");
            instance.Close();
        }
    }

    // XPortal maps Up/Down (and the D-pad) to scrolling its dropdown; while the picker is open those inputs are ours.
    static bool BeforeHandleInput() => !(instance && instance.Active);

    // "Last used" = the local player actually went through a portal; remember where it led.
    static void AfterTeleport(Player player, ZNetView ___m_nview)
    {
        try
        {
            if (!player || player != Player.m_localPlayer || !player.IsTeleporting() || !___m_nview || ___m_nview.GetZDO() == null) return;
            var from = Portal(___m_nview.GetZDO().m_uid);
            var to = from == null || !from.HasTarget() ? null : Portal(from.Target);
            if (to == null) return;
            var s = Store.For(StoreKey());
            string key = KeyOf(to.Location);
            s.used.RemoveAll(u => u.k == key);
            s.used.Insert(0, new Store.Used { k = key, t = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            if (s.used.Count > UsedMax) s.used.RemoveRange(UsedMax, s.used.Count - UsedMax);
            Store.Save();
        }
        catch (Exception e) { instance.Logger.LogWarning($"Could not record the teleport: {e.Message}"); }
    }

    static XpPortal Portal(ZDOID id)
    {
        try { return XpPortal.Of(mGetById.Invoke(portalsManager, new object[] { id })); }
        catch (TargetInvocationException) { return null; }   // XPortal's lookup throws for an id it doesn't know
    }

    // ZDOIDs change between sessions (XPortal tracks PreviousId for that); positions don't. XPortal keys its default portal
    // by the truncated position too.
    static string KeyOf(Vector3 p) => $"{(int)p.x},{(int)p.y},{(int)p.z}";

    static string StoreKey()
    {
        string player = Game.instance?.GetPlayerProfile()?.GetName() ?? "player";
        string world = ZNet.instance ? ZNet.instance.GetWorldName() : "world";
        return player + "@" + world;
    }

    // XPortal's KnownPortal, read through reflection: no compile-time link to XPortal.dll, so this mod can load before
    // XPortal (ScriptEngine at game start) without pulling XPortal's assembly in early.
    sealed class XpPortal
    {
        static PropertyInfo pLocation, pTarget; static MethodInfo mFriendly, mHasTarget;
        readonly object o;
        XpPortal(object o)
        {
            this.o = o;
            if (pLocation != null) return;
            var t = o.GetType();
            pLocation = AccessTools.Property(t, "Location"); pTarget = AccessTools.Property(t, "Target");
            mFriendly = AccessTools.Method(t, "GetFriendlyName"); mHasTarget = AccessTools.Method(t, "HasTarget");
        }
        public static XpPortal Of(object o) => o == null ? null : new XpPortal(o);
        public Vector3 Location => (Vector3)pLocation.GetValue(o);
        public ZDOID Target => (ZDOID)pTarget.GetValue(o);
        public string GetFriendlyName() => (string)mFriendly.Invoke(o, null);
        public bool HasTarget() => (bool)mHasTarget.Invoke(o, null);
    }

    // ---------- open / close ----------

    void Open(object panel)
    {
        mainPanel = (GameObject)fMainPanel.GetValue(panel);
        dropdown = (Dropdown)fDropdown.GetValue(panel);
        xpName = (InputField)fNameInput.GetValue(panel);
        xpDefault = (Toggle)fDefaultToggle.GetValue(panel);
        thisPortal = XpPortal.Of(fThisPortal.GetValue(panel));
        var pingObj = (GameObject)fPingButton.GetValue(panel);
        if (!mainPanel || !dropdown || !xpName || !xpDefault || thisPortal == null) return;
        xpPing = pingObj ? pingObj.GetComponent<Button>() : null;
        xpOk = mainPanel.transform.Find("XPortal_OkayButton")?.GetComponent<Button>();
        xpCancel = mainPanel.transform.Find("XPortal_CancelButton")?.GetComponent<Button>();
        if (!xpOk || !xpCancel) throw new Exception("XPortal's OK/Cancel buttons not found");
        font = dropdown.captionText ? dropdown.captionText.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // The portal list exactly as XPortal built it for this portal (index -> ZDOID), with names and positions.
        entries.Clear();
        foreach (var kv in (Dictionary<int, ZDOID>)fMapping.GetValue(panel))
        {
            if (kv.Value == ZDOID.None) { entries.Add(new Entry { Index = kv.Key, Key = "", Name = Localization.instance.Localize("$piece_portal_target_none"), Meters = -1 }); continue; }
            var p = Portal(kv.Value);
            if (p == null) continue;
            entries.Add(new Entry { Index = kv.Key, Key = KeyOf(p.Location), Name = p.GetFriendlyName(), Pos = p.Location, Meters = Vector3.Distance(thisPortal.Location, p.Location) });
        }

        // XPortal's panel stays alive but out of sight (its widgets and their Enter/Esc/gamepad bindings, and its wood
        // image); the picker shows on its own canvas, in a copy of that wood frame.
        if (!root || !canvasGo || builtFor != mainPanel) Build();
        xpWidgets.Clear();
        foreach (Transform c in mainPanel.transform) xpWidgets.Add(c.gameObject);
        HideXPortalWidgets();
        mainImg = mainPanel.GetComponent<Image>();
        if (mainImg && mainImg.enabled) { mainImg.enabled = false; hidMainImg = true; }
        ApplyScale();
        canvasGo.SetActive(true);
        store = Store.For(StoreKey());
        noDistance = ServerFlag("HidePortalDistance");
        foreach (var c in chips) if (c.id == "nearest") c.img.transform.gameObject.SetActive(!noDistance);
        root.SetActive(true);
        nameField.SetTextWithoutNotify(xpName.text);
        defaultOn = xpDefault.isOn; ShowDefault();
        mapFitted = false;
        if (mapMat) { Destroy(mapMat); mapMat = null; }
        foreach (var t in tabs) if (t.id == "map") t.img.GetComponent<Button>().interactable = MapAvailable();
        tab = Store.Data.lastTab == "list" ? "list" : Store.Data.lastTab == "map" && MapAvailable() ? "map" : "quick";
        mode = "az";
        search.SetTextWithoutNotify(""); lastSearch = "";
        Refresh(selectCurrent: true);
        focusIn = 1;   // XPortal's own queued focus targets its (now inactive) name field and does nothing
    }

    // Same size as Valheim's own menus: 1920x1080 reference x the game's GUI scale x our Size setting, never past the screen.
    void ApplyScale()
    {
        if (!scaler) return;
        float gui = 1f;
        try { gui = PlatformPrefs.GetFloat("GuiScale", 1f); } catch { }
        float want = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f) * gui * uiScale.Value;
        float fit = Mathf.Min(Screen.height * 0.96f / H, Screen.width * 0.97f / W);
        float f = Mathf.Max(0.1f, Mathf.Min(want, fit));
        if (!Mathf.Approximately(scaler.scaleFactor, f)) scaler.scaleFactor = f;
    }

    void HideXPortalWidgets()
    {
        foreach (var go in xpWidgets) if (go && go.activeSelf) go.SetActive(false);
    }

    // Puts XPortal's panel back as it was (on close, and as the fallback when opening failed).
    void Close()
    {
        CloseContext();
        if (canvasGo) canvasGo.SetActive(false);
        if (mapZoomDirty) { Store.Save(); mapZoomDirty = false; }
        foreach (var go in xpWidgets) if (go) go.SetActive(true);
        if (hidMainImg && mainImg) mainImg.enabled = true;
        hidMainImg = false;
        // XPortal decides the Ping button's visibility itself (target set, map allowed); mirror its rule.
        if (xpPing) xpPing.gameObject.SetActive(SelectedEntry() is Entry e && e.Key != "" && PingAllowed());
        xpWidgets.Clear();
    }

    bool PingAllowed() => !(ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey("nomap")) && !ServerFlag("PingMapDisabled");

    // XPortal's server-enforced settings (XPortalConfig.Instance.Server.<field>)
    static bool ServerFlag(string field)
    {
        try { return Traverse.Create(Traverse.Create(xpConfig).Property("Server").GetValue()).Field(field).GetValue() is bool b && b; }
        catch { return false; }
    }

    // ---------- the list model ----------

    Store.Entry S => store ??= Store.For(StoreKey());
    IEnumerable<Entry> Portals => entries.Where(e => e.Key != "");
    bool Pinned(Entry e) => e.Key != "" && S.pinned.Contains(e.Key);
    long UsedAt(Entry e) => S.used.FirstOrDefault(u => u.k == e.Key)?.t ?? 0;
    string Q => search ? search.text.Trim() : "";

    static int Rank(Entry e, string q)
    {
        int i = e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        if (i == 0) return 0;
        return char.IsLetterOrDigit(e.Name[i - 1]) ? 2 : 1;   // a word start beats the middle of a word
    }

    // Matches, best first: name starts with it, then a word starts with it, then anywhere; nearer first within each.
    List<Entry> Matches(string q) => Portals.Select(e => (e, r: Rank(e, q))).Where(x => x.r >= 0)
        .OrderBy(x => x.r).ThenBy(x => noDistance ? 0 : x.e.Meters).ThenBy(x => x.e.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.e).ToList();

    Entry SelectedEntry() => dropdown ? entries.FirstOrDefault(e => e.Index == dropdown.value) : null;

    // The server can hide distances (XPortal's HidePortalDistance); then no distance, direction or Nearest either.
    string Dist(float m) => m < 0 || noDistance ? "" : m >= 1000 ? $"{m / 1000:0.0} km" : $"{(int)m} m";

    static string When(long t)
    {
        if (t <= 0) return "";
        var d = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t;
        if (d < 60) return "just now";
        if (d < 3600) return $"{d / 60} min ago";
        if (d < 86400) return $"{d / 3600} h ago";
        if (d < 172800) return "yesterday";
        return $"{d / 86400} days ago";
    }

    string Direction(Entry e)
    {
        if (noDistance || e.Meters < 50) return "";
        float dx = e.Pos.x - thisPortal.Location.x, dz = e.Pos.z - thisPortal.Location.z;
        float a = (Mathf.Atan2(dx, dz) * Mathf.Rad2Deg + 360f + 22.5f) % 360f;
        return new[] { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" }[(int)(a / 45f) % 8];
    }

    // ---------- rebuild the visible tab ----------

    void Refresh(bool selectCurrent = false, Entry keep = null)
    {
        string q = Q;
        Clear(quickArea); Clear(listContent); Clear(railArea); railLetters.Clear(); Clear(mapListContent); dots.Clear(); unexplored.Clear(); nameTags.Clear(); playerDots.Clear(); hereDot = null; youTag = null; selRing = null; hoverRing = null; Clear(dotLayer);
        nav.Clear(); navRow = 0;
        quickArea.gameObject.SetActive(tab == "quick");
        listArea.gameObject.SetActive(tab == "list");
        chipsRow.SetActive(tab == "list");
        mapArea.gameObject.SetActive(tab == "map");
        foreach (var t in tabs)
        {
            bool on = t.id == tab;
            bool off = t.id == "map" && !MapAvailable();
            t.img.color = off ? Hex(0x15130F) : on ? SelCol : TabOff;
            t.t.color = off ? Faint : on ? Orange : Dim;
            Underline(t.img.transform, on);
        }
        foreach (var c in chips) { bool on = c.id == mode; c.img.color = on ? SelCol : TabOff; c.t.color = on ? Orange : Dim; Underline(c.img.transform, on); }

        int total = Portals.Count();
        var hits = q.Length > 0 ? Matches(q) : Portals.ToList();
        clearButton.SetActive(search.text.Length > 0);
        countLabel.text = q.Length == 0 ? $"{total} portals" : hits.Count == 0 ? "no match" : hits.Count == 1 ? "1 match · Enter to link" : $"{hits.Count} of {total} · Enter = top match";

        if (tab == "quick") BuildQuick(q, hits); else if (tab == "map") BuildMap(q, hits); else BuildList(q, hits);

        // Typing jumps to the top match (and makes it the destination); otherwise keep what was selected.
        int idx = -1;
        bool setTarget = false;
        if (q.Length > 0 && keep == null) { idx = hits.Count > 0 ? nav.FindIndex(n => n.Entry == hits[0]) : -1; setTarget = idx >= 0; }
        else
        {
            var want = keep ?? SelectedEntry();
            idx = nav.FindIndex(n => n.Entry == want);
        }
        Select(idx, setTarget);
        RoundAll(quickArea); RoundAll(listContent); RoundAll(mapListContent);
        if (ContextOpen) ctxMenu.transform.SetAsLastSibling();
    }

    void BuildQuick(string q, List<Entry> hits)
    {
        if (q.Length > 0)
        {
            var box = Panel("Matches", quickArea, Box);
            var vl = box.AddComponent<VerticalLayoutGroup>(); vl.childControlHeight = vl.childControlWidth = true; vl.childForceExpandHeight = false; vl.spacing = 2; vl.padding = new RectOffset(2, 2, 2, 2);
            box.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            foreach (var e in hits.Take(4)) Row(box.transform, e, withWhen: false);
            if (hits.Count == 0) Hint(box.transform, "No portal matches. Esc clears the search.");
            if (hits.Count > 4) Hint(box.transform, $"{hits.Count - 4} more: keep typing, or see them all on the List tab (Ctrl+2).");
        }

        // Pinned: tiles 3 wide, keys 1-6, in pin order
        var pins = S.pinned.Select(k => Portals.FirstOrDefault(e => e.Key == k)).Where(e => e != null).Take(MaxPins).ToList();
        var pinBox = Panel("Pinned", quickArea, PinBox);
        var pv = pinBox.AddComponent<VerticalLayoutGroup>(); pv.padding = new RectOffset(12, 12, 8, 12); pv.spacing = 8; pv.childControlHeight = pv.childControlWidth = true; pv.childForceExpandHeight = false;
        pinBox.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        SectionHead(pinBox.transform, "PINNED", q.Length == 0 ? "1-6 to jump · Ctrl+P pins the selected portal" : "");
        if (pins.Count == 0) Hint(pinBox.transform, "Nothing pinned yet. Select a portal and press Pin.");
        else
        {
            var grid = Grid(pinBox.transform, 3, 76f);
            for (int i = 0; i < pins.Count; i++) Tile(grid, pins[i], (i + 1).ToString(), Dist(pins[i].Meters), TileCol, pinned: true, navigable: q.Length == 0, navRow + i / 3, (i % 3 + 0.5f) / 3f);
            navRow += (pins.Count + 2) / 3;
        }

        // Recent: where you last travelled to, pinned ones not repeated (left out while typing: the matches need the room)
        if (q.Length > 0) return;
        var rec = Portals.Where(e => !Pinned(e) && UsedAt(e) > 0).OrderByDescending(UsedAt).Take(RecentTiles).ToList();
        var recBox = Panel("Recent", quickArea, new Color(0, 0, 0, 0));
        var rv = recBox.AddComponent<VerticalLayoutGroup>(); rv.padding = new RectOffset(12, 12, 4, 4); rv.spacing = 6; rv.childControlHeight = rv.childControlWidth = true; rv.childForceExpandHeight = false;
        recBox.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        SectionHead(recBox.transform, "RECENT", "");
        if (rec.Count == 0) Hint(recBox.transform, "Portals you travel to show up here.");
        else
        {
            var grid = Grid(recBox.transform, 4, 62f);
            for (int j = 0; j < rec.Count; j++) Tile(grid, rec[j], "", string.Join(" · ", new[] { When(UsedAt(rec[j])), Dist(rec[j].Meters) }.Where(x => x != "")), RowCol, pinned: false, navigable: true, navRow + j / 4, (j % 4 + 0.5f) / 4f);
            navRow += (rec.Count + 3) / 4;
        }

        // Nearest: so the Quick tab is useful before anything is pinned or used (rows as many as fit)
        if (noDistance) return;
        int pinRows = (pins.Count + 2) / 3;
        float used = (pins.Count == 0 ? 84f : 50f + pinRows * 76f + (pinRows - 1) * 10f) + (rec.Count == 0 ? 70f : 98f) + 20f;
        float room = (H - ContentTop - BottomBar - 8f) - used - 10f - 22f - 14f;
        int fit = Mathf.Clamp(Mathf.FloorToInt(room / (RowH + 2f)), 0, 6);
        var near = Portals.Where(e => !Pinned(e) && !rec.Contains(e)).OrderBy(e => e.Meters).Take(fit).ToList();
        if (near.Count == 0) return;
        var nearBox = Panel("Nearest", quickArea, new Color(0, 0, 0, 0));
        var nv = nearBox.AddComponent<VerticalLayoutGroup>(); nv.padding = new RectOffset(12, 12, 4, 4); nv.spacing = 2; nv.childControlHeight = nv.childControlWidth = true; nv.childForceExpandHeight = false;
        nearBox.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        SectionHead(nearBox.transform, "NEAREST", "");
        foreach (var e in near) Row(nearBox.transform, e, withWhen: false);
    }

    void BuildList(string q, List<Entry> hits)
    {
        IEnumerable<Entry> list = hits;
        if (mode == "recent") list = hits.Where(e => UsedAt(e) > 0).OrderByDescending(UsedAt);
        else if (mode == "pinned") list = hits.Where(Pinned).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        else if (mode == "nearest") list = hits.OrderBy(e => e.Meters);
        else if (q.Length == 0) list = hits.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var shown = list.ToList();

        if (mode == "az" && q.Length == 0)
        {
            var none = entries.FirstOrDefault(e => e.Key == "");
            if (none != null) Row(listContent, none, withWhen: false);
        }
        foreach (var e in shown) Row(listContent, e, withWhen: true);
        if (shown.Count == 0) Hint(listContent, mode == "recent" ? "No portal used yet." : mode == "pinned" ? "Nothing pinned yet." : "No portal matches.");
        listScroll.verticalNormalizedPosition = 1f;

        // Letter rail (A-Z, no search): jump to the first portal with that letter
        bool rail = mode == "az" && q.Length == 0;
        railArea.gameObject.SetActive(rail);
        if (!rail) return;
        foreach (var letter in shown.Select(e => Letter(e.Name)).Distinct())
        {
            var b = NewUi("L", railArea); b.AddComponent<LayoutElement>().flexibleHeight = 1;
            var t = Label(b.transform, letter, 13, Dim); t.alignment = TextAnchor.MiddleCenter; t.fontStyle = FontStyle.Bold;
            t.resizeTextForBestFit = true; t.resizeTextMinSize = 8; t.resizeTextMaxSize = Mathf.RoundToInt(13 * FontScale);
            railLetters.Add((letter, t));
        }
    }

    // ---------- map tab ----------

    // Valheim's own explored map, with its fog of war: nothing unexplored is revealed. Off when the server has no map
    // (nomap) or hides distances (a map would show them anyway).
    bool MapAvailable() => Minimap.instance && Minimap.instance.m_mapImageLarge && Minimap.instance.m_mapTexture
        && !(ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey("nomap")) && !noDistance;

    void BuildMapArea()
    {
        var ma = NewUi("Map", root.transform); mapArea = (RectTransform)ma.transform;
        Stretch(mapArea, Pad, ContentTop, Pad, BottomBar + 8);

        // Left: the portals, nearest first
        var sv = NewUi("MapList", ma.transform); var svrt = (RectTransform)sv.transform;
        svrt.anchorMin = new Vector2(0, 0); svrt.anchorMax = new Vector2(0, 1); svrt.pivot = new Vector2(0, 0.5f); svrt.sizeDelta = new Vector2(260, 0); svrt.anchoredPosition = Vector2.zero;
        sv.AddComponent<Image>().color = Box;
        var scroll = sv.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 0f;
        var vp = NewUi("Viewport", sv.transform); mapListViewport = (RectTransform)vp.transform;
        mapListViewport.anchorMin = Vector2.zero; mapListViewport.anchorMax = Vector2.one; mapListViewport.offsetMin = new Vector2(6, 6); mapListViewport.offsetMax = new Vector2(-6, -6);
        vp.AddComponent<RectMask2D>(); vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
        var ct = NewUi("Content", vp.transform); mapListContent = (RectTransform)ct.transform;
        mapListContent.anchorMin = new Vector2(0, 1); mapListContent.anchorMax = new Vector2(1, 1); mapListContent.pivot = new Vector2(0.5f, 1); mapListContent.sizeDelta = Vector2.zero;
        var vl = ct.AddComponent<VerticalLayoutGroup>(); vl.spacing = 4; vl.childControlHeight = vl.childControlWidth = true; vl.childForceExpandHeight = false; vl.childForceExpandWidth = true;
        ct.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = mapListViewport; scroll.content = mapListContent;

        // Right: the map, with a layer of portal dots on top
        var fr = NewUi("MapFrame", ma.transform); mapFrame = (RectTransform)fr.transform;
        mapFrame.anchorMin = Vector2.zero; mapFrame.anchorMax = Vector2.one; mapFrame.offsetMin = new Vector2(276, 0); mapFrame.offsetMax = Vector2.zero;
        fr.AddComponent<Image>().color = Box;
        fr.AddComponent<RectMask2D>();
        mapView = fr.AddComponent<MapView>(); mapView.Owner = this;
        var mi = NewUi("MapImage", fr.transform); var mirt = (RectTransform)mi.transform;
        mirt.anchorMin = Vector2.zero; mirt.anchorMax = Vector2.one; mirt.offsetMin = mirt.offsetMax = Vector2.zero;
        mapImage = mi.AddComponent<RawImage>(); mapImage.raycastTarget = false;
        var dl = NewUi("Dots", fr.transform); dotLayer = (RectTransform)dl.transform;
        dotLayer.anchorMin = Vector2.zero; dotLayer.anchorMax = Vector2.one; dotLayer.offsetMin = dotLayer.offsetMax = Vector2.zero;

        // The selected portal's name, next to its dot
        mapLabel = NewUi("Label", fr.transform); var lrt = (RectTransform)mapLabel.transform;
        lrt.anchorMin = lrt.anchorMax = Vector2.zero; lrt.pivot = new Vector2(0, 0.5f);
        var mlImg = mapLabel.AddComponent<Image>(); mlImg.color = Hex(0x0C0A08, 0.92f); mlImg.raycastTarget = false;   // click-through: dots under the label stay pickable Bordered(mapLabel);
        var hl = mapLabel.AddComponent<HorizontalLayoutGroup>(); hl.padding = new RectOffset(10, 10, 4, 4); hl.childControlWidth = hl.childControlHeight = true;
        mapLabel.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        mapLabel.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        mapLabelText = Label(mapLabel.transform, "", 16, Ink); mapLabelText.fontStyle = FontStyle.Bold;
        mapLabel.SetActive(false);

        var hint = NewUi("Hint", fr.transform); var hrt = (RectTransform)hint.transform;
        hrt.anchorMin = hrt.anchorMax = new Vector2(0, 1); hrt.pivot = new Vector2(0, 1); hrt.anchoredPosition = new Vector2(10, -10);
        var hintImg = hint.AddComponent<Image>(); hintImg.color = Hex(0x0C0A08, 0.85f); hintImg.raycastTarget = false;   // click-through too
        var hh = hint.AddComponent<HorizontalLayoutGroup>(); hh.padding = new RectOffset(10, 10, 4, 4); hh.childControlWidth = hh.childControlHeight = true;
        var hf = hint.AddComponent<ContentSizeFitter>(); hf.horizontalFit = hf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        Label(hint.transform, "Click a dot to pick · wheel to zoom · drag to move", 13, Dim);

        mapOff = NewUi("Off", fr.transform); var ort = (RectTransform)mapOff.transform; ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one; ort.offsetMin = ort.offsetMax = Vector2.zero;
        var ot = Label(mapOff.transform, "The map is off on this server.", 17, Faint); ot.alignment = TextAnchor.MiddleCenter;
        mapOff.SetActive(false);
    }

    void BuildMap(string q, List<Entry> hits)
    {
        bool ok = MapAvailable();
        mapOff.SetActive(!ok);
        mapImage.enabled = ok;
        if (!ok) return;
        EnsureMapMaterial();

        // Left list A-Z (while typing: the matches, best first, so Enter takes the top one)
        var list = q.Length > 0 ? hits : hits.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var none = entries.FirstOrDefault(e => e.Key == "");
        if (q.Length == 0 && none != null) Row(mapListContent, none, withWhen: false, compact: true);
        foreach (var e in list) Row(mapListContent, e, withWhen: false, compact: true);

        // Dots: pinned portals as pins, the rest as dots; this portal as a ring. A portal in a part of the world you
        // haven't explored gets no dot (its exact spot would give the place away); it stays in the list.
        foreach (var e in list)
        {
            if (!Explored(e.Pos)) unexplored.Add(e);
            var go = NewUi("Dot", dotLayer); var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.AddComponent<Image>(); img.preserveAspect = true;
            var b = go.AddComponent<Button>(); b.targetGraphic = img; NoNav(b);
            var entry = e;
            b.onClick.AddListener(() => { int i = nav.FindIndex(n => n.Entry == entry); if (i >= 0) Select(i, setTarget: true); focusIn = 1; });
            { var rc = go.AddComponent<RightClick>(); rc.OnRight = pos => ShowContext(entry, pos); rc.OnDouble = () => LinkTo(entry); }
            var hv = go.AddComponent<Hover>();
            hv.Show = () => { ShowMapLabel(entry, rt); ShowHoverRing(rt); };
            hv.Hide = () => { ShowMapLabel(SelectedEntry(), null); ShowHoverRing(null); };
            dots.Add((e, rt, img));
            nameTags.Add(NameTag(e.Name));
        }
        // You: Valheim's own player arrow from the big map, turned the way you face
        var here = NewUi("Here", dotLayer); hereDot = (RectTransform)here.transform;
        hereDot.anchorMin = hereDot.anchorMax = Vector2.zero; hereDot.pivot = new Vector2(0.5f, 0.5f); hereDot.sizeDelta = new Vector2(44, 44);
        var hi = here.AddComponent<Image>(); hi.raycastTarget = false; hi.preserveAspect = true;
        var marker = Minimap.instance.m_largeMarker ? Minimap.instance.m_largeMarker.GetComponent<Image>() : null;
        if (marker && marker.sprite) { hi.sprite = marker.sprite; hi.color = marker.color; } else { hi.sprite = RingSprite(); hi.color = Ink; }
        youTag = NameTag("You"); youTag.GetComponentInChildren<Text>().color = Orange;
        var hr = NewUi("HoverRing", dotLayer); hoverRing = (RectTransform)hr.transform;
        hoverRing.anchorMin = hoverRing.anchorMax = Vector2.zero; hoverRing.pivot = new Vector2(0.5f, 0.5f); hoverRing.sizeDelta = new Vector2(34, 34);
        var hri = hr.AddComponent<Image>(); hri.sprite = RingSprite(); hri.color = new Color(1f, 1f, 1f, 0.85f); hri.raycastTarget = false;
        hr.SetActive(false);
        var ring = NewUi("SelRing", dotLayer); selRing = (RectTransform)ring.transform;
        selRing.anchorMin = selRing.anchorMax = Vector2.zero; selRing.pivot = new Vector2(0.5f, 0.5f);
        var ri = ring.AddComponent<Image>(); ri.sprite = RingSprite(); ri.color = PinOn; ri.raycastTarget = false;

        // Open on where you stand, at the zoom you used last (remembered across opens and sessions).
        if (!mapFitted)
        {
            Canvas.ForceUpdateCanvases();
            // Open on the portal this one links to; without a destination, on where you stand.
            var target = SelectedEntry();
            var me = Player.m_localPlayer ? Player.m_localPlayer.transform.position : thisPortal.Location;
            mapCenter = ToUv(target != null && target.Key != "" ? target.Pos : me);
            mapZoom = Store.Data.mapZoom > 0f ? Mathf.Clamp(Store.Data.mapZoom, MinZoom, 1f) : DefaultZoom;
            mapFitted = true; mapJustFitted = true;
        }
        PlaceMap();
    }

    bool Explored(Vector3 p)
    {
        mIsExplored ??= AccessTools.Method(typeof(Minimap), "IsExplored", new[] { typeof(Vector3) });
        try { return mIsExplored == null || (bool)mIsExplored.Invoke(Minimap.instance, new object[] { p }); }
        catch { return true; }
    }

    void EnsureMapMaterial()
    {
        if (mapMat) return;
        // A copy of the large map's material: it shares the map and fog textures (so it stays live), but has its own
        // zoom and centre, so the real map isn't touched.
        mapMat = Instantiate(Minimap.instance.m_mapImageLarge.material);
        mapImage.material = mapMat;
        mapImage.texture = Minimap.instance.m_mapTexture;
    }

    Vector2 ToUv(Vector3 p)
    {
        var mm = Minimap.instance; float half = mm.m_textureSize / 2f;
        return new Vector2((p.x / mm.m_pixelSize + half) / mm.m_textureSize, (p.z / mm.m_pixelSize + half) / mm.m_textureSize);
    }

    Vector3 ToWorld(Vector2 uv)
    {
        var mm = Minimap.instance; float half = mm.m_textureSize / 2f;
        return new Vector3((uv.x * mm.m_textureSize - half) * mm.m_pixelSize, 0f, (uv.y * mm.m_textureSize - half) * mm.m_pixelSize);
    }

    float Aspect => mapFrame.rect.height > 0 ? mapFrame.rect.width / mapFrame.rect.height : 1.36f;

    public void ZoomMap(float steps, Vector2 pointer01)
    {
        float before = mapZoom;
        mapZoom = Mathf.Clamp(mapZoom * Mathf.Pow(0.8f, steps), MinZoom, 1f);
        Store.Data.mapZoom = mapZoom; mapZoomDirty = true;   // saved when the panel closes
        // keep the point under the cursor where it is
        var view = new Vector2(before * Aspect, before);
        var under = mapCenter + Vector2.Scale(pointer01 - new Vector2(0.5f, 0.5f), view);
        mapCenter = under - Vector2.Scale(pointer01 - new Vector2(0.5f, 0.5f), new Vector2(mapZoom * Aspect, mapZoom));
        PlaceMap();
    }

    public void PanMap(Vector2 delta01)
    {
        mapCenter -= Vector2.Scale(delta01, new Vector2(mapZoom * Aspect, mapZoom));
        PlaceMap();
    }

    // Put the map region and every dot where the current centre and zoom say.
    void PlaceMap()
    {
        if (!mapMat || !mapFrame) return;
        mapCenter = new Vector2(Mathf.Clamp01(mapCenter.x), Mathf.Clamp01(mapCenter.y));
        float w = mapZoom * Aspect, h = mapZoom;
        mapImage.uvRect = new Rect(mapCenter.x - w / 2f, mapCenter.y - h / 2f, w, h);
        mapMat.SetFloat("_zoom", mapZoom);
        mapMat.SetFloat("_pixelSize", 200f / mapZoom);
        mapMat.SetVector("_mapCenter", ToWorld(mapCenter));
        var size = mapFrame.rect.size;
        var selected = SelectedEntry();
        Vector2 Local(Vector3 p) { var uv = ToUv(p); return new Vector2((uv.x - (mapCenter.x - w / 2f)) / w * size.x, (uv.y - (mapCenter.y - h / 2f)) / h * size.y); }
        bool Inside(Vector2 v) => v.x > -20 && v.y > -20 && v.x < size.x + 20 && v.y < size.y + 20;
        // Names under the dots fade in as you zoom in (none when zoomed out, where they would only be clutter)
        float tagAlpha = Mathf.Clamp01((TagsFrom - mapZoom) / (TagsFrom - TagsFull));
        for (int i = 0; i < dots.Count; i++)
        {
            var (e, rt, img) = dots[i];
            var v = Local(e.Pos);
            // In the fog only the selected portal shows (your own destination; its distance and direction are shown anyway)
            rt.gameObject.SetActive(Inside(v) && (e == selected || !unexplored.Contains(e)));
            nameTags[i].gameObject.SetActive(false);
            rt.anchoredPosition = v;
            bool sel = e == selected, pin = Pinned(e);
            img.sprite = pin ? PinSprite() : DotSprite();
            img.color = pin ? PinOn : sel ? PinOn : Ink;
            rt.sizeDelta = pin ? new Vector2(28, 28) : new Vector2(sel ? 18 : 14, sel ? 18 : 14);
            if (sel) rt.SetAsLastSibling();
        }
        // Names: pinned portals first, then the ones nearest the middle; a name that would overlap one already placed
        // gives way (zoomed out, clusters would otherwise turn into an unreadable pile).
        if (tagAlpha > 0.01f)
        {
            var placed = new List<Rect>();
            var mid = size / 2f;
            // "You" and other players claim their spot first; portal names give way around them
            Rect TagRect(Vector2 at, string text) { float tw = text.Length * 7.5f + 12f; return new Rect(at.x - tw / 2f, at.y - 18f, tw, 18f); }
            var me = Player.m_localPlayer ? Player.m_localPlayer.transform.position : thisPortal.Location;
            var meAt = MapLocal(me);
            placed.Add(new Rect(meAt.x - 22f, meAt.y - 22f, 44f, 44f));
            placed.Add(TagRect(meAt + new Vector2(0, -24), "You"));
            tmpPlayers.Clear();
            try { ZNet.instance?.GetOtherPublicPlayers(tmpPlayers); } catch { }
            foreach (var pl in tmpPlayers)
            {
                var at = MapLocal(pl.m_position);
                placed.Add(new Rect(at.x - 15f, at.y - 15f, 30f, 30f));
                placed.Add(TagRect(at + new Vector2(0, -18), pl.m_name ?? ""));
            }
            var order = Enumerable.Range(0, dots.Count).Where(i => dots[i].rt.gameObject.activeSelf && dots[i].e != selected)
                .OrderBy(i => Pinned(dots[i].e) ? 0 : 1).ThenBy(i => (dots[i].rt.anchoredPosition - mid).sqrMagnitude);
            foreach (int i in order)
            {
                var (e, rt, _) = dots[i];
                var at = rt.anchoredPosition + new Vector2(0, -(Pinned(e) ? 18 : 12));
                var r = TagRect(at, e.Name);   // estimate of the label's size (13 px text + padding)
                if (placed.Any(o => o.Overlaps(r))) continue;
                placed.Add(r);
                var tag = nameTags[i];
                tag.gameObject.SetActive(true); tag.alpha = tagAlpha; ((RectTransform)tag.transform).anchoredPosition = at;
            }
        }
        if (selRing)
        {
            var d = dots.FirstOrDefault(x => x.e == selected);
            bool on = d.rt && d.rt.gameObject.activeSelf;
            selRing.gameObject.SetActive(on);
            if (on) { selRing.anchoredPosition = d.rt.anchoredPosition; selRing.sizeDelta = new Vector2(40, 40); selRing.SetAsLastSibling(); d.rt.SetAsLastSibling(); }
        }
        if (hereDot)
        {
            var v = Local(Player.m_localPlayer ? Player.m_localPlayer.transform.position : thisPortal.Location);
            hereDot.anchoredPosition = v; hereDot.gameObject.SetActive(Inside(v)); hereDot.SetAsLastSibling();
            if (youTag) { youTag.gameObject.SetActive(Inside(v)); youTag.alpha = 1f; ((RectTransform)youTag.transform).anchoredPosition = v + new Vector2(0, -24); youTag.transform.SetAsLastSibling(); }
            if (Player.m_localPlayer) hereDot.localRotation = Quaternion.Euler(0f, 0f, -Player.m_localPlayer.transform.rotation.eulerAngles.y);
        }
        PlacePlayers();
        ShowMapLabel(selected, null);
    }

    Vector2 MapLocal(Vector3 p)
    {
        float w = mapZoom * Aspect, h = mapZoom; var size = mapFrame.rect.size; var uv = ToUv(p);
        return new Vector2((uv.x - (mapCenter.x - w / 2f)) / w * size.x, (uv.y - (mapCenter.y - h / 2f)) / h * size.y);
    }

    // Friendly players who share their position: Valheim's player icon with their name, in blue like Valheim's own map.
    // Refreshed once a second while the Map tab is open (they move).
    void PlacePlayers()
    {
        if (!dotLayer || !ZNet.instance || !mapMat) return;
        tmpPlayers.Clear();
        try { ZNet.instance.GetOtherPublicPlayers(tmpPlayers); } catch { return; }
        while (playerDots.Count < tmpPlayers.Count)
        {
            var go = NewUi("Player", dotLayer); var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = new Vector2(30, 30);
            var img = go.AddComponent<Image>(); img.raycastTarget = false; img.preserveAspect = true;
            var icon = PlayerIcon();
            if (icon) img.sprite = icon; else { img.sprite = DotSprite(); img.color = FriendBlue; }
            var tag = NameTag("");
            var text = tag.GetComponentInChildren<Text>(); text.color = FriendBlue;
            playerDots.Add((rt, tag, text));
        }
        var size = mapFrame.rect.size;
        for (int i = 0; i < playerDots.Count; i++)
        {
            var (rt, tag, text) = playerDots[i];
            bool on = i < tmpPlayers.Count;
            Vector2 v = on ? MapLocal(tmpPlayers[i].m_position) : Vector2.zero;
            on = on && v.x > -20 && v.y > -20 && v.x < size.x + 20 && v.y < size.y + 20;
            rt.gameObject.SetActive(on); tag.gameObject.SetActive(on);
            if (!on) continue;
            rt.anchoredPosition = v; rt.SetAsLastSibling();
            string name = tmpPlayers[i].m_name;
            try { name = CensorShittyWords.FilterUGC(name, UGCType.CharacterName, tmpPlayers[i].m_characterID.UserID); } catch { }
            text.text = name; tag.alpha = 1f;
            ((RectTransform)tag.transform).anchoredPosition = v + new Vector2(0, -18); tag.transform.SetAsLastSibling();
        }
    }

    static Sprite playerIcon; static bool playerIconLooked;
    static Sprite PlayerIcon()
    {
        if (playerIconLooked) return playerIcon;
        playerIconLooked = true;
        try { playerIcon = Minimap.instance.m_icons.Find(x => x.m_name == Minimap.PinType.Player).m_icon; } catch { }
        return playerIcon;
    }

    // A small name under a dot: dark, about 65% see-through, no raycasts (clicks go to the dot).
    CanvasGroup NameTag(string name)
    {
        var go = NewUi("Tag", dotLayer); var r = (RectTransform)go.transform;
        r.anchorMin = r.anchorMax = Vector2.zero; r.pivot = new Vector2(0.5f, 1f);
        var bg = go.AddComponent<Image>(); bg.color = new Color(0, 0, 0, 0.35f); bg.raycastTarget = false;
        var hl = go.AddComponent<HorizontalLayoutGroup>(); hl.padding = new RectOffset(5, 5, 1, 1); hl.childControlWidth = hl.childControlHeight = true;
        var f = go.AddComponent<ContentSizeFitter>(); f.horizontalFit = f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var t = Label(go.transform, name, 11, Ink);
        var cg = go.AddComponent<CanvasGroup>(); cg.blocksRaycasts = false; cg.interactable = false;
        go.SetActive(false);
        return cg;
    }

    void ShowHoverRing(RectTransform at)
    {
        if (!hoverRing) return;
        bool on = at && at.gameObject.activeSelf;
        hoverRing.gameObject.SetActive(on);
        if (on) { hoverRing.anchoredPosition = at.anchoredPosition; float d = Mathf.Max(at.sizeDelta.x, 14f) + 14f; hoverRing.sizeDelta = new Vector2(d, d); hoverRing.SetAsLastSibling(); at.SetAsLastSibling(); }
    }

    void ShowMapLabel(Entry e, RectTransform at)
    {
        if (!mapLabel) return;
        if (at == null) at = dots.FirstOrDefault(d => d.e == e).rt;
        bool show = e != null && e.Key != "" && at && at.gameObject.activeSelf;
        mapLabel.SetActive(show);
        if (!show) return;
        mapLabelText.text = e.Name;
        var lrt = (RectTransform)mapLabel.transform;
        float width = mapLabelText.preferredWidth + 20f;
        float x = at.anchoredPosition.x + Mathf.Max(at.sizeDelta.x / 2f, 20f) + 6f;
        float y = Mathf.Clamp(at.anchoredPosition.y, 18f, mapFrame.rect.height - 18f);
        // flip to the left of the dot near the right edge
        bool left = x + width > mapFrame.rect.width;
        lrt.pivot = new Vector2(left ? 1 : 0, 0.5f);
        lrt.anchoredPosition = new Vector2(left ? at.anchoredPosition.x - Mathf.Max(at.sizeDelta.x / 2f, 20f) - 6f : x, y);
        mapLabel.transform.SetAsLastSibling();
    }

    // When the selection moves (keys, list), bring its dot into view if it is off the map.
    void MapFollowSelection()
    {
        var e = SelectedEntry();
        if (mapJustFitted) mapJustFitted = false;   // opening: keep this portal in the middle
        else if (e != null && e.Key != "" && mapMat)
        {
            var uv = ToUv(e.Pos); float w = mapZoom * Aspect, h = mapZoom;
            if (Mathf.Abs(uv.x - mapCenter.x) > w * 0.45f || Mathf.Abs(uv.y - mapCenter.y) > h * 0.45f) mapCenter = uv;
        }
        PlaceMap();
    }

    // Wheel = zoom at the cursor, drag = move. Hovered tells Update to leave the wheel to the map.
    sealed class MapView : MonoBehaviour, IScrollHandler, IDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public Plugin Owner; public bool Hovered;
        RectTransform Rt => (RectTransform)transform;
        public void OnPointerEnter(PointerEventData e) => Hovered = true;
        public void OnPointerExit(PointerEventData e) => Hovered = false;
        void OnDisable() => Hovered = false;
        public void OnScroll(PointerEventData e)
        {
            if (e.scrollDelta.y == 0) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Rt, e.position, e.enterEventCamera, out var local);
            var r = Rt.rect;
            Owner.ZoomMap(Mathf.Sign(e.scrollDelta.y), new Vector2((local.x - r.xMin) / r.width, (local.y - r.yMin) / r.height));
        }
        public void OnDrag(PointerEventData e)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Rt, e.position, e.pressEventCamera, out var now);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Rt, e.position - e.delta, e.pressEventCamera, out var before);
            var r = Rt.rect;
            Owner.PanMap(new Vector2((now.x - before.x) / r.width, (now.y - before.y) / r.height));
        }
    }

    static Sprite DotSprite() => dotSprite ? dotSprite : dotSprite = CircleSprite(false);
    static Sprite RingSprite() => ringSprite ? ringSprite : ringSprite = CircleSprite(true);

    // A filled dot with a dark rim (readable on any terrain), or a ring.
    static Sprite CircleSprite(bool ring)
    {
        const int N = 48; float c = N / 2f;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                float outer = Mathf.Clamp01(c - 1f - d + 0.5f);
                float inner = ring ? Mathf.Clamp01(d - (c - 9f) + 0.5f) : 1f;
                float rim = Mathf.Clamp01(d - (c - 6f) + 0.5f);          // the outer 5 px are the dark rim
                byte v = (byte)(255 * (1f - rim * 0.85f));
                px[y * N + x] = new Color32(v, v, v, (byte)(255 * outer * inner));
            }
        tex.SetPixels32(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
    }

    // ---------- letter rail ----------

    // The letter at a screen point on the rail (top to bottom, evenly spread like the layout does).
    public string RailLetterAt(Vector2 screen)
    {
        if (railLetters.Count == 0) return null;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(railArea, screen, null, out var local);
        var r = railArea.rect;
        float fromTop = (r.yMax - local.y) / r.height;
        int i = Mathf.Clamp(Mathf.FloorToInt(fromTop * railLetters.Count), 0, railLetters.Count - 1);
        return railLetters[i].l;
    }

    // Browse, don't choose: scroll the list so the first portal with this letter is at the top; the destination stays.
    public void ScrollToLetter(string l)
    {
        var n = nav.FirstOrDefault(x => x.Entry.Key != "" && Letter(x.Entry.Name) == l);
        if (n == null) return;
        Canvas.ForceUpdateCanvases();
        var item = n.Back.rectTransform;
        float top = -item.anchoredPosition.y - item.rect.height * (1 - item.pivot.y);
        float max = Mathf.Max(0f, listContent.rect.height - listViewport.rect.height);
        listContent.anchoredPosition = new Vector2(listContent.anchoredPosition.x, Mathf.Clamp(top, 0f, max));
        focusIn = 1;
    }

    public void RailHover(string l) => railHover = l;

    // Ember = the section at the top of the list now, ink = under the pointer, the rest dim.
    void PaintRail()
    {
        if (railLetters.Count == 0 || tab != "list") return;
        string current = null; float y = listContent.anchoredPosition.y;
        foreach (var n in nav)
        {
            if (n.Entry.Key == "") continue;
            var it = n.Back.rectTransform;
            float bottom = -it.anchoredPosition.y + it.rect.height * it.pivot.y;
            if (bottom > y + 1f) { current = Letter(n.Entry.Name); break; }
        }
        foreach (var (l, t) in railLetters) t.color = l == railHover ? Ink : l == current ? Orange : Dim;
    }

    sealed class RailScrub : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerMoveHandler, IPointerExitHandler
    {
        public Plugin Owner;
        public void OnPointerDown(PointerEventData e) { var l = Owner.RailLetterAt(e.position); if (l != null) Owner.ScrollToLetter(l); }
        public void OnDrag(PointerEventData e) { var l = Owner.RailLetterAt(e.position); Owner.RailHover(l); if (l != null) Owner.ScrollToLetter(l); }
        public void OnPointerMove(PointerEventData e) => Owner.RailHover(Owner.RailLetterAt(e.position));
        public void OnPointerExit(PointerEventData e) => Owner.RailHover(null);
        void OnDisable() => Owner?.RailHover(null);
    }

    static string Letter(string name) => name.Length > 0 && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : "#";

    // ---------- selection ----------

    // Selecting = choosing: XPortal's dropdown value is the destination (nothing is saved until Link = XPortal's OK).
    void Select(int i, bool setTarget)
    {
        sel = nav.Count == 0 || i < 0 ? -1 : Mathf.Clamp(i, 0, nav.Count - 1);
        if (sel >= 0 && setTarget) SetTarget(nav[sel].Entry);
        Paint();
        if (sel >= 0 && tab == "list") EnsureVisible(nav[sel].Back.rectTransform, listContent, listViewport);
        if (tab == "map") { if (sel >= 0) EnsureVisible(nav[sel].Back.rectTransform, mapListContent, mapListViewport); MapFollowSelection(); }
        ShowBottom();
    }

    void SetTarget(Entry e)
    {
        if (dropdown.value == e.Index) return;
        dropdown.value = e.Index;   // fires XPortal's OnDropdownValueChanged: target + Ping visibility
        dropdown.RefreshShownValue();
        HideXPortalWidgets();       // XPortal just re-showed its Ping button
    }

    void Paint()
    {
        for (int r = 0; r < nav.Count; r++) { nav[r].Back.color = r == sel ? SelCol : nav[r].Normal; if (nav[r].Bar) nav[r].Bar.SetActive(r == sel); }
    }

    // Left/Right, PageUp/PageDown: through the items in order.
    void Move(int d)
    {
        if (nav.Count == 0) return;
        Select(sel < 0 ? 0 : sel + d, setTarget: true);
    }

    // Up/Down: to the nearest item in the row above or below (tiles sit 3 or 4 to a row); stays put at the edge.
    void MoveRow(int d)
    {
        if (nav.Count == 0) return;
        if (sel < 0) { Select(0, setTarget: true); return; }
        var cur = nav[sel];
        int target = cur.Row + d, best = -1;
        float bestGap = float.MaxValue;
        for (int i = 0; i < nav.Count; i++)
        {
            if (nav[i].Row != target) continue;
            float gap = Mathf.Abs(nav[i].Col - cur.Col);
            if (gap < bestGap) { best = i; bestGap = gap; }
        }
        if (best >= 0) Select(best, setTarget: true);
    }

    void EnsureVisible(RectTransform item, RectTransform content, RectTransform viewport)
    {
        Canvas.ForceUpdateCanvases();
        float top = -item.anchoredPosition.y - item.rect.height * (1 - item.pivot.y);
        float bottom = top + item.rect.height;
        float view = viewport.rect.height, y = content.anchoredPosition.y;
        if (top < y) y = top; else if (bottom > y + view) y = bottom - view;
        content.anchoredPosition = new Vector2(content.anchoredPosition.x, Mathf.Max(0, y));
    }

    static void ScrollRows(RectTransform content, RectTransform viewport, float wheel, float rowH = RowH)
    {
        float max = Mathf.Max(0f, content.rect.height - viewport.rect.height);
        float y = content.anchoredPosition.y - Mathf.Sign(wheel) * scrollRows.Value * (rowH + 4f);
        content.anchoredPosition = new Vector2(content.anchoredPosition.x, Mathf.Clamp(y, 0f, max));
    }

    void ShowBottom()
    {
        var e = SelectedEntry();
        bool target = e != null && e.Key != "";
        selName.text = e == null ? "" : e.Name;
        if (!target) selInfo.text = "No destination: this portal leads nowhere";
        else
        {
            string dir = Direction(e), when = When(UsedAt(e));
            selInfo.text = Dist(e.Meters) + (dir != "" ? " " + dir : "") + (when != "" ? " · last used " + when : "");
        }
        pinLabel.transform.parent.gameObject.SetActive(target);
        pinLabel.text = target && Pinned(e) ? "Unpin" : "Pin";
        if (pinButtonIcon) pinButtonIcon.color = target && Pinned(e) ? PinOff : PinOn;
        pingButton.SetActive(target && xpPing && PingAllowed());
    }

    // ---------- actions ----------

    void TogglePin() => TogglePinFor(SelectedEntry());

    void TogglePinFor(Entry e)
    {
        if (e == null || e.Key == "") return;
        var s = S;
        bool full = !s.pinned.Contains(e.Key) && s.pinned.Count(k => Portals.Any(p => p.Key == k)) >= MaxPins;
        if (full) { selInfo.text = $"{MaxPins} pins at most: unpin one first"; focusIn = 1; return; }
        if (!s.pinned.Remove(e.Key)) s.pinned.Add(e.Key);
        Store.Save();
        Refresh(keep: SelectedEntry() ?? e);
        focusIn = 1;
    }

    // ---------- right-click menu ----------

    // Pin/Unpin, Ping, Link for the portal under the mouse, without selecting it first. Esc or a click outside closes it.
    void ShowContext(Entry e, Vector2 screen)
    {
        if (e == null || e.Key == "") return;
        CloseContext();
        ctxBlocker = NewUi("CtxBlocker", root.transform); var brt = (RectTransform)ctxBlocker.transform;
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = brt.offsetMax = Vector2.zero;
        var bimg = ctxBlocker.AddComponent<Image>(); bimg.color = new Color(0, 0, 0, 0.001f);
        var bb = ctxBlocker.AddComponent<Button>(); bb.targetGraphic = bimg; NoNav(bb); bb.onClick.AddListener(CloseContext);
        ctxBlocker.AddComponent<RightClick>().OnRight = _ => CloseContext();

        ctxMenu = NewUi("CtxMenu", root.transform); var mrt = (RectTransform)ctxMenu.transform;
        mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 0.5f); mrt.pivot = new Vector2(0, 1);
        ctxMenu.AddComponent<Image>().color = Hex(0x0C0A08, 0.98f); Bordered(ctxMenu);
        var vl = ctxMenu.AddComponent<VerticalLayoutGroup>(); vl.padding = new RectOffset(4, 4, 4, 4); vl.spacing = 2; vl.childControlWidth = vl.childControlHeight = true; vl.childForceExpandHeight = false;
        var fit = ctxMenu.AddComponent<ContentSizeFitter>(); fit.verticalFit = fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var head = Label(ctxMenu.transform, e.Name, 14, Faint); head.gameObject.AddComponent<LayoutElement>().minHeight = 26f;
        head.alignment = TextAnchor.MiddleLeft;
        CtxItem(Pinned(e) ? "Unpin" : "Pin", () => { CloseContext(); TogglePinFor(e); }, PinSprite(), Pinned(e) ? PinOff : PinOn);
        if (PingAllowed() && xpPing) CtxItem(Localization.instance.Localize("$hud_ping"), () => { CloseContext(); SetTarget(e); Ping(); }, PingSprite(), Ink);
        CtxItem("Link", () => { CloseContext(); SetTarget(e); Link(); }, LinkSprite(), Orange);
        RoundAll(ctxMenu.transform);

        // At the pointer, kept inside the panel
        var rootRt = (RectTransform)root.transform;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRt, screen, null, out var local);
        Canvas.ForceUpdateCanvases();
        var r = rootRt.rect; var size = mrt.rect.size;
        local.x = Mathf.Min(local.x + 2f, r.xMax - size.x - 8f);
        local.y = Mathf.Max(local.y - 2f, r.yMin + size.y + 8f);
        mrt.anchoredPosition = local;
        focusIn = 1;
    }

    void CtxItem(string text, Action act, Sprite icon, Color iconColor)
    {
        var go = NewUi("Item", ctxMenu.transform);
        var le = go.AddComponent<LayoutElement>(); le.minHeight = 34f; le.minWidth = 170f;
        var img = go.AddComponent<Image>(); img.color = BtnCol;
        var b = go.AddComponent<Button>(); b.targetGraphic = img; NoNav(b); Glow(go, 0.12f);   // soft: the white text stays readable
        var t = Label(go.transform, text, 16, Ink); t.rectTransform.offsetMin = new Vector2(40, 0);
        Icon(go.transform, icon, iconColor, 12f, 20f);
        b.onClick.AddListener(() => act());
    }

    // An icon at the left of a button, vertically centred
    static Image Icon(Transform parent, Sprite sprite, Color color, float left, float size)
    {
        var go = NewUi("Icon", parent); var r = (RectTransform)go.transform;
        r.anchorMin = r.anchorMax = new Vector2(0, 0.5f); r.pivot = new Vector2(0, 0.5f); r.sizeDelta = new Vector2(size, size); r.anchoredPosition = new Vector2(left, 0);
        var img = go.AddComponent<Image>(); img.sprite = sprite; img.preserveAspect = true; img.color = color; img.raycastTarget = false;
        return img;
    }
    static Image PinIcon(Transform parent, Color color, float left, float size) => Icon(parent, PinSprite(), color, left, size);

    // ---------- icons (drawn here, so they don't depend on the font) ----------

    static Sprite linkSprite, cancelSprite, pingSprite; static bool pingLooked;

    // Draws a white sprite from a signed distance function (negative = inside), anti-aliased; tinted by the Image colour.
    static Sprite SdfSprite(Func<float, float, float> sdf)
    {
        const int N = 64;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                px[y * N + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - sdf(x + 0.5f - N / 2f, y + 0.5f - N / 2f)) * 255f));
        tex.SetPixels32(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
    }

    // distance from (px,py) to the segment a-b
    static float Seg(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax, vy = by - ay, t = Mathf.Clamp01(((px - ax) * vx + (py - ay) * vy) / (vx * vx + vy * vy));
        float dx = px - ax - vx * t, dy = py - ay - vy * t;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // Link: two interlocking chain links, tilted 45 degrees
    static Sprite LinkSprite() => linkSprite ? linkSprite : linkSprite = SdfSprite((x, y) =>
    {
        float u = (x + y) * 0.7071f, v = (x - y) * 0.7071f;
        float a = Mathf.Abs(Seg(u, v, -17f, 0f, -3f, 0f) - 8f) - 3f;   // left link: a stadium ring
        float b = Mathf.Abs(Seg(u, v, 3f, 0f, 17f, 0f) - 8f) - 3f;     // right link
        return Mathf.Min(a, b);
    });

    // Cancel: a cross
    static Sprite CancelSprite() => cancelSprite ? cancelSprite : cancelSprite = SdfSprite((x, y) =>
        Mathf.Min(Seg(x, y, -16f, -16f, 16f, 16f), Seg(x, y, -16f, 16f, 16f, -16f)) - 3.5f);

    // Ping: Valheim's own ping icon from the map; if that can't be found, a dot with two signal arcs
    static Sprite PingSprite()
    {
        if (!pingLooked)
        {
            pingLooked = true;
            try { pingSprite = Minimap.instance.m_icons.Find(x => x.m_name == Minimap.PinType.Ping).m_icon; } catch { }
        }
        if (pingSprite) return pingSprite;
        return pingSprite = SdfSprite((x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            float dot = r - 5f;
            float ring1 = Mathf.Abs(r - 14f) - 2.5f, ring2 = Mathf.Abs(r - 24f) - 2.5f;
            float wedge = Mathf.Abs(Mathf.Atan2(y, x)) > 0.9f ? 99f : 0f;   // only the arcs to the right
            return Mathf.Min(dot, Mathf.Max(Mathf.Min(ring1, ring2), wedge));
        });
    }

    void CloseContext()
    {
        if (ctxMenu) Destroy(ctxMenu);
        if (ctxBlocker) Destroy(ctxBlocker);
        ctxMenu = ctxBlocker = null;
    }

    bool ContextOpen => ctxMenu;

    sealed class RightClick : MonoBehaviour, IPointerClickHandler
    {
        public Action<Vector2> OnRight;
        public Action OnDouble;   // double left-click
        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right) OnRight?.Invoke(e.position);
            else if (e.button == PointerEventData.InputButton.Left && e.clickCount == 2) OnDouble?.Invoke();
        }
    }

    // Link = XPortal's OK: PortalInfoSubmitted(name, target, default) and close. You then walk through the portal.
    void Link()
    {
        xpName.text = nameField.text;
        xpDefault.isOn = defaultOn;
        xpOk.onClick.Invoke();
    }

    void Cancel() => xpCancel.onClick.Invoke();

    // Double-click: make it the destination and link at once (closes the panel, like Link).
    void LinkTo(Entry e)
    {
        if (e == null) return;
        CloseContext();
        SetTarget(e);
        Link();
    }
    void Ping() { if (xpPing) xpPing.onClick.Invoke(); }

    void SetTab(string t)
    {
        CloseContext();
        if (t == tab || (t == "map" && !MapAvailable())) return;
        tab = t; Store.Data.lastTab = t; Store.Save();
        Refresh(keep: SelectedEntry());
        focusIn = 1;
    }

    void SetMode(string m) { mode = m; Refresh(keep: SelectedEntry()); focusIn = 1; }

    // ---------- input ----------

    private void Update()
    {
        if (!initDone && initWait > 0f)
        {
            initWait -= Time.unscaledDeltaTime; initRetry -= Time.unscaledDeltaTime;
            if (initRetry <= 0f) { initRetry = 1f; TryInit(final: initWait <= 0f); }
            return;
        }
        if (!Active) { AdoptOpenPanel(); return; }
        if (!mainPanel || !mainPanel.activeInHierarchy) { Close(); return; }
        HideXPortalWidgets();
        if (mainImg && mainImg.enabled) { mainImg.enabled = false; hidMainImg = true; }   // the unloading old copy may have put it back
        ApplyScale();
        PaintRail();
        if (tab == "map" && (playerRefresh -= Time.unscaledDeltaTime) <= 0f) { playerRefresh = 1f; PlacePlayers(); }

        // Refocusing selects all the text (legacy InputField); a frame later put the caret at the end, so the next key extends the query.
        if (caretEnd && search.isFocused) { search.MoveTextEnd(false); caretEnd = false; }
        if (focusIn >= 0 && focusIn-- == 0) { Focus(search); caretEnd = true; }

        // Mouse wheel on the List tab: a fixed number of rows per notch (the ScrollRect's own wheel step was far too small)
        float wheel = ZInput.GetMouseScrollWheel();
        if (wheel != 0f && tab == "list") ScrollRows(listContent, listViewport, wheel);
        if (wheel != 0f && tab == "map" && !mapView.Hovered) ScrollRows(mapListContent, mapListViewport, wheel, CompactH);   // over the map the wheel zooms

        bool ctrl = ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl);
        bool typing = lastSearch.Trim().Length > 0;

        // Gamepad
        // XPortal binds D-pad up/down as its own Jotunn buttons (BlockOtherInputs), so ask for those names too
        // (they also fire on the arrow keys, which are handled below).
        bool arrowUp = ZInput.GetKeyDown(KeyCode.UpArrow), arrowDown = ZInput.GetKeyDown(KeyCode.DownArrow);
        if (ZInput.GetButtonDown("JoyDPadUp") || (!arrowUp && ZInput.GetButtonDown("XPortal_DropdownScrollUp"))) MoveRow(-1);
        else if (ZInput.GetButtonDown("JoyDPadDown") || (!arrowDown && ZInput.GetButtonDown("XPortal_DropdownScrollDown"))) MoveRow(1);
        else if (ZInput.GetButtonDown("JoyDPadLeft")) Move(-1);
        else if (ZInput.GetButtonDown("JoyDPadRight")) Move(1);
        else if (ZInput.GetButtonDown("JoyButtonA")) { Link(); return; }
        else if (ZInput.GetButtonDown("JoyButtonB")) { Cancel(); return; }
        else if (ZInput.GetButtonDown("JoyButtonY")) { if (pingButton.activeSelf) { Ping(); return; } }
        else if (ZInput.GetButtonDown("JoyButtonX")) TogglePin();
        else if (ZInput.GetButtonDown("JoyTabLeft")) SetTab(tab == "map" ? "list" : "quick");
        else if (ZInput.GetButtonDown("JoyTabRight")) SetTab(tab == "quick" ? "list" : "map");

        // Renaming the portal: Enter / Esc go back to the search, every other key belongs to the name field.
        if (nameField.isFocused)
        {
            if (ZInput.GetKeyDown(KeyCode.Return) || ZInput.GetKeyDown(KeyCode.KeypadEnter) || ZInput.GetKeyDown(KeyCode.Escape)) focusIn = 1;
            lastSearch = search.text;
            return;
        }

        if (ctrl && ZInput.GetKeyDown(KeyCode.Alpha1)) SetTab("quick");
        else if (ctrl && ZInput.GetKeyDown(KeyCode.Alpha2)) SetTab("list");
        else if (ctrl && ZInput.GetKeyDown(KeyCode.Alpha3)) SetTab("map");
        else if (ctrl && ZInput.GetKeyDown(KeyCode.P)) TogglePin();
        else if (arrowUp) MoveRow(-1);
        else if (arrowDown) MoveRow(1);
        else if (!typing && ZInput.GetKeyDown(KeyCode.LeftArrow)) Move(-1);
        else if (!typing && ZInput.GetKeyDown(KeyCode.RightArrow)) Move(1);
        else if (ZInput.GetKeyDown(KeyCode.PageUp)) Move(-10);
        else if (ZInput.GetKeyDown(KeyCode.PageDown)) Move(10);
        else if (ZInput.GetKeyDown(KeyCode.Return) || ZInput.GetKeyDown(KeyCode.KeypadEnter))
        {
            // One match left while typing: that one; otherwise the selection (right after typing: the top match).
            if (typing)
            {
                var m = Matches(lastSearch.Trim());
                if (m.Count == 0) { lastSearch = search.text; focusIn = 1; return; }   // a typo must not link the last top match
                if (m.Count == 1) SetTarget(m[0]);
            }
            Link();
            return;
        }
        else if (ZInput.GetKeyDown(KeyCode.Escape) && ContextOpen) { CloseContext(); }
        else if (ZInput.GetKeyDown(KeyCode.Escape))
        {
            // The search field may already have eaten this Escape (it reverts its text), so decide on last frame's text.
            // Reverting can also have retargeted to the old text's top match: put back what was chosen before the key.
            if (lastSearch.Length > 0)
            {
                search.DeactivateInputField();   // else the still-focused field can handle this Escape after us and put its old text back
                search.text = "";
                if (beforeKey != null) { SetTarget(beforeKey); Refresh(keep: beforeKey); }
                focusIn = 1;
            }
            else { Cancel(); return; }
        }
        else if (!ctrl && !typing && tab == "quick")
        {
            for (int k = 0; k < MaxPins; k++)
                if (ZInput.GetKeyDown(KeyCode.Alpha1 + k) || ZInput.GetKeyDown(KeyCode.Keypad1 + k))
                {
                    int i = nav.FindIndex(n => n.Back.name == "Tile" + (k + 1));
                    if (i >= 0) Select(i, setTarget: true);
                }
        }
        lastSearch = search.text;
        beforeKey = SelectedEntry();
    }

    // On the Quick tab, with an empty search, 1-6 jump to pins instead of starting a search.
    char ValidateSearch(string text, int index, char c)
    {
        // Ctrl+P, Ctrl+1/2 are shortcuts; only those keys are kept out (Ctrl+V pastes, AltGr = Ctrl+Alt types letters).
        bool ctrl = ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl);
        bool alt = ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt);
        if (ctrl && !alt && (c == 'p' || c == 'P' || c == '1' || c == '2' || c == '3')) return '\0';
        if (tab == "quick" && text.Length == 0 && c >= '1' && c <= (char)('0' + MaxPins)) return '\0';
        return c;
    }

    static void Focus(InputField f)
    {
        if (!f || !f.gameObject.activeInHierarchy) return;
        EventSystem.current?.SetSelectedGameObject(f.gameObject);
        f.ActivateInputField();
    }

    // ---------- building the panel (plain Unity UI) ----------

    void Build()
    {
        tabs.Clear(); chips.Clear();
        if (canvasGo) Destroy(canvasGo);
        canvasGo = new GameObject("PortalCompassCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.layer = 5;   // UI
        DontDestroyOnLoad(canvasGo);
        var cv = canvasGo.GetComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 2100;   // above Jotunn's CustomGUIFront (2000)
        scaler = canvasGo.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; scaler.referencePixelsPerUnit = 50f;   // as Jotunn's canvas, so the wood frame slices the same
        builtFor = mainPanel;

        // The wood frame: a copy of XPortal's (Jotunn's) wood panel image
        var frame = NewUi("Frame", canvasGo.transform); var frt = (RectTransform)frame.transform;
        frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 0.5f); frt.sizeDelta = new Vector2(W, H); frt.anchoredPosition = Vector2.zero;
        var wood = frame.AddComponent<Image>(); var src = mainPanel.GetComponent<Image>();
        if (src) { wood.sprite = src.sprite; wood.type = src.type; wood.color = src.color; wood.material = src.material; wood.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier; }
        else wood.color = Hex(0x5D3B21);

        root = NewUi("PortalCompass", frame.transform);
        var rt = (RectTransform)root.transform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;

        // Dark backdrop inside the wood frame, so no text sits on the wood grain
        var bd = NewUi("Backdrop", root.transform); var bdr = (RectTransform)bd.transform;
        bdr.anchorMin = Vector2.zero; bdr.anchorMax = Vector2.one; bdr.offsetMin = new Vector2(10, 10); bdr.offsetMax = new Vector2(-10, -10);
        bd.AddComponent<Image>().color = Backdrop;

        // Header: title · Name · Default portal
        var title = Label(root.transform, Localization.instance.Localize("$hud_xportal_title"), 26, Orange);
        Place(title.rectTransform, Pad, 16, 280, 44); title.fontStyle = FontStyle.Bold;
        var nl = Label(root.transform, Localization.instance.Localize("$piece_portal_tag"), 16, Orange);
        Place(nl.rectTransform, 306, 20, 64, 38);
        nameField = Input(root.transform, Localization.instance.Localize("$piece_portal_tag.."), 17, dark: false);
        Place((RectTransform)nameField.transform, 372, 20, 300, 38);
        nameField.onValueChanged.AddListener(v => { if (xpName) xpName.SetTextWithoutNotify(v); });

        var def = NewUi("Default", root.transform); Place((RectTransform)def.transform, W - Pad - 230, 20, 230, 38);
        var defImg = def.AddComponent<Image>(); defImg.color = new Color(0, 0, 0, 0.01f);
        var defBtn = def.AddComponent<Button>(); defBtn.targetGraphic = defImg; NoNav(defBtn);
        var boxGo = NewUi("Box", def.transform); var brt = (RectTransform)boxGo.transform;
        brt.anchorMin = brt.anchorMax = new Vector2(0, 0.5f); brt.pivot = new Vector2(0, 0.5f); brt.sizeDelta = new Vector2(22, 22); brt.anchoredPosition = new Vector2(4, 0);
        defaultBox = boxGo.AddComponent<Image>(); defaultBox.raycastTarget = false; Bordered(boxGo);
        var defaultLabel = Label(def.transform, Localization.instance.Localize("$piece_portal_defaultportal"), 16, Ink);
        defaultLabel.rectTransform.offsetMin = new Vector2(32, 0);
        defBtn.onClick.AddListener(() => { defaultOn = !defaultOn; ShowDefault(); focusIn = 1; });

        // Hover help for Default portal, in Simplified Technical English (XPortal: "The Portal that newly built Portals
        // immediately connect to"; it is stored in XPortal's config on this computer).
        var tip = NewUi("DefaultTip", root.transform); var trt = (RectTransform)tip.transform;
        trt.anchorMin = trt.anchorMax = new Vector2(1, 1); trt.pivot = new Vector2(1, 1); trt.sizeDelta = new Vector2(420, 128); trt.anchoredPosition = new Vector2(-Pad, -62);
        tip.AddComponent<Image>().color = Hex(0x0C0A08, 0.99f); Bordered(tip);
        var tipText = Label(tip.transform, "When this box is on, this portal is the default portal.\nEach new portal that you build connects to the default portal immediately.\nYou can have only one default portal. This setting is on your computer only.", 15, Ink);
        tipText.horizontalOverflow = HorizontalWrapMode.Wrap; tipText.alignment = TextAnchor.UpperLeft;
        tipText.rectTransform.offsetMin = new Vector2(12, 10); tipText.rectTransform.offsetMax = new Vector2(-12, -10);
        tip.SetActive(false);
        var hover = def.AddComponent<Hover>(); hover.Show = () => { tip.SetActive(true); tip.transform.SetAsLastSibling(); }; hover.Hide = () => tip.SetActive(false);

        // Search + tabs
        var sb = NewUi("SearchBox", root.transform); Place((RectTransform)sb.transform, Pad, 72, W - 2 * Pad - 300, 48);
        sb.AddComponent<Image>().color = Field; Bordered(sb);
        Underline(sb.transform, true);
        search = Input(sb.transform, "Search portals: just start typing", 19, dark: true);
        var srt = (RectTransform)search.transform; srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one; srt.offsetMin = new Vector2(6, 0); srt.offsetMax = new Vector2(-250, 0);
        search.GetComponent<Image>().color = new Color(0, 0, 0, 0);
        search.onValueChanged.AddListener(_ => Refresh());
        search.onValidateInput = ValidateSearch;

        countLabel = Label(sb.transform, "", 14, Dim); countLabel.alignment = TextAnchor.MiddleRight;
        countLabel.rectTransform.offsetMax = new Vector2(-56, 0);

        // Clear button (shown while there is text): a big target at the right end of the field
        clearButton = NewUi("Clear", sb.transform); var crt = (RectTransform)clearButton.transform;
        crt.anchorMin = new Vector2(1, 0); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(1, 0.5f); crt.sizeDelta = new Vector2(48, -6); crt.anchoredPosition = new Vector2(-3, 0);
        var cimg = clearButton.AddComponent<Image>(); cimg.color = BtnCol;
        var cb = clearButton.AddComponent<Button>(); cb.targetGraphic = cimg; NoNav(cb);
        var cx2 = Label(clearButton.transform, "x", 20, Ink); cx2.alignment = TextAnchor.MiddleCenter; cx2.fontStyle = FontStyle.Bold;
        var ch = clearButton.AddComponent<Hover>(); ch.Show = () => cx2.color = Orange; ch.Hide = () => cx2.color = Ink;
        cb.onClick.AddListener(ClearSearch);
        clearButton.SetActive(false);
        search.gameObject.AddComponent<SearchClicks>().Owner = this;

        float tx = W - Pad - 290;
        foreach (var (id, label) in new[] { ("quick", "Quick"), ("list", "List"), ("map", "Map") })
        {
            float w = id == "map" ? 126 : 82;
            var go = NewUi("Tab_" + id, root.transform); Place((RectTransform)go.transform, tx, 72, w, 48); tx += w;
            var img = go.AddComponent<Image>(); var b = go.AddComponent<Button>(); b.targetGraphic = img; NoNav(b); Glow(go, 0.22f);
            var t = Label(go.transform, label, 17, Ink); t.alignment = TextAnchor.MiddleCenter;
            string tid = id; b.onClick.AddListener(() => SetTab(tid));
            tabs.Add((id, img, t));
        }

        var hint = Label(root.transform, "Up/Down move · Left/Right across tiles (not while typing) · Enter link · Esc clear, then close", 13, Faint);
        Place(hint.rectTransform, Pad + 4, 126, 680, 22);
        var hintRight = Label(root.transform, "Last tab reopens · Ctrl+1/2/3", 13, Faint); hintRight.alignment = TextAnchor.MiddleRight;
        Place(hintRight.rectTransform, W - Pad - 244, 126, 240, 22);

        // Quick tab area
        var qa = NewUi("Quick", root.transform); quickArea = (RectTransform)qa.transform;
        Stretch(quickArea, Pad, ContentTop, Pad, BottomBar + 8);
        qa.AddComponent<RectMask2D>();
        var qv = qa.AddComponent<VerticalLayoutGroup>(); qv.spacing = 10; qv.childControlHeight = qv.childControlWidth = true; qv.childForceExpandHeight = false; qv.childForceExpandWidth = true;

        // List tab: chips, then the scroll list with the letter rail
        chipsRow = NewUi("Chips", root.transform); Place((RectTransform)chipsRow.transform, Pad, ContentTop, W - 2 * Pad, 34);
        float cx = 0;
        foreach (var (id, label) in new[] { ("az", "A-Z"), ("recent", "Recent"), ("pinned", "Pinned"), ("nearest", "Nearest") })
        {
            var go = NewUi("Chip_" + id, chipsRow.transform); Place((RectTransform)go.transform, cx, 0, 108, 34); cx += 116;
            var img = go.AddComponent<Image>(); var b = go.AddComponent<Button>(); b.targetGraphic = img; NoNav(b); Glow(go, 0.22f);
            var t = Label(go.transform, label, 15, Ink); t.alignment = TextAnchor.MiddleCenter;
            string mid = id; b.onClick.AddListener(() => SetMode(mid));
            chips.Add((id, img, t));
        }
        var la = NewUi("List", root.transform); listArea = (RectTransform)la.transform;
        Stretch(listArea, Pad, ContentTop + 44, Pad, BottomBar + 8);
        var sv = NewUi("Scroll", la.transform); var svrt = (RectTransform)sv.transform;
        svrt.anchorMin = Vector2.zero; svrt.anchorMax = Vector2.one; svrt.offsetMin = Vector2.zero; svrt.offsetMax = new Vector2(-30, 0);
        sv.AddComponent<Image>().color = Box;
        listScroll = sv.AddComponent<ScrollRect>(); listScroll.horizontal = false; listScroll.movementType = ScrollRect.MovementType.Clamped; listScroll.scrollSensitivity = 0f;   // the wheel is handled in Update (rows per notch)
        var vp = NewUi("Viewport", sv.transform); listViewport = (RectTransform)vp.transform;
        listViewport.anchorMin = Vector2.zero; listViewport.anchorMax = Vector2.one; listViewport.offsetMin = new Vector2(6, 6); listViewport.offsetMax = new Vector2(-6, -6);
        vp.AddComponent<RectMask2D>(); vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
        var ct = NewUi("Content", vp.transform); listContent = (RectTransform)ct.transform;
        listContent.anchorMin = new Vector2(0, 1); listContent.anchorMax = new Vector2(1, 1); listContent.pivot = new Vector2(0.5f, 1); listContent.sizeDelta = Vector2.zero;
        var vl = ct.AddComponent<VerticalLayoutGroup>(); vl.spacing = 4; vl.childControlHeight = vl.childControlWidth = true; vl.childForceExpandHeight = false; vl.childForceExpandWidth = true;
        ct.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        listScroll.viewport = listViewport; listScroll.content = listContent;
        BuildMapArea();
        var rail = NewUi("Rail", la.transform); railArea = (RectTransform)rail.transform;
        railArea.anchorMin = new Vector2(1, 0); railArea.anchorMax = new Vector2(1, 1); railArea.pivot = new Vector2(1, 0.5f); railArea.sizeDelta = new Vector2(24, 0); railArea.anchoredPosition = Vector2.zero;
        var rl = rail.AddComponent<VerticalLayoutGroup>(); rl.childControlHeight = rl.childControlWidth = true; rl.childForceExpandHeight = true;
        rail.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
        rail.AddComponent<RailScrub>().Owner = this;

        // Bottom bar: the selected portal · Pin · Ping · Cancel · Link
        var line = NewUi("Line", root.transform); var lrt = (RectTransform)line.transform;
        lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0); lrt.pivot = new Vector2(0.5f, 0); lrt.offsetMin = new Vector2(Pad, BottomBar); lrt.offsetMax = new Vector2(-Pad, BottomBar + 2);
        line.AddComponent<Image>().color = Border;
        // No big title for the selection (it is highlighted in the list and on the map): one small line beside the buttons.
        selName = Label(root.transform, "", 15, Ink); selName.gameObject.SetActive(false);
        selInfo = Label(root.transform, "", 15, Dim); PlaceBottom(selInfo.rectTransform, Pad + 4, 14, 440, 44);
        selInfo.horizontalOverflow = HorizontalWrapMode.Wrap;
        float bx = W - Pad;
        BottomButton("Link", 130, ref bx, Orange, DarkBtn, LinkSprite()).onClick.AddListener(Link);
        BottomButton(Localization.instance.Localize("$menu_cancel"), 120, ref bx, BtnCol, Ink, CancelSprite()).onClick.AddListener(Cancel);
        var ping = BottomButton(Localization.instance.Localize("$hud_ping"), 100, ref bx, BtnCol, Ink, PingSprite()); ping.onClick.AddListener(Ping); pingButton = ping.gameObject;
        var pin = BottomButton("Pin", 110, ref bx, BtnCol, Ink, PinSprite()); pin.onClick.AddListener(TogglePin); pinLabel = pin.GetComponentInChildren<Text>();
        pinButtonIcon = pin.transform.Find("Icon").GetComponent<Image>(); pinButtonIcon.color = PinOn;
        RoundAll(root.transform);
    }

    Button BottomButton(string text, float w, ref float right, Color back, Color ink, Sprite icon = null)
    {
        right -= w;
        var go = NewUi("Btn", root.transform); PlaceBottom((RectTransform)go.transform, right, 14, w, 44); right -= 10;
        var img = go.AddComponent<Image>(); img.color = back;
        if (back != Orange) Bordered(go);
        var b = go.AddComponent<Button>(); b.targetGraphic = img; NoNav(b); Glow(go, back == Orange ? 0.45f : 0.22f);
        var t = Label(go.transform, text, 16, ink); t.alignment = TextAnchor.MiddleCenter; t.fontStyle = FontStyle.Bold;
        if (icon) { Icon(go.transform, icon, ink, 14f, 20f); t.rectTransform.offsetMin = new Vector2(28, 0); }
        return b;
    }

    void ShowDefault()
    {
        if (defaultBox) defaultBox.color = defaultOn ? PinOn : Field;
    }

    // compact: the narrow list on the Map tab (names shrink to fit instead of wrapping)
    void Row(Transform parent, Entry e, bool withWhen, bool compact = false)
    {
        var go = NewUi("Row", parent);
        go.AddComponent<LayoutElement>().preferredHeight = compact ? CompactH : RowH;
        var back = go.AddComponent<Image>(); back.color = RowCol;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = back; NoNav(btn); Glow(go, 0.09f);
        nav.Add(new Nav { Entry = e, Back = back, Bar = SelBar(go.transform), Normal = RowCol, Row = navRow++ });
        { var rc = go.AddComponent<RightClick>(); rc.OnRight = pos => ShowContext(e, pos); rc.OnDouble = () => LinkTo(e); }
        btn.onClick.AddListener(() => { Select(nav.FindIndex(n => n.Back == back), setTarget: true); focusIn = 1; });

        if (e.Key != "")
        {
            // Pin mark: click it to pin or unpin this portal
            var hit = NewUi("PinHit", go.transform); var hrt = (RectTransform)hit.transform;
            hrt.anchorMin = new Vector2(0, 0); hrt.anchorMax = new Vector2(0, 1); hrt.pivot = new Vector2(0, 0.5f); hrt.sizeDelta = new Vector2(48, 0); hrt.anchoredPosition = Vector2.zero;
            var hitImg = hit.AddComponent<Image>(); hitImg.color = new Color(1, 1, 1, 0.001f);
            var pb = hit.AddComponent<Button>(); pb.targetGraphic = hitImg; NoNav(pb);
            var pm = NewUi("PinMark", hit.transform); var prt = (RectTransform)pm.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f); prt.pivot = new Vector2(0.5f, 0.5f); prt.sizeDelta = compact ? new Vector2(20, 20) : new Vector2(24, 24); prt.anchoredPosition = new Vector2(2, 0);
            var pimg = pm.AddComponent<Image>(); pimg.sprite = PinSprite(); pimg.preserveAspect = true; pimg.raycastTarget = false;
            bool on = Pinned(e); pimg.color = on ? PinOn : PinOff;
            var hv = hit.AddComponent<Hover>(); hv.Show = () => pimg.color = PinOn; hv.Hide = () => pimg.color = Pinned(e) ? PinOn : PinOff;
            pb.onClick.AddListener(() => { Select(nav.FindIndex(n => n.Back == back), setTarget: true); TogglePin(); });
        }
        var name = Label(go.transform, e.Name, compact ? 16 : 17, Ink);
        name.rectTransform.offsetMin = new Vector2(50, 0); name.rectTransform.offsetMax = new Vector2(withWhen ? -200 : compact ? -72 : -90, 0);
        name.horizontalOverflow = HorizontalWrapMode.Wrap; name.verticalOverflow = VerticalWrapMode.Truncate;
        if (compact) { name.resizeTextForBestFit = true; name.resizeTextMaxSize = name.fontSize; name.resizeTextMinSize = Mathf.RoundToInt(13 * FontScale); }
        if (withWhen)
        {
            var w = Label(go.transform, When(UsedAt(e)), 13, Dim);
            w.rectTransform.anchorMin = new Vector2(1, 0); w.rectTransform.anchorMax = new Vector2(1, 1); w.rectTransform.pivot = new Vector2(1, 0.5f);
            w.rectTransform.sizeDelta = new Vector2(110, 0); w.rectTransform.anchoredPosition = new Vector2(-84, 0);
        }
        var d = Label(go.transform, Dist(e.Meters), compact ? 14 : 15, Dim); d.alignment = TextAnchor.MiddleRight;
        d.rectTransform.offsetMax = new Vector2(-16, 0);
    }

    void Tile(Transform grid, Entry e, string key, string sub, Color normal, bool pinned, bool navigable, int row, float col)
    {
        var go = NewUi(key != "" ? "Tile" + key : "Tile", grid);
        var back = go.AddComponent<Image>(); back.color = normal;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = back; NoNav(btn); Glow(go, 0.09f);
        if (navigable) nav.Add(new Nav { Entry = e, Back = back, Bar = SelBar(go.transform), Normal = normal, Row = row, Col = col });
        { var rc = go.AddComponent<RightClick>(); rc.OnRight = pos => ShowContext(e, pos); rc.OnDouble = () => LinkTo(e); }
        btn.onClick.AddListener(() =>
        {
            int i = nav.FindIndex(n => n.Back == back);
            if (i >= 0) Select(i, setTarget: true);
            else { SetTarget(e); sel = -1; Paint(); ShowBottom(); }
            focusIn = 1;
        });
        if (key != "") { var k = Label(go.transform, key, 12, Orange); k.fontStyle = FontStyle.Bold; k.alignment = TextAnchor.UpperLeft; k.rectTransform.offsetMin = new Vector2(10, 0); k.rectTransform.offsetMax = new Vector2(0, -6); }
        if (pinned) { var m = NewUi("PinMark", go.transform); var r = (RectTransform)m.transform; r.anchorMin = r.anchorMax = new Vector2(1, 1); r.pivot = new Vector2(1, 1); r.sizeDelta = new Vector2(18, 18); r.anchoredPosition = new Vector2(-8, -6); var mi = m.AddComponent<Image>(); mi.sprite = PinSprite(); mi.preserveAspect = true; mi.color = PinOn; mi.raycastTarget = false; }
        var n = Label(go.transform, e.Name, 16, Ink); n.fontStyle = FontStyle.Bold; n.alignment = TextAnchor.LowerLeft;
        n.horizontalOverflow = HorizontalWrapMode.Wrap; n.verticalOverflow = VerticalWrapMode.Truncate;
        var s = Label(go.transform, sub, 12, Dim);
        if (pinned)
        {
            n.rectTransform.offsetMin = new Vector2(10, 6); n.rectTransform.offsetMax = new Vector2(-80, 0);
            s.alignment = TextAnchor.LowerRight; s.rectTransform.offsetMin = new Vector2(0, 8); s.rectTransform.offsetMax = new Vector2(-10, 0);
        }
        else
        {
            n.alignment = TextAnchor.UpperLeft; n.rectTransform.offsetMin = new Vector2(10, 0); n.rectTransform.offsetMax = new Vector2(-8, -6);
            s.alignment = TextAnchor.LowerLeft; s.rectTransform.offsetMin = new Vector2(10, 6); s.rectTransform.offsetMax = new Vector2(-8, 0);
        }
    }

    GameObject Panel(string name, Transform parent, Color color)
    {
        var go = NewUi(name, parent);
        go.AddComponent<Image>().color = color;
        return go;
    }

    void SectionHead(Transform parent, string left, string right)
    {
        var go = NewUi("Head", parent); go.AddComponent<LayoutElement>().preferredHeight = 22f;
        var l = Label(go.transform, left, 13, Orange); l.fontStyle = FontStyle.Bold;
        if (right != "") { var r = Label(go.transform, right, 13, Faint); r.alignment = TextAnchor.MiddleRight; }
    }

    void Hint(Transform parent, string text)
    {
        var go = NewUi("Hint", parent); go.AddComponent<LayoutElement>().preferredHeight = 34f;
        var t = Label(go.transform, text, 15, Faint); t.rectTransform.offsetMin = new Vector2(12, 0); t.fontStyle = FontStyle.Italic;
    }

    Transform Grid(Transform parent, int columns, float height)
    {
        var go = NewUi("Grid", parent);
        var g = go.AddComponent<GridLayoutGroup>();
        float inner = W - 2 * Pad - 24;
        g.spacing = new Vector2(10, 10);
        g.cellSize = new Vector2((inner - 10 * (columns - 1)) / columns, height);
        g.constraint = GridLayoutGroup.Constraint.FixedColumnCount; g.constraintCount = columns;
        go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go.transform;
    }

    InputField Input(Transform parent, string placeholder, int size, bool dark)
    {
        var go = NewUi("Input", parent);
        var back = go.AddComponent<Image>(); back.color = Field;
        if (!dark) Bordered(go);
        var f = go.AddComponent<InputField>(); NoNav(f);
        var ink = Ink;   // "dark" now only means: no own border (it sits in a bordered box)
        var text = Label(go.transform, "", size, ink); text.supportRichText = false;
        text.rectTransform.offsetMin = new Vector2(8, 0); text.rectTransform.offsetMax = new Vector2(-8, 0);
        var ph = Label(go.transform, placeholder, size - 1, Faint);
        ph.fontStyle = FontStyle.Italic; ph.rectTransform.offsetMin = new Vector2(8, 0); ph.rectTransform.offsetMax = new Vector2(-8, 0);
        f.textComponent = text; f.placeholder = ph; f.targetGraphic = back; f.lineType = InputField.LineType.SingleLine;
        f.caretColor = ink;
        return f;
    }

    // A push pin, drawn once into a small texture (white, tinted by the Image colour): round head, collar, needle.
    static Sprite PinSprite()
    {
        if (pinSprite) return pinSprite;
        const int N = 64;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                // Pin axis tilted 45 degrees: head up-right, needle down-left. u = along the axis, v = across it.
                float cx = x + 0.5f - N / 2f, cy = y + 0.5f - N / 2f;
                float u = (cx + cy) * 0.7071f, v = (cx - cy) * 0.7071f;
                float d = 99f;
                d = Mathf.Min(d, Mathf.Sqrt(v * v + (u - 13f) * (u - 13f)) - 11f);            // head
                d = Mathf.Min(d, Mathf.Max(Mathf.Abs(v) - (4f + (u - (-2f)) * 0.25f), Mathf.Abs(u - 2f) - 5f));   // body
                d = Mathf.Min(d, Mathf.Max(Mathf.Abs(v) - 9f, Mathf.Abs(u + 4f) - 2.5f));      // collar
                d = Mathf.Min(d, Mathf.Max(Mathf.Abs(v) - 1.6f, Mathf.Abs(u + 16f) - 11f));    // needle
                byte a = (byte)(Mathf.Clamp01(0.5f - d) * 255f);
                px[y * N + x] = new Color32(255, 255, 255, a);
            }
        tex.SetPixels32(px); tex.Apply();
        return pinSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
    }

    // A 6 px rounded rectangle, 9-sliced, so every surface keeps the same corner at any size.
    static Sprite RoundSprite()
    {
        if (roundSprite) return roundSprite;
        const int N = 32; const float R = 7f;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - N / 2f) - (N / 2f - R)), dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - N / 2f) - (N / 2f - R));
                float d = Mathf.Sqrt(dx * dx + dy * dy) - R;
                px[y * N + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255f));
            }
        tex.SetPixels32(px); tex.Apply();
        return roundSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(10, 10, 10, 10));
    }

    // Round every flat surface under t (thin lines, bars and the invisible hit areas stay square).
    static void RoundAll(Transform t)
    {
        foreach (var img in t.GetComponentsInChildren<Image>(true))
        {
            if (img.sprite || img.color.a < 0.05f) continue;
            string n = img.gameObject.name;
            if (n == "Underline" || n == "SelBar" || n == "Line" || n == "Backdrop") continue;
            img.sprite = RoundSprite(); img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1.6f;
        }
    }

    static void Bordered(GameObject go)
    {
        var o = go.AddComponent<Outline>(); o.effectColor = Border; o.effectDistance = new Vector2(1, -1); o.useGraphicAlpha = false;
    }

    // The amber bar on the left of the selected row or tile (hidden until selected)
    static GameObject SelBar(Transform parent)
    {
        var go = NewUi("SelBar", parent); var r = (RectTransform)go.transform;
        r.anchorMin = new Vector2(0, 0); r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 0.5f); r.sizeDelta = new Vector2(4, -12); r.anchoredPosition = new Vector2(3, 0);
        var img = go.AddComponent<Image>(); img.color = PinOn; img.raycastTarget = false;
        go.SetActive(false);
        return go;
    }

    // A 3 px amber line along the bottom: the selected tab or chip, and the search field
    static void Underline(Transform parent, bool on)
    {
        var line = parent.Find("Underline");
        if (!line)
        {
            var go = NewUi("Underline", parent); var r = (RectTransform)go.transform;
            r.anchorMin = new Vector2(0, 0); r.anchorMax = new Vector2(1, 0); r.pivot = new Vector2(0.5f, 0); r.sizeDelta = new Vector2(0, 3); r.anchoredPosition = Vector2.zero;
            var img = go.AddComponent<Image>(); img.color = PinOn; img.raycastTarget = false;
            line = go.transform;
        }
        line.gameObject.SetActive(on);
    }

    // No keyboard navigation between controls, and no Unity colour tint (its pressed/selected states read as "turns dark").
    static void NoNav(Selectable s) { s.navigation = new Navigation { mode = Navigation.Mode.None }; s.transition = Selectable.Transition.None; }

    // Hover = a light yellow glow behind the label (Joost: "the hover state should be light yellow, not dark").
    static void Glow(GameObject go, float alpha)
    {
        var g = NewUi("Glow", go.transform); var r = (RectTransform)g.transform;
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero;
        g.transform.SetAsFirstSibling();
        var img = g.AddComponent<Image>(); img.color = new Color(HoverGlow.r, HoverGlow.g, HoverGlow.b, alpha); img.raycastTarget = false;
        g.SetActive(false);
        var h = go.AddComponent<Hover>(); h.Show = () => { if (g) g.SetActive(true); }; h.Hide = () => { if (g) g.SetActive(false); };
    }

    static void Clear(Transform t)
    {
        // Inactive at once so the layout skips them this frame; destroyed at the end of the frame.
        foreach (Transform c in t) { c.gameObject.SetActive(false); Destroy(c.gameObject); }
    }

    static GameObject NewUi(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    // Top-left placement in panel pixels
    static void Place(RectTransform r, float x, float y, float w, float h)
    {
        r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
        r.sizeDelta = new Vector2(w, h); r.anchoredPosition = new Vector2(x, -y);
    }

    static void PlaceBottom(RectTransform r, float x, float y, float w, float h)
    {
        r.anchorMin = r.anchorMax = new Vector2(0, 0); r.pivot = new Vector2(0, 0);
        r.sizeDelta = new Vector2(w, h); r.anchoredPosition = new Vector2(x, y);
    }

    static void Stretch(RectTransform r, float left, float top, float right, float bottom)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(0.5f, 0.5f);
        r.offsetMin = new Vector2(left, bottom); r.offsetMax = new Vector2(-right, -top);
    }

    Text Label(Transform parent, string text, int size, Color color)
    {
        var go = NewUi("Text", parent);
        var t = go.AddComponent<Text>();
        t.font = font; t.fontSize = Mathf.RoundToInt(size * FontScale); t.color = color; t.text = text;
        t.alignment = TextAnchor.MiddleLeft; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.raycastTarget = false;
        var r = t.rectTransform; r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero;
        return t;
    }

    public void ClearSearch()
    {
        var keep = SelectedEntry();
        search.text = "";
        if (keep != null) { SetTarget(keep); Refresh(keep: keep); }
        focusIn = 1;
    }

    // Right-click clears the search; double-click selects all of it (the legacy InputField has neither).
    sealed class SearchClicks : MonoBehaviour, IPointerClickHandler
    {
        public Plugin Owner; int selectAllIn = -1;
        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right) Owner.ClearSearch();
            else if (e.button == PointerEventData.InputButton.Left && e.clickCount >= 2) selectAllIn = 1;   // after the field's own click handling
        }
        void LateUpdate()
        {
            if (selectAllIn < 0 || selectAllIn-- > 0) return;
            var f = Owner.search; f.selectionAnchorPosition = 0; f.selectionFocusPosition = f.text.Length; f.ForceLabelUpdate();
            Owner.caretEnd = false;
        }
    }

    // Pointer enter/leave callbacks for hover help
    sealed class Hover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Action Show, Hide;
        public void OnPointerEnter(PointerEventData e) => Show?.Invoke();
        public void OnPointerExit(PointerEventData e) => Hide?.Invoke();
        void OnDisable() => Hide?.Invoke();
    }

    // ---------- pins, last used and the last tab, per player and world, in BepInEx/config ----------

    static class Store
    {
        [Serializable] public sealed class Used { public string k = ""; public long t; }
        [Serializable] public sealed class Entry { public string key = ""; public List<string> pinned = new(); public List<Used> used = new(); }
        [Serializable] public sealed class File { public int version = 2; public string lastTab = "quick"; public float mapZoom; public List<Entry> entries = new(); }

        static readonly string Path = System.IO.Path.Combine(Paths.ConfigPath, Plugin.Id + ".json");
        static File data;

        public static File Data { get { Load(); return data; } }

        public static Entry For(string key)
        {
            Load();
            var e = data.entries.FirstOrDefault(x => x.key == key);
            if (e == null) { e = new Entry { key = key }; data.entries.Add(e); }
            return e;
        }

        static void Load()
        {
            if (data != null) return;
            try
            {
                // Newtonsoft (shipped with Valheim): Unity's JsonUtility silently dropped the list of entries (0.2.0-0.3.2).
                data = System.IO.File.Exists(Path) ? Newtonsoft.Json.JsonConvert.DeserializeObject<File>(System.IO.File.ReadAllText(Path)) : null;
                // 0.1.0 stored session-bound ZDOIDs ("123:45"), which don't survive a restart: drop those.
                // 0.1.0 files have no version field, so this runs on every load; it only drops keys that aren't positions.
                if (data != null)
                    foreach (var e in data.entries)
                    {
                        e.pinned ??= new(); e.pinned.RemoveAll(k => k == null || !k.Contains(","));
                        e.used ??= new(); e.used.RemoveAll(u => u == null || u.k == null || !u.k.Contains(","));
                    }
            }
            catch (Exception e)
            {
                instance.Logger.LogWarning($"Could not read {Path}, starting empty (the old file is kept as .bak): {e.Message}");
                try { System.IO.File.Copy(Path, Path + ".bak", true); } catch { }
                data = null;
            }
            data ??= new File();
        }

        public static void Save()
        {
            try
            {
                // write to a temp file first, so a crash mid-write can't leave a broken file
                string tmp = Path + ".tmp";
                System.IO.File.WriteAllText(tmp, Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented));
                if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path);
                System.IO.File.Move(tmp, Path);
            }
            catch (Exception e) { instance.Logger.LogWarning($"Could not save {Path}: {e.Message}"); }
        }
    }
}

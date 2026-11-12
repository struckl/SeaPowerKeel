using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SeaPowerKeel.Loading;
using UnityEngine;

namespace SeaPowerKeel;

/// <summary>
/// The cards at the bottom left of the main menu, drawn by the game (his rule 2026-11-12: every window is drawn by the game).
/// <para>
/// The loading overlay is an own window only because the game can stand still while a mod starts. When it has glided to its
/// summary spot it hands over: Keel draws the same summary card there and the overlay fades out. If something changed since the
/// last start, the card divides like a cell: it bulges, pinches and splits, and What's new grows from the bottom up in the
/// corner, pushing the Keel card above it - never over the main menu's EXIT (docs/idea-news-cell-split.md).
/// </para>
/// </summary>
internal sealed partial class KeelHost
{
    private const float SplitSeconds = 1.15f, CardGap = 8f, StackWidth = 400f;

    private bool cardOn, cardConfirmed, cardHidden, cardFailed;
    private float cardSince, cardFadeFrom = -1f;
    private long cardSceneTicks;
    private Rect cardHome;                    // GUI units: where the overlay left the card (or where it would be)
    private Rect cardRect, creditRect;        // where it is drawn now
    private float cardTop = float.NaN;        // its top, following the stack after the split
    private float splitStart = -1f;
    private int stackMods = NewsMods;
    private float exitBottom = -1f, exitCheckedAt = -10f;
    private int cardErrors;

    private Texture2D roundTex, circleTex;
    private GUIStyle roundBody, cardHead, cardText, cardSmall, cardCredit;
    private float cardStyleUnit = -1f;

    private static readonly UnityEngine.Color CardBack = new(0x1E / 255f, 0x1F / 255f, 0x22 / 255f, 0.94f),
        CardGreen = new(0x63 / 255f, 0xB9 / 255f, 0x70 / 255f), CardAmber = new(0xD5 / 255f, 0xA8 / 255f, 0x76 / 255f),
        CardText = new(0xD7 / 255f, 0xD7 / 255f, 0xD7 / 255f), CardDim = new(0x94 / 255f, 0x9A / 255f, 0xA1 / 255f),
        CardEdge = new(0x3F / 255f, 0x41 / 255f, 0x47 / 255f);

    // ---- what is up -------------------------------------------------------------------------------------------------------

    private bool CardVisible
    {
        get
        {
            if (!cardOn || cardHidden)
                return false;
            if (LoadingWindow.MissionRunning || LoadingWindow.SceneLoadedTicks != cardSceneTicks)
            {
                cardHidden = true;          // it belongs to the main menu only
                return false;
            }
            return !LoadingWindow.OnMenuSubPage;
        }
    }

    private float CardAlpha
    {
        get
        {
            if (cardFadeFrom < 0f)
                return 1f;
            float a = 1f - (Time.realtimeSinceStartup - cardFadeFrom) / 0.8f;
            if (a <= 0f)
                cardHidden = true;
            return Mathf.Clamp01(a);
        }
    }

    /// <summary>The top of whatever stands at the bottom left (cards), for the notice box above it; NaN when nothing does.</summary>
    private float StackTop => CardVisible ? cardRect.y : NewsVisible ? newsRect.y : float.NaN;

    // ---- per frame ----------------------------------------------------------------------------------------------------------

    /// <summary>Called by OnGUI when the report window is not open.</summary>
    private void DrawMenuCards(float screenW, float screenH)
    {
        try
        {
            TakeOverSummary();
            // While the loading window is still up and on its way to the corner, What's new waits: it is born out of that card.
            bool overlayOnItsWay = LoadingWindow.Shown && !LoadingWindow.SummaryReady && !LoadingWindow.OverlayGone;
            bool card = CardVisible, newsUp = !overlayOnItsWay && NewsVisible;
            if (!card && !newsUp)
                return;
            EnsureCardStyles();
            float now = Time.realtimeSinceStartup;
            if (cardOn && !cardHidden && LoadingWindow.SummarySeconds > 0 && cardFadeFrom < 0f && now - cardSince > LoadingWindow.SummarySeconds)
                cardFadeFrom = now;
            Rect home = cardOn ? cardHome : DefaultHome(screenH);
            if (cardFailed && LoadingWindow.SummaryReady && !LoadingWindow.OverlayGone)
                home.y -= home.height + CardGap;        // the overlay keeps its own card in the corner: What's new stands above it
            float bottom = home.yMax, x = home.x, h = home.height;
            float width = Mathf.Max(home.width, StackWidth);
            float limit = ExitBottom(screenH) + 10f;

            // How many mods fit between EXIT and the corner.
            float newsH = 0f;
            if (newsUp)
            {
                float room = bottom - limit - (card ? CardGap + h : 0f);
                stackMods = Mathf.Min(NewsMods, news.Count);
                newsH = NewsHeight(width, stackMods);
                while (stackMods > 1 && newsH > room)
                    newsH = NewsHeight(width, --stackMods);
            }

            if (card && newsUp && splitStart < 0f && now - cardSince > 0.35f)
                splitStart = now;
            float p = splitStart < 0f ? (newsUp && !card ? 1f : 0f) : Mathf.Clamp01((now - splitStart) / SplitSeconds);
            float alpha = card ? CardAlpha : 1f;

            if (card && newsUp && p < 1f)
            {
                DrawSplit(p, home, width, bottom, newsH, alpha);
            }
            else
            {
                // Settled: What's new in the corner, the Keel card above it; the card glides back when What's new closes.
                if (newsUp)
                {
                    newsRect = new Rect(x, bottom - newsH, width, newsH);
                    DrawBody(newsRect, 1f);
                    DrawNewsContent(newsRect, stackMods, 1f);
                }
                if (card)
                {
                    float target = newsUp ? newsRect.y - CardGap - h : home.y;
                    cardTop = float.IsNaN(cardTop) ? target : Mathf.Lerp(cardTop, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
                    DrawSummaryCard(new Rect(x, cardTop, newsUp || splitStart >= 0f ? width : home.width, h), alpha, 1f);
                }
            }
            // The overlay fades out once the game has drawn the card (until then it covers the spot, so nothing blinks).
            if (card && !cardConfirmed && Event.current.type == EventType.Repaint)
            {
                cardConfirmed = true;
                LoadingWindow.GameDrawsSummary = true;
            }
            HandleCardEvents();
        }
        catch (Exception ex)
        {
            if (!cardConfirmed && cardOn)
            {
                cardOn = false;             // the overlay keeps its own summary
                cardFailed = true;
            }
            if (cardErrors++ == 0)
                log.LogWarning($"Menu cards: {ex}");
            cardHidden = true;
            if (cardErrors > 3)
                newsClosed = true;          // never an error every frame
        }
    }

    /// <summary>The overlay sits on its spot: Keel draws the card there from now on, and tells the overlay once it has.</summary>
    private void TakeOverSummary()
    {
        if (cardOn || cardFailed || !LoadingWindow.SummaryReady || LoadingWindow.GameDrawsSummary || !GameState.MenuUp())
            return;
        cardOn = true;
        cardSince = Time.realtimeSinceStartup;
        cardSceneTicks = LoadingWindow.SceneLoadedTicks;
        cardHome = new Rect(LoadingWindow.SummaryX / guiScale, LoadingWindow.SummaryY / guiScale,
                            LoadingWindow.SummaryW / guiScale, LoadingWindow.SummaryH / guiScale);
        cardTop = cardHome.y;
    }

    /// <summary>Where the summary card sits when there is no overlay to hand over (loading window switched off): the same formula.</summary>
    private Rect DefaultHome(float screenH)
    {
        float px = Screen.height, s = Mathf.Max(1f, px / 1080f * 0.75f);
        float w = 400 * s, h = 94 * s, x = 8 * s, y = px - px * 0.05f - h;
        return new Rect(x / guiScale, y / guiScale, w / guiScale, h / guiScale);
    }

    // ---- the split -----------------------------------------------------------------------------------------------------------

    private static float Smooth(float t) => t * t * (3f - 2f * t);
    private static float EaseOutBack(float t) { const float c = 1.4f; t -= 1f; return 1f + (c + 1f) * t * t * t + c * t * t; }

    /// <summary>
    /// One frame of the division, p from 0 to 1. A (0-0.25): the card bulges, bubbles swell out of its lower edge. B (0.25-0.65):
    /// the Keel card rises out of it, a neck of shrinking bubbles between the two halves, a few drops pop off when it tears.
    /// C (0.65-1): What's new grows from the bottom up to its size, pushing the Keel card above it; its text fades in.
    /// </summary>
    private void DrawSplit(float p, Rect home, float width, float bottom, float newsH, float alpha)
    {
        float x = home.x, h = home.height, w0 = home.width, y0 = home.y;
        Rect upper, lower = Rect.zero;
        if (p < 0.25f)
        {
            float q = p / 0.25f, swell = Mathf.Sin(q * Mathf.PI * 0.5f);
            upper = new Rect(x, y0, w0, h + h * 0.18f * swell);
            DrawBody(upper, alpha);
            // bubbles swelling out of the lower edge
            for (int i = 0; i < 5; i++)
            {
                float cx = x + w0 * (0.18f + 0.16f * i), r = h * (0.10f + 0.04f * (i % 2)) * swell;
                Circle(cx, upper.yMax - r * 0.4f, r, alpha);
            }
            DrawSummaryCard(new Rect(x, y0, w0, h), alpha, 1f);
            return;
        }
        if (p < 0.65f)
        {
            float q = (p - 0.25f) / 0.40f, e = Smooth(q);
            lower = new Rect(x, y0, w0, h);
            upper = new Rect(x, y0 - (h + CardGap) * e, w0, h);
            DrawBody(lower, alpha);
            // the neck: a column of bubbles whose width shrinks to nothing
            float neckW = w0 * 0.7f * Mathf.Pow(1f - e, 1.6f), top = upper.yMax - h * 0.25f, bot = lower.y + h * 0.25f;
            if (neckW > 1f)
                for (float cy = top; cy <= bot; cy += Mathf.Max(2f, neckW * 0.25f))
                    for (float cx = x + w0 / 2 - neckW / 2 + neckW * 0.25f; cx <= x + w0 / 2 + neckW / 2 - neckW * 0.25f + 0.1f; cx += Mathf.Max(2f, neckW * 0.25f))
                        Circle(cx, cy, neckW * 0.25f, alpha);
            if (q > 0.75f)
            {
                float d = (q - 0.75f) / 0.25f;            // drops flying off where it tore
                float mid = (upper.yMax + lower.y) / 2;
                for (int i = -1; i <= 1; i++)
                    Circle(x + w0 / 2 + i * w0 * 0.12f * (1 + d), mid + (i == 0 ? -1 : 1) * h * 0.08f * d, h * 0.05f * (1 - d), alpha);
            }
            DrawSummaryCard(upper, alpha, 1f);
            return;
        }
        {
            float q = (p - 0.65f) / 0.35f, e = EaseOutBack(q), s = Smooth(q);
            float w = Mathf.Lerp(w0, width, s);
            float lh = Mathf.Max(h * 0.6f, h + (newsH - h) * e);
            lower = new Rect(x, bottom - lh, w, lh);
            upper = new Rect(x, lower.y - CardGap - h, w, h);
            DrawBody(lower, alpha);
            newsRect = lower;
            DrawNewsContent(lower, stackMods, Mathf.Clamp01((q - 0.45f) / 0.55f));
            DrawSummaryCard(upper, alpha, 1f);
            cardTop = upper.y;
        }
    }

    // ---- drawing --------------------------------------------------------------------------------------------------------------

    private void DrawBody(Rect r, float alpha)
    {
        if (Event.current.type != EventType.Repaint)
            return;
        UnityEngine.Color was = GUI.color;
        GUI.color = new UnityEngine.Color(1f, 1f, 1f, alpha);
        roundBody.Draw(r, GUIContent.none, false, false, false, false);
        GUI.color = was;
    }

    private void Circle(float cx, float cy, float r, float alpha)
    {
        if (r <= 0.3f || Event.current.type != EventType.Repaint)
            return;
        UnityEngine.Color was = GUI.color;
        GUI.color = new UnityEngine.Color(CardBack.r, CardBack.g, CardBack.b, CardBack.a * alpha);
        GUI.DrawTexture(new Rect(cx - r, cy - r, 2 * r, 2 * r), circleTex);
        GUI.color = was;
    }

    /// <summary>The Keel card: what Keel did, how long the game took, what to look at (the overlay's summary, drawn by the game).</summary>
    private void DrawSummaryCard(Rect r, float alpha, float textAlpha)
    {
        cardRect = r;
        if (!cardOn)
            return;
        Progress.Snapshot s = LoadingWindow.Progress?.Take(0);
        if (s == null)
            return;
        float u = LoadingWindow.SummaryScale / guiScale;
        UnityEngine.Color was = GUI.color;
        DrawBody(r, alpha);
        string alert = LoadingWindow.Alert;
        bool amber = alert != null || s.WithErrors + s.NotStarted > 0;
        if (Event.current.type == EventType.Repaint)
        {
            GUI.color = new UnityEngine.Color(1, 1, 1, alpha);
            GUI.DrawTexture(new Rect(r.x + 1, r.y + 6, Mathf.Max(2f, 4 * u), r.height - 12), amber ? Tint(CardAmber) : Tint(CardGreen));
        }
        GUI.color = new UnityEngine.Color(1, 1, 1, alpha * textAlpha);
        float pad = 14 * u, y = r.y + 8 * u, lineH = 20 * u, tw = r.width - 2 * pad;
        string ver = string.IsNullOrEmpty(s.KeelVersion) ? "" : " " + s.KeelVersion;
        GUI.Label(new Rect(r.x + pad, y, tw, 24 * u),
            "Keel" + ver + " started " + s.Running + (s.Running == 1 ? " mod" : " mods") + (s.KeelSeconds >= 1 ? " in " + s.KeelSeconds.ToString("0") + " s" : ""), cardHead);
        y += 24 * u;
        string line2 = "Game ready after " + Clock(s.DoneElapsed > 0 ? s.DoneElapsed : s.Elapsed) + (s.Skipped > 0 ? "  -  " + s.Skipped + " skipped" : "");
        GUI.Label(new Rect(r.x + pad, y, tw, lineH), line2, cardText);
        y += lineH;
        string line3 = alert != null ? alert + " - click for details"
                     : s.WithErrors + s.NotStarted > 0 ? (s.WithErrors + s.NotStarted) + (s.WithErrors + s.NotStarted == 1 ? " mod has" : " mods have") + " problems - click for the report"
                     : "No problems  -  click for the report";
        cardSmall.normal.textColor = amber ? CardAmber : CardDim;
        GUI.Label(new Rect(r.x + pad, y, tw, lineH), line3, cardSmall);
        y += lineH;
        creditRect = new Rect(r.x + pad, y, 130 * u, lineH);
        GUI.Label(creditRect, "Made by Kanalratte", cardCredit);      // quiet; a click opens his Steam profile
        GUI.color = was;
    }

    private static string Clock(double seconds) => ((int)seconds / 60) + ":" + ((int)seconds % 60).ToString("00");

    /// <summary>The What's new card's height for <paramref name="count"/> mods at <paramref name="width"/>.</summary>
    private float NewsHeight(float width, int count) => NewsLayout(new Rect(0, 0, width, 0), count, false, 1f);

    private void DrawNewsContent(Rect r, int count, float alpha)
    {
        if (alpha <= 0.01f)
        {
            newsCloseRect = Rect.zero;
            return;
        }
        NewsLayout(r, count, true, alpha);
    }

    /// <summary>Lays out (and with <paramref name="draw"/> draws) the What's new card: title and ×, one line per mod, the foot.</summary>
    private float NewsLayout(Rect r, int count, bool draw, float alpha)
    {
        EnsureNewsStyles();
        const float padX = 16, padY = 12;
        float inner = r.width - 2 * padX, y = r.y + padY;
        UnityEngine.Color was = GUI.color;
        if (draw)
            GUI.color = new UnityEngine.Color(1, 1, 1, alpha);
        var title = new GUIContent($"WHAT'S NEW SINCE YOUR LAST GAME  ·  {Keel.Name}");
        float th = Mathf.Max(18f, newsTitle.CalcHeight(title, inner - 26));
        if (draw)
        {
            GUI.Label(new Rect(r.x + padX, y, inner - 26, th), title, newsTitle);
            var close = new Rect(r.xMax - padX - 22, y, 22, 18);
            GUI.Label(close, "×", newsClose);
            newsCloseRect = new Rect(close.x - 6, close.y - 6, close.width + 12, close.height + 12);
        }
        y += th + 8;
        foreach (NewsItem n in news.Take(count))
        {
            float tx = r.x + padX + 84, tw = inner - 84;
            string head = n.Kind == NewsKind.Updated && n.From.Length > 0 && n.To.Length > 0 && n.From != n.To
                ? $"<b>{n.Name}</b>  <color=#8b95a3>{n.From} → {n.To}</color>"
                : $"<b>{n.Name}</b>{(n.To.Length > 0 ? $"  <color=#8b95a3>{n.To}</color>" : "")}";
            var headC = new GUIContent(head);
            float hh = newsName.CalcHeight(headC, tw);
            string line = n.Lines.FirstOrDefault() ?? "";
            if (line.Length > 64)
                line = line.Substring(0, 61) + "…";
            var lineC = new GUIContent(line);
            float lh = line.Length > 0 ? newsLine.CalcHeight(lineC, tw) : 0f;
            if (draw)
            {
                GUI.Label(new Rect(r.x + padX, y + 2, 74, 18), n.Tag, newsPills[n.Kind]);
                GUI.Label(new Rect(tx, y, tw, hh), headC, newsName);
                if (lh > 0)
                    GUI.Label(new Rect(tx, y + hh, tw, lh), lineC, newsLine);
            }
            y += Mathf.Max(22f, hh + lh) + 6;
        }
        int more = news.Count - count;
        var foot = new GUIContent((more > 0 ? $"+{more} more  ·  " : "") + "click for every mod and every line  ·  × closes");
        float fh = newsFoot.CalcHeight(foot, inner);
        if (draw)
            GUI.Label(new Rect(r.x + padX, y, inner, fh), foot, newsFoot);
        y += fh + padY;
        GUI.color = was;
        return y - r.y;
    }

    // ---- clicks ------------------------------------------------------------------------------------------------------------------

    private void HandleCardEvents()
    {
        Event e = Event.current;
        if (e.type != EventType.MouseDown)
            return;
        if (NewsClick(e.mousePosition))
        {
            e.Use();
            return;
        }
        if (CardClick(e.mousePosition, e.button))
            e.Use();
    }

    /// <summary>A click on the Keel card: left opens the report (the credit opens his Steam profile), right hides the card.</summary>
    private bool CardClick(Vector2 gui, int button)
    {
        if (!CardVisible || !cardRect.Contains(gui))
            return false;
        if (!FreshClick())
            return true;
        if (button == 1)
        {
            if (LoadingWindow.ClickHides)
                cardHidden = true;
            return true;
        }
        if (creditRect.Contains(gui))
        {
            try { Application.OpenURL(LoadingWindow.ProfileUrl); } catch (Exception) { /* no browser: nothing to do */ }
            return true;
        }
        if (!showWindow)
            ToggleWindow();
        return true;
    }

    // ---- the main menu's EXIT --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The lower edge of the main menu's lowest entry (EXIT) in GUI units: the lowest Button of the game's MainMenuView in the left
    /// fifth of the screen (Noesis, by name), measured once a second. Else 0.56 of the screen height (EXIT ends near 0.50 at 1080p
    /// with ALTERNATIVE MODS in the menu).
    /// </summary>
    private float ExitBottom(float screenH)
    {
        float now = Time.realtimeSinceStartup;
        if (now - exitCheckedAt < 1f && exitBottom > 0f)
            return exitBottom;
        exitCheckedAt = now;
        float found = -1f;
        try
        {
            object view = AccessTools.TypeByName("SeapowerUI.MainMenuView")?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            Type tree = AccessTools.TypeByName("Noesis.VisualTreeHelper"), point = AccessTools.TypeByName("Noesis.Point");
            if (view != null && tree != null && point != null)
            {
                MethodInfo count = tree.GetMethod("GetChildrenCount", BindingFlags.Public | BindingFlags.Static);
                MethodInfo child = tree.GetMethod("GetChild", BindingFlags.Public | BindingFlags.Static);
                MethodInfo root = tree.GetMethod("GetRoot", BindingFlags.Public | BindingFlags.Static);
                object top = root?.Invoke(null, new[] { view }) ?? view;
                (float x0, float y0) = ToScreen(top, point, 0, 0);
                (float x1, float y1) = ToScreen(top, point, Prop(top, "ActualWidth"), Prop(top, "ActualHeight"));
                if (y1 > y0 && x1 > x0)
                {
                    float best = -1f;
                    var stack = new Stack<object>();
                    stack.Push(view);
                    int seen = 0;
                    while (stack.Count > 0 && seen++ < 4000)
                    {
                        object el = stack.Pop();
                        if (el.GetType().Name.EndsWith("Button") && Prop(el, "ActualHeight") > 0 && el.GetType().GetProperty("IsVisible")?.GetValue(el) is true)
                        {
                            (float bx, float by) = ToScreen(el, point, 0, Prop(el, "ActualHeight"));
                            float fx = (bx - x0) / (x1 - x0), fy = (by - y0) / (y1 - y0);
                            if (fx < 0.2f && fy > best && fy < 0.9f)
                                best = fy;
                        }
                        int n = (int)count.Invoke(null, new[] { el });
                        for (int i = 0; i < n; i++)
                            stack.Push(child.Invoke(null, new[] { el, (object)i }));
                    }
                    if (best > 0f)
                        found = best * screenH;
                }
            }
        }
        catch (Exception)
        {
            // the fallback below
        }
        exitBottom = found > 0f ? found : screenH * 0.56f;
        return exitBottom;
    }

    private static float Prop(object o, string name)
    {
        object v = o.GetType().GetProperty(name)?.GetValue(o);
        return v == null ? 0f : Convert.ToSingle(v);
    }

    private static (float, float) ToScreen(object visual, Type point, float x, float y)
    {
        object p = Activator.CreateInstance(point, x, y);
        object s = visual.GetType().GetMethod("PointToScreen", new[] { point })?.Invoke(visual, new[] { p });
        if (s == null)
            return (0f, 0f);
        return (Convert.ToSingle(point.GetProperty("X")?.GetValue(s) ?? point.GetField("X")?.GetValue(s) ?? 0f),
                Convert.ToSingle(point.GetProperty("Y")?.GetValue(s) ?? point.GetField("Y")?.GetValue(s) ?? 0f));
    }

    // ---- styles and textures ---------------------------------------------------------------------------------------------------

    private readonly Dictionary<UnityEngine.Color, Texture2D> tints = new();

    private Texture2D Tint(UnityEngine.Color c)
    {
        if (!tints.TryGetValue(c, out Texture2D t))
        {
            t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            tints[c] = t;
        }
        return t;
    }

    private void EnsureCardStyles()
    {
        EnsureNewsStyles();
        if (roundTex == null)
        {
            roundTex = RoundedRect(32, 9f, CardBack, CardEdge);
            circleTex = Disc(64);
            roundBody = new GUIStyle { border = new RectOffset(11, 11, 11, 11) };
            roundBody.normal.background = roundTex;
        }
        float u = LoadingWindow.SummaryScale / guiScale;
        if (Mathf.Abs(u - cardStyleUnit) < 0.001f)
            return;
        cardStyleUnit = u;
        GUIStyle Make(float points, FontStyle style, UnityEngine.Color color) => new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.Max(8, Mathf.RoundToInt(points * 96f / 72f * u)), fontStyle = style, wordWrap = false, clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
            normal = { textColor = color },
        };
        cardHead = Make(14, FontStyle.Bold, CardText);
        cardText = Make(12, FontStyle.Normal, CardDim);
        cardSmall = Make(10, FontStyle.Normal, CardDim);
        cardCredit = Make(10, FontStyle.Normal, CardEdge);
    }

    /// <summary>A rounded rectangle for a 9-slice style: <paramref name="fill"/> inside, a 1-pixel <paramref name="edge"/>, soft corners.</summary>
    internal static Texture2D RoundedRect(int size, float radius, UnityEngine.Color fill, UnityEngine.Color edge)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        var px = new UnityEngine.Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // distance outside the rounded rectangle (negative inside)
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) - radius;
                float cover = Mathf.Clamp01(0.5f - d);
                UnityEngine.Color c = d > -1.2f ? edge : fill;
                c.a = (d > -1.2f ? 1f : fill.a) * cover;
                px[y * size + x] = c;
            }
        t.SetPixels(px);
        t.Apply();
        return t;
    }

    /// <summary>A white disc with a soft edge, tinted with GUI.color.</summary>
    internal static Texture2D Disc(int size)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        var px = new UnityEngine.Color[size * size];
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                px[y * size + x] = new UnityEngine.Color(1, 1, 1, Mathf.Clamp01(r - d));
            }
        t.SetPixels(px);
        t.Apply();
        return t;
    }
}

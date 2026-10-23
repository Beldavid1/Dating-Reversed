// DrDebugMenu.cs (Stardew Valley 1.6.15 / SMAPI 4.3.2)

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Dating_Reversed
{
    internal sealed class DrDebugMenu : IClickableMenu
    {
        private readonly ModEntry _mod;

        // dropdown state
        private Rectangle _dropdownBounds;
        private bool _dropdownOpen;
        private int _dropdownScroll;
        private int _hoveredIndex = -1;
        private Rectangle _arrowBounds;
        private static readonly Rectangle DownArrowSource = new Rectangle(421, 472, 9, 9);

        private List<(string Raw, string Display)> _choices = new();
        private string _selectedRaw = NoneValue;

        // layout
        private const int VisibleRows = 8;
        private Vector2 _todayPos;
        private Vector2 _pickPos;

        // buttons
        private TextButton _btnSetToday = null!;
        private TextButton _btnRepick = null!;
        private TextButton _btnPlayNow = null!;
        private TextButton _btnClearToday = null!;
        private TextButton _btnStopEvent = null!;
        private TextButton _btnClearCd = null!;
        private TextButton _btnClose = null!;
        private readonly List<TextButton> _buttons = new();

        // Space between a button's edge and its label (9-slice border is 12px, plus a margin).
        private const int ButtonTextPad = 20;

        private readonly string _title;
        private readonly string _noneDisplay;
        private readonly string _showAllLabel;
        private readonly string _pickLabel;

        public DrDebugMenu(ModEntry mod)
            : base(0, 0, 0, 0, showUpperRightCloseButton: true)
        {
            _mod = mod;

            _title = T("debugMenu.title");
            _noneDisplay = T("debugMenu.none");
            _showAllLabel = T("debugMenu.showAllDateables");
            _pickLabel = T("debugMenu.pick");

            ReloadNames();

            var today = _mod.UiGetTodayNpc();
            _selectedRaw = string.IsNullOrWhiteSpace(today) ? NoneValue : today;

            Reflow();
        }

        private const int CheckBoxSize = 36;
        private const string NoneValue = "<none>";
        private bool _showAllDateables;
        private ClickableComponent _chkLabel = null!;
        private Vector2 _chkTextPos;
        private Rectangle _chkBounds;
        private static readonly Rectangle CheckBoxEmptySrc = new Rectangle(227, 425, 9, 9);
        private static readonly Rectangle CheckBoxCheckedSrc = new Rectangle(236, 425, 9, 9);
        private const float CheckBoxScale = 4f; // 9*4=36px

        private string T(string key, object? tokens = null)
            => _mod.Helper.Translation.Get(key, tokens).ToString();

        private string Disp(string s)
            => string.IsNullOrWhiteSpace(s) || s == NoneValue ? _noneDisplay : s;

        private static string GetNpcDisplayName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return rawName;

            int dotIndex = rawName.LastIndexOf('.');
            string tail = dotIndex >= 0 ? rawName[(dotIndex + 1)..] : rawName;

            int underscoreIndex = tail.LastIndexOf('_');
            if (underscoreIndex >= 0 && underscoreIndex < tail.Length - 1)
                tail = tail[(underscoreIndex + 1)..];

            return string.IsNullOrWhiteSpace(tail) ? rawName : tail;
        }

        private static int GetNpcChoicePriority(string rawName, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(rawName) && Game1.characterData.ContainsKey(rawName)
                && !string.IsNullOrWhiteSpace(displayName) && !Game1.characterData.ContainsKey(displayName))
            {
                return 4;
            }

            if (string.Equals(rawName, displayName, StringComparison.Ordinal))
                return 3;

            if (!rawName.Contains('.') && !rawName.Contains('_'))
                return 2;

            return 1;
        }

        private string GetDisplayNameForRaw(string rawName)
            => rawName == NoneValue ? _noneDisplay : GetNpcDisplayName(rawName);

        private void ReloadNames()
        {
            IEnumerable<string> rawNames = _mod.UiGetEligiblePool(includeAllDateables: _showAllDateables)
                .Where(static p => !string.IsNullOrWhiteSpace(p));

            var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawName in rawNames.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                string displayName = GetDisplayNameForRaw(rawName);

                if (!chosen.TryGetValue(displayName, out string? existingRawName))
                {
                    chosen[displayName] = rawName;
                    continue;
                }

                if (GetNpcChoicePriority(rawName, displayName) > GetNpcChoicePriority(existingRawName, displayName))
                    chosen[displayName] = rawName;
            }

            _choices = chosen
                .OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static p => (p.Value, p.Key))
                .ToList();

            if (_choices.Count == 0)
                _choices.Add((NoneValue, _noneDisplay));

            if (!_choices.Any(static p => p.Raw == NoneValue))
                _choices.Insert(0, (NoneValue, _noneDisplay));

            if (!_choices.Any(p => string.Equals(p.Raw, _selectedRaw, StringComparison.Ordinal)))
                _selectedRaw = NoneValue;

            int maxScroll = Math.Max(0, _choices.Count - VisibleRows);
            _dropdownScroll = Math.Clamp(_dropdownScroll, 0, maxScroll);
        }

        private void Reflow()
        {
            const int pad = 16;
            const int gap = 12;

            string[] row1Labels = { T("debugMenu.button.setToday"), T("debugMenu.button.repick"), T("debugMenu.button.playNow") };
            string[] row2Labels = { T("debugMenu.button.clearToday"), T("debugMenu.button.stopEvent"), T("debugMenu.button.clearCooldown"), T("debugMenu.button.close") };

            // Buttons share a row equally, so size the menu so the longest label in each row fits.
            static int RowWidth(string[] labels, int gap)
            {
                int widest = (int)Math.Ceiling(labels.Max(static l => Game1.smallFont.MeasureString(l).X));
                return labels.Length * (widest + ButtonTextPad * 2) + gap * (labels.Length - 1);
            }

            int titleW = SpriteText.getWidthOfString(_title);
            int checkboxRowW = CheckBoxSize + 10 + (int)Math.Ceiling(Game1.smallFont.MeasureString(_showAllLabel).X);
            int contentNeeded = new[] { titleW, checkboxRowW, RowWidth(row1Labels, gap), RowWidth(row2Labels, gap) }.Max();
            width = Math.Max(680, contentNeeded + pad * 2);
            width = Math.Min(width, Game1.uiViewport.Width - 128);

            int titleH = SpriteText.getHeightOfString(_title);
            int lineH = (int)Game1.smallFont.MeasureString("Today: Sophia").Y;

            const int chkSize = 36;
            int dropdownH = 72; // optional: change to 72 if you want it taller
            int buttonH = 56;

            height =
                pad + titleH + 12 +
                lineH + 10 +
                lineH + 6 +
                chkSize + 10 +
                dropdownH + 16 +
                buttonH + gap +
                buttonH +
                pad;

            height = Math.Min(height, Game1.uiViewport.Height - 128);

            // center
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            initializeUpperRightCloseButton();

            int contentX = xPositionOnScreen + pad;
            int contentW = width - pad * 2;

            int y = yPositionOnScreen + pad;

            // reserve title space
            y += titleH + 12;

            _todayPos = new Vector2(contentX, y);
            y += lineH + 10;

            _pickPos = new Vector2(contentX, y);
            y += lineH + 6;

            // checkbox row (above dropdown)
            _chkBounds = new Rectangle(contentX, y, CheckBoxSize, CheckBoxSize);

            _chkTextPos = new Vector2(contentX + CheckBoxSize + 10, y + 6);
            _chkLabel = new ClickableComponent(
                new Rectangle(contentX, y, contentW, CheckBoxSize),
                _showAllLabel
            );

            y += CheckBoxSize + 10;

            // dropdown
            _dropdownBounds = new Rectangle(contentX, y, contentW, dropdownH);
            y += dropdownH + 16;

            _buttons.Clear();

            // Arrow region inside the dropdown (vanilla-style: no second border box)
            const int borderPad = 12; // 3px * 4f scale for the 9-slice border
            Rectangle inner = new Rectangle(
                _dropdownBounds.X + borderPad,
                _dropdownBounds.Y + borderPad,
                _dropdownBounds.Width - borderPad * 2,
                _dropdownBounds.Height - borderPad * 2
            );

            // Make the arrow region a square on the right, matching inner height.
            int arrowW = inner.Height;
            _arrowBounds = new Rectangle(inner.Right - arrowW, inner.Y, arrowW, inner.Height);

            // row 1: 3 columns
            LayoutRow(y, cols: 3, contentX, contentW, gap, buttonH,
                _btnSetToday = new TextButton(Rectangle.Empty, row1Labels[0], () =>
                {
                    if (_selectedRaw != NoneValue)
                        _mod.UiSetTodayNpc(_selectedRaw);
                }),
                _btnRepick = new TextButton(Rectangle.Empty, row1Labels[1], () =>
                {
                    _mod.UiRepick();
                    var t = _mod.UiGetTodayNpc();
                    _selectedRaw = string.IsNullOrWhiteSpace(t) ? NoneValue : t;
                }),
                _btnPlayNow = new TextButton(Rectangle.Empty, row1Labels[2], () =>
                {
                    if (_selectedRaw != NoneValue)
                        _mod.UiSetTodayNpc(_selectedRaw);

                    _mod.UiPlayNow();
                })
            );

            y += buttonH + gap;

            // row 2: 4 columns
            LayoutRow(y, cols: 4, contentX, contentW, gap, buttonH,
                _btnClearToday = new TextButton(Rectangle.Empty, row2Labels[0], () =>
                {
                    _mod.UiClearTodayNpc();
                    _selectedRaw = NoneValue;
                }),
                _btnStopEvent = new TextButton(Rectangle.Empty, row2Labels[1], () =>
                {
                    _mod.UiStopCurrentEvent();
                }),
                _btnClearCd = new TextButton(Rectangle.Empty, row2Labels[2], () =>
                {
                    _mod.UiClearCooldown();
                }),
                _btnClose = new TextButton(Rectangle.Empty, row2Labels[3], () =>
                {
                    Game1.playSound("bigDeSelect");
                    exitThisMenu();
                })
            );

            _btnStopEvent.IsEnabled = () => Game1.eventUp;

            _buttons.AddRange(new[]
            {
                _btnSetToday, _btnRepick, _btnPlayNow,
                _btnClearToday, _btnStopEvent, _btnClearCd, _btnClose
            });

            // keep selection valid
            if (_choices.Count > 0 && !_choices.Any(p => string.Equals(p.Raw, _selectedRaw, StringComparison.Ordinal)))
                _selectedRaw = _choices[0].Raw;
        }

        private static void LayoutRow(int y, int cols, int x, int w, int gap, int h, params TextButton[] btns)
        {
            int btnW = (w - gap * (cols - 1)) / cols;

            for (int i = 0; i < btns.Length; i++)
            {
                int bx = x + i * (btnW + gap);
                btns[i].Bounds = new Rectangle(bx, y, btnW, h);
            }
        }

        private void ToggleDropdown()
        {
            // if we're about to OPEN it, refresh list (eligibility can change + checkbox toggle)
            if (!_dropdownOpen)
                ReloadNames();

            _dropdownOpen = !_dropdownOpen;
            _hoveredIndex = -1;

            if (_dropdownOpen)
            {
                int idx = _choices.FindIndex(p => string.Equals(p.Raw, _selectedRaw, StringComparison.Ordinal));
                if (idx >= 0)
                    _dropdownScroll = Math.Clamp(idx - 2, 0, Math.Max(0, _choices.Count - VisibleRows));
            }
        }
        private void DrawTriangleArrow(SpriteBatch b, Rectangle bounds, bool pointDown)
        {
            Color arrowColor = Game1.textColor;

            int unit = 3;   // row thickness
            int rows = 5;   // triangle height (increase to 5 for a bigger arrow)

            int cx = bounds.Center.X;
            int topY = bounds.Center.Y - (rows * unit) / 2 + 2;

            for (int r = 0; r < rows; r++)
            {
                // width progression:
                // down: 1,3,5,7...
                // up:   7,5,3,1...
                int idx = pointDown ? r : (rows - 1 - r);
                int w = (2 * idx + 1) * unit;
                int y = topY + r * unit;

                b.Draw(Game1.fadeToBlackRect, new Rectangle(cx - w / 2, y, w, unit), arrowColor);
            }
        }
        private void RerollHoveredIndex(int mouseX, int mouseY)
        {
            _hoveredIndex = -1;
            if (!_dropdownOpen)
                return;

            var (listRect, itemH, footerH) = GetDropdownListRect();

            int insideTop = listRect.Y + 12;
            int insideBottom = listRect.Bottom - footerH - 8;

            if (mouseX < listRect.X + 8 || mouseX > listRect.Right - 8)
                return;
            if (mouseY < insideTop || mouseY > insideBottom)
                return;

            int rel = mouseY - insideTop;
            int row = rel / itemH;
            int idx = _dropdownScroll + row;

            if (idx >= 0 && idx < _choices.Count)
                _hoveredIndex = idx;
        }

        private (Rectangle listRect, int itemH, int footerH) GetDropdownListRect()
        {
            int itemH = 28;
            int footerH = 22;

            int visible = Math.Min(VisibleRows, _choices.Count);
            int innerH = (visible * itemH) + footerH + 20;

            int below = Game1.uiViewport.Height - (_dropdownBounds.Bottom + 8);
            bool openUp = below < innerH;

            int listX = _dropdownBounds.X;
            int listW = _dropdownBounds.Width;

            int listY = openUp
                ? (_dropdownBounds.Y - innerH - 4)
                : (_dropdownBounds.Bottom + 4);

            return (new Rectangle(listX, listY, listW, innerH), itemH, footerH);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Reflow();
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                if (_dropdownOpen)
                    _dropdownOpen = false;
                else
                    exitThisMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            if (_dropdownOpen)
            {
                _dropdownOpen = false;
                return;
            }

            base.receiveRightClick(x, y, playSound);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // close X
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            // checkbox toggle
            if (_chkLabel.bounds.Contains(x, y) || _chkBounds.Contains(x, y))
            {
                Game1.playSound("drumkit6");
                _showAllDateables = !_showAllDateables;

                _dropdownOpen = false;
                _hoveredIndex = -1;
                _dropdownScroll = 0;

                ReloadNames();
                return;
            }

            // dropdown click (arrow or whole dropdown)
            if (_arrowBounds.Contains(x, y) || _dropdownBounds.Contains(x, y))
            {
                Game1.playSound("shwip");
                ToggleDropdown();
                return;
            }

            // if dropdown is open, handle clicks inside it first
            if (_dropdownOpen)
            {
                var (listRect, itemH, footerH) = GetDropdownListRect();

                if (!listRect.Contains(x, y))
                {
                    _dropdownOpen = false;
                    return;
                }

                int insideTop = listRect.Y + 12;
                int insideBottom = listRect.Bottom - footerH - 8;

                if (y >= insideTop && y <= insideBottom)
                {
                    int rel = y - insideTop;
                    int row = rel / itemH;
                    int idx = _dropdownScroll + row;

                    if (idx >= 0 && idx < _choices.Count)
                    {
                        _selectedRaw = _choices[idx].Raw;
                        Game1.playSound("smallSelect");
                        _dropdownOpen = false;
                    }
                }

                return;
            }

            // buttons
            foreach (var b in _buttons)
            {
                if (b.Enabled && b.Bounds.Contains(x, y))
                {
                    Game1.playSound("smallSelect");
                    b.OnClick();
                    return;
                }
            }

            base.receiveLeftClick(x, y, playSound);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            if (_dropdownOpen)
            {
                var (listRect, _, _) = GetDropdownListRect();
                if (listRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
                {
                    int maxScroll = Math.Max(0, _choices.Count - VisibleRows);
                    _dropdownScroll = Math.Clamp(_dropdownScroll + (direction > 0 ? -1 : 1), 0, maxScroll);
                    Game1.playSound("shiny4");
                    return;
                }
            }

            base.receiveScrollWheelAction(direction);
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);

            foreach (var b in _buttons)
                b.Hovered = b.Bounds.Contains(x, y) && b.Enabled;

            RerollHoveredIndex(x, y);
        }

        public override void draw(SpriteBatch b)
        {
            // 1) MAIN MENU BACKGROUND
            IClickableMenu.drawTextureBox(
                b,
                Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                xPositionOnScreen,
                yPositionOnScreen,
                width,
                height,
                Color.White,
                4f,
                true
            );

            // title
            int titleW = SpriteText.getWidthOfString(_title);
            int titleX = xPositionOnScreen + (width - titleW) / 2;
            int titleY = yPositionOnScreen + 16;
            SpriteText.drawString(b, _title, titleX, titleY);

            // today line
            string today = _mod.UiGetTodayNpc();
            Utility.drawTextWithShadow(b, T("debugMenu.today", new { name = Disp(GetDisplayNameForRaw(today)) }), Game1.smallFont, _todayPos, Game1.textColor);

            // pick label
            Utility.drawTextWithShadow(b, _pickLabel, Game1.smallFont, _pickPos, Game1.textColor);

            // checkbox
            b.Draw(
                Game1.mouseCursors,
                new Vector2(_chkBounds.X, _chkBounds.Y),
                CheckBoxEmptySrc,
                Color.White,
                0f,
                Vector2.Zero,
                CheckBoxScale,
                SpriteEffects.None,
                1f
            );

            if (_showAllDateables)
            {
                b.Draw(
                    Game1.mouseCursors,
                    new Vector2(_chkBounds.X, _chkBounds.Y),
                    CheckBoxCheckedSrc,
                    Color.White,
                    0f,
                    Vector2.Zero,
                    CheckBoxScale,
                    SpriteEffects.None,
                    1f
                );
            }

            Utility.drawTextWithShadow(
                b,
                _showAllLabel,
                Game1.smallFont,
                _chkTextPos,
                Game1.textColor
            );

            // 2) DROPDOWN FIELD (outer border)
            IClickableMenu.drawTextureBox(
                b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                _dropdownBounds.X, _dropdownBounds.Y, _dropdownBounds.Width, _dropdownBounds.Height,
                Color.White, 4f, false);

            // inner region (must match Reflow's borderPad)
            const int borderPad = 12; // 3px * 4f scale
            Rectangle inner = new Rectangle(
                _dropdownBounds.X + borderPad,
                _dropdownBounds.Y + borderPad,
                _dropdownBounds.Width - borderPad * 2,
                _dropdownBounds.Height - borderPad * 2
            );

            // integrated arrow region: darker fill + divider line
            b.Draw(Game1.fadeToBlackRect, _arrowBounds, Color.Black * 0.20f);
            b.Draw(
                Game1.fadeToBlackRect,
                new Rectangle(_arrowBounds.X, _arrowBounds.Y, 2, _arrowBounds.Height),
                Color.Black * 0.35f
            );

            // down when closed, up when open
            DrawTriangleArrow(b, _arrowBounds, pointDown: !_dropdownOpen);

            // selected text (doesn't run under arrow region)
            Rectangle textRect = new Rectangle(inner.X + 4, inner.Y, inner.Width - _arrowBounds.Width - 8, inner.Height);
            float textY = inner.Y + (inner.Height - Game1.smallFont.LineSpacing) / 2f;

            Utility.drawTextWithShadow(
                b,
                Disp(GetDisplayNameForRaw(_selectedRaw)),
                Game1.smallFont,
                new Vector2(textRect.X, textY),
                Game1.textColor
            );

            // 3) BUTTONS
            foreach (var btn in _buttons)
                btn.Draw(b);

            // 4) CLOSE X
            upperRightCloseButton?.draw(b);

            // 5) DROPDOWN LIST LAST (on top of buttons)
            if (_dropdownOpen)
            {
                var (listRect, itemH, footerH) = GetDropdownListRect();

                IClickableMenu.drawTextureBox(
                    b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    listRect.X, listRect.Y, listRect.Width, listRect.Height,
                    Color.White, 4f, false);

                int insideTop = listRect.Y + 12;
                int visible = Math.Min(VisibleRows, _choices.Count);

                for (int row = 0; row < visible; row++)
                {
                    int idx = _dropdownScroll + row;
                    if (idx < 0 || idx >= _choices.Count)
                        break;

                    Rectangle itemRect = new Rectangle(
                        listRect.X + 10,
                        insideTop + row * itemH,
                        listRect.Width - 20,
                        itemH);

                    if (idx == _hoveredIndex)
                        b.Draw(Game1.fadeToBlackRect, itemRect, Color.White * 0.15f);

                    Utility.drawTextWithShadow(
                        b,
                        Disp(_choices[idx].Display),
                        Game1.smallFont,
                        new Vector2(itemRect.X + 8, itemRect.Y + 4),
                        Game1.textColor);
                }

                int footerY = listRect.Bottom - footerH - 6;
                string footer = T("debugMenu.footer", new { shown = Math.Min(_dropdownScroll + visible, _choices.Count), total = _choices.Count });
                Vector2 fSize = Game1.smallFont.MeasureString(footer);

                Utility.drawTextWithShadow(
                    b,
                    footer,
                    Game1.smallFont,
                    new Vector2(listRect.Right - fSize.X - 12, footerY),
                    Game1.textColor);
            }

            // 6) MOUSE CURSOR LAST
            drawMouse(b);
        }

        private sealed class TextButton
        {
            public Rectangle Bounds;
            public readonly string Label;
            public readonly Action OnClick;
            public Func<bool>? IsEnabled;

            public bool Hovered;

            public bool Enabled => IsEnabled?.Invoke() ?? true;

            public TextButton(Rectangle bounds, string label, Action onClick)
            {
                Bounds = bounds;
                Label = label;
                OnClick = onClick;
            }

            public void Draw(SpriteBatch b)
            {
                float scale = 4f;
                Color boxColor = Enabled ? Color.White : (Color.White * 0.4f);

                IClickableMenu.drawTextureBox(
                    b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height,
                    boxColor, scale, false);

                if (Hovered && Enabled)
                    b.Draw(Game1.fadeToBlackRect, Bounds, Color.White * 0.08f);

                // The menu is sized to fit every label, but on a very narrow screen shrink the text rather than overflow.
                Vector2 size = Game1.smallFont.MeasureString(Label);
                float textScale = size.X > 0 ? Math.Min(1f, (Bounds.Width - ButtonTextPad * 2) / size.X) : 1f;
                size *= textScale;
                float tx = Bounds.Center.X - size.X / 2f;
                float ty = Bounds.Center.Y - size.Y / 2f;

                Utility.drawTextWithShadow(
                    b,
                    Label,
                    Game1.smallFont,
                    new Vector2(tx, ty),
                    Enabled ? Game1.textColor : (Game1.textColor * 0.6f),
                    textScale);
            }
        }
    }
}

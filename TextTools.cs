// Selecting and copying text, and Find, on top of the page image. The text comes from TextDocument (PdfText.cs).
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PdfReaderApp
{
    partial class MainWindow
    {
        static readonly Brush SelectionBrush = Frozen(Color.FromArgb(0x70, 0x3D, 0x8B, 0xFD));
        static readonly Brush MatchBrush = Frozen(Color.FromArgb(0x66, 0xFF, 0xD4, 0x00));
        static readonly Brush CurrentMatchBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0x8C, 0x00));

        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        class Match { public int Page, Start, End; }

        Canvas textLayer;
        FrameworkElement findBar;
        TextBox findBox;
        TextBlock findHint, findCount;
        readonly DispatcherTimer findTimer = new DispatcherTimer();

        TextDocument textDoc;
        int selAnchor = -1, selStart, selEnd;   // Character boundaries on the current page.
        bool selecting;
        List<Match> matches = new List<Match>();
        int matchIndex = -1, findVersion;
        Match revealMatch;

        void InitTextTools()
        {
            textLayer = Find<Canvas>("TextLayer");
            findBar = Find<FrameworkElement>("FindBar");
            findBox = Find<TextBox>("FindBox");
            findHint = Find<TextBlock>("FindHint");
            findCount = Find<TextBlock>("FindCount");
            Find<Button>("FindButton").Click += delegate { OpenFind(); };
            Find<Button>("FindNextButton").Click += delegate { StepMatch(1); };
            Find<Button>("FindPrevButton").Click += delegate { StepMatch(-1); };
            Find<Button>("FindCloseButton").Click += delegate { CloseFind(); };

            findTimer.Interval = TimeSpan.FromMilliseconds(250);
            findTimer.Tick += delegate { findTimer.Stop(); RunFind(); };
            findBox.TextChanged += delegate
            {
                findHint.Visibility = findBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                findTimer.Stop();
                findTimer.Start();
            };
            findBox.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    if (findTimer.IsEnabled) { findTimer.Stop(); RunFind(); }
                    else StepMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
            };

            textLayer.MouseLeftButtonDown += TextMouseDown;
            textLayer.MouseMove += TextMouseMove;
            textLayer.MouseLeftButtonUp += delegate { if (selecting) { selecting = false; textLayer.ReleaseMouseCapture(); } };

            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
            copy.Click += delegate { CopySelection(); };
            var all = new MenuItem { Header = "Select all on this page", InputGestureText = "Ctrl+A" };
            all.Click += delegate { SelectAllOnPage(); };
            menu.Items.Add(copy);
            menu.Items.Add(all);
            menu.Opened += delegate { copy.IsEnabled = selEnd > selStart; all.IsEnabled = CurrentText().HasText; };
            textLayer.ContextMenu = menu;
        }

        // ---------- Document changes ----------

        void OpenText(byte[] bytes)
        {
            CloseText();
            textDoc = TextDocument.Open(bytes);
        }

        void CloseText()
        {
            if (textDoc != null) { textDoc.Dispose(); textDoc = null; }
            findVersion++;
            matches = new List<Match>();
            matchIndex = -1;
            revealMatch = null;
            selAnchor = -1; selStart = selEnd = 0;
            if (findBar != null && findBar.Visibility == Visibility.Visible) { findBox.Text = ""; findCount.Text = ""; }
            if (textLayer != null) textLayer.Children.Clear();
        }

        PageText CurrentText() { return textDoc == null ? new PageText() : textDoc.Page(pageIndex); }

        // ---------- Selection ----------

        Point ViewPoint(MouseEventArgs e)
        {
            Point p = e.GetPosition(textLayer);
            double w = textLayer.ActualWidth, h = textLayer.ActualHeight;
            return new Point(w > 0 ? p.X / w : 0, h > 0 ? p.Y / h : 0);
        }

        void TextMouseDown(object sender, MouseButtonEventArgs e)
        {
            PageText text = CurrentText();
            bool exact;
            int at = text.BoundaryAt(ViewPoint(e), out exact);
            if (at < 0 || !exact && !NearText(text, e))
            {
                ClearSelection();
                return;
            }
            if (e.ClickCount == 2) { SelectAround(text, at, false); e.Handled = true; return; }
            if (e.ClickCount >= 3) { SelectAround(text, at, true); e.Handled = true; return; }
            selAnchor = at;
            selStart = selEnd = at;
            selecting = textLayer.CaptureMouse();
            Focus();
            DrawTextLayer();
            e.Handled = true;
        }

        // A press just beside a line (in the margin next to it) still starts a selection there.
        bool NearText(PageText text, MouseEventArgs e)
        {
            Point v = ViewPoint(e);
            double step = 12.0 / Math.Max(1, textLayer.ActualWidth);
            return text.IsOverText(new Point(v.X - step, v.Y)) || text.IsOverText(new Point(v.X + step, v.Y));
        }

        void TextMouseMove(object sender, MouseEventArgs e)
        {
            PageText text = CurrentText();
            if (selecting && e.LeftButton == MouseButtonState.Pressed)
            {
                bool exact;
                int at = text.BoundaryAt(ViewPoint(e), out exact);
                if (at >= 0)
                {
                    selStart = Math.Min(selAnchor, at);
                    selEnd = Math.Max(selAnchor, at);
                    DrawTextLayer();
                }
                AutoScroll(e);
                return;
            }
            textLayer.Cursor = text.IsOverText(ViewPoint(e)) ? Cursors.IBeam : null;
        }

        // Dragging past the visible part of a zoomed page scrolls it.
        void AutoScroll(MouseEventArgs e)
        {
            Point p = e.GetPosition(pageScroller);
            const double edge = 24, step = 18;
            if (p.Y < edge) pageScroller.ScrollToVerticalOffset(pageScroller.VerticalOffset - step);
            else if (p.Y > pageScroller.ViewportHeight - edge) pageScroller.ScrollToVerticalOffset(pageScroller.VerticalOffset + step);
            if (p.X < edge) pageScroller.ScrollToHorizontalOffset(pageScroller.HorizontalOffset - step);
            else if (p.X > pageScroller.ViewportWidth - edge) pageScroller.ScrollToHorizontalOffset(pageScroller.HorizontalOffset + step);
        }

        // Double-click selects a word; triple-click selects the line.
        void SelectAround(PageText text, int at, bool line)
        {
            string t = text.Text;
            if (t.Length == 0) return;
            int i = Math.Max(0, Math.Min(at, t.Length - 1));
            Func<char, bool> inside = line
                ? (Func<char, bool>)(c => c != '\r' && c != '\n')
                : (c => char.IsLetterOrDigit(c) || c == '\'' || c == '’' || c == '-' || c == '_');
            if (!inside(t[i]) && i > 0 && inside(t[i - 1])) i--;
            if (!inside(t[i])) return;
            int s = i, en = i + 1;
            while (s > 0 && inside(t[s - 1])) s--;
            while (en < t.Length && inside(t[en])) en++;
            selAnchor = s; selStart = s; selEnd = en;
            selecting = false;
            Focus();
            DrawTextLayer();
        }

        void SelectAllOnPage()
        {
            PageText text = CurrentText();
            if (!text.HasText) return;
            selAnchor = 0; selStart = 0; selEnd = text.Text.Length;
            DrawTextLayer();
        }

        void ClearSelection()
        {
            if (selEnd == selStart && selAnchor < 0) return;
            selAnchor = -1; selStart = selEnd = 0;
            selecting = false;
            DrawTextLayer();
        }

        bool CopySelection()
        {
            if (selEnd <= selStart) return false;
            string s = CurrentText().Copy(selStart, selEnd);
            if (s.Length == 0) return false;
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetText(s); return true; }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Another app can hold the clipboard for a moment.
                    if (attempt >= 4) return false;
                    System.Threading.Thread.Sleep(40);
                }
            }
        }

        // ---------- Find ----------

        void OpenFind()
        {
            if (doc == null) return;
            findBar.Visibility = Visibility.Visible;
            string picked = selEnd > selStart ? CurrentText().Copy(selStart, selEnd) : "";
            if (picked.Length > 0 && picked.Length <= 100 && picked.IndexOf('\n') < 0) findBox.Text = picked;
            findBox.Focus();
            findBox.SelectAll();
            if (textDoc == null) findCount.Text = "Text unavailable";
        }

        void CloseFind()
        {
            findBar.Visibility = Visibility.Collapsed;
            findTimer.Stop();
            findVersion++;
            matches = new List<Match>();
            matchIndex = -1;
            findCount.Text = "";
            DrawTextLayer();
            Focus();
        }

        async void RunFind()
        {
            int version = ++findVersion;
            string query = findBox.Text;
            TextDocument source = textDoc;
            matches = new List<Match>();
            matchIndex = -1;
            if (source == null) { findCount.Text = "Text unavailable"; return; }
            if (query.Trim().Length == 0) { findCount.Text = ""; DrawTextLayer(); return; }
            findCount.Text = "Searching...";
            int startPage = pageIndex;
            List<Match> found = await Task.Run(() =>
            {
                var list = new List<Match>();
                for (int p = 0; p < source.PageCount; p++)
                {
                    if (version != findVersion) return null;
                    foreach (int[] m in source.Page(p).Find(query)) list.Add(new Match { Page = p, Start = m[0], End = m[1] });
                }
                return list;
            });
            if (found == null || version != findVersion || source != textDoc) return;
            matches = found;
            if (matches.Count == 0)
            {
                findCount.Text = "No matches";
                DrawTextLayer();
                return;
            }
            // Start at the first match on or after the page being read.
            matchIndex = matches.FindIndex(m => m.Page >= startPage);
            if (matchIndex < 0) matchIndex = 0;
            ShowMatch();
        }

        void StepMatch(int delta)
        {
            if (findBar.Visibility != Visibility.Visible) { OpenFind(); return; }
            if (matches.Count == 0) return;
            matchIndex = (matchIndex + delta + matches.Count) % matches.Count;
            ShowMatch();
        }

        void ShowMatch()
        {
            Match m = matches[matchIndex];
            findCount.Text = (matchIndex + 1) + " of " + matches.Count;
            revealMatch = m;
            if (m.Page != pageIndex) GoTo(m.Page);   // The match is revealed once that page is drawn.
            else { DrawTextLayer(); RevealMatch(); }
        }

        // Scrolls a zoomed page so the current match is in view.
        void RevealMatch()
        {
            Match m = revealMatch;
            if (m == null || m.Page != pageIndex || textDoc == null) return;
            revealMatch = null;
            List<Rect> rects = textDoc.Page(m.Page).RectsFor(m.Start, m.End);
            if (rects.Count == 0 || textLayer.ActualWidth == 0) return;
            Rect r = rects[0];
            Point at = textLayer.TranslatePoint(new Point(r.X * textLayer.ActualWidth, r.Y * textLayer.ActualHeight), pageScroller);
            double h = r.Height * textLayer.ActualHeight;
            if (at.Y < 0 || at.Y + h > pageScroller.ViewportHeight)
                pageScroller.ScrollToVerticalOffset(pageScroller.VerticalOffset + at.Y - pageScroller.ViewportHeight / 3);
            if (at.X < 0 || at.X > pageScroller.ViewportWidth)
                pageScroller.ScrollToHorizontalOffset(pageScroller.HorizontalOffset + at.X - pageScroller.ViewportWidth / 3);
        }

        // ---------- Drawing ----------

        // Called when the page, zoom, selection or matches change.
        void DrawTextLayer()
        {
            if (textLayer == null) return;
            textLayer.Children.Clear();
            if (textDoc == null || doc == null) return;
            double w = pageImage.Width, h = pageImage.Height;
            if (!(w > 0) || !(h > 0)) return;
            PageText text = CurrentText();
            Match current = matchIndex >= 0 && matchIndex < matches.Count ? matches[matchIndex] : null;
            foreach (Match m in matches)
                if (m.Page == pageIndex)
                    foreach (Rect r in text.RectsFor(m.Start, m.End)) AddRect(r, w, h, m == current ? CurrentMatchBrush : MatchBrush);
            if (selEnd > selStart)
                foreach (Rect r in text.RectsFor(selStart, selEnd)) AddRect(r, w, h, SelectionBrush);
        }

        void AddRect(Rect r, double w, double h, Brush fill)
        {
            var box = new Rectangle { Width = Math.Max(1, r.Width * w), Height = Math.Max(1, r.Height * h), Fill = fill, IsHitTestVisible = false };
            Canvas.SetLeft(box, r.X * w);
            Canvas.SetTop(box, r.Y * h);
            textLayer.Children.Add(box);
        }
    }
}

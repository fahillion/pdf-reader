// The text layer: which characters are on a page and where, read with PDFium (pdfium.dll, beside the exe).
// Windows' own PDF engine still draws the pages; this only finds the text that's already in the PDF,
// for selecting, copying and Find. Scanned pages without real text simply have none.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace PdfReaderApp
{
    static class Pdfium
    {
        const string Dll = "pdfium.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct RectF { public float Left, Top, Right, Bottom; }

        [DllImport(Dll)] public static extern void FPDF_InitLibrary();
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string password);
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Dll)] public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
        [DllImport(Dll)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Dll)] public static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport(Dll)] public static extern int FPDFText_CountChars(IntPtr textPage);
        [DllImport(Dll)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
        [DllImport(Dll)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, out RectF rect);
    }

    // One page's text. Boxes are in PDF page space (points, y up); ToView maps page space to 0..1 of the
    // page as displayed, which includes the page's own rotation.
    class PageText
    {
        public string Text = "";
        public Rect[] Boxes = new Rect[0];
        public Matrix ToView = Matrix.Identity;
        public Matrix FromView = Matrix.Identity;
        string searchable;
        int[] searchMap;

        public bool HasText { get { foreach (Rect r in Boxes) if (!r.IsEmpty) return true; return false; } }

        static bool Usable(Rect r) { return !r.IsEmpty && r.Width > 0 && r.Height > 0; }

        // The character boundary (0..Text.Length) nearest a point given in 0..1 view space.
        // Returns -1 when the page has no text. "exact" reports whether the point is over a character.
        public int BoundaryAt(Point view, out bool exact)
        {
            exact = false;
            Point p = FromView.Transform(view);
            int best = -1; double bestScore = double.MaxValue;
            for (int i = 0; i < Boxes.Length; i++)
            {
                Rect b = Boxes[i];
                if (!Usable(b)) continue;
                if (b.Contains(p))
                {
                    exact = true;
                    return p.X < b.Left + b.Width / 2 ? i : i + 1;
                }
                // Prefer characters on the same line: vertical distance counts far more than horizontal.
                double dy = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
                double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
                double score = dy * 20 + dx;
                if (score < bestScore) { bestScore = score; best = p.X < b.Left + b.Width / 2 ? i : i + 1; }
            }
            return best;
        }

        public bool IsOverText(Point view)
        {
            Point p = FromView.Transform(view);
            foreach (Rect b in Boxes) if (Usable(b) && b.Contains(p)) return true;
            return false;
        }

        // Highlight rectangles (0..1 view space) for characters [start, end), merged along each line.
        public List<Rect> RectsFor(int start, int end)
        {
            var result = new List<Rect>();
            Rect cur = Rect.Empty;
            for (int i = Math.Max(0, start); i < Math.Min(end, Boxes.Length); i++)
            {
                Rect b = Boxes[i];
                if (!Usable(b)) continue;
                if (cur.IsEmpty) { cur = b; continue; }
                double overlap = Math.Min(cur.Bottom, b.Bottom) - Math.Max(cur.Top, b.Top);
                if (overlap >= 0.5 * Math.Min(cur.Height, b.Height) && b.Left >= cur.Left - b.Width) cur.Union(b);
                else { result.Add(ToViewRect(cur)); cur = b; }
            }
            if (!cur.IsEmpty) result.Add(ToViewRect(cur));
            return result;
        }

        Rect ToViewRect(Rect r)
        {
            Point a = ToView.Transform(r.TopLeft), b = ToView.Transform(r.BottomRight);
            return new Rect(a, b);
        }

        // The text to copy: PDFium's own line breaks are kept; its marker for a hyphen that ended a line
        // becomes a hyphen; other control characters are dropped.
        public string Copy(int start, int end)
        {
            var sb = new StringBuilder();
            for (int i = Math.Max(0, start); i < Math.Min(end, Text.Length); i++)
            {
                char c = Text[i];
                if (c == '\u0002') sb.Append('-');
                else if (c >= ' ' || c == '\r' || c == '\n' || c == '\t') sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // Every match of the query, ignoring case and treating any run of spaces or line breaks as one space.
        public List<int[]> Find(string query)
        {
            string q = Normalize(query, null);
            var found = new List<int[]>();
            if (q.Length == 0) return found;
            if (searchable == null) { var map = new List<int>(); searchable = Normalize(Text, map); searchMap = map.ToArray(); }
            int at = 0;
            while ((at = searchable.IndexOf(q, at, StringComparison.Ordinal)) >= 0)
            {
                found.Add(new[] { searchMap[at], searchMap[at + q.Length - 1] + 1 });
                at += q.Length;
            }
            return found;
        }

        static string Normalize(string s, List<int> map)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\u0002') continue;   // A line-end hyphen: "inter-\nnational" still finds "international".
                if (char.IsWhiteSpace(c) || c < ' ')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') { sb.Append(' '); if (map != null) map.Add(i); }
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
                if (map != null) map.Add(i);
            }
            if (map == null) return sb.ToString().Trim();
            return sb.ToString();
        }
    }

    // A PDFium copy of the open document. PDFium isn't thread-safe, so every call goes through Gate.
    sealed class TextDocument : IDisposable
    {
        static readonly object Gate = new object();
        static bool initialized;

        IntPtr memory, document;
        readonly PageText[] pages;

        public int PageCount { get { return pages.Length; } }

        TextDocument(IntPtr memory, IntPtr document, int count)
        {
            this.memory = memory;
            this.document = document;
            pages = new PageText[count];
        }

        // Returns null when pdfium.dll is missing or can't read the file; the app then works without text.
        public static TextDocument Open(byte[] data)
        {
            try
            {
                lock (Gate)
                {
                    if (!initialized) { Pdfium.FPDF_InitLibrary(); initialized = true; }
                    // PDFium reads from this buffer for as long as the document is open.
                    IntPtr mem = Marshal.AllocHGlobal(data.Length);
                    Marshal.Copy(data, 0, mem, data.Length);
                    IntPtr doc = Pdfium.FPDF_LoadMemDocument(mem, data.Length, null);
                    if (doc == IntPtr.Zero) { Marshal.FreeHGlobal(mem); return null; }
                    return new TextDocument(mem, doc, Pdfium.FPDF_GetPageCount(doc));
                }
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
        }

        public PageText Page(int index)
        {
            if (index < 0 || index >= pages.Length) return new PageText();
            lock (Gate)
            {
                if (pages[index] != null) return pages[index];
                if (document == IntPtr.Zero) return new PageText();
                var result = new PageText();
                IntPtr page = Pdfium.FPDF_LoadPage(document, index);
                if (page != IntPtr.Zero)
                {
                    try
                    {
                        result.ToView = ViewMatrix(page);
                        result.FromView = result.ToView;
                        if (result.FromView.HasInverse) result.FromView.Invert();
                        IntPtr textPage = Pdfium.FPDFText_LoadPage(page);
                        if (textPage != IntPtr.Zero)
                        {
                            try
                            {
                                int n = Math.Max(0, Pdfium.FPDFText_CountChars(textPage));
                                var text = new char[n];
                                var boxes = new Rect[n];
                                for (int i = 0; i < n; i++)
                                {
                                    uint u = Pdfium.FPDFText_GetUnicode(textPage, i);
                                    text[i] = u <= 0xFFFF ? (char)u : '�';
                                    Pdfium.RectF r;
                                    boxes[i] = Pdfium.FPDFText_GetLooseCharBox(textPage, i, out r) != 0 && r.Right > r.Left && r.Top != r.Bottom
                                        ? new Rect(new Point(r.Left, Math.Min(r.Top, r.Bottom)), new Point(r.Right, Math.Max(r.Top, r.Bottom)))
                                        : Rect.Empty;
                                }
                                result.Text = new string(text);
                                result.Boxes = boxes;
                            }
                            finally { Pdfium.FPDFText_ClosePage(textPage); }
                        }
                    }
                    finally { Pdfium.FPDF_ClosePage(page); }
                }
                pages[index] = result;
                return result;
            }
        }

        // PDFium's own page-to-screen mapping, measured at three points on a large virtual screen and
        // scaled to 0..1, so it matches the rotated page Windows draws.
        static Matrix ViewMatrix(IntPtr page)
        {
            double w = Pdfium.FPDF_GetPageWidthF(page), h = Pdfium.FPDF_GetPageHeightF(page);
            int sx = (int)Math.Max(1, Math.Round(w * 100)), sy = (int)Math.Max(1, Math.Round(h * 100));
            int x0, y0, x1, y1, x2, y2;
            Pdfium.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, 0, out x0, out y0);
            Pdfium.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 1000, 0, out x1, out y1);
            Pdfium.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, 1000, out x2, out y2);
            return new Matrix(
                (x1 - x0) / 1000.0 / sx, (y1 - y0) / 1000.0 / sy,
                (x2 - x0) / 1000.0 / sx, (y2 - y0) / 1000.0 / sy,
                (double)x0 / sx, (double)y0 / sy);
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (document != IntPtr.Zero) { Pdfium.FPDF_CloseDocument(document); document = IntPtr.Zero; }
                if (memory != IntPtr.Zero) { Marshal.FreeHGlobal(memory); memory = IntPtr.Zero; }
            }
        }
    }
}

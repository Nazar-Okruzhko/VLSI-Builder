// +-----------------------------------------------------------------------------------------+
// |                        VLSI BUILDER  v1.0                                               |
// |  C# .NET 6.0 / WinForms  -  Single-file edition                                         |
// |-----------------------------------------------------------------------------------------|
// |  Build / Publish:                                                                       |
// |    dotnet build                                                                         |
// |    dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true|
// +-----------------------------------------------------------------------------------------+

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VLSIBuilder
{
    // --------------------------------------------------------------------------
    //  ENTRY POINT
    // --------------------------------------------------------------------------

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new MainForm());
        }
    }

    // --------------------------------------------------------------------------
    //  LAYER TYPE
    // --------------------------------------------------------------------------

    public enum LayerType
    {
        NWell       = 1,
        PWell       = 2,
        NActive     = 3,
        PActive     = 4,
        Polysilicon = 5,
        Contact     = 6,
        Metal1      = 7,
        Metal2      = 8,
        Via         = 9
    }

    // --------------------------------------------------------------------------
    //  VLSI ELEMENT
    // --------------------------------------------------------------------------

    public sealed class VLSIElement
    {
        public LayerType Layer    { get; set; }
        public Rectangle Bounds   { get; set; }
        public bool      Selected { get; set; }

        public VLSIElement(LayerType layer, Rectangle bounds)
        { Layer = layer; Bounds = bounds; }
    }

    // --------------------------------------------------------------------------
    //  LAYER DEFINITION  — solid colours, fixed z-order
    // --------------------------------------------------------------------------

    public static class LayerDefinition
    {
        public readonly struct LayerInfo
        {
            public readonly string Name;
            public readonly Color  Fill;
            public readonly Color  Border;
            public LayerInfo(string n, Color f, Color b) { Name = n; Fill = f; Border = b; }
        }

        private static readonly LayerInfo[] Table = new LayerInfo[10]; // index = (int)LayerType

        static LayerDefinition()
        {
            Table[(int)LayerType.NWell]       = new("N-Well",      Color.FromArgb(218, 182, 242), Color.FromArgb(138,  48, 192));
            Table[(int)LayerType.PWell]       = new("P-Well",      Color.FromArgb(196, 124,  72), Color.FromArgb(136,  58,   8));
            Table[(int)LayerType.NActive]     = new("N-Active",    Color.FromArgb(120, 210, 120), Color.FromArgb( 18, 128,  18));
            Table[(int)LayerType.PActive]     = new("P-Active",    Color.FromArgb(232, 222,  88), Color.FromArgb(128, 118,  12));
            Table[(int)LayerType.Polysilicon] = new("Polysilicon", Color.FromArgb(218,  54,  54), Color.FromArgb(148,   0,   0));
            Table[(int)LayerType.Contact]     = new("Contact",     Color.FromArgb( 58,  58,  58), Color.FromArgb(  0,   0,   0));
            Table[(int)LayerType.Metal1]      = new("Metal 1",     Color.FromArgb( 88, 148, 230), Color.FromArgb(  0,  48, 172));
            Table[(int)LayerType.Metal2]      = new("Metal 2",     Color.FromArgb(218, 112, 218), Color.FromArgb(148,   0, 148));
            Table[(int)LayerType.Via]         = new("Via",         Color.FromArgb(178, 128,  58), Color.FromArgb( 98,  52,   0));
        }

        public static LayerInfo Get(LayerType l)       => Table[(int)l];
        public static string    GetName(LayerType l)   => Table[(int)l].Name;
        public static Color     GetFill(LayerType l)   => Table[(int)l].Fill;
        public static Color     GetBorder(LayerType l) => Table[(int)l].Border;

        // Render order: wells/active at bottom; Polysilicon, Contact, Metal, Via always on top
        public static readonly int[] ZOrderTable = new int[10];

        public static int ZOrder(LayerType l) => l switch
        {
            LayerType.NWell       => 0,
            LayerType.PWell       => 1,
            LayerType.NActive     => 2,
            LayerType.PActive     => 3,
            LayerType.Polysilicon => 6,
            LayerType.Contact     => 7,
            LayerType.Metal1      => 8,
            LayerType.Via         => 9,
            LayerType.Metal2      => 10,
            _                     => 5,
        };
    }

    // --------------------------------------------------------------------------
    //  CANVAS CONTROL
    // --------------------------------------------------------------------------

    public sealed class CanvasControl : Control
    {
        // -- Constants ----------------------------------------------------------
        private const int   GridSize = 20;
        private const float MinZoom  = 0.04f;
        private const float MaxZoom  = 50f;
        private const int   WorldMin = -2000;
        private const int   WorldMax =  8000;

        // -- GDI cache: one brush+pen per layer (static — shared, never disposed) --
        // Index = (int)LayerType  (1-based, index 0 unused)
        private static readonly SolidBrush[] s_fillBrush   = new SolidBrush[10];
        private static readonly Pen[]        s_borderPen   = new Pen[10];
        private static readonly SolidBrush   s_contactBg   = new SolidBrush(Color.FromArgb(32, 32, 32));
        private static readonly SolidBrush   s_whiteBrush  = new SolidBrush(Color.White);

        static CanvasControl()
        {
            for (int i = 1; i <= 9; i++)
            {
                var lt = (LayerType)i;
                s_fillBrush[i] = new SolidBrush(LayerDefinition.GetFill(lt));
                // Width will be set per-frame via pen.Width — no reallocation
                s_borderPen[i] = new Pen(LayerDefinition.GetBorder(lt), 1.5f);
            }
        }

        // -- GDI cache: instance pens whose width tracks 1/_zoom ---------------
        //    Widths are updated in OnPaint once, before any drawing.
        private readonly Pen _gridFine   = new(Color.FromArgb(226, 226, 234), 1f);
        private readonly Pen _gridCoarse = new(Color.FromArgb(192, 192, 208), 1f);
        private readonly Pen _gridAxis   = new(Color.FromArgb(155, 155, 215), 1f);

        // Handle rendering (dashed outline + solid corner squares)
        private readonly Pen _selOutPen  = new(Color.FromArgb(0,  114, 210), 1f) { DashStyle = DashStyle.Dash };
        private readonly Pen _hvrOutPen  = new(Color.FromArgb(40, 175,  40), 1f) { DashStyle = DashStyle.Dash };
        private readonly Pen _selHndPen  = new(Color.FromArgb(0,   94, 185), 1f);
        private readonly Pen _hvrHndPen  = new(Color.FromArgb(20, 150,  20), 1f);

        // Net trace (8 entries matching NetPalette)
        private static readonly Color[] NetPalette =
        {
            Color.FromArgb(255, 215,   0),
            Color.FromArgb(  0, 220, 220),
            Color.FromArgb(220,  50, 220),
            Color.FromArgb( 80, 255,  80),
            Color.FromArgb(255, 120,   0),
            Color.FromArgb(255,  60, 100),
            Color.FromArgb(120, 200, 255),
            Color.FromArgb(255, 255, 100),
        };
        private readonly SolidBrush[] _netGlowBrush = new SolidBrush[8];
        private readonly Pen[]        _netGlowPen   = new Pen[8];

        // -- Data ---------------------------------------------------------------
        private readonly List<VLSIElement> _elements = new();
        private readonly List<VLSIElement> _selected = new();

        // Pre-sorted element list — rebuilt only when elements are added/removed
        private readonly List<VLSIElement> _sorted     = new();
        private          bool              _sortDirty  = true;

        // Cached transistor counts — recomputed only when elements change
        private int  _cachedNmos = 0, _cachedPmos = 0;
        private bool _transDirty = true;

        // -- Mode ---------------------------------------------------------------
        private LayerType _layer    = LayerType.Metal1;
        private bool      _testMode = false;

        // -- Viewport -----------------------------------------------------------
        private float _panX = 80f, _panY = 80f, _zoom = 2f;

        // -- Draw (right-drag) --------------------------------------------------
        private bool  _drawing;
        private Point _drawA, _drawB;

        // -- Rubber-band (left-drag on empty) -----------------------------------
        private bool  _banding;
        private Point _bandA, _bandB;

        // -- Move (left-drag on body) -------------------------------------------
        private bool  _moving;
        private Point _moveStart;
        private Dictionary<VLSIElement, Rectangle> _moveOrigins = new();

        // -- Resize (left-drag on handle) ---------------------------------------
        private bool         _resizing;
        private int          _resizeHandle;
        private VLSIElement? _resizeEl;
        private Rectangle    _resizeOrig;

        // -- Pan (middle-drag) --------------------------------------------------
        private bool  _panning;
        private Point _panStart;
        private float _panX0, _panY0;

        // -- Hover state --------------------------------------------------------
        private VLSIElement? _hoverEl;
        private int          _hoverHandle = -1;

        // -- Test mode: multi-net traces ----------------------------------------
        private readonly List<(HashSet<VLSIElement> Net, VLSIElement Source, Color Colour)> _traces = new();

        // -- Status throttle — only emit when coordinates or counts change ------
        private Point  _lastStatusSnap  = new(int.MinValue, 0);
        private int    _lastElCount     = -1;
        private int    _lastSelCount    = -1;
        private bool   _lastTestMode    = false;
        private int    _lastNetCount    = -1;

        // -- Events -------------------------------------------------------------
        public event Action<LayerType>? LayerChanged;
        public event Action<bool>?      TestModeChanged;
        public event Action<string>?    StatusChanged;

        // -- Properties ---------------------------------------------------------
        public LayerType CurrentLayer
        {
            get => _layer;
            set { _layer = value; LayerChanged?.Invoke(value); Invalidate(); }
        }

        public bool TestMode
        {
            get => _testMode;
            set
            {
                _testMode = value;
                _traces.Clear();
                Cursor = value ? Cursors.Hand : Cursors.Cross;
                TestModeChanged?.Invoke(value);
                Invalidate();
            }
        }

        public int ElementCount  => _elements.Count;
        public int SelectedCount => _selected.Count;

        // -- Constructor --------------------------------------------------------
        public CanvasControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint  |
                     ControlStyles.UserPaint             |
                     ControlStyles.ResizeRedraw,          true);
            BackColor = Color.White;
            Cursor    = Cursors.Cross;

            // Build net GDI cache from palette
            for (int i = 0; i < 8; i++)
            {
                _netGlowBrush[i] = new SolidBrush(Color.FromArgb(100, NetPalette[i]));
                _netGlowPen[i]   = new Pen(NetPalette[i], 3f);
            }
        }

        // -- Dispose: release cached instance GDI objects ----------------------
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gridFine.Dispose(); _gridCoarse.Dispose(); _gridAxis.Dispose();
                _selOutPen.Dispose(); _hvrOutPen.Dispose();
                _selHndPen.Dispose(); _hvrHndPen.Dispose();
                foreach (var b in _netGlowBrush) b.Dispose();
                foreach (var p in _netGlowPen)   p.Dispose();
            }
            base.Dispose(disposing);
        }

        // -----------------------------------------------------------------------
        //  DIRTY FLAGS
        // -----------------------------------------------------------------------

        private void MarkElementsDirty()
        {
            _sortDirty  = true;
            _transDirty = true;
        }

        private List<VLSIElement> SortedElements()
        {
            if (!_sortDirty) return _sorted;
            _sorted.Clear();
            _sorted.AddRange(_elements);
            _sorted.Sort(static (a, b) =>
                LayerDefinition.ZOrder(a.Layer).CompareTo(LayerDefinition.ZOrder(b.Layer)));
            _sortDirty = false;
            return _sorted;
        }

        private (int nmos, int pmos) GetTransistorCounts()
        {
            if (!_transDirty) return (_cachedNmos, _cachedPmos);
            int n = 0, p = 0;
            // count poly-active intersections — O(polys * actives)
            foreach (var poly in _elements)
            {
                if (poly.Layer != LayerType.Polysilicon) continue;
                foreach (var act in _elements)
                {
                    if (act.Layer == LayerType.NActive && poly.Bounds.IntersectsWith(act.Bounds)) n++;
                    else if (act.Layer == LayerType.PActive && poly.Bounds.IntersectsWith(act.Bounds)) p++;
                }
            }
            _cachedNmos = n; _cachedPmos = p;
            _transDirty = false;
            return (n, p);
        }

        // -----------------------------------------------------------------------
        //  COORDINATE HELPERS
        // -----------------------------------------------------------------------

        private PointF ScreenToWorld(Point s) =>
            new((s.X - _panX) / _zoom, (s.Y - _panY) / _zoom);

        private Point WorldToScreen(PointF w) =>
            new((int)(w.X * _zoom + _panX), (int)(w.Y * _zoom + _panY));

        private static Point SnapGrid(PointF w) =>
            new((int)(Math.Round(w.X / GridSize) * GridSize),
                (int)(Math.Round(w.Y / GridSize) * GridSize));

        private static Rectangle ClampBounds(Rectangle r)
        {
            int x = Math.Clamp(r.X, WorldMin, WorldMax - GridSize);
            int y = Math.Clamp(r.Y, WorldMin, WorldMax - GridSize);
            int w = Math.Clamp(r.Width,  GridSize, WorldMax - x);
            int h = Math.Clamp(r.Height, GridSize, WorldMax - y);
            return new Rectangle(x, y, w, h);
        }

        private Rectangle ScreenPairToWorldRect(Point sa, Point sb)
        {
            Point wa = SnapGrid(ScreenToWorld(sa));
            Point wb = SnapGrid(ScreenToWorld(sb));
            return ClampBounds(new Rectangle(
                Math.Min(wa.X, wb.X), Math.Min(wa.Y, wb.Y),
                Math.Abs(wb.X - wa.X), Math.Abs(wb.Y - wa.Y)));
        }

        // -----------------------------------------------------------------------
        //  HANDLE GEOMETRY  — no heap allocation
        // -----------------------------------------------------------------------

        private float HalfHandle => Math.Clamp(8f / _zoom, 0.5f, 30f);
        private float VisHandle  => Math.Clamp(6.5f / _zoom, 0.3f, 50f);
        private float HandlePad  => 3f / _zoom;

        // Inline corner math — avoids allocating PointF[] on every call
        private void GetHandleCorners(VLSIElement el, float p,
            out float tlx, out float tly,
            out float trx, out float try_,
            out float blx, out float bly,
            out float brx, out float bry)
        {
            tlx = el.Bounds.Left  - p; tly = el.Bounds.Top    - p;
            trx = el.Bounds.Right + p; try_ = tly;
            blx = tlx;                  bly = el.Bounds.Bottom + p;
            brx = trx;                  bry = bly;
        }

        private int HitHandle(VLSIElement el, PointF w)
        {
            float hs = HalfHandle, p = HandlePad;
            GetHandleCorners(el, p,
                out float tlx, out float tly,
                out float trx, out float try_,
                out float blx, out float bly,
                out float brx, out float bry);

            float wx = w.X, wy = w.Y;
            if (Math.Abs(wx - tlx) <= hs && Math.Abs(wy - tly) <= hs) return 0;
            if (Math.Abs(wx - trx) <= hs && Math.Abs(wy - try_) <= hs) return 1;
            if (Math.Abs(wx - blx) <= hs && Math.Abs(wy - bly) <= hs) return 2;
            if (Math.Abs(wx - brx) <= hs && Math.Abs(wy - bry) <= hs) return 3;
            return -1;
        }

        private static Cursor HandleCursor(int idx) =>
            (idx == 0 || idx == 3) ? Cursors.SizeNWSE : Cursors.SizeNESW;

        private Rectangle ApplyResize(Rectangle orig, int handle, Point snap)
        {
            int ax, ay, bx, by;
            switch (handle)
            {
                case 0: ax = snap.X; ay = snap.Y; bx = orig.Right;  by = orig.Bottom; break;
                case 1: ax = orig.Left; ay = snap.Y; bx = snap.X;   by = orig.Bottom; break;
                case 2: ax = snap.X; ay = orig.Top; bx = orig.Right; by = snap.Y;     break;
                default: ax = orig.Left; ay = orig.Top; bx = snap.X; by = snap.Y;    break;
            }
            return ClampBounds(new Rectangle(
                Math.Min(ax, bx), Math.Min(ay, by),
                Math.Max(Math.Abs(bx - ax), GridSize),
                Math.Max(Math.Abs(by - ay), GridSize)));
        }

        // -----------------------------------------------------------------------
        //  HIT TESTING  — manual loop, zero allocation
        // -----------------------------------------------------------------------

        private VLSIElement? HitTest(PointF w)
        {
            int px = (int)w.X, py = (int)w.Y;
            VLSIElement? best = null;
            int bestZ = -1;
            for (int i = 0; i < _elements.Count; i++)
            {
                VLSIElement el = _elements[i];
                if (!el.Bounds.Contains(px, py)) continue;
                int z = LayerDefinition.ZOrder(el.Layer);
                if (z > bestZ) { bestZ = z; best = el; }
            }
            return best;
        }

        // -----------------------------------------------------------------------
        //  NET TRACING
        // -----------------------------------------------------------------------

        private static bool IsConductive(LayerType l) =>
            l is LayerType.Metal1 or LayerType.Metal2
              or LayerType.Polysilicon or LayerType.Contact or LayerType.Via;

        private static bool CanConnect(LayerType a, LayerType b)
        {
            if (a == b) return true;
            if (a == LayerType.Contact || b == LayerType.Contact)
            {
                LayerType other = a == LayerType.Contact ? b : a;
                return other is LayerType.Metal1 or LayerType.NActive
                             or LayerType.PActive or LayerType.Polysilicon;
            }
            if (a == LayerType.Via || b == LayerType.Via)
            {
                LayerType other = a == LayerType.Via ? b : a;
                return other is LayerType.Metal1 or LayerType.Metal2;
            }
            return false;
        }

        private HashSet<VLSIElement> TraceNet(VLSIElement source)
        {
            var net   = new HashSet<VLSIElement> { source };
            var queue = new Queue<VLSIElement>();
            queue.Enqueue(source);
            while (queue.Count > 0)
            {
                VLSIElement cur = queue.Dequeue();
                Rectangle   inf = Rectangle.Inflate(cur.Bounds, 2, 2);
                for (int i = 0; i < _elements.Count; i++)
                {
                    VLSIElement el = _elements[i];
                    if (net.Contains(el)) continue;
                    if (!IsConductive(el.Layer) &&
                        el.Layer != LayerType.NActive &&
                        el.Layer != LayerType.PActive) continue;
                    if (!CanConnect(cur.Layer, el.Layer)) continue;
                    if (!inf.IntersectsWith(el.Bounds)) continue;
                    net.Add(el);
                    queue.Enqueue(el);
                }
            }
            return net;
        }

        // -----------------------------------------------------------------------
        //  MOUSE INPUT
        // -----------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e.Button == MouseButtons.Middle)
            {
                _panning = true; _panStart = e.Location;
                _panX0 = _panX; _panY0 = _panY;
                Cursor = Cursors.SizeAll;
                return;
            }

            if (_testMode)
            {
                if (e.Button == MouseButtons.Left)
                {
                    PointF w = ScreenToWorld(e.Location);
                    VLSIElement? hit = HitTest(w);
                    if (hit != null && IsConductive(hit.Layer))
                    {
                        if (!ModifierKeys.HasFlag(Keys.Control)) _traces.Clear();
                        // Toggle: if already traced, remove; else add
                        int existIdx = -1;
                        for (int i = 0; i < _traces.Count; i++)
                            if (_traces[i].Net.Contains(hit)) { existIdx = i; break; }
                        if (existIdx >= 0)
                            _traces.RemoveAt(existIdx);
                        else
                            _traces.Add((TraceNet(hit), hit,
                                NetPalette[_traces.Count % NetPalette.Length]));
                    }
                    else if (!ModifierKeys.HasFlag(Keys.Control))
                    {
                        _traces.Clear();
                    }
                    Invalidate();
                }
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                _drawing = true; _drawA = _drawB = e.Location;
                Cursor = Cursors.Cross;
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                PointF w = ScreenToWorld(e.Location);

                // Resize handle check: hovered element first, then selected
                VLSIElement? resizeTarget = null;
                int          resizeH      = -1;
                if (_hoverEl != null) { int h = HitHandle(_hoverEl, w); if (h >= 0) { resizeTarget = _hoverEl; resizeH = h; } }
                if (resizeTarget == null)
                    for (int i = 0; i < _selected.Count; i++)
                    {
                        int h = HitHandle(_selected[i], w);
                        if (h >= 0) { resizeTarget = _selected[i]; resizeH = h; break; }
                    }

                if (resizeTarget != null)
                {
                    if (!_selected.Contains(resizeTarget))
                    { ClearSelection(); resizeTarget.Selected = true; _selected.Add(resizeTarget); }
                    _resizing = true; _resizeHandle = resizeH;
                    _resizeEl = resizeTarget; _resizeOrig = resizeTarget.Bounds;
                    Cursor = HandleCursor(resizeH);
                    Invalidate(); return;
                }

                VLSIElement? hit = HitTest(w);
                if (hit != null)
                {
                    if (!ModifierKeys.HasFlag(Keys.Control) && !_selected.Contains(hit)) ClearSelection();
                    if (!_selected.Contains(hit)) { hit.Selected = true; _selected.Add(hit); }
                    _moving = true; _moveStart = e.Location;
                    _moveOrigins = _selected.ToDictionary(el => el, el => el.Bounds);
                    Cursor = Cursors.SizeAll;
                }
                else
                {
                    if (!ModifierKeys.HasFlag(Keys.Control)) ClearSelection();
                    _banding = true; _bandA = _bandB = e.Location;
                }
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_panning)
            {
                _panX = _panX0 + (e.X - _panStart.X);
                _panY = _panY0 + (e.Y - _panStart.Y);
                Invalidate(); EmitStatus(e.Location); return;
            }
            if (_drawing)  { _drawB = e.Location; Invalidate(); EmitStatus(e.Location); return; }
            if (_banding)  { _bandB = e.Location; Invalidate(); EmitStatus(e.Location); return; }
            if (_resizing && _resizeEl != null)
            {
                _resizeEl.Bounds = ApplyResize(_resizeOrig, _resizeHandle,
                                               SnapGrid(ScreenToWorld(e.Location)));
                MarkElementsDirty();
                Invalidate(); EmitStatus(e.Location); return;
            }
            if (_moving)
            {
                float dx  = (e.X - _moveStart.X) / _zoom;
                float dy  = (e.Y - _moveStart.Y) / _zoom;
                int   sdx = (int)(Math.Round(dx / GridSize) * GridSize);
                int   sdy = (int)(Math.Round(dy / GridSize) * GridSize);
                for (int i = 0; i < _selected.Count; i++)
                {
                    var (el, orig) = (_selected[i], _moveOrigins[_selected[i]]);
                    el.Bounds = ClampBounds(new Rectangle(orig.X + sdx, orig.Y + sdy,
                                                          orig.Width, orig.Height));
                }
                MarkElementsDirty();
                Invalidate(); EmitStatus(e.Location); return;
            }

            PointF world = ScreenToWorld(e.Location);

            if (_testMode)
            {
                VLSIElement? h = HitTest(world);
                Cursor = (h != null && IsConductive(h.Layer)) ? Cursors.Hand : Cursors.Default;
                EmitStatus(e.Location); return;
            }

            var prevEl  = _hoverEl;
            var prevHnd = _hoverHandle;

            VLSIElement? hit2 = HitTest(world);
            _hoverEl     = hit2;
            _hoverHandle = hit2 != null ? HitHandle(hit2, world) : -1;

            if (_hoverHandle < 0)
                for (int i = 0; i < _selected.Count; i++)
                {
                    int h = HitHandle(_selected[i], world);
                    if (h >= 0) { _hoverEl = _selected[i]; _hoverHandle = h; break; }
                }

            Cursor = _hoverHandle >= 0 ? HandleCursor(_hoverHandle)
                   : _hoverEl != null  ? Cursors.SizeAll
                   :                     Cursors.Cross;

            if (prevEl != _hoverEl || prevHnd != _hoverHandle) Invalidate();
            EmitStatus(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Middle)
            { _panning = false; Cursor = _testMode ? Cursors.Hand : Cursors.Cross; return; }

            if (e.Button == MouseButtons.Right && _drawing)
            {
                _drawing = false;
                Rectangle r = ScreenPairToWorldRect(_drawA, e.Location);
                if (r.Width >= GridSize && r.Height >= GridSize)
                {
                    _elements.Add(new VLSIElement(_layer, r));
                    MarkElementsDirty();
                }
                Cursor = Cursors.Cross; Invalidate(); return;
            }

            if (e.Button == MouseButtons.Left)
            {
                if (_resizing) { _resizing = false; Cursor = Cursors.Cross; Invalidate(); return; }
                if (_banding)
                {
                    _banding = false;
                    Rectangle sel = ScreenPairToWorldRect(_bandA, e.Location);
                    if (sel.Width > 0 && sel.Height > 0)
                        for (int i = 0; i < _elements.Count; i++)
                        {
                            var el = _elements[i];
                            if (sel.IntersectsWith(el.Bounds) && !_selected.Contains(el))
                            { el.Selected = true; _selected.Add(el); }
                        }
                }
                _moving = false; _moveOrigins.Clear();
                Cursor = Cursors.Cross; Invalidate();
            }
        }

        // Scroll = pan Y  |  Ctrl+Scroll = zoom centred on canvas
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (ModifierKeys.HasFlag(Keys.Control))
                ZoomBy(e.Delta > 0 ? 1.13f : (1f / 1.13f));
            else
                _panY += e.Delta > 0 ? 40f : -40f;
            Invalidate();
        }

        private void ZoomBy(float factor)
        {
            float prev = _zoom;
            _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
            float cx = Width  / 2f, cy = Height / 2f;
            _panX = cx - (cx - _panX) * (_zoom / prev);
            _panY = cy - (cy - _panY) * (_zoom / prev);
            Invalidate();
        }

        // -----------------------------------------------------------------------
        //  KEYBOARD
        // -----------------------------------------------------------------------

        protected override bool IsInputKey(Keys k) =>
            k is Keys.Tab or Keys.Delete
              or Keys.Left or Keys.Right or Keys.Up or Keys.Down
              || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Tab)            { TestMode = !TestMode; e.Handled = true; return; }
            if (e.KeyCode == Keys.Delete)         { DeleteSelected(); return; }
            if (e.Control && e.KeyCode == Keys.A) { SelectAll(); return; }
            if (e.Control && e.KeyCode == Keys.Z) { Undo(); return; }
            if (e.KeyCode == Keys.Home)           { _panX = 80f; _panY = 80f; _zoom = 2f; Invalidate(); return; }

            if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
            {
                int n = e.KeyCode - Keys.D0;
                if (Enum.IsDefined(typeof(LayerType), n)) CurrentLayer = (LayerType)n;
                return;
            }
            if (e.Control && e.KeyCode == Keys.Oemplus)  { ZoomBy(1.2f); return; }
            if (e.Control && e.KeyCode == Keys.OemMinus) { ZoomBy(1f / 1.2f); return; }

            int dx = 0, dy = 0;
            if (e.KeyCode == Keys.Left)  dx = -GridSize;
            if (e.KeyCode == Keys.Right) dx =  GridSize;
            if (e.KeyCode == Keys.Up)    dy = -GridSize;
            if (e.KeyCode == Keys.Down)  dy =  GridSize;
            if ((dx | dy) != 0)
            {
                for (int i = 0; i < _selected.Count; i++)
                    _selected[i].Bounds = ClampBounds(new Rectangle(
                        _selected[i].Bounds.X + dx, _selected[i].Bounds.Y + dy,
                        _selected[i].Bounds.Width,  _selected[i].Bounds.Height));
                MarkElementsDirty();
                Invalidate();
            }
        }

        // -----------------------------------------------------------------------
        //  PUBLIC COMMANDS
        // -----------------------------------------------------------------------

        public void ClearAll()
        {
            _elements.Clear(); ClearSelection(); _traces.Clear();
            MarkElementsDirty(); Invalidate();
        }

        public void DeleteSelected()
        {
            for (int i = 0; i < _selected.Count; i++) _elements.Remove(_selected[i]);
            ClearSelection(); MarkElementsDirty(); Invalidate();
        }

        public void SelectAll()
        {
            ClearSelection();
            for (int i = 0; i < _elements.Count; i++)
            { _elements[i].Selected = true; _selected.Add(_elements[i]); }
            Invalidate();
        }

        private void ClearSelection()
        {
            for (int i = 0; i < _selected.Count; i++) _selected[i].Selected = false;
            _selected.Clear();
        }

        public void Undo()
        {
            if (_elements.Count == 0) return;
            _selected.Remove(_elements[^1]);
            _elements.RemoveAt(_elements.Count - 1);
            MarkElementsDirty(); Invalidate();
        }

        // -- Status bar: only update string when something actually changed ------
        private void EmitStatus(Point screen)
        {
            Point snap  = SnapGrid(ScreenToWorld(screen));
            int   elCnt = _elements.Count;
            int   selCnt = _selected.Count;
            int   netCnt = _traces.Count;

            if (snap == _lastStatusSnap     && elCnt  == _lastElCount  &&
                selCnt == _lastSelCount     && _testMode == _lastTestMode &&
                netCnt == _lastNetCount)
                return;

            _lastStatusSnap = snap; _lastElCount = elCnt;
            _lastSelCount = selCnt; _lastTestMode = _testMode; _lastNetCount = netCnt;

            string netInfo = netCnt > 0 ? $"  Nets: {netCnt}" : "";
            StatusChanged?.Invoke(
                $"X: {snap.X,5}  Y: {snap.Y,5}   " +
                $"Zoom: {_zoom,5:F2}x   " +
                $"Layer: {LayerDefinition.GetName(_layer),-12}" +
                $"Elements: {elCnt,4}   Selected: {selCnt,3}   " +
                $"Mode: {(_testMode ? "TEST" : "DRAW")} ?{netInfo}");
        }

        // -----------------------------------------------------------------------
        //  PAINTING
        // -----------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode   = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // Update zoom-dependent pen widths ONCE per frame (no re-allocation)
            float iZoom = 1f / _zoom;
            _gridFine.Width   = 0.5f * iZoom;
            _gridCoarse.Width = 1f   * iZoom;
            _gridAxis.Width   = 1.5f * iZoom;
            float hpw = 1.5f * iZoom;
            _selOutPen.Width = hpw; _hvrOutPen.Width = hpw;
            _selHndPen.Width = hpw; _hvrHndPen.Width = hpw;
            // Net glow pen widths
            float npw = 3f * iZoom;
            for (int i = 0; i < 8; i++) _netGlowPen[i].Width = npw;

            g.TranslateTransform(_panX, _panY);
            g.ScaleTransform(_zoom, _zoom);

            PaintGrid(g);

            var sorted = SortedElements();
            for (int i = 0; i < sorted.Count; i++) PaintElement(g, sorted[i]);

            if (_testMode && _traces.Count > 0) PaintAllTracedNets(g);

            if (!_testMode)
            {
                if (_hoverEl != null && !_selected.Contains(_hoverEl))
                    PaintHandles(g, _hoverEl, selected: false);
                for (int i = 0; i < _selected.Count; i++)
                    PaintHandles(g, _selected[i], selected: true);
            }

            g.ResetTransform();

            if (_drawing)  PaintDrawPreview(g);
            if (_banding)  PaintRubberBand(g);
            if (_testMode) PaintTestLegend(g);
        }

        // -- Grid (reuses cached pens, no allocation) -------------------------
        private void PaintGrid(Graphics g)
        {
            PointF tl = ScreenToWorld(new Point(0, 0));
            PointF br = ScreenToWorld(new Point(Width, Height));
            int x0 = (int)Math.Floor(tl.X   / GridSize) * GridSize;
            int y0 = (int)Math.Floor(tl.Y   / GridSize) * GridSize;
            int x1 = (int)Math.Ceiling(br.X / GridSize) * GridSize;
            int y1 = (int)Math.Ceiling(br.Y / GridSize) * GridSize;

            if (_zoom >= 0.28f)
                for (int x = x0; x <= x1; x += GridSize) g.DrawLine(_gridFine, x, y0, x, y1);
            if (_zoom >= 0.28f)
                for (int y = y0; y <= y1; y += GridSize) g.DrawLine(_gridFine, x0, y, x1, y);

            for (int x = x0; x <= x1; x += GridSize * 5) g.DrawLine(_gridCoarse, x, y0, x, y1);
            for (int y = y0; y <= y1; y += GridSize * 5) g.DrawLine(_gridCoarse, x0, y, x1, y);

            g.DrawLine(_gridAxis, x0, 0, x1, 0);
            g.DrawLine(_gridAxis, 0, y0, 0, y1);
        }

        // -- Element (uses cached static brushes/pens — zero allocation) ------
        private void PaintElement(Graphics g, VLSIElement el)
        {
            int idx = (int)el.Layer;
            SolidBrush fill   = s_fillBrush[idx];
            Pen        border = s_borderPen[idx];
            border.Width = 1.5f / _zoom;

            if (el.Layer == LayerType.Contact)
            {
                g.FillRectangle(s_contactBg, el.Bounds);
                g.DrawRectangle(border, el.Bounds);
                float cs = Math.Max(GridSize * 0.5f, 2f);
                float gp = GridSize;
                for (float cy = el.Bounds.Y + gp / 2f; cy < el.Bounds.Bottom - gp / 4; cy += gp)
                    for (float cx = el.Bounds.X + gp / 2f; cx < el.Bounds.Right - gp / 4; cx += gp)
                        g.FillRectangle(fill, cx - cs / 2f, cy - cs / 2f, cs, cs);
            }
            else
            {
                g.FillRectangle(fill, el.Bounds);
                g.DrawRectangle(border, el.Bounds);
            }
        }

        // -- Handles (uses cached pens — zero allocation) ---------------------
        private void PaintHandles(Graphics g, VLSIElement el, bool selected)
        {
            float p   = HandlePad;
            float vis = VisHandle;

            Pen outPen = selected ? _selOutPen : _hvrOutPen;
            Pen hndPen = selected ? _selHndPen : _hvrHndPen;

            var inf = new RectangleF(el.Bounds.Left - p, el.Bounds.Top - p,
                                     el.Bounds.Width + p * 2, el.Bounds.Height + p * 2);
            g.DrawRectangle(outPen, inf.X, inf.Y, inf.Width, inf.Height);

            GetHandleCorners(el, p,
                out float tlx, out float tly, out float trx, out float try_,
                out float blx, out float bly, out float brx, out float bry);

            float hv = vis / 2f;
            void DrawHandle(float cx, float cy)
            {
                g.FillRectangle(s_whiteBrush, cx - hv, cy - hv, vis, vis);
                g.DrawRectangle(hndPen, cx - hv, cy - hv, vis, vis);
            }
            DrawHandle(tlx, tly); DrawHandle(trx, try_);
            DrawHandle(blx, bly); DrawHandle(brx, bry);
        }

        // -- All traced nets (cached pens/brushes — zero allocation) ----------
        private void PaintAllTracedNets(Graphics g)
        {
            for (int ti = 0; ti < _traces.Count; ti++)
            {
                var (net, source, _) = _traces[ti];
                int ci = ti % 8;
                SolidBrush glowBrush = _netGlowBrush[ci];
                Pen        glowPen   = _netGlowPen[ci];
                Color      col       = NetPalette[ci];

                foreach (VLSIElement el in net)
                {
                    int p   = (int)(5 / _zoom) + 1;
                    Rectangle inf = Rectangle.Inflate(el.Bounds, p, p);
                    g.FillRectangle(glowBrush, inf);
                    g.DrawRectangle(glowPen, inf);
                }

                // V+HIGH label — font size varies with zoom; use world-unit font
                float fs = Math.Clamp(9f / _zoom, 0.5f, 80f);
                using var fnt    = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.World);
                using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                using var tb     = new SolidBrush(col);
                float tx = source.Bounds.X + 2f, ty = source.Bounds.Y + 2f;
                g.DrawString("V+HIGH", fnt, shadow, tx + 0.8f / _zoom, ty + 0.8f / _zoom);
                g.DrawString("V+HIGH", fnt, tb, tx, ty);
            }
        }

        // -- Draw preview (screen-space) --------------------------------------
        private void PaintDrawPreview(Graphics g)
        {
            Point wa = SnapGrid(ScreenToWorld(_drawA));
            Point wb = SnapGrid(ScreenToWorld(_drawB));
            Point sa = WorldToScreen(wa), sb = WorldToScreen(wb);
            int x = Math.Min(sa.X, sb.X), y = Math.Min(sa.Y, sb.Y);
            int w = Math.Abs(sb.X - sa.X), h = Math.Abs(sb.Y - sa.Y);
            if (w < 1 || h < 1) return;

            int idx = (int)_layer;
            using var fb = new SolidBrush(Color.FromArgb(155, LayerDefinition.GetFill(_layer)));
            s_borderPen[idx].Width = 2f;
            s_borderPen[idx].DashStyle = DashStyle.Dash;
            g.FillRectangle(fb, x, y, w, h);
            g.DrawRectangle(s_borderPen[idx], x, y, w, h);
            s_borderPen[idx].DashStyle = DashStyle.Solid;

            if (w > 30 && h > 16)
            {
                string txt = $"  {Math.Abs(wb.X - wa.X) / GridSize} x {Math.Abs(wb.Y - wa.Y) / GridSize} cells  ";
                using var fnt = new Font("Consolas", 8.5f);
                using var bgB = new SolidBrush(Color.FromArgb(210, Color.White));
                using var tb  = new SolidBrush(Color.FromArgb(30, 30, 30));
                SizeF sz = g.MeasureString(txt, fnt);
                g.FillRectangle(bgB, x + 2, y + 2, sz.Width, sz.Height);
                g.DrawString(txt, fnt, tb, x + 2, y + 2);
            }
        }

        // -- Rubber-band (screen-space) ----------------------------------------
        private void PaintRubberBand(Graphics g)
        {
            int x = Math.Min(_bandA.X, _bandB.X), y = Math.Min(_bandA.Y, _bandB.Y);
            int w = Math.Abs(_bandB.X - _bandA.X), h = Math.Abs(_bandB.Y - _bandA.Y);
            if (w < 1 || h < 1) return;
            using var fb = new SolidBrush(Color.FromArgb(36, 0, 114, 210));
            using var bp = new Pen(Color.FromArgb(0, 114, 210), 1f) { DashStyle = DashStyle.Dash };
            g.FillRectangle(fb, x, y, w, h);
            g.DrawRectangle(bp, x, y, w, h);
        }

        // -- Test legend (screen-space; transistor counts cached) --------------
        private void PaintTestLegend(Graphics g)
        {
            var (nmos, pmos) = GetTransistorCounts();
            int total = nmos + pmos;
            string gate = total == 0 ? "--" :
                          nmos == 1 && pmos == 1 ? "CMOS Inverter" :
                          nmos == 2 && pmos == 2 ? "NAND-2 / NOR-2" :
                          $"{total} transistors";

            string netInfo  = _traces.Count > 0 ? $"{_traces.Count} net(s) active" : "Click Metal/Poly";
            string ctrlHint = _traces.Count > 0 ? "Ctrl+click = add net" : "";

            var rows = new (string t, Color c)[]
            {
                ("[ TEST MODE ]",    Color.FromArgb(255, 210,  50)),
                ($"NMOS  : {nmos}", Color.FromArgb(100, 235, 100)),
                ($"PMOS  : {pmos}", Color.FromArgb(235, 120, 235)),
                ($"Total : {total}",Color.White),
                ($"Gate  : {gate}", Color.FromArgb(190, 220, 255)),
                ("",                Color.White),
                (netInfo,           Color.FromArgb(255, 230, 100)),
                (ctrlHint,          Color.FromArgb(180, 180, 180)),
                ("",                Color.White),
                ("TAB to exit",     Color.FromArgb(145, 145, 158)),
            };

            using var fnt = new Font("Consolas", 9f);
            float lh = 16f;
            float maxW = 0f;
            for (int i = 0; i < rows.Length; i++)
            {
                float w = g.MeasureString(rows[i].t + "    ", fnt).Width;
                if (w > maxW) maxW = w;
            }

            float px = 12f, py = 44f;
            int extraRows = _traces.Count;
            float totalH = rows.Length * lh + extraRows * lh + 12;

            using var bg  = new SolidBrush(Color.FromArgb(228, 20, 20, 28));
            using var frm = new Pen(Color.FromArgb(110, 255, 210, 50), 1f);
            g.FillRectangle(bg,  px - 8, py - 6, maxW + 18, totalH);
            g.DrawRectangle(frm, px - 8, py - 6, maxW + 17, totalH - 1);

            for (int i = 0; i < rows.Length; i++)
            {
                using var tb = new SolidBrush(rows[i].c);
                g.DrawString(rows[i].t, fnt, tb, px, py);
                py += lh;
            }

            for (int i = 0; i < _traces.Count; i++)
            {
                Color col = NetPalette[i % 8];
                using var sb2 = new SolidBrush(col);
                g.FillRectangle(sb2, px, py + 2, 10, 10);
                g.DrawString($" Net {i + 1}  ({_traces[i].Net.Count} segs)", fnt, sb2, px + 12, py);
                py += lh;
            }
        }
    }

    // --------------------------------------------------------------------------
    //  MAIN FORM
    // --------------------------------------------------------------------------

    public sealed class MainForm : Form
    {
        private CanvasControl        _canvas    = null!;
        private ToolStripStatusLabel _statusLbl = null!;
        private Label                _modeLbl   = null!;
        private readonly Button[]    _layerBtns = new Button[9];

        public MainForm()
        {
            Text          = "VLSI Builder";
            Size          = new Size(1260, 860);
            MinimumSize   = new Size(760, 560);
            BackColor     = Color.White;
            Font          = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview    = true;

            SuspendLayout();
            BuildMenu();
            BuildStatusBar();
            BuildContent();
            ResumeLayout(true);

            KeyDown += (_, e) =>
            { if (e.KeyCode == Keys.Tab) { _canvas.TestMode = !_canvas.TestMode; e.Handled = true; } };
        }

        private void BuildMenu()
        {
            var menu = new MenuStrip { BackColor = Color.White, RenderMode = ToolStripRenderMode.System };

            var fileM = new ToolStripMenuItem("&File");
            fileM.DropDownItems.Add("&New",  null, (_, _) => ConfirmNew());
            fileM.DropDownItems.Add(new ToolStripSeparator());
            fileM.DropDownItems.Add("E&xit", null, (_, _) => Close());

            var editM = new ToolStripMenuItem("&Edit");
            editM.DropDownItems.Add("Select All\tCtrl+A",   null, (_, _) => _canvas.SelectAll());
            editM.DropDownItems.Add("Delete Selected\tDel", null, (_, _) => _canvas.DeleteSelected());
            editM.DropDownItems.Add("Undo\tCtrl+Z",         null, (_, _) => _canvas.Undo());

            var viewM = new ToolStripMenuItem("&View");
            viewM.DropDownItems.Add("Toggle Test Mode\tTab", null, (_, _) => _canvas.TestMode = !_canvas.TestMode);
            viewM.DropDownItems.Add("Zoom In\tCtrl++",       null, (_, _) => { _canvas.Focus(); SendKeys.Send("^{=}"); });
            viewM.DropDownItems.Add("Zoom Out\tCtrl+-",      null, (_, _) => { _canvas.Focus(); SendKeys.Send("^{-}"); });
            viewM.DropDownItems.Add("Reset View\tHome",      null, (_, _) => { _canvas.Focus(); SendKeys.Send("{HOME}"); });

            var helpM = new ToolStripMenuItem("&Help");
            helpM.DropDownItems.Add("Shortcuts", null, ShowHelp);
            helpM.DropDownItems.Add("About",     null, ShowAbout);

            menu.Items.AddRange(new ToolStripItem[] { fileM, editM, viewM, helpM });
            Controls.Add(menu);
            MainMenuStrip = menu;
        }

        private void BuildStatusBar()
        {
            var ss = new StatusStrip { BackColor = Color.FromArgb(242, 242, 248), SizingGrip = true };
            _statusLbl = new ToolStripStatusLabel(
                "Right-drag=Draw   Left-drag handle=Resize   Left-drag body=Move   " +
                "Middle=Pan   Scroll=Pan Y   Ctrl+Scroll=Zoom   TAB=Test mode")
            {
                Font      = new Font("Consolas", 8f),
                Spring    = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            ss.Items.Add(_statusLbl);
            Controls.Add(ss);
        }

        private void BuildContent()
        {
            var table = new TableLayoutPanel
            {
                Dock            = DockStyle.Fill,
                ColumnCount     = 2, RowCount = 1,
                Padding         = new Padding(0), Margin = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100f));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            table.Controls.Add(BuildLeftPanel(), 0, 0);

            _canvas = new CanvasControl { Dock = DockStyle.Fill };
            _canvas.LayerChanged    += OnLayerChanged;
            _canvas.TestModeChanged += OnTestModeChanged;
            _canvas.StatusChanged   += msg => _statusLbl.Text = msg;
            _canvas.CurrentLayer    = LayerType.Metal1;

            table.Controls.Add(_canvas, 1, 0);
            Controls.Add(table);
        }

        private Panel BuildLeftPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor  = Color.FromArgb(246, 246, 250),
                AutoScroll = false,
            };
            panel.Paint += (_, e) =>
            {
                using var p = new Pen(Color.FromArgb(206, 206, 220), 1f);
                e.Graphics.DrawLine(p, panel.Width - 1, 0, panel.Width - 1, panel.Height);
            };

            int y = 10;
            _modeLbl = new Label
            {
                Text      = "? DRAW MODE",
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 108, 198),
                Location  = new Point(10, y), AutoSize = true,
            };
            panel.Controls.Add(_modeLbl);
            y += 30;

            for (int i = 0; i < 9; i++)
            {
                LayerType layer = (LayerType)(i + 1);
                Button btn = MakeLayerButton(layer, i + 1, y);
                _layerBtns[i] = btn;
                panel.Controls.Add(btn);
                y += 38;
            }
            return panel;
        }

        private Button MakeLayerButton(LayerType layer, int num, int y)
        {
            Color fill   = LayerDefinition.GetFill  (layer);
            Color border = LayerDefinition.GetBorder(layer);
            bool  def    = layer == LayerType.Metal1;

            var btn = new Button
            {
                Location  = new Point(8, y), Size = new Size(156, 34),
                Text      = $"  {num}: {LayerDefinition.GetName(layer)}",
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                Font      = new Font("Segoe UI", 8.5f, def ? FontStyle.Bold : FontStyle.Regular),
                Cursor    = Cursors.Hand,
            };
            btn.FlatAppearance.BorderColor        = def ? Color.FromArgb(0, 108, 198) : Color.FromArgb(186, 186, 202);
            btn.FlatAppearance.BorderSize         = def ? 2 : 1;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 244, 255);
            btn.Paint += (_, e) =>
            {
                Rectangle sw = new(btn.Width - 30, (btn.Height - 16) / 2, 16, 16);
                using var fb = new SolidBrush(fill);
                using var bp = new Pen(border, 1.5f);
                e.Graphics.FillRectangle(fb, sw);
                e.Graphics.DrawRectangle(bp, sw);
            };
            btn.Click += (_, _) => { _canvas.CurrentLayer = layer; _canvas.Focus(); };
            return btn;
        }

        private void OnLayerChanged(LayerType layer)
        {
            for (int i = 0; i < 9; i++)
            {
                bool active = (LayerType)(i + 1) == layer;
                _layerBtns[i].FlatAppearance.BorderSize  = active ? 2 : 1;
                _layerBtns[i].FlatAppearance.BorderColor = active
                    ? Color.FromArgb(0, 108, 198) : Color.FromArgb(186, 186, 202);
                _layerBtns[i].Font = new Font("Segoe UI", 8.5f,
                    active ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        private void OnTestModeChanged(bool test)
        {
            _modeLbl.Text      = test ? "? TEST MODE" : "? DRAW MODE";
            _modeLbl.ForeColor = test ? Color.FromArgb(210, 110, 0) : Color.FromArgb(0, 108, 198);
        }

        private void ConfirmNew()
        {
            if (_canvas.ElementCount == 0 ||
                MessageBox.Show("Clear the canvas? This cannot be undone.",
                    "New Layout", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
                    == DialogResult.OK)
                _canvas.ClearAll();
        }

        private static void ShowHelp(object? s, EventArgs e) =>
            MessageBox.Show(
                "DRAW\n" +
                "  Right-drag          Draw rectangle on active layer\n" +
                "  Keys 1 – 9          Select layer\n\n" +
                "SELECT / RESIZE / MOVE\n" +
                "  Left-click body     Select element\n" +
                "  Left-drag corner    Resize element (grid-snapped)\n" +
                "  Left-drag body      Move selected (grid-snapped)\n" +
                "  Left-drag empty     Rubber-band multi-select\n" +
                "  Ctrl+click          Add to selection\n" +
                "  Arrow keys          Nudge selected 1 grid unit\n\n" +
                "VIEWPORT\n" +
                "  Middle-drag         Pan\n" +
                "  Scroll              Pan vertically\n" +
                "  Ctrl+Scroll         Zoom (centred on canvas)\n" +
                "  Ctrl+ + / -         Zoom in / out\n" +
                "  Home                Reset zoom & pan\n\n" +
                "EDIT\n" +
                "  Delete              Delete selected\n" +
                "  Ctrl+A              Select all\n" +
                "  Ctrl+Z              Undo last drawn shape\n\n" +
                "TEST MODE\n" +
                "  TAB                 Toggle Draw / Test\n" +
                "  Left-click Metal / Poly  Trace connected net (V+HIGH)\n" +
                "  Ctrl+click          Add another net (multi-highlight)\n" +
                "  Click traced net    Toggle off",
                "Keyboard Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);

        private static void ShowAbout(object? s, EventArgs e) =>
            MessageBox.Show(
                "VLSI Builder  v1.3\n\n" +
                "Lightweight VLSI layout editor.\n\n" +
                "Layers rendered in fixed z-order:\n" +
                "  N-Well, P-Well  (bottom)\n" +
                "  N-Active, P-Active\n" +
                "  Polysilicon, Contact, Metal 1, Via, Metal 2  (top)\n\n" +
                "Test Mode detects NMOS/PMOS transistors and traces\n" +
                "connected metal/poly nets with V+HIGH annotation.\n" +
                "Ctrl+click to highlight multiple independent nets.\n\n" +
                "Built with C# · .NET 6.0 · WinForms",
                "About VLSI Builder", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
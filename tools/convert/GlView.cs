// The 3D view: OpenGL through the opengl32 that comes with Windows, one or two pictures side by side on a single camera,
// so whatever you do to one happens to the other.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CadConvert
{
    static class Gl
    {
        const string L = "opengl32.dll";
        public const uint DEPTH_TEST = 0x0B71, LIGHTING = 0x0B50, LIGHT0 = 0x4000, LIGHT1 = 0x4001, COLOR_MATERIAL = 0x0B57, NORMALIZE = 0x0BA1, POLYGON_OFFSET_FILL = 0x8037,
            SCISSOR_TEST = 0x0C11, BLEND = 0x0BE2, LINE_SMOOTH = 0x0B20, COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x100, PROJECTION = 0x1701, MODELVIEW = 0x1700,
            TRIANGLES = 4, LINE_STRIP = 3, LINES = 1, FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, UNSIGNED_INT = 0x1405, VERTEX_ARRAY = 0x8074, NORMAL_ARRAY = 0x8075, COLOR_ARRAY = 0x8076,
            COMPILE = 0x1300, POSITION = 0x1203, DIFFUSE = 0x1201, AMBIENT = 0x1200, SPECULAR = 0x1202, SHININESS = 0x1601, FRONT_AND_BACK = 0x0408, AMBIENT_AND_DIFFUSE = 0x1602,
            LIGHT_MODEL_TWO_SIDE = 0x0B52, LIGHT_MODEL_AMBIENT = 0x0B53, SRC_ALPHA = 0x302, ONE_MINUS_SRC_ALPHA = 0x303, LEQUAL = 0x203, BACK = 0x405, BGRA = 0x80E1, LINE_SMOOTH_HINT = 0x0C52, NICEST = 0x1102;
        [DllImport(L)] public static extern void glClearColor(float r, float g, float b, float a);
        [DllImport(L)] public static extern void glClear(uint mask);
        [DllImport(L)] public static extern void glEnable(uint cap);
        [DllImport(L)] public static extern void glDisable(uint cap);
        [DllImport(L)] public static extern void glViewport(int x, int y, int w, int h);
        [DllImport(L)] public static extern void glScissor(int x, int y, int w, int h);
        [DllImport(L)] public static extern void glMatrixMode(uint mode);
        [DllImport(L)] public static extern void glLoadIdentity();
        [DllImport(L)] public static extern void glPushMatrix();
        [DllImport(L)] public static extern void glPopMatrix();
        [DllImport(L)] public static extern void glOrtho(double l, double r, double b, double t, double n, double f);
        [DllImport(L)] public static extern void glMultMatrixf(float[] m);
        [DllImport(L)] public static extern void glTranslated(double x, double y, double z);
        [DllImport(L)] public static extern void glLightfv(uint light, uint name, float[] v);
        [DllImport(L)] public static extern void glLightModeli(uint name, int v);
        [DllImport(L)] public static extern void glLightModelfv(uint name, float[] v);
        [DllImport(L)] public static extern void glMaterialfv(uint face, uint name, float[] v);
        [DllImport(L)] public static extern void glMaterialf(uint face, uint name, float v);
        [DllImport(L)] public static extern void glColorMaterial(uint face, uint mode);
        [DllImport(L)] public static extern void glColor3f(float r, float g, float b);
        [DllImport(L)] public static extern void glBegin(uint mode);
        [DllImport(L)] public static extern void glEnd();
        [DllImport(L)] public static extern void glVertex2f(float x, float y);
        [DllImport(L)] public static extern void glLineWidth(float w);
        [DllImport(L)] public static extern void glPolygonOffset(float f, float u);
        [DllImport(L)] public static extern void glEnableClientState(uint a);
        [DllImport(L)] public static extern void glDisableClientState(uint a);
        [DllImport(L)] public static extern void glVertexPointer(int size, uint type, int stride, IntPtr p);
        [DllImport(L)] public static extern void glNormalPointer(uint type, int stride, IntPtr p);
        [DllImport(L)] public static extern void glColorPointer(int size, uint type, int stride, IntPtr p);
        [DllImport(L)] public static extern void glDrawElements(uint mode, int count, uint type, IntPtr p);
        [DllImport(L)] public static extern void glDrawArrays(uint mode, int first, int count);
        [DllImport(L)] public static extern uint glGenLists(int n);
        [DllImport(L)] public static extern void glNewList(uint list, uint mode);
        [DllImport(L)] public static extern void glEndList();
        [DllImport(L)] public static extern void glCallList(uint list);
        [DllImport(L)] public static extern void glDeleteLists(uint list, int n);
        [DllImport(L)] public static extern void glListBase(uint b);
        [DllImport(L)] public static extern void glCallLists(int n, uint type, byte[] lists);
        [DllImport(L)] public static extern void glRasterPos2i(int x, int y);
        [DllImport(L)] public static extern void glReadBuffer(uint mode);
        [DllImport(L)] public static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, IntPtr data);
        [DllImport(L)] public static extern void glBlendFunc(uint s, uint d);
        [DllImport(L)] public static extern void glHint(uint t, uint m);
        [DllImport(L)] public static extern void glDepthFunc(uint f);
        [DllImport(L)] public static extern void glDepthRange(double near, double far);
        [DllImport(L)] public static extern IntPtr wglGetProcAddress(string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool ChoosePixelFormatArb(IntPtr dc, int[] ints, float[] floats, uint max, int[] formats, out uint found);
        [DllImport(L)] public static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport(L)] public static extern bool wglMakeCurrent(IntPtr dc, IntPtr ctx);
        [DllImport(L)] public static extern bool wglDeleteContext(IntPtr ctx);
        [DllImport(L)] public static extern bool wglUseFontBitmapsW(IntPtr dc, uint first, uint count, uint b);

        [StructLayout(LayoutKind.Sequential)]
        public struct Pfd
        {
            public ushort nSize, nVersion;
            public uint dwFlags;
            public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }
        [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr dc, ref Pfd p);
        [DllImport("gdi32.dll")] public static extern int DescribePixelFormat(IntPtr dc, int format, uint size, ref Pfd p);
        [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr dc, int format, ref Pfd p);
        [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr w);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr w, IntPtr dc);
    }

    public class Item
    {
        public Mesh M;
        public float[] Matrix;
        public float[] Color;
        public bool Overlay;    // Drawn over everything: a sketch.
    }

    public class GlView : Control
    {
        public List<Item> LeftItems = new List<Item>(), RightItems = new List<Item>();
        public bool Two = true;
        public bool Edges = true;
        public string LeftCaption = "", RightCaption = "";
        IntPtr dc, ctx;
        uint font;
        bool working, smooth;
        double[] r = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        double panX, panY, mmPerPixel = 1, cx, cy, cz, radius = 100;
        Point last;
        readonly List<uint> oldLists = new List<uint>();

        public GlView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            TabStop = true;
            Rotate(35.264 * Math.PI / 180, -45 * Math.PI / 180);
        }

        protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ClassStyle |= 0x20; return p; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            dc = Gl.GetDC(Handle);
            Gl.Pfd p = new Gl.Pfd { nVersion = 1, dwFlags = 0x25, cColorBits = 32, cDepthBits = 24 };
            p.nSize = (ushort)Marshal.SizeOf(typeof(Gl.Pfd));
            // Smooth edges if the graphics card offers them, else the plain format.
            int format = SmoothFormat();
            smooth = format != 0;
            if (smooth) Gl.DescribePixelFormat(dc, format, p.nSize, ref p);
            else format = Gl.ChoosePixelFormat(dc, ref p);
            if (format == 0 || !Gl.SetPixelFormat(dc, format, ref p)) return;
            ctx = Gl.wglCreateContext(dc);
            if (ctx == IntPtr.Zero || !Gl.wglMakeCurrent(dc, ctx)) return;
            working = true;
            using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold))
            {
                IntPtr old = Gl.SelectObject(dc, f.ToHfont());
                font = Gl.glGenLists(96);
                Gl.wglUseFontBitmapsW(dc, 32, 96, font);
                Gl.SelectObject(dc, old);
            }
            if (smooth) Gl.glEnable(0x809D);
            Gl.glEnable(Gl.DEPTH_TEST);
            Gl.glDepthFunc(Gl.LEQUAL);
            Gl.glEnable(Gl.NORMALIZE);
            Gl.glLightModeli(Gl.LIGHT_MODEL_TWO_SIDE, 1);
            Gl.glLightModelfv(Gl.LIGHT_MODEL_AMBIENT, new[] { 0.30f, 0.30f, 0.32f, 1f });
            Gl.glEnable(Gl.LIGHT0);
            Gl.glEnable(Gl.LIGHT1);
            Gl.glLightfv(Gl.LIGHT0, Gl.DIFFUSE, new[] { 0.62f, 0.62f, 0.60f, 1f });
            Gl.glLightfv(Gl.LIGHT0, Gl.SPECULAR, new[] { 0.25f, 0.25f, 0.25f, 1f });
            Gl.glLightfv(Gl.LIGHT1, Gl.DIFFUSE, new[] { 0.24f, 0.25f, 0.28f, 1f });
            Gl.glLightfv(Gl.LIGHT1, Gl.SPECULAR, new[] { 0f, 0f, 0f, 1f });
            Gl.glColorMaterial(Gl.FRONT_AND_BACK, Gl.AMBIENT_AND_DIFFUSE);
            Gl.glEnable(Gl.COLOR_MATERIAL);
            Gl.glMaterialfv(Gl.FRONT_AND_BACK, Gl.SPECULAR, new[] { 0.35f, 0.35f, 0.35f, 1f });
            Gl.glMaterialf(Gl.FRONT_AND_BACK, Gl.SHININESS, 40f);
            Gl.glPolygonOffset(1f, 1f);
            Gl.glBlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.glHint(Gl.LINE_SMOOTH_HINT, Gl.NICEST);
        }

        // Asking for several samples a pixel takes a function only OpenGL can hand over, so a throwaway window and context first:
        // a window's format can only be set once.
        static int SmoothFormat()
        {
            using (Control temp = new Control())
            {
                IntPtr h = temp.Handle, tdc = Gl.GetDC(h), tctx = IntPtr.Zero;
                try
                {
                    Gl.Pfd p = new Gl.Pfd { nVersion = 1, dwFlags = 0x25, cColorBits = 32, cDepthBits = 24 };
                    p.nSize = (ushort)Marshal.SizeOf(typeof(Gl.Pfd));
                    int basic = Gl.ChoosePixelFormat(tdc, ref p);
                    if (basic == 0 || !Gl.SetPixelFormat(tdc, basic, ref p)) return 0;
                    tctx = Gl.wglCreateContext(tdc);
                    if (tctx == IntPtr.Zero || !Gl.wglMakeCurrent(tdc, tctx)) return 0;
                    IntPtr f = Gl.wglGetProcAddress("wglChoosePixelFormatARB");
                    if (f == IntPtr.Zero) return 0;
                    Gl.ChoosePixelFormatArb choose = (Gl.ChoosePixelFormatArb)Marshal.GetDelegateForFunctionPointer(f, typeof(Gl.ChoosePixelFormatArb));
                    foreach (int samples in new[] { 8, 4, 2 })
                    {
                        // Drawn to a window, by OpenGL, double buffered, in colour; colour bits, depth bits; sample buffers, samples.
                        int[] want = { 0x2001, 1, 0x2010, 1, 0x2011, 1, 0x2013, 0x202B, 0x2014, 32, 0x2022, 24, 0x2041, 1, 0x2042, samples, 0 };
                        int[] formats = new int[1];
                        uint found;
                        if (choose(tdc, want, null, 1, formats, out found) && found > 0) return formats[0];
                    }
                    return 0;
                }
                catch (Exception) { return 0; }
                finally
                {
                    Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                    if (tctx != IntPtr.Zero) Gl.wglDeleteContext(tctx);
                    Gl.ReleaseDC(h, tdc);
                }
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (ctx != IntPtr.Zero) { Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); Gl.wglDeleteContext(ctx); ctx = IntPtr.Zero; }
            if (dc != IntPtr.Zero) { Gl.ReleaseDC(Handle, dc); dc = IntPtr.Zero; }
            working = false;
            base.OnHandleDestroyed(e);
        }

        public bool Working { get { return working; } }

        public void Drop(Mesh m)
        {
            if (!m.Uploaded) return;
            m.Uploaded = false;
            if (m.FaceList != 0) oldLists.Add(m.FaceList);
            if (m.LineList != 0) oldLists.Add(m.LineList);
            m.FaceList = m.LineList = 0;
        }

        static void Upload(Mesh m)
        {
            m.Uploaded = true;
            if (m.I.Length > 0)
            {
                GCHandle v = GCHandle.Alloc(m.V, GCHandleType.Pinned), n = GCHandle.Alloc(m.N, GCHandleType.Pinned), i = GCHandle.Alloc(m.I, GCHandleType.Pinned);
                GCHandle c = m.C != null ? GCHandle.Alloc(m.C, GCHandleType.Pinned) : default(GCHandle);
                Gl.glEnableClientState(Gl.VERTEX_ARRAY);
                Gl.glEnableClientState(Gl.NORMAL_ARRAY);
                if (m.C != null) Gl.glEnableClientState(Gl.COLOR_ARRAY);
                Gl.glVertexPointer(3, Gl.FLOAT, 0, v.AddrOfPinnedObject());
                Gl.glNormalPointer(Gl.FLOAT, 0, n.AddrOfPinnedObject());
                if (m.C != null) Gl.glColorPointer(3, Gl.UNSIGNED_BYTE, 0, c.AddrOfPinnedObject());
                m.FaceList = Gl.glGenLists(1);
                Gl.glNewList(m.FaceList, Gl.COMPILE);
                Gl.glDrawElements(Gl.TRIANGLES, m.I.Length, Gl.UNSIGNED_INT, i.AddrOfPinnedObject());
                Gl.glEndList();
                Gl.glDisableClientState(Gl.COLOR_ARRAY);
                Gl.glDisableClientState(Gl.NORMAL_ARRAY);
                Gl.glDisableClientState(Gl.VERTEX_ARRAY);
                v.Free();
                n.Free();
                i.Free();
                if (m.C != null) c.Free();
            }
            if (m.Lines.Count > 0)
            {
                Gl.glEnableClientState(Gl.VERTEX_ARRAY);
                m.LineList = Gl.glGenLists(1);
                Gl.glNewList(m.LineList, Gl.COMPILE);
                foreach (float[] l in m.Lines)
                {
                    GCHandle p = GCHandle.Alloc(l, GCHandleType.Pinned);
                    Gl.glVertexPointer(3, Gl.FLOAT, 0, p.AddrOfPinnedObject());
                    Gl.glDrawArrays(Gl.LINE_STRIP, 0, l.Length / 3);
                    p.Free();
                }
                Gl.glEndList();
                Gl.glDisableClientState(Gl.VERTEX_ARRAY);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!working) { e.Graphics.Clear(SystemColors.Control); e.Graphics.DrawString("OpenGL isn't available on this computer, so there's no 3D view.", Font, SystemBrushes.ControlText, 12, 12); return; }
            Render();
            Gl.SwapBuffers(dc);
        }

        void Render()
        {
            Gl.wglMakeCurrent(dc, ctx);
            foreach (uint l in oldLists) Gl.glDeleteLists(l, 1);
            oldLists.Clear();
            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
            Gl.glEnable(Gl.SCISSOR_TEST);
            if (Two)
            {
                int half = w / 2;
                Picture(LeftItems, 0, half, h, LeftCaption);
                Picture(RightItems, half + 1, w - half - 1, h, RightCaption);
                Gl.glViewport(0, 0, w, h);
                Gl.glScissor(0, 0, w, h);
                Gl.glMatrixMode(Gl.PROJECTION);
                Gl.glLoadIdentity();
                Gl.glOrtho(0, w, 0, h, -1, 1);
                Gl.glMatrixMode(Gl.MODELVIEW);
                Gl.glLoadIdentity();
                Gl.glDisable(Gl.LIGHTING);
                Gl.glDisable(Gl.DEPTH_TEST);
                Gl.glColor3f(0.55f, 0.57f, 0.60f);
                Gl.glBegin(Gl.LINES);
                Gl.glVertex2f(half + 0.5f, 0);
                Gl.glVertex2f(half + 0.5f, h);
                Gl.glEnd();
                Gl.glEnable(Gl.DEPTH_TEST);
            }
            else Picture(LeftItems, 0, w, h, LeftCaption);
        }

        void Picture(List<Item> items, int x, int w, int h, string caption)
        {
            Gl.glViewport(x, 0, w, h);
            Gl.glScissor(x, 0, w, h);
            Gl.glClearColor(0.925f, 0.933f, 0.945f, 1f);
            Gl.glClear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
            double depth = radius * 4 + 10;
            Gl.glMatrixMode(Gl.PROJECTION);
            Gl.glLoadIdentity();
            Gl.glOrtho(-w / 2.0 * mmPerPixel, w / 2.0 * mmPerPixel, -h / 2.0 * mmPerPixel, h / 2.0 * mmPerPixel, -depth, depth);
            Gl.glMatrixMode(Gl.MODELVIEW);
            Gl.glLoadIdentity();
            Gl.glLightfv(Gl.LIGHT0, Gl.POSITION, new[] { 0.35f, 0.55f, 1f, 0f });
            Gl.glLightfv(Gl.LIGHT1, Gl.POSITION, new[] { -0.7f, -0.35f, 0.5f, 0f });
            Gl.glTranslated(panX, panY, 0);
            Gl.glMultMatrixf(new[] { (float)r[0], (float)r[3], (float)r[6], 0f, (float)r[1], (float)r[4], (float)r[7], 0f, (float)r[2], (float)r[5], (float)r[8], 0f, 0f, 0f, 0f, 1f });
            Gl.glTranslated(-cx, -cy, -cz);

            // Edges and faces are each only near the true surface, so an edge can sink just under its face, and which ones do
            // changes with the zoom. Faces are pushed back a little, by a fixed amount in mm or a pixel and a half if that's more.
            double back = Math.Min(0.02, Math.Max(0.15, mmPerPixel * 1.5) / (2 * depth));
            Gl.glDepthRange(back, 1);
            Gl.glEnable(Gl.LIGHTING);
            foreach (Item i in items)
            {
                if (i.Overlay || i.M.I.Length == 0) continue;
                if (!i.M.Uploaded) Upload(i.M);
                if (i.Color != null) Gl.glColor3f(i.Color[0], i.Color[1], i.Color[2]);
                Gl.glPushMatrix();
                if (i.Matrix != null) Gl.glMultMatrixf(i.Matrix);
                Gl.glCallList(i.M.FaceList);
                Gl.glPopMatrix();
            }
            Gl.glDepthRange(0, 1 - back);
            Gl.glDisable(Gl.LIGHTING);
            if (!smooth)
            {
                Gl.glEnable(Gl.LINE_SMOOTH);
                Gl.glEnable(Gl.BLEND);
            }
            if (Edges)
            {
                Gl.glColor3f(0.13f, 0.14f, 0.16f);
                Gl.glLineWidth(smooth ? 1.25f : 1f);
                foreach (Item i in items)
                {
                    if (i.Overlay) continue;
                    if (!i.M.Uploaded) Upload(i.M);
                    if (i.M.LineList == 0) continue;
                    Gl.glPushMatrix();
                    if (i.Matrix != null) Gl.glMultMatrixf(i.Matrix);
                    Gl.glCallList(i.M.LineList);
                    Gl.glPopMatrix();
                }
            }
            Gl.glDisable(Gl.DEPTH_TEST);
            Gl.glColor3f(0.75f, 0.05f, 0.55f);
            Gl.glLineWidth(1.6f);
            foreach (Item i in items)
            {
                if (!i.Overlay) continue;
                if (!i.M.Uploaded) Upload(i.M);
                if (i.M.LineList != 0) Gl.glCallList(i.M.LineList);
            }
            Gl.glDisable(Gl.BLEND);
            Gl.glDisable(Gl.LINE_SMOOTH);
            Gl.glDepthRange(0, 1);
            Caption(caption, w, h);
            Gl.glEnable(Gl.DEPTH_TEST);
        }

        void Caption(string s, int w, int h)
        {
            if (string.IsNullOrEmpty(s) || font == 0) return;
            Gl.glMatrixMode(Gl.PROJECTION);
            Gl.glLoadIdentity();
            Gl.glOrtho(0, w, 0, h, -1, 1);
            Gl.glMatrixMode(Gl.MODELVIEW);
            Gl.glLoadIdentity();
            int y = h - 18;
            foreach (string line in s.Split('\n'))
            {
                byte[] codes = new byte[line.Length];
                for (int i = 0; i < line.Length; i++) codes[i] = (byte)(line[i] < 32 || line[i] > 127 ? '?' : line[i]);
                Gl.glColor3f(0.12f, 0.14f, 0.18f);
                Gl.glRasterPos2i(10, y);
                Gl.glListBase(font - 32);
                Gl.glCallLists(codes.Length, Gl.UNSIGNED_BYTE, codes);
                y -= 17;
            }
        }

        // Frames everything in both pictures; the same framing for both.
        public void Fit()
        {
            double[] box = Mesh.EmptyBox();
            foreach (List<Item> list in new[] { LeftItems, RightItems })
                foreach (Item i in list) i.M.Grow(box, i.Matrix);
            if (box[0] > box[3]) { Invalidate(); return; }
            cx = (box[0] + box[3]) / 2;
            cy = (box[1] + box[4]) / 2;
            cz = (box[2] + box[5]) / 2;
            radius = Math.Max(1e-3, Math.Sqrt(Math.Pow(box[3] - box[0], 2) + Math.Pow(box[4] - box[1], 2) + Math.Pow(box[5] - box[2], 2)) / 2);
            // What the box looks like from where the camera is now, so the part fills the picture at this angle.
            float[] view = { (float)r[0], (float)r[3], (float)r[6], 0f, (float)r[1], (float)r[4], (float)r[7], 0f, (float)r[2], (float)r[5], (float)r[8], 0f,
                (float)-(r[0] * cx + r[1] * cy + r[2] * cz), (float)-(r[3] * cx + r[4] * cy + r[5] * cz), (float)-(r[6] * cx + r[7] * cy + r[8] * cz), 1f };
            double[] seen = Mesh.EmptyBox();
            foreach (List<Item> list in new[] { LeftItems, RightItems })
                foreach (Item i in list) i.M.Grow(seen, i.Matrix == null ? view : Times(view, i.Matrix));
            panX = -(seen[0] + seen[3]) / 2;
            panY = -(seen[1] + seen[4]) / 2;
            int w = Math.Max(1, Two ? ClientSize.Width / 2 : ClientSize.Width), h = Math.Max(1, ClientSize.Height - 50);
            mmPerPixel = Math.Max(1e-6, Math.Max((seen[3] - seen[0]) / w, (seen[4] - seen[1]) / h) * 1.08);
            Invalidate();
        }

        public static float[] Times(float[] a, float[] b)
        {
            float[] m = new float[16];
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    for (int k = 0; k < 4; k++)
                        m[col * 4 + row] += a[k * 4 + row] * b[col * 4 + k];
            return m;
        }

        public void Turn(double aboutX, double aboutY)
        {
            r = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            Rotate(aboutX, aboutY);
            panX = panY = 0;
            Invalidate();
        }

        // Zooms out (factor above 1) or in, about the middle of the view.
        public void Zoom(double factor)
        {
            mmPerPixel *= factor;
            panX *= 1 / factor;
            panY *= 1 / factor;
            Invalidate();
        }

        // A turntable turn for the README's animation: about whichever of the model's own axes stands most upright on screen.
        public void Spin(double angle)
        {
            int k = 0;
            for (int i = 1; i < 3; i++) if (Math.Abs(r[3 + i]) > Math.Abs(r[3 + k])) k = i;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            int a = (k + 1) % 3, b = (k + 2) % 3;
            // The rotation about model axis k, as a matrix taking model to model; then view = r * that.
            double[] m = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            m[a * 3 + a] = c; m[a * 3 + b] = -s; m[b * 3 + a] = s; m[b * 3 + b] = c;
            double[] n = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    n[i * 3 + j] = r[i * 3] * m[j] + r[i * 3 + 1] * m[3 + j] + r[i * 3 + 2] * m[6 + j];
            r = n;
            Invalidate();
        }

        void Rotate(double aboutX, double aboutY)
        {
            double ca = Math.Cos(aboutX), sa = Math.Sin(aboutX), cb = Math.Cos(aboutY), sb = Math.Sin(aboutY);
            double[] m = { cb, 0, sb, sa * sb, ca, -sa * cb, -ca * sb, sa, ca * cb };
            double[] n = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    n[i * 3 + j] = m[i * 3] * r[j] + m[i * 3 + 1] * r[3 + j] + m[i * 3 + 2] * r[6 + j];
            // Keep it a true rotation over a long drag.
            double l = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            n[0] /= l; n[1] /= l; n[2] /= l;
            double d = n[0] * n[3] + n[1] * n[4] + n[2] * n[5];
            n[3] -= d * n[0]; n[4] -= d * n[1]; n[5] -= d * n[2];
            l = Math.Sqrt(n[3] * n[3] + n[4] * n[4] + n[5] * n[5]);
            n[3] /= l; n[4] /= l; n[5] /= l;
            n[6] = n[1] * n[5] - n[2] * n[4];
            n[7] = n[2] * n[3] - n[0] * n[5];
            n[8] = n[0] * n[4] - n[1] * n[3];
            r = n;
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); last = e.Location; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.None) return;
            int dx = e.X - last.X, dy = e.Y - last.Y;
            last = e.Location;
            if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) == 0) Rotate(dy * 0.008, dx * 0.008);
            else { panX += dx * mmPerPixel; panY -= dy * mmPerPixel; }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            double factor = Math.Pow(0.85, e.Delta / 120.0);
            int w = ClientSize.Width, h = ClientSize.Height, mx = e.X;
            if (Two) { w /= 2; if (mx > w) mx -= w; }
            // The point under the mouse stays put, in whichever picture the mouse is over.
            double at = (mx - w / 2.0) * mmPerPixel, up = (h / 2.0 - e.Y) * mmPerPixel;
            panX = at - (at - panX) * factor;
            panY = up - (up - panY) * factor;
            mmPerPixel *= factor;
            Invalidate();
        }

        // For testing: a drag and a wheel turn through the same handlers the mouse uses.
        public void TestMouse(int dx, int dy, int wheel)
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 200, 200, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, 200 + dx, 200 + dy, 0));
            OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, 200, 200, wheel));
        }

        // For testing: how many pixels of each half of the picture, below the caption, differ from the background.
        public int[] Drawn()
        {
            using (Bitmap b = Snapshot())
            {
                int[] count = new int[2];
                for (int y = 50; y < b.Height; y += 3)
                    for (int x = 0; x < b.Width; x += 3)
                    {
                        Color c = b.GetPixel(x, y);
                        if (Math.Abs(c.R - 236) + Math.Abs(c.G - 238) + Math.Abs(c.B - 241) > 24) count[x < b.Width / 2 ? 0 : 1]++;
                    }
                return count;
            }
        }

        public Bitmap Snapshot()
        {
            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            Render();
            Gl.glReadBuffer(Gl.BACK);
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Gl.glReadPixels(0, 0, w, h, Gl.BGRA, Gl.UNSIGNED_BYTE, d.Scan0);
            b.UnlockBits(d);
            b.RotateFlip(RotateFlipType.RotateNoneFlipY);
            Gl.SwapBuffers(dc);
            return b;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrayOrganizer.UI
{
    /// <summary>
    /// Draws the app icons in code, natively per size (no scaling of a bitmap).
    /// Tray: monochrome outlined folder (Fluent line style), stroke centers snapped to half pixels.
    /// Window: colorful filled folder holding three colored squares.
    /// </summary>
    public static class AppIconFactory
    {
        private static readonly int[] TraySizes = { 16, 20, 24, 32 };
        private static readonly int[] WindowSizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

        private static double Snap(double v)
        {
            return Math.Floor(v + 0.5);
        }

        // ---------------------------------------------------------------- tray (outline)

        private static BitmapSource RenderTray(int size, Color color)
        {
            double s = size;
            int w = size >= 24 ? 2 : 1;            // stroke width in whole pixels
            double hw = w / 2.0;
            bool odd = (w % 2) == 1;

            double margin = size >= 32 ? 2 : 1;    // outer edge margin
            double left = margin + hw;
            double right = s - margin - hw;
            double topOuter = Snap(s * 0.14);
            double top = topOuter + hw;
            double bottom = s - topOuter - hw;
            double tabH = Snap(s * 0.14);
            double bodyTop = top + tabH;
            double tabRightRaw = s * 0.42;
            double tabRight = odd ? Math.Floor(tabRightRaw) + 0.5 : Snap(tabRightRaw);
            double r = Snap(s * 0.1 * 2) / 2.0;
            if (r < 1.0) r = 1.0;

            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(left + r, top), false, true);
                ctx.LineTo(new Point(tabRight, top), true, true);
                ctx.LineTo(new Point(tabRight + tabH, bodyTop), true, true);
                ctx.LineTo(new Point(right - r, bodyTop), true, true);
                ctx.ArcTo(new Point(right, bodyTop + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(new Point(right, bottom - r), true, true);
                ctx.ArcTo(new Point(right - r, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(new Point(left + r, bottom), true, true);
                ctx.ArcTo(new Point(left, bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(new Point(left, top + r), true, true);
                ctx.ArcTo(new Point(left + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            }
            geo.Freeze();

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, w);
            pen.LineJoin = PenLineJoin.Round;
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            pen.Freeze();

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawGeometry(null, pen, geo);
            }
            return ToBitmap(visual, size);
        }

        // ---------------------------------------------------------------- window (colorful)

        private static Geometry FolderBack(double left, double top, double right, double bottom,
                                           double tabRight, double tabH, double r)
        {
            double bodyTop = top + tabH;
            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(left + r, top), true, true);
                ctx.LineTo(new Point(tabRight, top), true, false);
                ctx.LineTo(new Point(tabRight + tabH, bodyTop), true, false);
                ctx.LineTo(new Point(right - r, bodyTop), true, false);
                ctx.ArcTo(new Point(right, bodyTop + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(right, bottom - r), true, false);
                ctx.ArcTo(new Point(right - r, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(left + r, bottom), true, false);
                ctx.ArcTo(new Point(left, bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(left, top + r), true, false);
                ctx.ArcTo(new Point(left + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            return geo;
        }

        private static Brush Vertical(Color top, Color bottom)
        {
            var b = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
            b.Freeze();
            return b;
        }

        private static Brush Solid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static BitmapSource RenderWindow(int size)
        {
            double s = size;
            bool snap = size <= 48;

            // coordinate helper: fraction of the canvas -> pixels (whole pixels when small)
            Func<double, double> P = delegate(double f)
            {
                double v = f * s;
                return snap ? Snap(v) : v;
            };

            double left = P(0.06);
            double right = s - left;
            double top = P(0.14);
            double bodyTopY = P(0.24);
            double tabH = bodyTopY - top;
            double bottom = P(0.88);
            double tabRight = P(0.34);
            double r = snap ? Math.Max(1.0, Snap(s * 0.07)) : s * 0.07;

            double frontTop = P(0.47);

            double sq = P(size <= 20 ? 0.20 : 0.22);
            double gap = Math.Max(1.0, P(0.045));
            double total = 3 * sq + 2 * gap;
            double startX = snap ? Math.Floor((s - total) / 2.0) : (s - total) / 2.0;
            double sqTop = P(0.30);
            double sqR = size < 24 ? 0.0 : sq * 0.2;

            Color[] colors =
            {
                Color.FromRgb(0x00, 0x78, 0xD4),
                Color.FromRgb(0x16, 0xC6, 0x0C),
                Color.FromRgb(0xF7, 0x63, 0x0C)
            };

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // folder back + tab (deeper amber)
                Geometry back = FolderBack(left, top, right, bottom, tabRight, tabH, r);
                dc.DrawGeometry(Vertical(Color.FromRgb(0xF2, 0xB1, 0x1C), Color.FromRgb(0xD9, 0x8E, 0x04)), null, back);

                // the three squares, partly tucked behind the front panel
                for (int i = 0; i < 3; i++)
                {
                    double x = startX + i * (sq + gap);
                    var rect = new Rect(x, sqTop, sq, sq);
                    if (size >= 48)
                    {
                        double o = Math.Max(1.0, s * 0.012);
                        dc.DrawRoundedRectangle(Solid(Color.FromArgb(0x30, 0, 0, 0)), null,
                            new Rect(x, sqTop + o, sq, sq), sqR, sqR);
                    }
                    dc.DrawRoundedRectangle(Solid(colors[i]), null, rect, sqR, sqR);
                }

                // front panel (light, soft gradient) with rounded corners
                double fr = r;
                var front = new StreamGeometry();
                using (StreamGeometryContext ctx = front.Open())
                {
                    ctx.BeginFigure(new Point(left, frontTop + fr), true, true);
                    ctx.ArcTo(new Point(left + fr, frontTop), new Size(fr, fr), 0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(new Point(right - fr, frontTop), true, false);
                    ctx.ArcTo(new Point(right, frontTop + fr), new Size(fr, fr), 0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(new Point(right, bottom - fr), true, false);
                    ctx.ArcTo(new Point(right - fr, bottom), new Size(fr, fr), 0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(new Point(left + fr, bottom), true, false);
                    ctx.ArcTo(new Point(left, bottom - fr), new Size(fr, fr), 0, false, SweepDirection.Clockwise, true, false);
                }
                front.Freeze();
                dc.DrawGeometry(Vertical(Color.FromRgb(0xFF, 0xDD, 0x73), Color.FromRgb(0xFF, 0xC4, 0x2E)), null, front);

                // subtle highlight along the top edge of the front panel
                if (size >= 48)
                {
                    double hl = Math.Max(1.0, s * 0.008);
                    var hp = new Pen(Solid(Color.FromArgb(0x90, 255, 255, 255)), hl);
                    hp.StartLineCap = PenLineCap.Round;
                    hp.EndLineCap = PenLineCap.Round;
                    hp.Freeze();
                    dc.DrawLine(hp, new Point(left + fr, frontTop + hl / 2), new Point(right - fr, frontTop + hl / 2));
                }
            }
            return ToBitmap(visual, size);
        }

        // ---------------------------------------------------------------- helpers

        private static BitmapSource ToBitmap(DrawingVisual visual, int size)
        {
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private static byte[] EncodePng(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        private static Color TrayColor(bool lightTaskbar)
        {
            return lightTaskbar ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(255, 255, 255);
        }

        /// <summary>
        /// Monochrome outlined-folder tray icon: near-black on a light taskbar, white on a dark one.
        /// The caller owns the returned HICON (Icon.Handle) and should DestroyIcon it when replaced.
        /// </summary>
        public static System.Drawing.Icon CreateTrayIcon(bool lightTaskbar)
        {
            int px = 16;
            try
            {
                System.Drawing.Size sz = System.Windows.Forms.SystemInformation.SmallIconSize;
                if (sz.Width >= 8) px = sz.Width;
            }
            catch { }

            BitmapSource bmp = RenderTray(px, TrayColor(lightTaskbar));
            byte[] png = EncodePng(bmp);

            using (var ms = new MemoryStream(png))
            using (var gdi = new System.Drawing.Bitmap(ms))
            {
                IntPtr hIcon = gdi.GetHicon();
                return System.Drawing.Icon.FromHandle(hIcon);
            }
        }

        /// <summary>Colorful folder-with-squares icon for Window.Icon (multi-size).</summary>
        public static ImageSource CreateWindowIcon()
        {
            try
            {
                int[] sizes = WindowSizes;
                var pngs = new List<byte[]>();
                foreach (int s in sizes)
                    pngs.Add(EncodePng(RenderWindow(s)));

                using (var ico = new MemoryStream())
                {
                    var w = new BinaryWriter(ico);
                    w.Write((short)0);              // reserved
                    w.Write((short)1);              // type: icon
                    w.Write((short)sizes.Length);   // count
                    int offset = 6 + 16 * sizes.Length;
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                        w.Write(dim);               // width (0 = 256)
                        w.Write(dim);               // height
                        w.Write((byte)0);           // colors
                        w.Write((byte)0);           // reserved
                        w.Write((short)1);          // planes
                        w.Write((short)32);         // bit count
                        w.Write(pngs[i].Length);    // bytes in resource
                        w.Write(offset);            // offset
                        offset += pngs[i].Length;
                    }
                    for (int i = 0; i < pngs.Count; i++)
                        w.Write(pngs[i]);
                    w.Flush();
                    ico.Position = 0;

                    BitmapDecoder decoder = BitmapDecoder.Create(ico, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        BitmapFrame frame = decoder.Frames[0];
                        frame.Freeze();
                        return frame;
                    }
                }
            }
            catch { }

            return RenderWindow(64);
        }

        // ---------------------------------------------------------------- previews (review only)

        private static BitmapSource Upscale(BitmapSource src, int scale)
        {
            int w = src.PixelWidth;
            int h = src.PixelHeight;
            var px = new byte[w * h * 4];
            src.CopyPixels(px, w * 4, 0);
            int W = w * scale;
            int H = h * scale;
            var outPx = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int si = ((y / scale) * w + (x / scale)) * 4;
                    int di = (y * W + x) * 4;
                    outPx[di] = px[si];
                    outPx[di + 1] = px[si + 1];
                    outPx[di + 2] = px[si + 2];
                    outPx[di + 3] = px[si + 3];
                }
            BitmapSource r = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, outPx, W * 4);
            r.Freeze();
            return r;
        }

        private static BitmapSource OnBackground(BitmapSource icon, Color bg, int scale)
        {
            int w = icon.PixelWidth * scale;
            int h = icon.PixelHeight * scale;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Solid(bg), null, new Rect(0, 0, w, h));
                dc.DrawImage(icon, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private static void SavePng(BitmapSource bmp, string path)
        {
            File.WriteAllBytes(path, EncodePng(bmp));
        }

        /// <summary>Writes PNG previews of the tray icons (light/dark) and window icons plus a contact sheet.</summary>
        public static void SaveIconPreviews(string dir)
        {
            Directory.CreateDirectory(dir);
            Color lightBg = Color.FromRgb(0xF3, 0xF3, 0xF3);
            Color darkBg = Color.FromRgb(0x20, 0x20, 0x20);

            // contact sheet: rows of tray (light), tray (dark) at 8x, window sizes native + 8x for the small ones
            var sheet = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(sheet, BitmapScalingMode.NearestNeighbor);
            int sheetW = 1200;
            int sheetH = 0;
            using (DrawingContext dc = sheet.RenderOpen())
            {
                double y = 10;
                for (int row = 0; row < 2; row++)
                {
                    bool light = row == 0;
                    Color bg = light ? lightBg : darkBg;
                    dc.DrawRectangle(Solid(bg), null, new Rect(0, y - 10, sheetW, 8 * 32 + 20));
                    double x = 10;
                    foreach (int sz in TraySizes)
                    {
                        BitmapSource b = RenderTray(sz, TrayColor(light));
                        SavePng(OnBackground(b, bg, 1), Path.Combine(dir, "tray_" + (light ? "light" : "dark") + "_" + sz + ".png"));
                        dc.DrawImage(Upscale(b, 8), new Rect(x, y, sz * 8, sz * 8));
                        x += sz * 8 + 20;
                    }
                    y += 8 * 32 + 20;
                }

                // window icons: small ones at 8x, then all native
                dc.DrawRectangle(Solid(lightBg), null, new Rect(0, y - 10, sheetW / 2, 8 * 32 + 20));
                dc.DrawRectangle(Solid(darkBg), null, new Rect(sheetW / 2, y - 10, sheetW / 2, 8 * 32 + 20));
                double xs = 10;
                foreach (int sz in WindowSizes)
                {
                    BitmapSource b = RenderWindow(sz);
                    SavePng(b, Path.Combine(dir, "window_" + sz + ".png"));
                    if (sz <= 32)
                    {
                        dc.DrawImage(Upscale(b, 8), new Rect(xs, y, sz * 8, sz * 8));
                        xs += sz * 8 + 20;
                    }
                }
                y += 8 * 32 + 20;

                dc.DrawRectangle(Solid(lightBg), null, new Rect(0, y - 10, sheetW / 2, 256 + 20));
                dc.DrawRectangle(Solid(darkBg), null, new Rect(sheetW / 2, y - 10, sheetW / 2, 256 + 20));
                double xn = 10;
                foreach (int sz in WindowSizes)
                {
                    if (sz == 256) continue;
                    BitmapSource b = RenderWindow(sz);
                    dc.DrawImage(b, new Rect(xn, y, sz, sz));
                    dc.DrawImage(b, new Rect(sheetW / 2 + xn, y, sz, sz));
                    xn += sz + 14;
                }
                y += 256 + 20;
                sheetH = (int)y;
            }
            var rtb = new RenderTargetBitmap(sheetW, sheetH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(sheet);
            rtb.Freeze();
            SavePng(rtb, Path.Combine(dir, "contact.png"));

            // 256 px window icon on both backgrounds for a closer look
            BitmapSource big = RenderWindow(256);
            SavePng(OnBackground(big, lightBg, 1), Path.Combine(dir, "window_256_on_light.png"));
            SavePng(OnBackground(big, darkBg, 1), Path.Combine(dir, "window_256_on_dark.png"));
        }
    }
}

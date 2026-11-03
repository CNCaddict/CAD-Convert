// The picture Inventor stores in its files, as it last saved them: a PNG in one of the top-level property streams.
using System;
using System.Drawing;
using System.IO;
using InvRe;

namespace CadConvert
{
    public static class Thumbnail
    {
        static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // Null if the file has none that can be read.
        public static Bitmap Read(string file)
        {
            try
            {
                CfbFile cfb = new CfbFile(file);
                foreach (CfbEntry e in cfb.Root.Children)
                {
                    if (e.Type != 2 || e.Size < 100 || e.Size > 8000000) continue;
                    byte[] d = cfb.ReadStream(e);
                    int at = Find(d);
                    if (at < 0) continue;
                    using (MemoryStream m = new MemoryStream(d, at, d.Length - at))
                    using (Image i = Image.FromStream(m))
                        return new Bitmap(i);
                }
            }
            catch (Exception) { }
            return null;
        }

        static int Find(byte[] d)
        {
            for (int i = 0; i + Png.Length < d.Length; i++)
            {
                int k = 0;
                while (k < Png.Length && d[i + k] == Png[k]) k++;
                if (k == Png.Length) return i;
            }
            return -1;
        }

        // Fitted inside a square, for a list.
        public static Bitmap Small(Image image, int size)
        {
            Bitmap b = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float scale = Math.Min((float)size / image.Width, (float)size / image.Height);
                float w = image.Width * scale, h = image.Height * scale;
                g.DrawImage(image, (size - w) / 2, (size - h) / 2, w, h);
            }
            return b;
        }
    }
}

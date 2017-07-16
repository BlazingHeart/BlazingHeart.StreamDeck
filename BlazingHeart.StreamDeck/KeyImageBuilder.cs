using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BlazingHeart.StreamDeck
{
    public class KeyImageBuilder : IDisposable
    {
        Bitmap _bitmap;
        Graphics _graphics;

        static Font font = new Font(FontFamily.GenericSansSerif, 50, FontStyle.Bold);

        public Bitmap Bitmap { get { return _bitmap; } }

        public KeyImageBuilder()
        {
            _bitmap = new Bitmap(StreamDeck.ImageWidth, StreamDeck.ImageHeight, PixelFormat.Format24bppRgb);
            _graphics = Graphics.FromImage(_bitmap);
        }

        public Bitmap ToBitmap()
        {
            return (Bitmap)_bitmap.Clone();
        }

        public KeyImageBuilder Fill(int colour)
        {
            _graphics.FillRectangle(new SolidBrush(Color.FromArgb((int)(colour | 0xFF000000))), 0, 0, _bitmap.Width, _bitmap.Height);

            return this;
        }

        public KeyImageBuilder DrawText(string text, int colour, bool centreX, bool centreY)
        {
            var measure = _graphics.MeasureString(text, font);
            float x = centreX ? (_bitmap.Width - measure.Width) / 2.0f : 0;
            float y = centreY ? (_bitmap.Height - measure.Height) / 2.0f : 0;

            _graphics.DrawString(text, font, new SolidBrush(Color.FromArgb((int)(colour | 0xFF000000))), new PointF(x, y));

            return this;
        }

        public void Dispose()
        {
            _graphics?.Dispose();
            _bitmap.Dispose();
        }
    }
}

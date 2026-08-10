using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SleepPicker
{
    /// <summary>
    /// Draws the moon at a given phase, for the dynamic tray icon.
    ///
    /// The phases are drawn rather than shipped as 101 bitmaps: baking every level at
    /// every size the notification area asks for would have added more to the executable
    /// than the rest of the program weighs, and still have nothing to show at a scaling
    /// factor nobody thought to bake. Two arcs cost nothing and are exact at any size.
    ///
    /// <c>tools\MakeMoonPhases.py</c> renders the same geometry to
    /// <c>docs\moon-phases.png</c>, so the whole series can be looked at as a picture.
    /// The constants below and the ones in that script are the same numbers.
    /// </summary>
    internal static class MoonIcon
    {
        // Measured off assets\SleepPicker.png -- the artwork the executable icon is drawn
        // from -- as fractions of the icon's edge, so every size is the same picture.
        private const float DiskRadius = 0.414f;    // 26.5 / 64
        private const float RimWidth = 0.047f;      // 3 / 64

        /// <summary>
        /// The artwork's crescent is tilted, its unlit side facing the upper right.
        /// Tilting the phase series to match keeps this recognisably the same moon as the
        /// one on the executable.
        /// </summary>
        private const float TiltDegrees = 38f;

        private static readonly Color Gold = Color.FromArgb(0xF7, 0xC9, 0x48);
        private static readonly Color Rim = Color.FromArgb(0x5B, 0x49, 0x18);

        /// <summary>
        /// Charge below which the unlit limb is drawn as a ring -- earthshine, the "old
        /// moon in the new moon's arms". By then the lit crescent has almost no area
        /// left, and without the ring the tray slot would go blank near 0% and read as a
        /// program that had crashed rather than a battery that is nearly flat.
        /// </summary>
        private const float EarthshineFrom = 20f;

        /// <summary>
        /// The moon at <paramref name="percent"/> charge, as a square icon
        /// <paramref name="size"/> pixels on a side. The caller owns the result and must
        /// dispose it.
        ///
        /// A waxing moon is the mirror image of the waning one at the same phase -- lit on
        /// the other limb -- which is how the real moon tells a night before full from a
        /// night after it. Charging is drawn waxing for the same reason: the charge is on
        /// its way up rather than down.
        /// </summary>
        public static Icon Create(int percent, int size, bool waxing)
        {
            if (size < 1)
            {
                throw new ArgumentOutOfRangeException("size");
            }
            if (percent < 0) { percent = 0; }
            if (percent > 100) { percent = 100; }

            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    Draw(graphics, percent, size, waxing);
                }
                return ToIcon(bitmap);
            }
        }

        private static void Draw(Graphics graphics, int percent, int size, bool waxing)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float centre = size / 2f;
            float radius = DiskRadius * size;
            // Below about 21px the rim would come out thinner than a pixel and dissolve
            // into a grey haze, so it is held at one pixel instead.
            float nominalRim = Math.Max(1f, RimWidth * size);

            // Signed horizontal semi-axis of the terminator, the day/night line. It is a
            // great circle seen edge on, so it projects to an ellipse sharing the disk's
            // vertical radius: negative bulges right (gibbous), positive bites in from the
            // left (crescent), zero is the straight edge of a half moon. At 100% it is
            // -radius, where the ellipse coincides with the limb and the whole disk is
            // lit; at 0% it is +radius and none of it is.
            float terminator = radius * (1f - (2f * percent / 100f));

            // A rim of fixed width eats a thin crescent alive: at 16px, the size the
            // notification area asks for at 100% scaling, a crescent is narrower than one
            // pixel below about 15% charge, and a full-width rim would swallow every last
            // scrap of gold -- leaving 15% and 0% looking alike, which is the one place
            // the reading matters most. So the rim is given at most a third of whatever
            // the crescent is wide, and thins away with it, leaving gold the middle half.
            float litWidth = radius - terminator;
            float rim = Math.Min(nominalRim, litWidth / 4f);

            // Read bottom-up: each call is prepended, so the last one written is the first
            // one a point goes through. Centre the moon, tilt it, and -- when it is waxing
            // -- flip the finished picture about the vertical, tilt and all.
            graphics.TranslateTransform(centre, centre);
            if (waxing)
            {
                graphics.ScaleTransform(-1f, 1f);
            }
            // Negative turns anticlockwise on screen, where y points down.
            graphics.RotateTransform(-TiltDegrees);
            graphics.TranslateTransform(-centre, -centre);

            if (percent < EarthshineFrom)
            {
                int alpha = (int)Math.Round(255f * (EarthshineFrom - percent) / EarthshineFrom);
                // Full width, not the thinned rim: by the time the ring matters it is the
                // only thing left to see, and a hairline would not be seen.
                using (Pen pen = new Pen(Color.FromArgb(alpha, Rim), nominalRim))
                {
                    float inset = radius - (nominalRim / 2f);
                    graphics.DrawEllipse(pen, centre - inset, centre - inset, inset * 2f, inset * 2f);
                }
            }

            // The rim is the lit region with the same region, shrunk by the rim width,
            // laid over it in gold. Shrinking pulls the limb in and pushes the terminator
            // out, which is the one expression "terminator + rim" gives for both a gibbous
            // bulge and a crescent bite.
            FillLitRegion(graphics, Rim, centre, radius, terminator);
            FillLitRegion(graphics, Gold, centre, radius - rim, terminator + rim);
        }

        private static void FillLitRegion(Graphics graphics, Color colour,
            float centre, float radius, float terminator)
        {
            if (radius <= 0f)
            {
                return;
            }

            // A terminator wider than the disk means the ellipse swallows it whole and
            // nothing is lit. Clamping is what says so: the ellipse then coincides with
            // the limb and the two arcs below retrace each other, enclosing no area.
            float half = Math.Min(Math.Abs(terminator), radius);

            using (GraphicsPath path = new GraphicsPath())
            {
                // The lit half of the limb, traced from the bottom pole round to the top.
                path.AddArc(centre - radius, centre - radius, radius * 2f, radius * 2f, 90f, 180f);

                if (half < 0.01f)
                {
                    // Half moon: straight back down the middle.
                    path.AddLine(centre, centre - radius, centre, centre + radius);
                }
                else
                {
                    // ...and back along the terminator: its right half where it bulges
                    // right, its left half where it bites in.
                    path.AddArc(centre - half, centre - radius, half * 2f, radius * 2f,
                        270f, terminator < 0f ? 180f : -180f);
                }
                path.CloseFigure();

                using (Brush brush = new SolidBrush(colour))
                {
                    graphics.FillPath(brush, path);
                }
            }
        }

        /// <summary>
        /// An icon that owns its own handle. <see cref="Icon.FromHandle"/> wraps a handle
        /// without taking ownership, so the wrapper is cloned and the handle destroyed
        /// here -- otherwise every refresh would leak one icon for the life of the process.
        /// </summary>
        private static Icon ToIcon(Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (Icon unowned = Icon.FromHandle(handle))
                {
                    return (Icon)unowned.Clone();
                }
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DestroyIcon(IntPtr icon);
        }
    }
}

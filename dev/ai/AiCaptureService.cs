using Godot;
using System.IO;
using System.Threading.Tasks;

namespace Grove.Dev.Ai
{
    // Screenshot capture from the rendered viewport. Waits for the frame to finish drawing before reading the texture (else
    // the image is stale/black), creates missing dirs, and flags black/empty captures so the runner can fail loudly.
    public static class AiCaptureService
    {
        public struct Result
        {
            public bool Ok;
            public string Path;
            public string Error;   // null when Ok
            public float MeanLuma; // 0..1, for black-frame detection
        }

        // Capture the current frame to `absPath` (PNG). `ctx` is any in-tree Node (used to await the render + get the viewport).
        public static async Task<Result> Capture(Node ctx, string absPath)
        {
            var r = new Result { Path = absPath };
            try
            {
                // wait until the GPU has finished this frame, so GetImage() reads the drawn pixels (not an empty/previous buffer)
                await ctx.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

                Viewport vp = ctx.GetViewport();
                Texture2D tex = vp?.GetTexture();
                Image img = tex?.GetImage();
                if (img == null || img.IsEmpty()) { r.Error = "viewport image was null/empty"; return r; }

                r.MeanLuma = MeanLuma(img);

                string dir = Path.GetDirectoryName(absPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                Error err = img.SavePng(absPath);
                if (err != Error.Ok) { r.Error = $"SavePng failed: {err}"; return r; }
                if (!File.Exists(absPath)) { r.Error = "file not written"; return r; }

                if (r.MeanLuma < 0.01f) { r.Error = $"capture is black (mean luma {r.MeanLuma:0.000})"; return r; }
                r.Ok = true;
                return r;
            }
            catch (System.Exception e) { r.Error = e.Message; return r; }
        }

        // Cheap average brightness over a coarse grid — enough to catch all-black / empty frames.
        private static float MeanLuma(Image img)
        {
            int w = img.GetWidth(), h = img.GetHeight();
            if (w == 0 || h == 0) return 0f;
            float sum = 0f; int n = 0;
            int sx = Mathf.Max(1, w / 32), sy = Mathf.Max(1, h / 32);
            for (int y = 0; y < h; y += sy)
                for (int x = 0; x < w; x += sx)
                {
                    Color c = img.GetPixel(x, y);
                    sum += 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
                    n++;
                }
            return n > 0 ? sum / n : 0f;
        }
    }
}

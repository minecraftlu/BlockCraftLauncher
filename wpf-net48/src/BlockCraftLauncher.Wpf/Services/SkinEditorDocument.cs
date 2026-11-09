using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>A full cuboid's six faces in the standard Java 64x64 UV sheet.</summary>
    internal sealed class SkinUvRegion
    {
        public readonly string Part;
        public readonly bool Overlay;
        public readonly int U, V, Width, Height, Depth;

        public SkinUvRegion(string part, bool overlay, int u, int v,
            int width, int height, int depth)
        {
            Part = part; Overlay = overlay;
            U = u; V = v; Width = width; Height = height; Depth = depth;
        }

        static bool Within(int x, int y, int left, int top, int width, int height)
        {
            return x >= left && y >= top && x < left + width && y < top + height;
        }

        public bool Contains(int x, int y)
        {
            return Within(x, y, U + Depth, V, Width, Depth) ||
                Within(x, y, U + Depth + Width, V, Width, Depth) ||
                Within(x, y, U, V + Depth, Depth, Height) ||
                Within(x, y, U + Depth, V + Depth, Width, Height) ||
                Within(x, y, U + Depth + Width, V + Depth, Depth, Height) ||
                Within(x, y, U + 2 * Depth + Width, V + Depth, Width, Height);
        }
    }

    /// <summary>
    /// Pure local skin editor document. Stores raw straight-alpha BGRA pixels
    /// and never contacts Microsoft, Minecraft or a third-party upload service.
    /// Modern Java textures are 64x64; old 64x32 textures are expanded locally.
    /// </summary>
    internal sealed class SkinEditorDocument
    {
        public const int Size = 64;
        const int Stride = Size * 4;
        const int MaxUndo = 40;
        readonly byte[] pixels = new byte[Size * Stride];
        readonly List<byte[]> undo = new List<byte[]>();
        readonly List<byte[]> redo = new List<byte[]>();
        byte[] strokeBefore;
        bool editing;
        public bool SlimArms { get; private set; }
        public bool Dirty { get; private set; }
        public event Action Changed;

        public SkinEditorDocument(bool slim = false)
        {
            ResetTemplate(slim);
            Dirty = false;
        }

        public static IReadOnlyList<SkinUvRegion> Regions(bool slim)
        {
            int arm = slim ? 3 : 4;
            return new[]
            {
                new SkinUvRegion("头部", false, 0, 0, 8, 8, 8),
                new SkinUvRegion("头部", true, 32, 0, 8, 8, 8),
                new SkinUvRegion("身体", false, 16, 16, 8, 12, 4),
                new SkinUvRegion("身体", true, 16, 32, 8, 12, 4),
                new SkinUvRegion("右臂", false, 40, 16, arm, 12, 4),
                new SkinUvRegion("右臂", true, 40, 32, arm, 12, 4),
                new SkinUvRegion("左臂", false, 32, 48, arm, 12, 4),
                new SkinUvRegion("左臂", true, 48, 48, arm, 12, 4),
                new SkinUvRegion("右腿", false, 0, 16, 4, 12, 4),
                new SkinUvRegion("右腿", true, 0, 32, 4, 12, 4),
                new SkinUvRegion("左腿", false, 16, 48, 4, 12, 4),
                new SkinUvRegion("左腿", true, 0, 48, 4, 12, 4)
            };
        }

        public bool CanPaint(int x, int y, string part, string layer)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size) return false;
            return Regions(SlimArms).Any(r =>
                (part == "全部部位" || r.Part == part) &&
                (layer == "全部图层" || r.Overlay == (layer == "外层")) &&
                r.Contains(x, y));
        }

        int Offset(int x, int y) => (y * Size + x) * 4;

        public Color Pixel(int x, int y)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size)
                return Colors.Transparent;
            int i = Offset(x, y);
            return Color.FromArgb(pixels[i + 3], pixels[i + 2],
                pixels[i + 1], pixels[i]);
        }

        void PutRaw(int x, int y, Color color)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size) return;
            int i = Offset(x, y);
            pixels[i] = color.B; pixels[i + 1] = color.G;
            pixels[i + 2] = color.R; pixels[i + 3] = color.A;
        }

        static Color Shade(Color c, int amount)
        {
            byte Tune(byte x) => (byte)Math.Max(0, Math.Min(255, x + amount));
            return Color.FromArgb(c.A, Tune(c.R), Tune(c.G), Tune(c.B));
        }

        void FillRect(int x, int y, int w, int h, Color color)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++) PutRaw(xx, yy, color);
        }

        void PaintCuboid(SkinUvRegion r, Color baseColor)
        {
            FillRect(r.U + r.Depth, r.V, r.Width, r.Depth, Shade(baseColor, 16));
            FillRect(r.U + r.Depth + r.Width, r.V, r.Width, r.Depth, Shade(baseColor, -16));
            FillRect(r.U, r.V + r.Depth, r.Depth, r.Height, Shade(baseColor, -12));
            FillRect(r.U + r.Depth, r.V + r.Depth, r.Width, r.Height, baseColor);
            FillRect(r.U + r.Depth + r.Width, r.V + r.Depth,
                r.Depth, r.Height, Shade(baseColor, -12));
            FillRect(r.U + 2 * r.Depth + r.Width, r.V + r.Depth,
                r.Width, r.Height, Shade(baseColor, -20));
        }

        /// <summary>Original generic starter, not a copied branded skin.</summary>
        public void ResetTemplate(bool slim)
        {
            SlimArms = slim;
            Array.Clear(pixels, 0, pixels.Length);
            foreach (var r in Regions(slim).Where(x => !x.Overlay))
            {
                Color color = r.Part == "头部" ? Color.FromRgb(214, 166, 128) :
                    r.Part == "身体" ? Color.FromRgb(44, 130, 146) :
                    r.Part.EndsWith("臂") ? Color.FromRgb(60, 147, 157) :
                    Color.FromRgb(66, 75, 130);
                PaintCuboid(r, color);
            }
            // Face details and an original teal sweatshirt with cuffs.
            PutRaw(10, 11, Color.FromRgb(43, 52, 74));
            PutRaw(13, 11, Color.FromRgb(43, 52, 74));
            PutRaw(11, 14, Color.FromRgb(128, 70, 77));
            PutRaw(12, 14, Color.FromRgb(128, 70, 77));
            FillRect(21, 20, 6, 2, Color.FromRgb(35, 104, 124));
            FillRect(21, 28, 6, 2, Color.FromRgb(37, 107, 125));
            FillRect(5, 29, 2, 3, Color.FromRgb(47, 54, 84));
            FillRect(21, 61, 2, 3, Color.FromRgb(47, 54, 84));
            // A little removable second-layer hair, not another opaque base.
            FillRect(40, 8, 8, 2, Color.FromArgb(231, 91, 56, 43));
            FillRect(40, 10, 1, 4, Color.FromArgb(200, 91, 56, 43));
            FillRect(47, 10, 1, 3, Color.FromArgb(200, 91, 56, 43));
            undo.Clear(); redo.Clear(); editing = false;
            strokeBefore = null; Dirty = true;
            Changed?.Invoke();
        }

        public void MarkUnsaved()
        {
            Dirty = true;
            Changed?.Invoke();
        }

        public void SetArmModel(bool slim)
        {
            if (SlimArms == slim) return;
            BeginChange();
            SlimArms = slim;
            EndChange();
            // Slim switches the model UV mapping; it does not resample or
            // silently delete painted pixels in previously wide arms.
            Changed?.Invoke();
        }

        void Remember(List<byte[]> stack, byte[] state)
        {
            if (stack.Count >= MaxUndo) stack.RemoveAt(0);
            stack.Add(state);
        }

        public void BeginChange()
        {
            if (editing) return;
            editing = true;
            strokeBefore = (byte[])pixels.Clone();
        }

        public void EndChange()
        {
            if (!editing) return;
            editing = false;
            if (strokeBefore != null && !pixels.SequenceEqual(strokeBefore))
            {
                Remember(undo, strokeBefore);
                redo.Clear(); Dirty = true;
                Changed?.Invoke();
            }
            strokeBefore = null;
        }

        public bool Undo()
        {
            EndChange();
            if (undo.Count == 0) return false;
            Remember(redo, (byte[])pixels.Clone());
            var back = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            Buffer.BlockCopy(back, 0, pixels, 0, pixels.Length);
            Dirty = true; Changed?.Invoke();
            return true;
        }

        public bool Redo()
        {
            EndChange();
            if (redo.Count == 0) return false;
            Remember(undo, (byte[])pixels.Clone());
            var next = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            Buffer.BlockCopy(next, 0, pixels, 0, pixels.Length);
            Dirty = true; Changed?.Invoke();
            return true;
        }

        public void Draw(int x, int y, Color color, int brushSize,
            bool erase, string part, string layer)
        {
            int radius = Math.Max(1, Math.Min(6, brushSize)) - 1;
            for (int dy = 0; dy <= radius; dy++)
                for (int dx = 0; dx <= radius; dx++)
                {
                    int px = x + dx - radius / 2;
                    int py = y + dy - radius / 2;
                    if (CanPaint(px, py, part, layer))
                        PutRaw(px, py, erase ? Colors.Transparent : color);
                }
            Changed?.Invoke();
        }

        public void Fill(int x, int y, Color replacement, string part, string layer)
        {
            if (!CanPaint(x, y, part, layer)) return;
            var oldColor = Pixel(x, y);
            if (oldColor == replacement) return;
            BeginChange();
            var visited = new bool[Size * Size];
            var q = new Queue<int>();
            q.Enqueue(y * Size + x);
            while (q.Count > 0)
            {
                int id = q.Dequeue(), px = id % Size, py = id / Size;
                if (visited[id]) continue;
                visited[id] = true;
                if (!CanPaint(px, py, part, layer) || Pixel(px, py) != oldColor)
                    continue;
                PutRaw(px, py, replacement);
                if (px > 0) q.Enqueue(id - 1);
                if (px < Size - 1) q.Enqueue(id + 1);
                if (py > 0) q.Enqueue(id - Size);
                if (py < Size - 1) q.Enqueue(id + Size);
            }
            EndChange();
        }

        public void ClearSelected(string part, string layer)
        {
            BeginChange();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (CanPaint(x, y, part, layer))
                        PutRaw(x, y, Colors.Transparent);
            EndChange();
        }

        public WriteableBitmap TextureImage()
        {
            var img = new WriteableBitmap(Size, Size, 96, 96,
                PixelFormats.Bgra32, null);
            img.WritePixels(new Int32Rect(0, 0, Size, Size),
                pixels, Stride, 0);
            return img;
        }

        static Color Over(Color bottom, Color top)
        {
            if (top.A == 0) return bottom;
            if (top.A == 255) return top;
            double a = top.A / 255.0, b = bottom.A / 255.0;
            double output = a + b * (1 - a);
            if (output <= 0) return Colors.Transparent;
            byte Mix(byte c1, byte c2) =>
                (byte)Math.Max(0, Math.Min(255,
                    Math.Round((c1 * b * (1 - a) + c2 * a) / output)));
            return Color.FromArgb((byte)Math.Round(output * 255),
                Mix(bottom.R, top.R), Mix(bottom.G, top.G), Mix(bottom.B, top.B));
        }

        static void PutPreview(byte[] result, int width, int x, int y, Color c)
        {
            int offset = (y * width + x) * 4;
            var before = Color.FromArgb(result[offset + 3],
                result[offset + 2], result[offset + 1], result[offset]);
            var after = Over(before, c);
            result[offset] = after.B; result[offset + 1] = after.G;
            result[offset + 2] = after.R; result[offset + 3] = after.A;
        }

        void DrawPreviewFace(byte[] pixelsOut, bool showOverlay, int x, int y,
            int width, int height, int faceX, int faceY,
            int outerX, int outerY)
        {
            for (int dy = 0; dy < height; dy++)
                for (int dx = 0; dx < width; dx++)
                {
                    PutPreview(pixelsOut, 16, x + dx, y + dy,
                        Pixel(faceX + dx, faceY + dy));
                    if (showOverlay)
                        PutPreview(pixelsOut, 16, x + dx, y + dy,
                            Pixel(outerX + dx, outerY + dy));
                }
        }

        /// <summary>True texture front/back, not a fake generated photo.</summary>
        public BitmapSource AvatarImage(bool back, bool showOverlay)
        {
            const int width = 16, height = 32;
            var result = new byte[width * height * 4];
            int arm = SlimArms ? 3 : 4;
            DrawPreviewFace(result, showOverlay, 4, 0, 8, 8,
                back ? 24 : 8, 8, back ? 56 : 40, 8);
            DrawPreviewFace(result, showOverlay, 4, 8, 8, 12,
                back ? 32 : 20, 20, back ? 32 : 20, 36);
            DrawPreviewFace(result, showOverlay, 4 - arm, 8, arm, 12,
                back ? 44 : 44, back ? 52 : 20,
                back ? 60 : 44, back ? 52 : 36);
            DrawPreviewFace(result, showOverlay, 12, 8, arm, 12,
                back ? 52 : 36, back ? 20 : 52,
                back ? 52 : 52, back ? 36 : 52);
            DrawPreviewFace(result, showOverlay, 4, 20, 4, 12,
                back ? 28 : 4, back ? 52 : 20,
                back ? 12 : 4, back ? 52 : 36);
            DrawPreviewFace(result, showOverlay, 8, 20, 4, 12,
                back ? 12 : 20, back ? 20 : 52,
                back ? 12 : 4, back ? 36 : 52);
            return BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, result, width * 4);
        }

        static void MirrorFace(byte[] source, byte[] destination,
            int sx, int sy, int dx, int dy, int w, int h)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int input = ((sy + y) * 64 + sx + w - x - 1) * 4;
                    int output = ((dy + y) * 64 + dx + x) * 4;
                    Buffer.BlockCopy(source, input, destination, output, 4);
                }
        }

        static void MirrorLegacyLimb(byte[] data, int sourceU, int targetU)
        {
            byte[] original = (byte[])data.Clone();
            // Horizontal mirroring and left/right face exchange preserve the
            // old 64x32 skin look instead of leaving left limbs transparent.
            const int w = 4, d = 4, h = 12;
            MirrorFace(original, data, sourceU + d, 16, targetU + d, 48, w, d);
            MirrorFace(original, data, sourceU + d + w, 16,
                targetU + d + w, 48, w, d);
            MirrorFace(original, data, sourceU + d, 20, targetU + d, 52, w, h);
            MirrorFace(original, data, sourceU + 2 * d + w, 20,
                targetU + 2 * d + w, 52, w, h);
            MirrorFace(original, data, sourceU, 20,
                targetU + d + w, 52, d, h);
            MirrorFace(original, data, sourceU + d + w, 20, targetU, 52, d, h);
        }

        /// <summary>
        /// Only accept a real PNG, <=4 MiB, exactly 64x64 or legacy 64x32.
        /// Encoder strips original metadata on subsequent save/export.
        /// </summary>
        public void LoadPng(string path, bool slim = false)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("没有找到 PNG 皮肤文件", path);
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("皮肤 PNG 文件不得超过 4 MiB");
            byte[] loaded;
            int height;
            using (var stream = File.OpenRead(path))
            {
                byte[] signature = new byte[8];
                if (stream.Read(signature, 0, signature.Length) != 8 ||
                    !signature.SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
                    throw new InvalidDataException("皮肤文件必须是 PNG 图片");
                stream.Position = 0;
                var decoder = new PngBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count < 1)
                    throw new InvalidDataException("皮肤 PNG 未包含图像帧");
                var frame = decoder.Frames[0];
                height = frame.PixelHeight;
                if (frame.PixelWidth != Size || (height != Size && height != 32))
                    throw new InvalidDataException("只支持 64×64 或旧版 64×32 像素的 Minecraft Java 皮肤");
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                loaded = new byte[height * Stride];
                converted.CopyPixels(loaded, Stride, 0);
            }
            Array.Clear(pixels, 0, pixels.Length);
            Buffer.BlockCopy(loaded, 0, pixels, 0, loaded.Length);
            if (height == 32)
            {
                MirrorLegacyLimb(pixels, 40, 32);
                MirrorLegacyLimb(pixels, 0, 16);
            }
            SlimArms = slim && height == 64;
            undo.Clear(); redo.Clear(); editing = false; strokeBefore = null;
            Dirty = false;
            Changed?.Invoke();
        }

        public byte[] ExportPngBytes()
        {
            if (editing) EndChange();
            var bitmap = TextureImage();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var buffer = new MemoryStream())
            {
                encoder.Save(buffer);
                return buffer.ToArray();
            }
        }

        public void SavePng(string path)
        {
            byte[] data = ExportPngBytes();
            using (var stream = new FileStream(path, FileMode.Create,
                FileAccess.Write, FileShare.None))
                stream.Write(data, 0, data.Length);
            Dirty = false;
        }

        public static string LocalLibrary =>
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
                "BlockCraftLauncher", "Skins");

        public static string SafeLibraryPath(string displayName)
        {
            string title = (displayName ?? "").Trim();
            if (title.Length < 1 || title.Length > 48 ||
                title == "." || title == ".." ||
                title.EndsWith(".") || title.EndsWith(" ") ||
                title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                title.Contains("/") || title.Contains("\\") ||
                new[] {"CON","PRN","AUX","NUL","COM1","COM2","LPT1","LPT2" }
                    .Contains(title, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("皮肤名称应为 1～48 字、且不能包含路径或非法字符");
            return Path.Combine(LocalLibrary, title + ".png");
        }

        public static void RunOfflineSelfTest()
        {
            void Require(bool ok, string message)
            {
                if (!ok) throw new InvalidOperationException("皮肤 DIY 离线自检：" + message);
            }
            var skin = new SkinEditorDocument();
            Require(skin.TextureImage().PixelWidth == 64 &&
                skin.TextureImage().PixelHeight == 64, "标准 64x64 像素贴图");
            Require(skin.Pixel(10, 11).A == 255, "默认人物有可见的脸");
            Require(skin.AvatarImage(false, true).PixelWidth == 16 &&
                skin.AvatarImage(true, true).PixelHeight == 32, "正反面预览格式");
            Require(!skin.CanPaint(63, 47, "全部部位", "基础层"), "拒绝绘制无效 UV 区域");
            Require(!skin.CanPaint(40, 8, "头部", "基础层") &&
                skin.CanPaint(40, 8, "头部", "外层"), "基础层与外层分离");
            Color old = skin.Pixel(10, 11);
            skin.BeginChange();
            skin.Draw(10, 11, Colors.Magenta, 1, false, "头部", "基础层");
            skin.EndChange();
            Require(skin.Pixel(10, 11) == Colors.Magenta, "画笔修改选中像素");
            Require(skin.Undo() && skin.Pixel(10, 11) == old, "撤销还原");
            Require(skin.Redo() && skin.Pixel(10, 11) == Colors.Magenta, "重做恢复");
            skin.Fill(10, 11, Colors.Orange, "头部", "基础层");
            Require(skin.Pixel(10, 11) == Colors.Orange, "填充选中颜色");
            skin.ClearSelected("头部", "外层");
            Require(skin.Pixel(40, 8).A == 0, "清除外层");
            skin.SetArmModel(true);
            Require(skin.SlimArms && skin.AvatarImage(false, true).PixelHeight == 32,
                "切换 Alex 纤细手臂的显示");

            string folder = Path.Combine(Path.GetTempPath(), "BlockCraft-Skin-Selftest-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "selftest.png");
                skin.SavePng(path);
                var another = new SkinEditorDocument();
                another.LoadPng(path, true);
                Require(another.Pixel(10, 11) == skin.Pixel(10, 11) &&
                    another.SlimArms, "导入导出 PNG 的颜色和模型信息");
                var legacyData = new byte[64 * 32 * 4];
                for (int i = 3; i < legacyData.Length; i += 4) legacyData[i] = 255;
                var oldBitmap = BitmapSource.Create(64, 32, 96, 96,
                    PixelFormats.Bgra32, null, legacyData, 64 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(oldBitmap));
                string legacy = Path.Combine(folder, "legacy.png");
                using (var stream = File.Create(legacy)) encoder.Save(stream);
                another.LoadPng(legacy);
                Require(another.Pixel(36, 52).A == 255 &&
                    another.Pixel(20, 52).A == 255,
                    "旧版 64x32 皮肤的左手和左腿镜像补全");
                bool rejected = false;
                try { another.LoadPng(path + ".missing"); }
                catch (FileNotFoundException) { rejected = true; }
                Require(rejected, "拒绝不存在的皮肤");
                string malformed = Path.Combine(folder, "fake.png");
                File.WriteAllText(malformed, "NOT A PNG");
                rejected = false;
                try { another.LoadPng(malformed); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "拒绝伪装 PNG");
                rejected = false;
                try { SafeLibraryPath("../outside"); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "皮肤库路径不可穿越");
                MinecraftSkinUploadService.RunOfflineSelfTest();
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }
    }
}

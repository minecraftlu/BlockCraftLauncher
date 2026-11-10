using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    /// <summary>
    /// Local, mouse-operated Minecraft Java skin model. The outer skin is
    /// intentionally built ONLY from visible texels: a transparent WPF
    /// GeometryModel3D still writes depth and otherwise hides the opaque
    /// body underneath, making real skins look like scattered floating parts.
    /// </summary>
    internal sealed class Skin3DPreview : UserControl
    {
        const int AtlasSize = SkinEditorDocument.Size;
        readonly Grid interaction = new Grid { Background = Brushes.Transparent };
        readonly Viewport3D viewport = new Viewport3D();
        readonly Model3DGroup parts = new Model3DGroup();
        readonly ModelVisual3D figure = new ModelVisual3D();
        readonly AxisAngleRotation3D yaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -28);
        readonly AxisAngleRotation3D pitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -11);
        readonly PerspectiveCamera camera = new PerspectiveCamera();
        readonly DispatcherTimer spin = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        Point previousMouse;
        bool dragging;
        bool autoRotating;
        double distance = 54;

        public event Action<bool> AutoRotationChanged;
        internal int PartCount => parts.Children.Count;
        internal int VisibleOuterQuads { get; private set; }
        internal double Yaw => yaw.Angle;
        internal double Pitch => pitch.Angle;
        internal double CameraDistance => distance;
        internal bool IsAutoRotating => autoRotating;

        public Skin3DPreview()
        {
            MinWidth = 160;
            Height = 296;
            Focusable = true;
            Cursor = Cursors.SizeAll;
            ToolTip = "左键拖动：360° 旋转；滚轮：缩放；双击：重置视角";
            var surface = new Border {
                Child = interaction,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };
            surface.SetResourceReference(Border.BackgroundProperty, "SearchSurfaceBrush");
            surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrushTheme");
            Content = surface;
            // Subtle background gradients work in light and dark themes.
            interaction.Children.Add(new Border {
                Background = new LinearGradientBrush(
                    Color.FromArgb(30, 79, 145, 208),
                    Color.FromArgb(6, 74, 109, 158), 90),
                IsHitTestVisible = false
            });
            interaction.Children.Add(viewport);
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(168, 168, 168)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(88, 88, 88),
                new Vector3D(-0.5, -0.7, -1)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(35, 35, 35),
                new Vector3D(0.65, 0.15, -0.3)));
            viewport.Children.Add(new ModelVisual3D { Content = lights });
            figure.Content = parts;
            var transforms = new Transform3DGroup();
            transforms.Children.Add(new RotateTransform3D(yaw, new Point3D(0, 16, 0)));
            transforms.Children.Add(new RotateTransform3D(pitch, new Point3D(0, 16, 0)));
            figure.Transform = transforms;
            viewport.Children.Add(figure);
            camera.UpDirection = new Vector3D(0, 1, 0);
            camera.FieldOfView = 35;
            viewport.Camera = camera;
            RenderOptions.SetBitmapScalingMode(viewport, BitmapScalingMode.NearestNeighbor);
            ResetView();

            spin.Tick += (sender, args) => {
                if (!IsVisible) { StopAutoRotate(); return; }
                yaw.Angle = (yaw.Angle + 0.75) % 360.0;
            };
            IsVisibleChanged += (sender, args) => {
                if (!IsVisible) StopAutoRotate();
            };
            interaction.MouseLeftButtonDown += (sender, e) => {
                if (e.ClickCount >= 2) {
                    ResetView();
                    e.Handled = true;
                    return;
                }
                StopAutoRotate();
                dragging = true;
                previousMouse = e.GetPosition(interaction);
                interaction.CaptureMouse();
                Focus();
                e.Handled = true;
            };
            interaction.MouseMove += (sender, e) => {
                if (!dragging) return;
                if (e.LeftButton != MouseButtonState.Pressed) {
                    StopDrag();
                    return;
                }
                var point = e.GetPosition(interaction);
                RotateBy(point.X - previousMouse.X, point.Y - previousMouse.Y);
                previousMouse = point;
                e.Handled = true;
            };
            interaction.MouseLeftButtonUp += (sender, e) => {
                StopDrag();
                e.Handled = true;
            };
            interaction.LostMouseCapture += (sender, e) => dragging = false;
            interaction.MouseWheel += (sender, e) => {
                ZoomByWheelDelta(e.Delta);
                e.Handled = true;
            };
            Unloaded += (sender, e) => {
                StopDrag();
                StopAutoRotate();
            };
        }

        internal void RotateBy(double deltaX, double deltaY)
        {
            if (double.IsNaN(deltaX) || double.IsInfinity(deltaX) ||
                double.IsNaN(deltaY) || double.IsInfinity(deltaY)) return;
            yaw.Angle = (yaw.Angle + deltaX * 0.65) % 360.0;
            pitch.Angle = Math.Max(-42, Math.Min(42, pitch.Angle + deltaY * 0.45));
        }

        internal void ZoomByWheelDelta(int delta)
        {
            distance = Math.Max(43, Math.Min(90, distance - delta / 120.0 * 4));
            UpdateCamera();
        }

        public bool ToggleAutoRotate()
        {
            if (autoRotating) StopAutoRotate();
            else {
                autoRotating = true;
                spin.Start();
                AutoRotationChanged?.Invoke(true);
            }
            return autoRotating;
        }

        public void StopAutoRotate()
        {
            if (!autoRotating) return;
            autoRotating = false;
            spin.Stop();
            AutoRotationChanged?.Invoke(false);
        }

        void StopDrag()
        {
            dragging = false;
            if (interaction.IsMouseCaptured) interaction.ReleaseMouseCapture();
        }

        void UpdateCamera()
        {
            camera.Position = new Point3D(0, 16.5, distance);
            camera.LookDirection = new Vector3D(0, -0.5, -distance);
        }

        public void ResetView()
        {
            StopAutoRotate();
            yaw.Angle = -28;
            pitch.Angle = -11;
            distance = 54;
            UpdateCamera();
        }

        /// <summary>
        /// Builds each body face from its exact cropped skin atlas rectangle.
        /// IMPORTANT: a single 64x64 ImageBrush on a mesh of disconnected,
        /// separately UV-mapped cuboids was visibly leaving the base avatar
        /// transparent in WPF. Face-local ImageBrush textures have normalized
        /// (0..1) UVs and cannot accidentally sample the empty atlas margins.
        /// </summary>
        public void SetSkin(BitmapSource skin, bool slim, bool outer)
        {
            parts.Children.Clear();
            VisibleOuterQuads = 0;
            if (skin == null) return;
            if (skin.PixelWidth != AtlasSize || skin.PixelHeight != AtlasSize)
                throw new ArgumentException("360° 预览仅接受标准 64×64 Minecraft Java 皮肤");
            var normalized = skin.Format == PixelFormats.Bgra32 ? skin :
                new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[AtlasSize * AtlasSize * 4];
            normalized.CopyPixels(pixels, AtlasSize * 4, 0);

            foreach (var region in SkinEditorDocument.Regions(slim))
            {
                if (region.Overlay && !outer) continue;
                double x = 0, y = 0;
                switch (region.Part)
                {
                    case "头部": y = 28; break;
                    case "身体": y = 18; break;
                    case "右臂": x = -(4 + region.Width / 2.0); y = 18; break;
                    case "左臂": x = 4 + region.Width / 2.0; y = 18; break;
                    case "右腿": x = -2; y = 6; break;
                    case "左腿": x = 2; y = 6; break;
                    default: continue;
                }
                // Outer clothes float slightly above base to avoid z-fighting.
                double extra = region.Overlay ?
                    (region.Part == "头部" ? 0.75 : 0.55) : 0;
                // The base body must be visible on GPUs where WPF's
                // 3D ImageBrush atlas sampling fails: render each base texel
                // using a solid-color geometry instead of a texture sampler.
                // Outer clothing still uses the face-cropped texture path.
                var part = region.Overlay ?
                    BuildPart(region, x, y,
                        region.Width + extra, region.Height + extra,
                        region.Depth + extra, normalized, pixels, out int visibleOuter) :
                    BuildPixelPart(region, x, y,
                        region.Width, region.Height, region.Depth,
                        pixels, out int visibleBase);
                int visible = region.Overlay ? visibleOuter : visibleBase;
                if (region.Overlay) VisibleOuterQuads += visible;
                // Keep exactly six base groups + six outer groups for the
                // existing skin editor UI selftest and outer-layer switch.
                parts.Children.Add(part);
            }
        }

        /// <summary>
        /// GPU-independent base renderer: direct pixel-colored quads, grouped
        /// by exact ARGB color. It never invokes ImageBrush, so the opaque
        /// skin silhouette cannot disappear from atlas texture sampling.
        /// </summary>
        static void AddPixelFace(Model3DGroup part, byte[] pixels,
            Point3D tl, Point3D bl, Point3D br, Point3D tr,
            int u, int v, int width, int height, ref int quadCount)
        {
            var meshes = new Dictionary<uint, MeshGeometry3D>();
            for (int py = 0; py < height; py++)
                for (int px = 0; px < width; px++)
                {
                    int i = ((v + py) * AtlasSize + u + px) * 4;
                    byte b = pixels[i], g = pixels[i + 1];
                    byte r = pixels[i + 2], a = pixels[i + 3];
                    if (a == 0) continue;
                    uint color = ((uint)a << 24) | ((uint)r << 16) |
                        ((uint)g << 8) | b;
                    if (!meshes.TryGetValue(color, out var mesh))
                    {
                        mesh = new MeshGeometry3D();
                        meshes[color] = mesh;
                    }
                    double x0 = px / (double)width, x1 = (px + 1.0) / width;
                    double y0 = py / (double)height, y1 = (py + 1.0) / height;
                    Quad(mesh,
                        OnFace(tl, bl, br, tr, x0, y0),
                        OnFace(tl, bl, br, tr, x0, y1),
                        OnFace(tl, bl, br, tr, x1, y1),
                        OnFace(tl, bl, br, tr, x1, y0),
                        0, 0, 1, 1);
                    quadCount++;
                }
            foreach (var entry in meshes)
            {
                uint key = entry.Key;
                var color = Color.FromArgb(
                    (byte)(key >> 24), (byte)(key >> 16),
                    (byte)(key >> 8), (byte)key);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                part.Children.Add(new GeometryModel3D(entry.Value,
                    new DiffuseMaterial(brush)));
            }
        }

        static Model3DGroup BuildPixelPart(SkinUvRegion uv,
            double x, double y, double width, double height, double depth,
            byte[] pixels, out int quads)
        {
            double left = x - width / 2, right = x + width / 2;
            double bottom = y - height / 2, top = y + height / 2;
            double near = depth / 2, far = -depth / 2;
            int u = uv.U, v = uv.V, w = uv.Width, h = uv.Height, d = uv.Depth;
            var result = new Model3DGroup();
            quads = 0;
            AddPixelFace(result, pixels,
                P(left, top, near), P(left, bottom, near),
                P(right, bottom, near), P(right, top, near),
                u + d, v + d, w, h, ref quads);
            AddPixelFace(result, pixels,
                P(right, top, far), P(right, bottom, far),
                P(left, bottom, far), P(left, top, far),
                u + 2 * d + w, v + d, w, h, ref quads);
            AddPixelFace(result, pixels,
                P(left, top, far), P(left, bottom, far),
                P(left, bottom, near), P(left, top, near),
                u, v + d, d, h, ref quads);
            AddPixelFace(result, pixels,
                P(right, top, near), P(right, bottom, near),
                P(right, bottom, far), P(right, top, far),
                u + d + w, v + d, d, h, ref quads);
            AddPixelFace(result, pixels,
                P(left, top, far), P(left, top, near),
                P(right, top, near), P(right, top, far),
                u + d, v, w, d, ref quads);
            AddPixelFace(result, pixels,
                P(left, bottom, near), P(left, bottom, far),
                P(right, bottom, far), P(right, bottom, near),
                u + d + w, v, w, d, ref quads);
            return result;
        }

        static Point3D P(double x, double y, double z) => new Point3D(x, y, z);

        static Point3D OnFace(Point3D tl, Point3D bl, Point3D br, Point3D tr,
            double x, double y)
        {
            return new Point3D(
                tl.X * (1 - x) * (1 - y) + tr.X * x * (1 - y) +
                    bl.X * (1 - x) * y + br.X * x * y,
                tl.Y * (1 - x) * (1 - y) + tr.Y * x * (1 - y) +
                    bl.Y * (1 - x) * y + br.Y * x * y,
                tl.Z * (1 - x) * (1 - y) + tr.Z * x * (1 - y) +
                    bl.Z * (1 - x) * y + br.Z * x * y);
        }

        static void Quad(MeshGeometry3D mesh, Point3D tl, Point3D bl,
            Point3D br, Point3D tr, double u0, double v0, double u1, double v1)
        {
            int start = mesh.Positions.Count;
            mesh.Positions.Add(tl); mesh.Positions.Add(bl);
            mesh.Positions.Add(br); mesh.Positions.Add(tr);
            mesh.TextureCoordinates.Add(new Point(u0, v0));
            mesh.TextureCoordinates.Add(new Point(u0, v1));
            mesh.TextureCoordinates.Add(new Point(u1, v1));
            mesh.TextureCoordinates.Add(new Point(u1, v0));
            foreach (int offset in new[] { 0, 1, 2, 0, 2, 3 })
                mesh.TriangleIndices.Add(start + offset);
        }

        /// <summary>
        /// Base faces are entire opaque quads. Outer faces use only pixels
        /// with nonzero alpha so transparent hat/clothing texels never mask
        /// the base skin in WPF's depth buffer.
        /// </summary>
        static MeshGeometry3D FaceMesh(Point3D tl, Point3D bl, Point3D br,
            Point3D tr, int u, int v, int width, int height, bool overlay,
            byte[] pixels, out int visible)
        {
            var mesh = new MeshGeometry3D();
            visible = 0;
            if (!overlay)
            {
                Quad(mesh, tl, bl, br, tr, 0, 0, 1, 1);
                visible = 1;
                return mesh;
            }

            for (int py = 0; py < height; py++)
                for (int px = 0; px < width; px++)
                {
                    int index = ((v + py) * AtlasSize + u + px) * 4 + 3;
                    if (pixels[index] == 0) continue;
                    double x0 = px / (double)width, x1 = (px + 1.0) / width;
                    double y0 = py / (double)height, y1 = (py + 1.0) / height;
                    // Normalized UVs into the cropped face, NOT the 64x64
                    // atlas. Half-texel centers avoid sampling adjacent rows.
                    double cu = (px + 0.5) / width, cv = (py + 0.5) / height;
                    const double epsilon = 0.01;
                    double hu = epsilon / width, hv = epsilon / height;
                    Quad(mesh,
                        OnFace(tl, bl, br, tr, x0, y0),
                        OnFace(tl, bl, br, tr, x0, y1),
                        OnFace(tl, bl, br, tr, x1, y1),
                        OnFace(tl, bl, br, tr, x1, y0),
                        cu - hu, cv - hv, cu + hu, cv + hv);
                    visible++;
                }
            return mesh;
        }

        static void AddFace(Model3DGroup part, BitmapSource atlas, byte[] pixels,
            bool overlay, Point3D tl, Point3D bl, Point3D br, Point3D tr,
            int u, int v, int width, int height, ref int totalQuads)
        {
            var mesh = FaceMesh(tl, bl, br, tr, u, v, width, height,
                overlay, pixels, out int quads);
            if (quads == 0) return;
            totalQuads += quads;

            // Separate cropped source image per physical face. This is the
            // vital difference to the previous full-atlas shared material.
            var crop = new CroppedBitmap(atlas, new Int32Rect(u, v, width, height));
            var brush = new ImageBrush(crop)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1)
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            var material = new DiffuseMaterial(brush);
            part.Children.Add(new GeometryModel3D(mesh, material));
        }

        static Model3DGroup BuildPart(SkinUvRegion uv, double x, double y,
            double width, double height, double depth, BitmapSource atlas,
            byte[] pixels, out int quads)
        {
            double left = x - width / 2, right = x + width / 2;
            double bottom = y - height / 2, top = y + height / 2;
            double near = depth / 2, far = -depth / 2;
            int u = uv.U, v = uv.V, w = uv.Width, h = uv.Height, d = uv.Depth;
            var part = new Model3DGroup();
            quads = 0;
            AddFace(part, atlas, pixels, uv.Overlay,
                P(left, top, near), P(left, bottom, near),
                P(right, bottom, near), P(right, top, near),
                u + d, v + d, w, h, ref quads); // Front
            AddFace(part, atlas, pixels, uv.Overlay,
                P(right, top, far), P(right, bottom, far),
                P(left, bottom, far), P(left, top, far),
                u + 2 * d + w, v + d, w, h, ref quads); // Back
            AddFace(part, atlas, pixels, uv.Overlay,
                P(left, top, far), P(left, bottom, far),
                P(left, bottom, near), P(left, top, near),
                u, v + d, d, h, ref quads); // Left
            AddFace(part, atlas, pixels, uv.Overlay,
                P(right, top, near), P(right, bottom, near),
                P(right, bottom, far), P(right, top, far),
                u + d + w, v + d, d, h, ref quads); // Right
            AddFace(part, atlas, pixels, uv.Overlay,
                P(left, top, far), P(left, top, near),
                P(right, top, near), P(right, top, far),
                u + d, v, w, d, ref quads); // Top
            AddFace(part, atlas, pixels, uv.Overlay,
                P(left, bottom, near), P(left, bottom, far),
                P(right, bottom, far), P(right, bottom, near),
                u + d + w, v, w, d, ref quads); // Bottom
            return part;
        }

        static Color FirstPixel(BitmapSource source)
        {
            var bgra = source.Format == PixelFormats.Bgra32 ? source :
                new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(bytes, bgra.PixelWidth * 4, 0);
            return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
        }

        /// <summary>Offline verification of face-local texture source, UVs, and overlay transparency.</summary>
        internal static void VerifyGeometry()
        {
            var doc = new SkinEditorDocument();
            var atlas = doc.TextureImage();
            var empty = new byte[AtlasSize * AtlasSize * 4];
            foreach (bool slim in new[] { false, true })
                foreach (var region in SkinEditorDocument.Regions(slim))
                {
                    var part = BuildPart(region, 0, 16, region.Width,
                        region.Height, region.Depth, atlas, empty, out int quads);
                    int expected = region.Overlay ? 0 : 6;
                    if (quads != expected || part.Children.Count != expected)
                        throw new InvalidOperationException("360° 贴图透明面数量异常：" + region.Part);
                    foreach (var face in part.Children.Cast<GeometryModel3D>())
                    {
                        var mesh = (MeshGeometry3D)face.Geometry;
                        if (mesh.Positions.Count != 4 || mesh.TriangleIndices.Count != 6 ||
                            mesh.TextureCoordinates.Count != 4 ||
                            mesh.TextureCoordinates.Any(p => p.X < 0 || p.X > 1 ||
                                p.Y < 0 || p.Y > 1))
                            throw new InvalidOperationException("基础层面片坐标无效：" + region.Part);
                        var brush = ((DiffuseMaterial)face.Material).Brush as ImageBrush;
                        if (!(brush?.ImageSource is BitmapSource image) ||
                            image.PixelWidth > 12 || image.PixelHeight > 12 ||
                            image.PixelWidth < 3 || image.PixelHeight < 3)
                            throw new InvalidOperationException("皮肤面未使用单独裁剪的贴图");
                    }
                    if (region.Overlay)
                    {
                        var onePixel = new byte[empty.Length];
                        int index = ((region.V + region.Depth) * AtlasSize +
                            region.U + region.Depth) * 4 + 3;
                        onePixel[index] = 255;
                        var one = BuildPart(region, 0, 16, region.Width, region.Height,
                            region.Depth, atlas, onePixel, out int actual);
                        if (actual != 1 || one.Children.Count != 1 ||
                            ((MeshGeometry3D)((GeometryModel3D)one.Children[0]).Geometry)
                                .Positions.Count != 4)
                            throw new InvalidOperationException("外层单像素映射丢失：" + region.Part);
                    }
                }
            // Verify that the front head face actually samples the opaque
            // cheeks/skin texels, never transparent atlas padding.
            var head = SkinEditorDocument.Regions(false).First(r =>
                r.Part == "头部" && !r.Overlay);
            var all = new byte[AtlasSize * AtlasSize * 4];
            atlas.CopyPixels(all, AtlasSize * 4, 0);
            var headModel = BuildPart(head, 0, 28, 8, 8, 8, atlas, all, out int faceCount);
            var headFront = (GeometryModel3D)headModel.Children[0];
            var headTexture = (BitmapSource)((ImageBrush)((DiffuseMaterial)headFront.Material).Brush).ImageSource;
            if (faceCount != 6 || headTexture.PixelWidth != 8 ||
                headTexture.PixelHeight != 8 ||
                FirstPixel(headTexture) != doc.Pixel(8, 8) ||
                FirstPixel(headTexture).A == 0)
                throw new InvalidOperationException("皮肤正面纹理错用了透明图集或 UV 区域");
            var solidHead = BuildPixelPart(head, 0, 28, 8, 8, 8,
                all, out int pixelsDrawn);
            if (pixelsDrawn != 384 || solidHead.Children.Count < 6 ||
                !solidHead.Children.Cast<GeometryModel3D>().Any(face =>
                    ((DiffuseMaterial)face.Material).Brush is SolidColorBrush color &&
                    color.Color == doc.Pixel(8, 8)))
                throw new InvalidOperationException(
                    "兼容渲染必须把头部所有基础像素变成不依赖贴图的彩色面片");
        }
    }
}

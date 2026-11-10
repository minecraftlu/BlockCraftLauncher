using System;
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
        /// Uses the normalized, local 64x64 PNG only. No network or account
        /// data. Transparent outer pixels must never create depth-writing
        /// polygons, because they visually punch holes in the base model.
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
            var image = new ImageBrush(normalized) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            var material = new DiffuseMaterial(image);
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
                double extra = region.Overlay ?
                    (region.Part == "头部" ? 0.75 : 0.55) : 0;
                var mesh = Cuboid(region, x, y,
                    region.Width + extra, region.Height + extra,
                    region.Depth + extra, pixels, out int visibleQuads);
                if (region.Overlay) VisibleOuterQuads += visibleQuads;
                parts.Children.Add(new GeometryModel3D(mesh, material));
            }
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

        static int Face(MeshGeometry3D mesh, Point3D tl, Point3D bl,
            Point3D br, Point3D tr, int u, int v, int width, int height,
            bool overlay, byte[] pixels)
        {
            if (!overlay) {
                const double inset = 0.08;
                Quad(mesh, tl, bl, br, tr,
                    (u + inset) / AtlasSize, (v + inset) / AtlasSize,
                    (u + width - inset) / AtlasSize,
                    (v + height - inset) / AtlasSize);
                return 1;
            }

            int visible = 0;
            // Individual texel quads preserve the depth buffer under fully
            // transparent pixels. A textured full-box overlay DOES NOT: WPF
            // writes depth even for those transparent fragments.
            for (int py = 0; py < height; py++)
                for (int px = 0; px < width; px++)
                {
                    int index = ((v + py) * AtlasSize + (u + px)) * 4 + 3;
                    if (index < 0 || index >= pixels.Length || pixels[index] == 0)
                        continue;
                    double x0 = px / (double)width, x1 = (px + 1.0) / width;
                    double y0 = py / (double)height, y1 = (py + 1.0) / height;
                    // Sample the very center of the source pixel: no stray
                    // alpha from adjacent atlas texels and no fuzzy seams.
                    double centerU = (u + px + 0.5) / AtlasSize;
                    double centerV = (v + py + 0.5) / AtlasSize;
                    const double half = 0.02 / AtlasSize;
                    Quad(mesh,
                        OnFace(tl, bl, br, tr, x0, y0),
                        OnFace(tl, bl, br, tr, x0, y1),
                        OnFace(tl, bl, br, tr, x1, y1),
                        OnFace(tl, bl, br, tr, x1, y0),
                        centerU - half, centerV - half,
                        centerU + half, centerV + half);
                    visible++;
                }
            return visible;
        }

        static MeshGeometry3D Cuboid(SkinUvRegion uv, double x, double y,
            double width, double height, double depth, byte[] pixels,
            out int quads)
        {
            double left = x - width / 2, right = x + width / 2;
            double bottom = y - height / 2, top = y + height / 2;
            double near = depth / 2, far = -depth / 2;
            int u = uv.U, v = uv.V, w = uv.Width, h = uv.Height, d = uv.Depth;
            var mesh = new MeshGeometry3D();
            quads = 0;
            quads += Face(mesh, P(left, top, near), P(left, bottom, near),
                P(right, bottom, near), P(right, top, near),
                u + d, v + d, w, h, uv.Overlay, pixels); // Front
            quads += Face(mesh, P(right, top, far), P(right, bottom, far),
                P(left, bottom, far), P(left, top, far),
                u + 2 * d + w, v + d, w, h, uv.Overlay, pixels); // Back
            quads += Face(mesh, P(left, top, far), P(left, bottom, far),
                P(left, bottom, near), P(left, top, near),
                u, v + d, d, h, uv.Overlay, pixels); // Left
            quads += Face(mesh, P(right, top, near), P(right, bottom, near),
                P(right, bottom, far), P(right, top, far),
                u + d + w, v + d, d, h, uv.Overlay, pixels); // Right
            quads += Face(mesh, P(left, top, far), P(left, top, near),
                P(right, top, near), P(right, top, far),
                u + d, v, w, d, uv.Overlay, pixels); // Top
            quads += Face(mesh, P(left, bottom, near), P(left, bottom, far),
                P(right, bottom, far), P(right, bottom, near),
                u + d + w, v, w, d, uv.Overlay, pixels); // Bottom
            return mesh;
        }

        internal static void VerifyGeometry()
        {
            var empty = new byte[AtlasSize * AtlasSize * 4];
            foreach (bool slim in new[] { false, true })
                foreach (var region in SkinEditorDocument.Regions(slim))
                {
                    var mesh = Cuboid(region, 0, 16, region.Width, region.Height,
                        region.Depth, empty, out int quads);
                    int expected = region.Overlay ? 0 : 6;
                    if (quads != expected || mesh.Positions.Count != expected * 4 ||
                        mesh.TextureCoordinates.Count != expected * 4 ||
                        mesh.TriangleIndices.Count != expected * 6 ||
                        mesh.TextureCoordinates.Any(p => p.X < 0 || p.X > 1 ||
                            p.Y < 0 || p.Y > 1))
                        throw new InvalidOperationException(
                            "360° 皮肤预览透明像素/UV 回归：" + region.Part);
                    if (!region.Overlay) continue;
                    // One colored outer-layer pixel should produce exactly one
                    // quad, not six complete transparent faces masking the body.
                    var onePixel = new byte[empty.Length];
                    int index = ((region.V + region.Depth) * AtlasSize +
                        region.U + region.Depth) * 4 + 3;
                    onePixel[index] = 255;
                    var one = Cuboid(region, 0, 16, region.Width, region.Height,
                        region.Depth, onePixel, out int actual);
                    if (actual != 1 || one.Positions.Count != 4 ||
                        one.TriangleIndices.Count != 6)
                        throw new InvalidOperationException(
                            "透明外层单个像素映射不正确：" + region.Part);
                }
        }
    }
}

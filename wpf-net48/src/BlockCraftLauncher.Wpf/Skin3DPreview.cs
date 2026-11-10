using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    /// <summary>
    /// Fully local Minecraft Java skin viewer. Six correctly UV-mapped faces
    /// per body part, with optional outer layers, classic/slim arms and no
    /// browser, network upload or WebGL dependency.
    /// </summary>
    internal sealed class Skin3DPreview : UserControl
    {
        readonly Grid interaction = new Grid { Background = Brushes.Transparent };
        readonly Viewport3D viewport = new Viewport3D();
        readonly Model3DGroup parts = new Model3DGroup();
        readonly ModelVisual3D figure = new ModelVisual3D();
        readonly AxisAngleRotation3D yaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -22);
        readonly AxisAngleRotation3D pitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -8);
        readonly PerspectiveCamera camera = new PerspectiveCamera();
        Point previousMouse;
        bool dragging;
        double distance = 58;

        internal int PartCount => parts.Children.Count;
        internal double Yaw => yaw.Angle;

        public Skin3DPreview()
        {
            MinWidth = 160;
            Height = 246;
            Focusable = true;
            Cursor = Cursors.SizeAll;
            ToolTip = "按住鼠标左键拖动：360° 旋转；滚轮：缩放；可点击重置视角";
            var surface = new Border
            {
                Child = interaction, CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1), ClipToBounds = true
            };
            surface.SetResourceReference(Border.BackgroundProperty, "SearchSurfaceBrush");
            surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrushTheme");
            Content = surface;
            interaction.Children.Add(viewport);
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(207, 207, 207)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(70, 70, 70),
                new Vector3D(-0.4, -0.7, -1)));
            viewport.Children.Add(new ModelVisual3D { Content = lights });
            figure.Content = parts;
            var transforms = new Transform3DGroup();
            transforms.Children.Add(new RotateTransform3D(yaw, new Point3D(0, 16, 0)));
            transforms.Children.Add(new RotateTransform3D(pitch, new Point3D(0, 16, 0)));
            figure.Transform = transforms;
            viewport.Children.Add(figure);
            camera.UpDirection = new Vector3D(0, 1, 0);
            camera.FieldOfView = 37;
            viewport.Camera = camera;
            RenderOptions.SetBitmapScalingMode(viewport, BitmapScalingMode.NearestNeighbor);
            ResetView();

            interaction.MouseLeftButtonDown += (sender, e) =>
            {
                dragging = true;
                previousMouse = e.GetPosition(interaction);
                interaction.CaptureMouse();
                Focus();
                e.Handled = true;
            };
            interaction.MouseMove += (sender, e) =>
            {
                if (!dragging) return;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    StopDrag();
                    return;
                }
                var point = e.GetPosition(interaction);
                RotateBy(point.X - previousMouse.X, point.Y - previousMouse.Y);
                previousMouse = point;
                e.Handled = true;
            };
            interaction.MouseLeftButtonUp += (sender, e) =>
            {
                StopDrag();
                e.Handled = true;
            };
            interaction.LostMouseCapture += (sender, e) => dragging = false;
            interaction.MouseWheel += (sender, e) =>
            {
                ZoomByWheelDelta(e.Delta);
                e.Handled = true;
            };
            Unloaded += (sender, e) => StopDrag();
        }

        internal void RotateBy(double deltaX, double deltaY)
        {
            if (double.IsNaN(deltaX) || double.IsInfinity(deltaX) ||
                double.IsNaN(deltaY) || double.IsInfinity(deltaY))
                return;
            yaw.Angle = (yaw.Angle + deltaX * 0.75) % 360.0;
            pitch.Angle = Math.Max(-55, Math.Min(55, pitch.Angle + deltaY * 0.55));
        }

        internal void ZoomByWheelDelta(int delta)
        {
            distance = Math.Max(44, Math.Min(92, distance - delta / 120.0 * 4));
            UpdateCamera();
        }

        internal double Pitch => pitch.Angle;
        internal double CameraDistance => distance;

        void StopDrag()
        {
            dragging = false;
            if (interaction.IsMouseCaptured) interaction.ReleaseMouseCapture();
        }

        void UpdateCamera()
        {
            camera.Position = new Point3D(0, 16, distance);
            camera.LookDirection = new Vector3D(0, 0, -distance);
        }

        public void ResetView()
        {
            yaw.Angle = -22;
            pitch.Angle = -8;
            distance = 58;
            UpdateCamera();
        }

        /// <summary>Bind only the normalized 64x64 texture; no image is sent remotely.</summary>
        public void SetSkin(BitmapSource skin, bool slim, bool outer)
        {
            parts.Children.Clear();
            if (skin == null) return;
            if (skin.PixelWidth != 64 || skin.PixelHeight != 64)
                throw new ArgumentException("360° 预览仅接受标准 64×64 Minecraft Java 皮肤");
            var image = new ImageBrush(skin) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            var material = new DiffuseMaterial(image);
            foreach (var region in SkinEditorDocument.Regions(slim))
            {
                if (region.Overlay && !outer) continue;
                double centerX = 0, centerY = 0;
                switch (region.Part)
                {
                    case "头部": centerY = 28; break;
                    case "身体": centerY = 18; break;
                    case "右臂":
                        centerX = -(4 + region.Width / 2.0);
                        centerY = 18;
                        break;
                    case "左臂":
                        centerX = 4 + region.Width / 2.0;
                        centerY = 18;
                        break;
                    case "右腿": centerX = -2; centerY = 6; break;
                    case "左腿": centerX = 2; centerY = 6; break;
                    default: continue;
                }
                // Expand armor/hat by half a skin pixel to avoid z-fighting.
                double extra = region.Overlay ? 0.5 : 0;
                var mesh = Cuboid(region, centerX, centerY,
                    region.Width + extra, region.Height + extra,
                    region.Depth + extra);
                parts.Children.Add(new GeometryModel3D(mesh, material));
            }
        }

        static Point3D P(double x, double y, double z) => new Point3D(x, y, z);

        static void Face(MeshGeometry3D mesh, Point3D tl, Point3D bl,
            Point3D br, Point3D tr, int u, int v, int width, int height)
        {
            int first = mesh.Positions.Count;
            mesh.Positions.Add(tl);
            mesh.Positions.Add(bl);
            mesh.Positions.Add(br);
            mesh.Positions.Add(tr);
            // Small inset prevents bleeding from neighbouring UV atlas tiles.
            const double inset = 0.015;
            double u0 = (u + inset) / 64.0, u1 = (u + width - inset) / 64.0;
            double v0 = (v + inset) / 64.0, v1 = (v + height - inset) / 64.0;
            mesh.TextureCoordinates.Add(new Point(u0, v0));
            mesh.TextureCoordinates.Add(new Point(u0, v1));
            mesh.TextureCoordinates.Add(new Point(u1, v1));
            mesh.TextureCoordinates.Add(new Point(u1, v0));
            foreach (int offset in new[] { 0, 1, 2, 0, 2, 3 })
                mesh.TriangleIndices.Add(first + offset);
        }

        static MeshGeometry3D Cuboid(SkinUvRegion uv, double x, double y,
            double width, double height, double depth)
        {
            double left = x - width / 2, right = x + width / 2;
            double bottom = y - height / 2, top = y + height / 2;
            double near = depth / 2, far = -depth / 2;
            int u = uv.U, v = uv.V, w = uv.Width, h = uv.Height, d = uv.Depth;
            var mesh = new MeshGeometry3D();
            // All corners are ordered counterclockwise viewed from outside.
            Face(mesh, P(left, top, near), P(left, bottom, near),
                P(right, bottom, near), P(right, top, near), u + d, v + d, w, h); // Front
            Face(mesh, P(right, top, far), P(right, bottom, far),
                P(left, bottom, far), P(left, top, far), u + 2 * d + w, v + d, w, h); // Back
            Face(mesh, P(left, top, far), P(left, bottom, far),
                P(left, bottom, near), P(left, top, near), u, v + d, d, h); // Left
            Face(mesh, P(right, top, near), P(right, bottom, near),
                P(right, bottom, far), P(right, top, far), u + d + w, v + d, d, h); // Right
            Face(mesh, P(left, top, far), P(left, top, near),
                P(right, top, near), P(right, top, far), u + d, v, w, d); // Top
            Face(mesh, P(left, bottom, near), P(left, bottom, far),
                P(right, bottom, far), P(right, bottom, near), u + d + w, v, w, d); // Bottom
            return mesh;
        }

        internal static void VerifyGeometry()
        {
            foreach (bool slim in new[] { false, true })
                foreach (var region in SkinEditorDocument.Regions(slim))
                {
                    var mesh = Cuboid(region, 0, 16, region.Width, region.Height, region.Depth);
                    if (mesh.Positions.Count != 24 || mesh.TextureCoordinates.Count != 24 ||
                        mesh.TriangleIndices.Count != 36 ||
                        mesh.TextureCoordinates.Any(p => p.X < 0 || p.X > 1 ||
                            p.Y < 0 || p.Y > 1))
                        throw new InvalidOperationException(
                            "360° 皮肤预览 UV / 三角面映射自检失败：" + region.Part);
                }
        }
    }
}

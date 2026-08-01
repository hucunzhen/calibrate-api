using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CalibOperatorPInvoke;
using MvCameraControl;
using static CalibOperatorCLI_Example.CameraService;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 相机交互采集标定图：实时预览 →「确定」保存当前帧 → 达到最少张数后「完成」。
    /// 预览走 WriteableBitmap 直写像素（无 CalibImage / PNG 转换）。
    /// </summary>
    public sealed class CameraCalibCaptureDialog : Window
    {
        private const int PreviewMaxWidth = 1280;

        private readonly int _minFrames;
        private readonly string _saveDirectory;
        private readonly string _namePrefix;
        private readonly string _fileExtension;

        private readonly CameraService _camera;
        private readonly Image _previewImage;
        private readonly TextBlock _statusText;
        private readonly ListBox _capturedList;
        private readonly Button _btnComplete;

        private readonly object _frameLock = new();
        private byte[]? _fullBuffer;
        private byte[]? _previewBuffer;
        private int _fullWidth;
        private int _fullHeight;
        private CameraFramePixelLayout _fullLayout = CameraFramePixelLayout.Unsupported;
        private WriteableBitmap? _previewBitmap;
        private int _previewWidth;
        private int _previewHeight;
        private int _uiPumpScheduled;
        private int _frameGeneration;

        private readonly List<string> _savedPaths = new();

        public IReadOnlyList<string> SavedImagePaths => _savedPaths;
        public string SaveDirectory => _saveDirectory;
        public int CapturedCount => _savedPaths.Count;

        public CameraCalibCaptureDialog(
            int deviceIndex,
            int minFrames,
            string saveDirectory,
            string namePrefix,
            string fileExtension,
            Window? owner)
        {
            _minFrames = Math.Max(1, minFrames);
            _saveDirectory = saveDirectory;
            _namePrefix = string.IsNullOrWhiteSpace(namePrefix) ? "Image_" : namePrefix;
            _fileExtension = NormalizeExtension(fileExtension);

            Title = "棋盘标定 — 相机采集";
            Width = 1080;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));

            _camera = new CameraService();
            if (!_camera.ConnectByIndex(deviceIndex))
                throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");

            Directory.CreateDirectory(_saveDirectory);

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new DockPanel
            {
                Margin = new Thickness(10),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
            };
            Grid.SetColumn(left, 0);

            _statusText = new TextBlock
            {
                Text = BuildStatusText(),
                Foreground = Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 10, 10, 8)
            };
            DockPanel.SetDock(_statusText, Dock.Top);
            left.Children.Add(_statusText);

            var hint = new TextBlock
            {
                Text = "调整标定板位姿与对焦，单击「确定」保存当前预览帧。建议覆盖画面四角与中心，达到最少采集张数后单击「完成」。",
                Foreground = Brushes.Silver,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 0, 10, 8),
                FontSize = 12
            };
            DockPanel.SetDock(hint, Dock.Top);
            left.Children.Add(hint);

            _capturedList = new ListBox
            {
                Margin = new Thickness(10, 0, 10, 10),
                Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            left.Children.Add(_capturedList);

            var right = new DockPanel { Margin = new Thickness(0, 10, 10, 10) };
            Grid.SetColumn(right, 1);

            var bottomBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var btnConfirm = new Button { Content = "确定（保存本帧）", Width = 120, Margin = new Thickness(0, 0, 8, 0) };
            _btnComplete = new Button { Content = "完成", Width = 88, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            var btnCancel = new Button { Content = "取消", Width = 72, IsCancel = true };
            btnConfirm.Click += (_, _) => CaptureCurrentFrame();
            _btnComplete.Click += (_, _) =>
            {
                if (_savedPaths.Count < _minFrames)
                {
                    MessageBox.Show(this, $"至少需要 {_minFrames} 张，当前 {_savedPaths.Count} 张。", "未完成采集",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                DialogResult = true;
                Close();
            };
            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
            bottomBtns.Children.Add(btnConfirm);
            bottomBtns.Children.Add(_btnComplete);
            bottomBtns.Children.Add(btnCancel);
            DockPanel.SetDock(bottomBtns, Dock.Bottom);
            right.Children.Add(bottomBtns);

            _previewImage = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var previewHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                Child = _previewImage
            };
            right.Children.Add(previewHost);

            root.Children.Add(left);
            root.Children.Add(right);
            Content = root;

            Closed += (_, _) => StopCamera();

            _camera.FrameGrabbed += OnCameraFrameGrabbed;
            if (!_camera.StartGrabbing())
                throw new InvalidOperationException("相机开始预览失败");
        }

        private void OnCameraFrameGrabbed(IFrameOut frameOut)
        {
            var layout = GetFramePixelLayout(frameOut);
            if (layout == CameraFramePixelLayout.Unsupported)
                return;

            int width = (int)frameOut.Image.Width;
            int height = (int)frameOut.Image.Height;
            int needBytes = layout == CameraFramePixelLayout.Mono8
                ? width * height
                : width * height * 3;

            lock (_frameLock)
            {
                if (_fullBuffer == null || _fullBuffer.Length < needBytes)
                    _fullBuffer = new byte[needBytes];

                if (!TryCopyFramePixels(frameOut, _fullBuffer, out width, out height, out layout))
                    return;

                _fullWidth = width;
                _fullHeight = height;
                _fullLayout = layout;
                _frameGeneration++;
            }

            SchedulePreviewPump();
        }

        private void SchedulePreviewPump()
        {
            if (Interlocked.CompareExchange(ref _uiPumpScheduled, 1, 0) != 0)
                return;

            Dispatcher.BeginInvoke(PumpPreviewToUi, DispatcherPriority.Render);
        }

        private void PumpPreviewToUi()
        {
            int genAtStart = Volatile.Read(ref _frameGeneration);
            try
            {
                byte[]? full;
                int fw, fh;
                CameraFramePixelLayout layout;
                lock (_frameLock)
                {
                    if (_fullBuffer == null || _fullLayout == CameraFramePixelLayout.Unsupported)
                        return;
                    full = _fullBuffer;
                    fw = _fullWidth;
                    fh = _fullHeight;
                    layout = _fullLayout;
                }

                int pw = fw;
                int ph = fh;
                if (pw > PreviewMaxWidth)
                {
                    pw = PreviewMaxWidth;
                    ph = Math.Max(1, fh * pw / fw);
                }

                bool isMono = layout == CameraFramePixelLayout.Mono8;
                int previewStride = isMono ? pw : pw * 3;
                int previewBytes = previewStride * ph;
                if (_previewBuffer == null || _previewBuffer.Length < previewBytes)
                    _previewBuffer = new byte[previewBytes];

                DownsampleFrame(full, fw, fh, isMono, _previewBuffer, pw, ph);

                if (_previewBitmap == null || _previewWidth != pw || _previewHeight != ph || !IsSamePixelFormat(_previewBitmap, isMono))
                {
                    _previewWidth = pw;
                    _previewHeight = ph;
                    _previewBitmap = new WriteableBitmap(
                        pw, ph, 96, 96,
                        isMono ? PixelFormats.Gray8 : PixelFormats.Bgr24,
                        null);
                    _previewImage.Source = _previewBitmap;
                }

                _previewBitmap.WritePixels(
                    new Int32Rect(0, 0, pw, ph),
                    _previewBuffer,
                    previewStride,
                    0);
            }
            finally
            {
                Interlocked.Exchange(ref _uiPumpScheduled, 0);
                if (Volatile.Read(ref _frameGeneration) != genAtStart)
                    SchedulePreviewPump();
            }
        }

        private void CaptureCurrentFrame()
        {
            byte[]? snapshot;
            int w, h;
            CameraFramePixelLayout layout;
            lock (_frameLock)
            {
                if (_fullBuffer == null || _fullLayout == CameraFramePixelLayout.Unsupported)
                {
                    MessageBox.Show(this, "尚无预览帧，请稍候或检查相机。", "无法保存",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int bytes = _fullLayout == CameraFramePixelLayout.Mono8
                    ? _fullWidth * _fullHeight
                    : _fullWidth * _fullHeight * 3;
                snapshot = new byte[bytes];
                Buffer.BlockCopy(_fullBuffer, 0, snapshot, 0, bytes);
                w = _fullWidth;
                h = _fullHeight;
                layout = _fullLayout;
            }

            using var toSave = CreateCalibImageFromPixels(snapshot, w, h, layout);
            if (toSave == null)
            {
                MessageBox.Show(this, "无法构造保存图像。", "保存错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            int index = _savedPaths.Count + 1;
            string fileName = $"{_namePrefix}{index:D3}{_fileExtension}";
            string fullPath = Path.Combine(_saveDirectory, fileName);
            if (!CalibAPI.SaveImage(fullPath, toSave))
            {
                MessageBox.Show(this, $"保存失败: {fullPath}", "保存错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _savedPaths.Add(Path.GetFullPath(fullPath));
            _capturedList.Items.Add(fileName);
            _statusText.Text = BuildStatusText();
            _btnComplete.IsEnabled = _savedPaths.Count >= _minFrames;
        }

        private void StopCamera()
        {
            _camera.FrameGrabbed -= OnCameraFrameGrabbed;
            if (_camera.IsGrabbing)
                _camera.StopGrabbing();
            _camera.Dispose();
        }

        private string BuildStatusText() =>
            $"已采集 {_savedPaths.Count} / 至少 {_minFrames} 张\n保存目录:\n{_saveDirectory}";

        private static string NormalizeExtension(string ext)
        {
            ext = (ext ?? ".bmp").Trim();
            if (string.IsNullOrEmpty(ext))
                return ".bmp";
            return ext.StartsWith(".", StringComparison.Ordinal) ? ext : "." + ext;
        }

        private static bool IsSamePixelFormat(WriteableBitmap bitmap, bool isMono) =>
            isMono ? bitmap.Format == PixelFormats.Gray8 : bitmap.Format == PixelFormats.Bgr24;

        /// <summary>最近邻降采样到预览尺寸（Mono8 或 BGR24 紧密排列）。</summary>
        private static void DownsampleFrame(
            byte[] src,
            int srcW,
            int srcH,
            bool isMono,
            byte[] dst,
            int dstW,
            int dstH)
        {
            if (srcW == dstW && srcH == dstH)
            {
                int bytes = isMono ? srcW * srcH : srcW * srcH * 3;
                Buffer.BlockCopy(src, 0, dst, 0, bytes);
                return;
            }

            if (isMono)
            {
                for (int y = 0; y < dstH; y++)
                {
                    int sy = y * srcH / dstH;
                    int dstRow = y * dstW;
                    int srcRow = sy * srcW;
                    for (int x = 0; x < dstW; x++)
                    {
                        int sx = x * srcW / dstW;
                        dst[dstRow + x] = src[srcRow + sx];
                    }
                }

                return;
            }

            for (int y = 0; y < dstH; y++)
            {
                int sy = y * srcH / dstH;
                int dstRow = y * dstW * 3;
                int srcRow = sy * srcW * 3;
                for (int x = 0; x < dstW; x++)
                {
                    int sx = x * srcW / dstW;
                    int si = srcRow + sx * 3;
                    int di = dstRow + x * 3;
                    dst[di + 0] = src[si + 0];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                }
            }
        }
    }
}

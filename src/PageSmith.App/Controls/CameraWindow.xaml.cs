using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Wpf.Ui.Controls;

namespace PageSmith.App.Controls;

/// <summary>Live camera preview for photographing documents; each photo is saved as a JPEG for Scan to PDF.</summary>
public partial class CameraWindow : FluentWindow
{
    private readonly object _frameLock = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private WriteableBitmap? _preview;
    private byte[]? _frame;
    private int _frameWidth, _frameHeight;
    private bool _previewPending;
    private bool _loadingCameras;

    public CameraWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadCamerasAsync();
        Closed += async (_, _) => await StopAsync();
    }

    public List<string> Captured { get; } = [];

    /// <summary>Opens the camera window; returns the photos taken (possibly none).</summary>
    public static IReadOnlyList<string> Show(Window? owner)
    {
        var window = new CameraWindow { Owner = owner };
        window.ShowDialog();
        return window.Captured;
    }

    private async Task LoadCamerasAsync()
    {
        _loadingCameras = true;
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var cameras = groups.Where(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color)).ToList();
            if (cameras.Count == 0)
            {
                ShowMessage("No camera was found. Connect a webcam, or use your phone's camera and add the photos instead.");
                return;
            }
            Cameras.ItemsSource = cameras;
            // Prefer a rear camera on tablets: it is the one pointed at paper.
            Cameras.SelectedItem = cameras.FirstOrDefault(c => c.SourceInfos.Any(s => s.DeviceInformation?.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Back)) ?? cameras[0];
        }
        catch (Exception ex)
        {
            ShowMessage($"The camera could not be listed: {ex.Message}");
        }
        finally
        {
            _loadingCameras = false;
        }
        await StartAsync();
    }

    private async void OnCameraChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingCameras) return;
        await StopAsync();
        await StartAsync();
    }

    private async Task StartAsync()
    {
        if (Cameras.SelectedItem is not MediaFrameSourceGroup group) return;
        Starting.Visibility = Visibility.Visible;
        MessagePanel.Visibility = Visibility.Collapsed;
        CaptureButton.IsEnabled = false;
        try
        {
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
            var info = group.SourceInfos.First(s => s.SourceKind == MediaFrameSourceKind.Color);
            var source = _capture.FrameSources[info.Id];
            // The sharpest format up to 4K, for legible text.
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width <= 3840)
                .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
                .FirstOrDefault();
            if (format != null) await source.SetFormatAsync(format);
            _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrameArrived;
            var status = await _reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                ShowMessage(status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                    ? "Another app is using the camera. Close it and try again."
                    : $"The camera could not start ({status}).");
                return;
            }
            CaptureButton.IsEnabled = true;
        }
        catch (UnauthorizedAccessException)
        {
            ShowMessage("Windows is blocking camera access for desktop apps. Turn on \"Let desktop apps access your camera\" in privacy settings, then reopen this window.", privacy: true);
        }
        catch (Exception ex)
        {
            ShowMessage($"The camera could not start: {ex.Message}");
        }
        finally
        {
            Starting.Visibility = Visibility.Collapsed;
        }
    }

    private async Task StopAsync()
    {
        CaptureButton.IsEnabled = false;
        if (_reader != null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { /* already stopped */ }
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
    }

    private void ShowMessage(string text, bool privacy = false)
    {
        Starting.Visibility = Visibility.Collapsed;
        MessageText.Text = text;
        PrivacyButton.Visibility = privacy ? Visibility.Visible : Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
        CaptureButton.IsEnabled = false;
    }

    private void OnOpenPrivacy(object sender, RoutedEventArgs e) => AppServices.OpenUri("ms-settings:privacy-webcam");

    // ------------------------------------------------------------------ frames (camera thread)

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var video = frame?.VideoMediaFrame;
        if (video == null) return;
        SoftwareBitmap? bitmap = video.SoftwareBitmap;
        var owned = false;
        if (bitmap == null && video.Direct3DSurface != null)
        {
            bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(video.Direct3DSurface).AsTask().GetAwaiter().GetResult();
            owned = true;
        }
        if (bitmap == null) return;
        try
        {
            using var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyToBuffer(bytes.AsBuffer());
            lock (_frameLock)
            {
                _frame = bytes;
                _frameWidth = bgra.PixelWidth;
                _frameHeight = bgra.PixelHeight;
                if (_previewPending) return;
                _previewPending = true;
            }
            Dispatcher.BeginInvoke(ShowLatestFrame);
        }
        finally
        {
            if (owned) bitmap.Dispose();
        }
    }

    private void ShowLatestFrame()
    {
        byte[]? bytes;
        int w, h;
        lock (_frameLock)
        {
            _previewPending = false;
            (bytes, w, h) = (_frame, _frameWidth, _frameHeight);
        }
        if (bytes == null) return;
        if (_preview == null || _preview.PixelWidth != w || _preview.PixelHeight != h)
        {
            _preview = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            Preview.Source = _preview;
        }
        _preview.WritePixels(new Int32Rect(0, 0, w, h), bytes, w * 4, 0);
        Starting.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ capture

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        byte[]? bytes;
        int w, h;
        lock (_frameLock) (bytes, w, h) = (_frame, _frameWidth, _frameHeight);
        if (bytes == null) return;
        var photo = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bytes, w * 4);
        var path = ScanSources.NewFileName("Photo");
        File.WriteAllBytes(path, ImageIO.EncodeJpeg(photo, 92));
        Captured.Add(path);
        CountText.Text = Captured.Count == 1 ? "1 photo" : $"{Captured.Count} photos";
        Flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, 0, TimeSpan.FromMilliseconds(260)));
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}

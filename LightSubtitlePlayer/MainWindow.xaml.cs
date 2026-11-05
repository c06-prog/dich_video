using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LightSubtitlePlayer
{
    public partial class MainWindow : Window
    {
        private readonly List<SubtitleItem> _subtitles = new();
        private readonly DispatcherTimer _playbackTimer = new();
        private readonly DispatcherTimer _watermarkFloatTimer = new();

        private string? _currentVideoPath = null;
        private bool _isDraggingSlider = false;
        private bool _isDraggingBlur = false;
        private Border? _activeDragBox = null;
        private Point _dragStartPoint;
        private double _origLeft, _origTop;
        private bool _isUpdatingAttributionText = false;
        private bool _isSyncingStyleControls = false;
        private bool _isSyncingHideCheck = false;

        // Trạng thái theo dõi câu phụ đề đang được đọc bởi AI để không phát lặp
        private int _lastSpokenSubtitleIndex = -1;
        private bool _isTtsPlaying = false;

        // Lưu trữ cấu hình kiểu chữ, màu chữ & nền độc lập cho Phụ đề, Credit và Miễn trừ trách nhiệm
        public class BoxStyleState
        {
            public int FontIndex { get; set; } = 0;
            public int TextColorIndex { get; set; } = 0;
            public double FontSize { get; set; } = 26;
            public double BgOpacity { get; set; } = 0.5;
            public int BgColorIndex { get; set; } = 0;
        }

        private readonly BoxStyleState _subtitleStyle = new() { FontIndex = 0, TextColorIndex = 0, FontSize = 26, BgOpacity = 0.5, BgColorIndex = 0 };
        private readonly BoxStyleState _creditStyle = new() { FontIndex = 0, TextColorIndex = 0, FontSize = 18, BgOpacity = 0.5, BgColorIndex = 0 };
        private readonly BoxStyleState _disclaimerStyle = new() { FontIndex = 0, TextColorIndex = 0, FontSize = 13, BgOpacity = 0.5, BgColorIndex = 0 };

        // Vận tốc & vị trí Watermark chạy tự do
        private double _wmVelocityX = 2.0;
        private double _wmVelocityY = 1.5;
        private double _wmPosX = 20.0;
        private double _wmPosY = 20.0;

        public MainWindow()
        {
            InitializeComponent();

            // Timer đồng bộ thời gian phát video (~16 FPS, tiết kiệm CPU cho máy yếu)
            _playbackTimer.Interval = TimeSpan.FromMilliseconds(60);
            _playbackTimer.Tick += PlaybackTimer_Tick;

            // Timer di chuyển Watermark
            _watermarkFloatTimer.Interval = TimeSpan.FromMilliseconds(40);
            _watermarkFloatTimer.Tick += WatermarkFloatTimer_Tick;

            Loaded += async (s, e) =>
            {
                UpdateAttributionDisplay();
                CenterAllBoxesInitial();

                // Tự động kiểm tra và tải ngầm yt-dlp.exe, ffmpeg.exe nếu máy khách chưa có
                await DependencyBootstrapper.EnsureToolsReadyAsync(new Progress<DownloadProgress>(p =>
                {
                    PbDownload.Visibility = p.Percent < 100 ? Visibility.Visible : Visibility.Collapsed;
                    PbDownload.IsIndeterminate = false;
                    PbDownload.Value = p.Percent;
                    LblStatus.Text = p.Message;
                }));
            };
        }

        // ================= TRÌNH PHÁT VIDEO =================
        private void BtnOpenVideo_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new()
            {
                Filter = "Video Files (*.mp4;*.wmv;*.avi;*.webm)|*.mp4;*.wmv;*.avi;*.webm|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                LoadLocalVideo(dlg.FileName);
            }
        }

        private void LoadLocalVideo(string filePath)
        {
            _currentVideoPath = filePath;
            VideoPlayer.Source = new Uri(filePath);
            VideoPlayer.Volume = 1.0;
            VideoPlayer.Play();
            _lastSpokenSubtitleIndex = -1;
            _playbackTimer.Start();
            LblStatus.Text = $"Đang phát: {System.IO.Path.GetFileName(filePath)}";
        }

        private void VideoPlayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (VideoPlayer.Source == null) return;
            if (_playbackTimer.IsEnabled)
            {
                VideoPlayer.Pause();
                TtsAudioPlayer.Pause();
                _playbackTimer.Stop();
                LblStatus.Text = "Tạm dừng";
            }
            else
            {
                VideoPlayer.Play();
                if (_isTtsPlaying) TtsAudioPlayer.Play();
                _playbackTimer.Start();
                LblStatus.Text = "Đang phát";
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.Source != null)
            {
                VideoPlayer.Play();
                if (_isTtsPlaying) TtsAudioPlayer.Play();
                _playbackTimer.Start();
                LblStatus.Text = "Đang phát";
            }
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Pause();
            TtsAudioPlayer.Pause();
            _playbackTimer.Stop();
            LblStatus.Text = "Tạm dừng";
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Stop();
            TtsAudioPlayer.Stop();
            _isTtsPlaying = false;
            _lastSpokenSubtitleIndex = -1;
            _playbackTimer.Stop();
            TimelineSlider.Value = 0;
            TxtSubtitleRender.Text = string.Empty;
            CreditContainer.Visibility = Visibility.Collapsed;
            DisclaimerContainer.Visibility = Visibility.Collapsed;
            LblStatus.Text = "Đã dừng";
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.Source == null) return;
            _lastSpokenSubtitleIndex = -1;
            VideoPlayer.Position = VideoPlayer.Position - TimeSpan.FromSeconds(5);
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.Source == null) return;
            _lastSpokenSubtitleIndex = -1;
            VideoPlayer.Position = VideoPlayer.Position + TimeSpan.FromSeconds(5);
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // ================= TÍNH NĂNG ẨN / HIỆN PHỤ ĐỀ =================
        private void MenuToggleSubtitle_Click(object sender, RoutedEventArgs e)
        {
            ChkHideSubtitle.IsChecked = !(ChkHideSubtitle.IsChecked == true);
        }

        private void ChkHideSubtitle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isSyncingHideCheck) return;
            _isSyncingHideCheck = true;

            bool isHidden = (sender == ChkHideSubtitle)
                ? (ChkHideSubtitle.IsChecked == true)
                : (ChkHideSubtitleTab.IsChecked == true);

            if (ChkHideSubtitle != null) ChkHideSubtitle.IsChecked = isHidden;
            if (ChkHideSubtitleTab != null) ChkHideSubtitleTab.IsChecked = isHidden;

            if (SubtitleBox != null)
            {
                SubtitleBox.Visibility = isHidden ? Visibility.Collapsed : Visibility.Visible;
            }

            if (LblStatus != null)
            {
                LblStatus.Text = isHidden ? "Đã ẩn phụ đề trên video (Giọng đọc AI vẫn hoạt động)." : "Đã hiện phụ đề trên video.";
            }

            _isSyncingHideCheck = false;
        }

        // ================= ĐỒNG BỘ TIẾN TRÌNH THỜI GIAN & PHÁT GIỌNG AI =================
        private void PlaybackTimer_Tick(object? sender, EventArgs e)
        {
            if (VideoPlayer.Source == null || !VideoPlayer.NaturalDuration.HasTimeSpan) return;

            TimeSpan cur = VideoPlayer.Position;
            TimeSpan total = VideoPlayer.NaturalDuration.TimeSpan;

            if (!_isDraggingSlider)
            {
                TimelineSlider.Maximum = total.TotalSeconds;
                TimelineSlider.Value = cur.TotalSeconds;
                LblTimeProgress.Text = cur.ToString(@"hh\:mm\:ss");
                LblTotalTime.Text = total.ToString(@"hh\:mm\:ss");
            }

            // Xử lý Credit & Disclaimer theo mốc thời gian đã cài đặt
            double startSec = double.TryParse(InputCreditStart.Text, out double s) ? s : 0;
            double endSec = double.TryParse(InputCreditEnd.Text, out double ed) ? ed : 5;
            bool isInRange = cur.TotalSeconds >= startSec && cur.TotalSeconds <= endSec;

            bool showCredit = ChkShowCredit.IsChecked == true && isInRange;
            bool showDisclaimer = ChkShowDisclaimer.IsChecked == true && isInRange;

            if (showCredit && CreditContainer.Visibility != Visibility.Visible)
            {
                CreditContainer.Visibility = Visibility.Visible;
                CreditContainer.UpdateLayout();
                if (double.IsNaN(Canvas.GetLeft(CreditContainer)))
                    CenterHorizontalBox(CreditContainer, 65);
            }
            else if (!showCredit && CreditContainer.Visibility == Visibility.Visible)
            {
                CreditContainer.Visibility = Visibility.Collapsed;
            }

            if (showDisclaimer && DisclaimerContainer.Visibility != Visibility.Visible)
            {
                DisclaimerContainer.Visibility = Visibility.Visible;
                DisclaimerContainer.UpdateLayout();
                if (double.IsNaN(Canvas.GetLeft(DisclaimerContainer)))
                    CenterHorizontalBox(DisclaimerContainer, 15);
            }
            else if (!showDisclaimer && DisclaimerContainer.Visibility == Visibility.Visible)
            {
                DisclaimerContainer.Visibility = Visibility.Collapsed;
            }

            // Xử lý hiển thị phụ đề sạch (đã lọc thẻ AI) & kích hoạt giọng đọc Neural AI
            if (_subtitles.Count > 0)
            {
                SubtitleItem? activeSub = null;
                foreach (var item in _subtitles)
                {
                    if (cur >= item.Start && cur <= item.End)
                    {
                        activeSub = item;
                        break;
                    }
                }

                if (activeSub != null)
                {
                    TxtSubtitleRender.Text = activeSub.CleanDisplayText;

                    // Nếu câu phụ đề này đã có file giọng đọc AI và chưa được phát trong lượt này
                    if (ChkEnableVoiceover.IsChecked == true &&
                        activeSub.Index != _lastSpokenSubtitleIndex &&
                        !string.IsNullOrEmpty(activeSub.AudioFilePath) &&
                        File.Exists(activeSub.AudioFilePath))
                    {
                        _lastSpokenSubtitleIndex = activeSub.Index;
                        PlaySubtitleVoice(activeSub.AudioFilePath);
                    }
                }
                else
                {
                    TxtSubtitleRender.Text = string.Empty;
                }
            }
        }

        private void PlaySubtitleVoice(string audioPath)
        {
            try
            {
                double duckVol = (SliderVideoDucking?.Value ?? 25.0) / 100.0;
                VideoPlayer.Volume = duckVol;

                TtsAudioPlayer.Stop();
                TtsAudioPlayer.Source = new Uri(audioPath);
                TtsAudioPlayer.Volume = 1.0;
                TtsAudioPlayer.Play();
                _isTtsPlaying = true;
            }
            catch
            {
                // Bỏ qua nếu file âm thanh đang bận
            }
        }

        private void TtsAudioPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            _isTtsPlaying = false;
            VideoPlayer.Volume = 1.0;
        }

        private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void TimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            _lastSpokenSubtitleIndex = -1;
            VideoPlayer.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSlider)
            {
                LblTimeProgress.Text = TimeSpan.FromSeconds(TimelineSlider.Value).ToString(@"hh\:mm\:ss");
            }
        }

        // ================= KÉO THẢ & TỰ HÍT TÂM CHO CẢ PHỤ ĐỀ, CREDIT & DISCLAIMER =================
        private void DraggableBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border box)
            {
                _activeDragBox = box;
                _dragStartPoint = e.GetPosition(OverlayCanvas);
                _origLeft = Canvas.GetLeft(box);
                _origTop = Canvas.GetTop(box);
                if (double.IsNaN(_origLeft)) _origLeft = (OverlayCanvas.ActualWidth - box.ActualWidth) / 2.0;
                if (double.IsNaN(_origTop)) _origTop = 50;

                if (CmbStyleTarget != null && CmbStyleTarget.SelectedIndex != 3)
                {
                    if (box == SubtitleBox) CmbStyleTarget.SelectedIndex = 0;
                    else if (box == CreditContainer) CmbStyleTarget.SelectedIndex = 1;
                    else if (box == DisclaimerContainer) CmbStyleTarget.SelectedIndex = 2;
                }

                box.CaptureMouse();
                ShowGuideLines();
            }
        }

        private void DraggableBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_activeDragBox == null) return;

            Point currentPoint = e.GetPosition(OverlayCanvas);
            double deltaX = currentPoint.X - _dragStartPoint.X;
            double deltaY = currentPoint.Y - _dragStartPoint.Y;

            double newLeft = _origLeft + deltaX;
            double newTop = _origTop + deltaY;

            newLeft = Math.Max(0, Math.Min(newLeft, OverlayCanvas.ActualWidth - _activeDragBox.ActualWidth));
            newTop = Math.Max(0, Math.Min(newTop, OverlayCanvas.ActualHeight - _activeDragBox.ActualHeight));

            double canvasMidX = OverlayCanvas.ActualWidth / 2.0;
            double boxMidX = newLeft + (_activeDragBox.ActualWidth / 2.0);
            if (Math.Abs(canvasMidX - boxMidX) < 15.0)
            {
                newLeft = canvasMidX - (_activeDragBox.ActualWidth / 2.0);
                GuideLineVertical.Stroke = new SolidColorBrush(Color.FromRgb(78, 201, 176));
            }
            else
            {
                GuideLineVertical.Stroke = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            }

            double canvasMidY = OverlayCanvas.ActualHeight / 2.0;
            double boxMidY = newTop + (_activeDragBox.ActualHeight / 2.0);
            if (Math.Abs(canvasMidY - boxMidY) < 15.0)
            {
                newTop = canvasMidY - (_activeDragBox.ActualHeight / 2.0);
                GuideLineHorizontal.Stroke = new SolidColorBrush(Color.FromRgb(78, 201, 176));
            }
            else
            {
                GuideLineHorizontal.Stroke = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            }

            Canvas.SetLeft(_activeDragBox, newLeft);
            Canvas.SetTop(_activeDragBox, newTop);
        }

        private void DraggableBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeDragBox != null)
            {
                _activeDragBox.ReleaseMouseCapture();
                _activeDragBox = null;
                HideGuideLines();
            }
        }

        private void ShowGuideLines()
        {
            double midX = OverlayCanvas.ActualWidth / 2.0;
            double midY = OverlayCanvas.ActualHeight / 2.0;

            GuideLineVertical.X1 = midX;
            GuideLineVertical.Y1 = 0;
            GuideLineVertical.X2 = midX;
            GuideLineVertical.Y2 = OverlayCanvas.ActualHeight;

            GuideLineHorizontal.X1 = 0;
            GuideLineHorizontal.Y1 = midY;
            GuideLineHorizontal.X2 = OverlayCanvas.ActualWidth;
            GuideLineHorizontal.Y2 = midY;

            GuideLineVertical.Visibility = Visibility.Visible;
            GuideLineHorizontal.Visibility = Visibility.Visible;
        }

        private void HideGuideLines()
        {
            GuideLineVertical.Visibility = Visibility.Collapsed;
            GuideLineHorizontal.Visibility = Visibility.Collapsed;
        }

        private void CenterHorizontalBox(FrameworkElement box, double topPos)
        {
            if (OverlayCanvas.ActualWidth <= 0) return;
            box.UpdateLayout();
            double boxWidth = box.ActualWidth > 0 ? box.ActualWidth : 300;
            double left = (OverlayCanvas.ActualWidth - boxWidth) / 2.0;
            Canvas.SetLeft(box, Math.Max(0, left));
            Canvas.SetTop(box, Math.Max(0, topPos));
        }

        private void CenterSubtitle()
        {
            if (OverlayCanvas.ActualWidth <= 0 || SubtitleBox.ActualWidth <= 0) return;
            double left = (OverlayCanvas.ActualWidth - SubtitleBox.ActualWidth) / 2.0;
            double top = OverlayCanvas.ActualHeight - SubtitleBox.ActualHeight - 50.0;
            Canvas.SetLeft(SubtitleBox, Math.Max(0, left));
            Canvas.SetTop(SubtitleBox, Math.Max(0, top));
        }

        private void CenterAllBoxesInitial()
        {
            CenterSubtitle();
            CenterHorizontalBox(DisclaimerContainer, 15);
            CenterHorizontalBox(CreditContainer, 65);
        }

        private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            CenterSubtitle();
            if (CreditContainer.ActualWidth > 0)
                Canvas.SetLeft(CreditContainer, Math.Max(0, (OverlayCanvas.ActualWidth - CreditContainer.ActualWidth) / 2.0));
            if (DisclaimerContainer.ActualWidth > 0)
                Canvas.SetLeft(DisclaimerContainer, Math.Max(0, (OverlayCanvas.ActualWidth - DisclaimerContainer.ActualWidth) / 2.0));
        }

        private void BtnCenterSelectedTarget_Click(object sender, RoutedEventArgs e)
        {
            int idx = CmbStyleTarget?.SelectedIndex ?? 0;
            if (idx == 0 || idx == 3) CenterSubtitle();
            if (idx == 1 || idx == 3) CenterHorizontalBox(CreditContainer, Canvas.GetTop(CreditContainer));
            if (idx == 2 || idx == 3) CenterHorizontalBox(DisclaimerContainer, Canvas.GetTop(DisclaimerContainer));
        }

        // ================= VÙNG CHE MỜ (KHÔNG VIỀN VÀNG, CHỈNH KÍCH THƯỚC) =================
        private void ToggleBlurBox_Click(object sender, RoutedEventArgs e)
        {
            BlurBox.Visibility = (BlurBox.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
            LblStatus.Text = (BlurBox.Visibility == Visibility.Visible) ? "Đã bật vùng che mờ." : "Đã tắt vùng che mờ.";
        }

        private void SliderBlurSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BlurBox == null || LblBlurWidth == null || LblBlurHeight == null) return;
            BlurBox.Width = SliderBlurWidth.Value;
            BlurBox.Height = SliderBlurHeight.Value;
            LblBlurWidth.Text = $"{SliderBlurWidth.Value:0} px";
            LblBlurHeight.Text = $"{SliderBlurHeight.Value:0} px";
        }

        private void BlurBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingBlur = true;
            _dragStartPoint = e.GetPosition(OverlayCanvas);
            _origLeft = Canvas.GetLeft(BlurBox);
            _origTop = Canvas.GetTop(BlurBox);
            BlurBox.CaptureMouse();
        }

        private void BlurBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingBlur) return;
            Point pt = e.GetPosition(OverlayCanvas);
            Canvas.SetLeft(BlurBox, _origLeft + (pt.X - _dragStartPoint.X));
            Canvas.SetTop(BlurBox, _origTop + (pt.Y - _dragStartPoint.Y));
        }

        private void BlurBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingBlur)
            {
                _isDraggingBlur = false;
                BlurBox.ReleaseMouseCapture();
            }
        }

        // ================= ĐỊNH DẠNG KIỂU CHỮ, MÀU CHỮ & NỀN =================
        private IEnumerable<(Border Box, TextBlock Text, BoxStyleState State)> GetSelectedStyleTargets()
        {
            int idx = CmbStyleTarget?.SelectedIndex ?? 0;
            if (idx == 0 || idx == 3) yield return (SubtitleBox, TxtSubtitleRender, _subtitleStyle);
            if (idx == 1 || idx == 3) yield return (CreditContainer, TxtCreditContent, _creditStyle);
            if (idx == 2 || idx == 3) yield return (DisclaimerContainer, TxtDisclaimerContent, _disclaimerStyle);
        }

        private void CmbStyleTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFont == null || CmbTextColor == null || SliderFontSize == null || SliderBgOpacity == null || CmbBgColor == null) return;

            int idx = CmbStyleTarget.SelectedIndex;
            BoxStyleState state = idx switch
            {
                1 => _creditStyle,
                2 => _disclaimerStyle,
                _ => _subtitleStyle
            };

            _isSyncingStyleControls = true;
            CmbFont.SelectedIndex = state.FontIndex;
            CmbTextColor.SelectedIndex = state.TextColorIndex;
            SliderFontSize.Value = state.FontSize;
            LblFontSize.Text = $"{state.FontSize:0} px";
            SliderBgOpacity.Value = state.BgOpacity;
            LblBgOpacity.Text = $"{state.BgOpacity * 100:0}%";
            CmbBgColor.SelectedIndex = state.BgColorIndex;
            _isSyncingStyleControls = false;
        }

        private void CmbFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingStyleControls || TxtSubtitleRender == null) return;
            if (CmbFont.SelectedItem is ComboBoxItem item)
            {
                string fontName = item.Content.ToString() ?? "Segoe UI";
                foreach (var target in GetSelectedStyleTargets())
                {
                    target.State.FontIndex = CmbFont.SelectedIndex;
                    target.Text.FontFamily = new FontFamily(fontName);
                }
            }
        }

        private void CmbTextColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingStyleControls || TxtSubtitleRender == null) return;

            foreach (var target in GetSelectedStyleTargets())
            {
                target.State.TextColorIndex = CmbTextColor.SelectedIndex;
                ApplyTextColor(target.Text, target.State.TextColorIndex);
            }
        }

        private static void ApplyTextColor(TextBlock textBlock, int colorIndex)
        {
            Color textColor = colorIndex switch
            {
                1 => Color.FromRgb(255, 215, 0),   // Vàng Rạp Phim (Cinema Gold)
                2 => Color.FromRgb(0, 255, 255),   // Xanh Lơ (Cyan)
                3 => Color.FromRgb(80, 250, 123),  // Xanh Lá (Neon Green)
                4 => Color.FromRgb(255, 182, 193), // Hồng Phấn (Pastel Pink)
                5 => Color.FromRgb(255, 165, 0),   // Cam Sáng (Orange)
                6 => Color.FromRgb(255, 85, 85),   // Đỏ Tươi (Crimson Red)
                _ => Colors.White                  // Trắng mặc định
            };
            textBlock.Foreground = new SolidColorBrush(textColor);
        }

        private void SliderFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblFontSize != null) LblFontSize.Text = $"{SliderFontSize.Value:0} px";
            if (_isSyncingStyleControls || TxtSubtitleRender == null) return;

            foreach (var target in GetSelectedStyleTargets())
            {
                target.State.FontSize = SliderFontSize.Value;
                target.Text.FontSize = SliderFontSize.Value;
            }
        }

        private void SliderBgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblBgOpacity != null) LblBgOpacity.Text = $"{SliderBgOpacity.Value * 100:0}%";
            if (_isSyncingStyleControls || SubtitleBox == null) return;

            foreach (var target in GetSelectedStyleTargets())
            {
                target.State.BgOpacity = SliderBgOpacity.Value;
                ApplyBoxBackground(target.Box, target.State);
            }
        }

        private void CmbBgColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingStyleControls || SubtitleBox == null) return;

            foreach (var target in GetSelectedStyleTargets())
            {
                target.State.BgColorIndex = CmbBgColor.SelectedIndex;
                ApplyBoxBackground(target.Box, target.State);
            }
        }

        private static void ApplyBoxBackground(Border box, BoxStyleState state)
        {
            byte alpha = (byte)(state.BgOpacity * 255);
            Color baseColor = state.BgColorIndex switch
            {
                1 => Color.FromRgb(30, 30, 45),
                2 => Color.FromRgb(45, 45, 48),
                3 => Color.FromRgb(90, 20, 20),
                _ => Colors.Black
            };
            box.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        }

        // ================= WATERMARK BẢN QUYỀN =================
        private void TxtInputWatermark_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtWatermark != null) TxtWatermark.Text = TxtInputWatermark.Text;
        }

        private void SliderWatermarkOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtWatermark != null) TxtWatermark.Opacity = SliderWatermarkOpacity.Value;
        }

        private void ChkFloatingWatermark_Checked(object sender, RoutedEventArgs e)
        {
            Canvas.SetRight(TxtWatermark, double.NaN);
            Canvas.SetLeft(TxtWatermark, _wmPosX);
            Canvas.SetTop(TxtWatermark, _wmPosY);
            _watermarkFloatTimer.Start();
        }

        private void ChkFloatingWatermark_Unchecked(object sender, RoutedEventArgs e)
        {
            _watermarkFloatTimer.Stop();
            Canvas.SetLeft(TxtWatermark, double.NaN);
            Canvas.SetRight(TxtWatermark, 20);
            Canvas.SetTop(TxtWatermark, 20);
        }

        private void WatermarkFloatTimer_Tick(object? sender, EventArgs e)
        {
            if (OverlayCanvas.ActualWidth <= 0 || OverlayCanvas.ActualHeight <= 0) return;

            _wmPosX += _wmVelocityX;
            _wmPosY += _wmVelocityY;

            double maxX = OverlayCanvas.ActualWidth - TxtWatermark.ActualWidth - 10;
            double maxY = OverlayCanvas.ActualHeight - TxtWatermark.ActualHeight - 10;

            if (_wmPosX <= 10 || _wmPosX >= maxX) _wmVelocityX = -_wmVelocityX;
            if (_wmPosY <= 10 || _wmPosY >= maxY) _wmVelocityY = -_wmVelocityY;

            _wmPosX = Math.Max(10, Math.Min(_wmPosX, maxX));
            _wmPosY = Math.Max(10, Math.Min(_wmPosY, maxY));

            Canvas.SetLeft(TxtWatermark, _wmPosX);
            Canvas.SetTop(TxtWatermark, _wmPosY);
        }

        // ================= CẬP NHẬT CREDIT & MIỄN TRỪ TRÁCH NHIỆM =================
        private void UpdateAttributionTexts(object sender, TextChangedEventArgs e)
        {
            UpdateAttributionDisplay();
        }

        private void UpdateAttributionDisplay()
        {
            if (_isUpdatingAttributionText) return;
            _isUpdatingAttributionText = true;

            if (TxtCreditContent != null && TxtInputSubber != null && TxtInputAuthor != null)
            {
                string credit = $"Vietsub bởi {TxtInputSubber.Text} - Bản quyền video thuộc về kênh {TxtInputAuthor.Text}";
                TxtCreditContent.Text = credit;
                if (TxtCustomCredit != null) TxtCustomCredit.Text = credit;
            }
            if (TxtDisclaimerContent != null && TxtInputAuthor != null)
            {
                string disclaimer = $"Tất cả quyền hình ảnh và âm thanh thuộc về {TxtInputAuthor.Text}. Bản dịch tiếng Việt được thực hiện nhằm mục đích học tập/chia sẻ thông tin kiến thức.";
                TxtDisclaimerContent.Text = disclaimer;
                if (TxtCustomDisclaimer != null) TxtCustomDisclaimer.Text = disclaimer;
            }

            _isUpdatingAttributionText = false;
        }

        private void TxtCustomCredit_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingAttributionText || TxtCreditContent == null) return;
            TxtCreditContent.Text = TxtCustomCredit.Text;
        }

        private void TxtCustomDisclaimer_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingAttributionText || TxtDisclaimerContent == null) return;
            TxtDisclaimerContent.Text = TxtCustomDisclaimer.Text;
        }

        // ================= PHÂN TÍCH VÀ CẬP NHẬT PHỤ ĐỀ =================
        private void TxtSrtEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            ReloadSubtitlesFromEditor();
        }

        private void BtnApplySub_Click(object sender, RoutedEventArgs e)
        {
            ReloadSubtitlesFromEditor();
            MessageBox.Show($"Đã nạp thành công {_subtitles.Count} câu phụ đề!\n(Các ký hiệu [ngắt], [nhấn] đã được ẩn khỏi màn hình video và chuyển cho bộ đọc AI).", "Thông báo");
        }

        private void ReloadSubtitlesFromEditor()
        {
            _subtitles.Clear();
            string content = TxtSrtEditor.Text;
            if (string.IsNullOrWhiteSpace(content)) return;

            var items = SrtHelper.ParseSrt(content);
            _subtitles.AddRange(items);
            LblStatus.Text = $"Đã nhận diện {_subtitles.Count} câu phụ đề (Sẵn sàng tạo giọng đọc AI).";
        }

        // ================= CHUYỂN PHỤ ĐỀ THÀNH GIỌNG ĐỌC AI =================
        private void SliderTtsParams_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblTtsRate != null && SliderTtsRate != null)
                LblTtsRate.Text = $"{(SliderTtsRate.Value >= 0 ? "+" : "")}{SliderTtsRate.Value:0}%";
            if (LblTtsPitch != null && SliderTtsPitch != null)
                LblTtsPitch.Text = $"{(SliderTtsPitch.Value >= 0 ? "+" : "")}{SliderTtsPitch.Value:0} Hz";
            if (LblVideoDucking != null && SliderVideoDucking != null)
                LblVideoDucking.Text = $"{SliderVideoDucking.Value:0}%";
        }

        private TtsConfig GetCurrentTtsConfig()
        {
            int voiceIdx = CmbTtsVoice?.SelectedIndex ?? 0;
            bool isNaturalMode = RbReadNatural?.IsChecked == true;
            int baseRate = (int)(SliderTtsRate?.Value ?? 5);
            int basePitch = (int)(SliderTtsPitch?.Value ?? 0);

            string voiceId = "vi-VN-NamMinhNeural";
            if (voiceIdx == 1 || voiceIdx == 3) voiceId = "vi-VN-HoaiMyNeural";
            if (voiceIdx == 2) baseRate += 10;
            if (voiceIdx == 3) { baseRate -= 6; basePitch -= 3; }

            return new TtsConfig
            {
                VoiceName = voiceId,
                NaturalMode = isNaturalMode,
                BaseRatePercent = baseRate,
                BasePitchHz = basePitch,
                UseCustomApi = (voiceIdx == 4),
                ApiKey = TxtApiKey?.Password.Trim() ?? string.Empty
            };
        }

        private async void BtnGenerateVoice_Click(object sender, RoutedEventArgs e)
        {
            ReloadSubtitlesFromEditor();
            if (_subtitles.Count == 0)
            {
                MessageBox.Show("Chưa có nội dung phụ đề! Vui lòng dán phụ đề SRT vào tab 'AI & Dịch' trước.", "Thông báo");
                return;
            }

            TtsConfig config = GetCurrentTtsConfig();
            BtnGenerateVoice.IsEnabled = false;
            PbTtsGen.Visibility = Visibility.Visible;
            PbTtsGen.Value = 0;

            var progress = new Progress<DownloadProgress>(p =>
            {
                PbTtsGen.Value = p.Percent;
                LblStatus.Text = p.Message;
            });

            int successCount = await NeuralTtsHelper.GenerateAllVoicesAsync(_subtitles, config, progress);

            BtnGenerateVoice.IsEnabled = true;
            PbTtsGen.Visibility = Visibility.Collapsed;
            _lastSpokenSubtitleIndex = -1;

            if (successCount > 0)
            {
                LblStatus.Text = $"Đã tải xong {successCount}/{_subtitles.Count} file giọng đọc AI (.mp3)! Bấm Phát video hoặc Xuất MP4.";
                MessageBox.Show($"Đã tạo thành công {successCount}/{_subtitles.Count} câu lồng tiếng AI!\nChế độ: {(config.NaturalMode ? "Đọc Tự Nhiên (Nhấn nhá, cao thấp)" : "Đọc Bình Thường")}\n\nBạn có thể bấm ▶ Phát để xem trực tiếp hoặc bấm 🎬 Xuất MP4 để lưu thành video hoàn chỉnh!", "Hoàn tất lồng tiếng AI");
            }
            else
            {
                LblStatus.Text = "Tạo giọng đọc thất bại.";
                MessageBox.Show("Không thể tạo file âm thanh. Hãy đảm bảo máy có kết nối mạng và đã chạy lệnh: pip install edge-tts", "Hướng dẫn cài đặt");
            }
        }

        private async void BtnPreviewVoice_Click(object sender, RoutedEventArgs e)
        {
            ReloadSubtitlesFromEditor();
            SubtitleItem sample = _subtitles.Count > 0
                ? _subtitles[0]
                : SrtHelper.CreateSubtitleItem(1, TimeSpan.Zero, TimeSpan.FromSeconds(4), "Xin chào, [ngắt] đây là giọng đọc AI [nhấn: tự nhiên] chuẩn TikTok và YouTube.");

            LblStatus.Text = "Đang tạo giọng nghe thử...";
            TtsConfig config = GetCurrentTtsConfig();
            string? path = await NeuralTtsHelper.GenerateSingleVoiceAsync(sample, config);

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                LblStatus.Text = $"Đang phát thử: {sample.CleanDisplayText}";
                PlaySubtitleVoice(path);
            }
            else
            {
                MessageBox.Show("Không thể phát thử. Hãy mở Terminal chạy: pip install edge-tts", "Thông báo");
            }
        }

        private void BtnOpenTtsFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TtsCache");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        // ================= XUẤT VIDEO MP4 HOÀN CHỈNH (HARDSUB + GIỌNG AI BẰNG FFMPEG) =================
        private async void BtnExportVideo_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentVideoPath) || !File.Exists(_currentVideoPath))
            {
                MessageBox.Show("Vui lòng mở hoặc tải một video trước khi xuất file!", "Thông báo");
                return;
            }

            SaveFileDialog saveDlg = new()
            {
                Title = "Lưu Video MP4 Hoàn Chỉnh",
                Filter = "MP4 Video (*.mp4)|*.mp4",
                FileName = $"Vietsub_AI_{System.IO.Path.GetFileNameWithoutExtension(_currentVideoPath)}_{DateTime.Now:HHmmss}.mp4"
            };

            if (saveDlg.ShowDialog() != true) return;

            bool includeVoice = ChkEnableVoiceover.IsChecked == true;
            bool hasGeneratedAudio = _subtitles.Exists(x => !string.IsNullOrEmpty(x.AudioFilePath) && File.Exists(x.AudioFilePath));

            if (includeVoice && _subtitles.Count > 0 && !hasGeneratedAudio)
            {
                var ans = MessageBox.Show("Bạn đang bật 'Lồng tiếng AI' nhưng chưa bấm tạo file âm thanh.\nBạn có muốn phần mềm tự động tạo giọng đọc AI trước khi xuất video không?",
                    "Tự động tạo giọng AI", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (ans == MessageBoxResult.Cancel) return;
                if (ans == MessageBoxResult.Yes)
                {
                    TtsConfig ttsCfg = GetCurrentTtsConfig();
                    PbExport.Visibility = Visibility.Visible;
                    var ttsProg = new Progress<DownloadProgress>(p =>
                    {
                        PbExport.Value = p.Percent;
                        LblStatus.Text = p.Message;
                    });
                    await NeuralTtsHelper.GenerateAllVoicesAsync(_subtitles, ttsCfg, ttsProg);
                }
            }

            BtnPause_Click(sender, e);

            var exportOptions = new ExportOptions
            {
                InputVideoPath = _currentVideoPath,
                OutputVideoPath = saveDlg.FileName,
                BurnSubtitles = !(ChkHideSubtitle.IsChecked == true) && _subtitles.Count > 0,
                MixVoiceover = includeVoice,
                OriginalVideoVolume = (SliderVideoDucking?.Value ?? 25.0) / 100.0,
                SubtitleColorIndex = _subtitleStyle.TextColorIndex,
                SubtitleFontSize = (int)_subtitleStyle.FontSize,
                Subtitles = new List<SubtitleItem>(_subtitles)
            };

            BtnExportVideo.IsEnabled = false;
            BtnQuickExport.IsEnabled = false;
            PbExport.Visibility = Visibility.Visible;
            PbExport.IsIndeterminate = true;
            LblStatus.Text = "Đang xuất video MP4 bằng FFmpeg...";

            var exportProgress = new Progress<DownloadProgress>(p =>
            {
                PbExport.IsIndeterminate = false;
                PbExport.Value = p.Percent;
                LblStatus.Text = p.Message;
            });

            DownloadResult res = await VideoExportHelper.ExportVideoAsync(exportOptions, exportProgress);

            BtnExportVideo.IsEnabled = true;
            BtnQuickExport.IsEnabled = true;
            PbExport.Visibility = Visibility.Collapsed;

            if (res.Success && !string.IsNullOrEmpty(res.SavedFilePath))
            {
                LblStatus.Text = $"Xuất video thành công: {System.IO.Path.GetFileName(res.SavedFilePath)}";
                var openAns = MessageBox.Show($"Đã xuất video MP4 hoàn chỉnh thành công!\n\nĐường dẫn: {res.SavedFilePath}\n\nBạn có muốn mở thư mục chứa video ngay không?",
                    "Xuất Video Thành Công", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (openAns == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{res.SavedFilePath}\"",
                        UseShellExecute = true
                    });
                }
            }
            else
            {
                LblStatus.Text = "Xuất video thất bại.";
                MessageBox.Show($"Không thể xuất video:\n{res.ErrorMessage}\n\nHãy đảm bảo đã có file ffmpeg.exe trong thư mục phần mềm hoặc đã cài FFmpeg vào máy.", "Lỗi Xuất Video");
            }
        }

        // ================= TẢI VIDEO ĐA NỀN TẢNG (YT-DLP) =================
        private async void BtnDownloadVideo_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtVideoUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Vui lòng dán liên kết video!", "Thông báo");
                return;
            }

            BtnDownloadVideo.IsEnabled = false;
            PbDownload.Visibility = Visibility.Visible;
            PbDownload.IsIndeterminate = true;
            LblStatus.Text = "Đang tải video...";

            var progress = new Progress<DownloadProgress>(p =>
            {
                PbDownload.IsIndeterminate = false;
                PbDownload.Value = p.Percent;
                LblStatus.Text = p.Message;
            });

            DownloadResult result = await VideoDownloadHelper.DownloadVideoAsync(url, progress);

            BtnDownloadVideo.IsEnabled = true;
            PbDownload.Visibility = Visibility.Collapsed;

            if (result.Success && !string.IsNullOrEmpty(result.SavedFilePath))
            {
                LblStatus.Text = "Tải thành công!";
                LoadLocalVideo(result.SavedFilePath);
            }
            else
            {
                MessageBox.Show($"Không thể tải video: {result.ErrorMessage}", "Lỗi tải video");
                LblStatus.Text = "Tải video thất bại.";
            }
        }

        // ================= AI DỊCH THUẬT & PROMPT CHUẨN RẠP + LỒNG TIẾNG AI =================
        private void BtnCopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(AiHelper.CinemaPromptTemplate);
            MessageBox.Show("Đã sao chép Prompt Chuẩn Rạp Phim + Đánh Dấu Nhấn Nhá Giọng AI!\n\nKhi dán kết quả SRT vào phần mềm:\n• Màn hình video sẽ tự ẩn các ký hiệu [ngắt], [nhấn] để chữ hiển thị sạch đẹp.\n• Bộ đọc Giọng AI sẽ tự hiểu các ký hiệu này để đọc nhấn nhá, cao thấp như người thật!", "Thành công");
        }

        private async void BtnAutoAiSub_Click(object sender, RoutedEventArgs e)
        {
            string apiKey = TxtApiKey.Password.Trim();
            string rawTranscript = TxtSrtEditor.Text.Trim();

            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show("Vui lòng nhập API Key để sử dụng tính năng này!", "Thông báo");
                return;
            }

            if (string.IsNullOrEmpty(rawTranscript))
            {
                MessageBox.Show("Vui lòng dán transcript hoặc phụ đề cần dịch vào khung bên dưới!", "Thông báo");
                return;
            }

            AiProvider provider = CmbAiModel.SelectedIndex switch
            {
                1 => AiProvider.OpenAI_GPT4o,
                2 => AiProvider.Claude_35,
                3 => AiProvider.DeepSeek_V3,
                _ => AiProvider.Gemini_20_Flash
            };

            BtnAutoAiSub.IsEnabled = false;
            LblStatus.Text = "AI đang xử lý dịch thuật & chèn nhịp điệu giọng đọc...";

            AiResponse res = await AiHelper.TranslateWithAiAsync(rawTranscript, apiKey, provider);

            BtnAutoAiSub.IsEnabled = true;
            if (res.Success)
            {
                TxtSrtEditor.Text = res.TranslatedSrt;
                LblStatus.Text = "AI đã dịch xong phụ đề kèm đánh dấu nhấn nhá giọng đọc!";
            }
            else
            {
                MessageBox.Show($"Lỗi gọi API: {res.ErrorMessage}", "Lỗi AI");
                LblStatus.Text = "Dịch AI thất bại.";
            }
        }

        // ================= THỦY VÂN SỐ ẨN =================
        private void BtnInjectStego_Click(object sender, RoutedEventArgs e)
        {
            string currentSrt = TxtSrtEditor.Text;
            if (string.IsNullOrWhiteSpace(currentSrt))
            {
                MessageBox.Show("Chưa có nội dung phụ đề để cấy mã ẩn!", "Thông báo");
                return;
            }

            string signature = $"Vietsub by {TxtInputSubber.Text} | {DateTime.Now:yyyy-MM-dd}";
            string securedSrt = SrtHelper.InjectSteganography(currentSrt, signature);

            TxtSrtEditor.Text = securedSrt;
            MessageBox.Show($"Đã cấy thành công Thủy vân ẩn: '{signature}' vào phụ đề!\nKý tự này tàng hình hoàn toàn nhưng tồn tại vĩnh viễn trong file.", "Bảo mật số");
        }

        private void BtnVerifyStego_Click(object sender, RoutedEventArgs e)
        {
            string content = TxtSrtEditor.Text;
            string extracted = SrtHelper.ExtractSteganography(content);

            if (!string.IsNullOrEmpty(extracted))
            {
                MessageBox.Show($"XÁC MINH BẢN QUYỀN THÀNH CÔNG!\n\nChữ ký số tìm thấy bên trong phụ đề:\n👉 {extracted}", "Chứng thực");
            }
            else
            {
                MessageBox.Show("Không tìm thấy chữ ký thủy vân ẩn nào trong nội dung này!", "Kết quả");
            }
        }
    }

    // =========================================================================
    // CÁC MÔ HÌNH VÀ HÀM BỔ TRỢ (SRT PARSER, NEURAL TTS, FFMPEG EXPORT, AI)
    // =========================================================================

    public class SubtitleItem
    {
        public int Index { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string RawText { get; set; } = string.Empty;
        public string CleanDisplayText { get; set; } = string.Empty;
        public string NaturalSpeechText { get; set; } = string.Empty;
        public string NormalSpeechText { get; set; } = string.Empty;
        public int NaturalPitchOffsetHz { get; set; } = 0;
        public int NaturalRateOffsetPercent { get; set; } = 0;
        public string? AudioFilePath { get; set; }
    }

    public class TtsConfig
    {
        public string VoiceName { get; set; } = "vi-VN-NamMinhNeural";
        public bool NaturalMode { get; set; } = true;
        public int BaseRatePercent { get; set; } = 5;
        public int BasePitchHz { get; set; } = 0;
        public bool UseCustomApi { get; set; } = false;
        public string ApiKey { get; set; } = string.Empty;
    }

    public class ExportOptions
    {
        public string InputVideoPath { get; set; } = string.Empty;
        public string OutputVideoPath { get; set; } = string.Empty;
        public bool BurnSubtitles { get; set; } = true;
        public bool MixVoiceover { get; set; } = true;
        public double OriginalVideoVolume { get; set; } = 0.25;
        public int SubtitleColorIndex { get; set; } = 0;
        public int SubtitleFontSize { get; set; } = 22;
        public List<SubtitleItem> Subtitles { get; set; } = new();
    }

    public class DownloadProgress
    {
        public double Percent { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class DownloadResult
    {
        public bool Success { get; set; }
        public string? SavedFilePath { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public enum AiProvider
    {
        Gemini_20_Flash,
        OpenAI_GPT4o,
        Claude_35,
        DeepSeek_V3
    }

    public class AiResponse
    {
        public bool Success { get; set; }
        public string TranslatedSrt { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public static class SrtHelper
    {
        private const char ZwZero = '\u200B';
        private const char ZwOne = '\u200C';
        private const char ZwEnd = '\u200D';

        public static List<SubtitleItem> ParseSrt(string srtContent)
        {
            var result = new List<SubtitleItem>();
            if (string.IsNullOrWhiteSpace(srtContent)) return result;

            string normalized = srtContent.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            string[] blocks = Regex.Split(normalized, @"\n\s*\n");
            int count = 1;

            foreach (var block in blocks)
            {
                if (string.IsNullOrWhiteSpace(block)) continue;
                var lines = block.Split('\n');
                int timelineIdx = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("-->"))
                    {
                        timelineIdx = i;
                        break;
                    }
                }

                if (timelineIdx != -1)
                {
                    var parts = lines[timelineIdx].Split(new[] { "-->" }, StringSplitOptions.None);
                    if (parts.Length == 2 &&
                        TimeSpan.TryParse(parts[0].Trim().Replace(',', '.'), out TimeSpan start) &&
                        TimeSpan.TryParse(parts[1].Trim().Replace(',', '.'), out TimeSpan end))
                    {
                        string rawText = string.Join("\n", lines, timelineIdx + 1, lines.Length - (timelineIdx + 1)).Trim();
                        result.Add(CreateSubtitleItem(count++, start, end, rawText));
                    }
                }
            }

            return result;
        }

        public static SubtitleItem CreateSubtitleItem(int index, TimeSpan start, TimeSpan end, string rawText)
        {
            int pitchOffset = 0;
            int rateOffset = 0;

            if (Regex.IsMatch(rawText, @"\[(cao|vui|hào hứng|nhấn mạnh)\]", RegexOptions.IgnoreCase))
            {
                pitchOffset += 6;
                rateOffset += 5;
            }
            if (Regex.IsMatch(rawText, @"\[(trầm|buồn|nghiêm túc|chậm)\]", RegexOptions.IgnoreCase))
            {
                pitchOffset -= 6;
                rateOffset -= 5;
            }
            if (rawText.Contains('!'))
            {
                pitchOffset += 3;
                rateOffset += 3;
            }
            else if (rawText.Contains('?'))
            {
                pitchOffset += 4;
            }

            string cleanDisplay = rawText;
            cleanDisplay = Regex.Replace(cleanDisplay, @"\[nhấn:\s*([^\]]+)\]", "$1", RegexOptions.IgnoreCase);
            cleanDisplay = Regex.Replace(cleanDisplay, @"\[(ngắt|nghỉ|cao|trầm|vui|buồn|nhanh|chậm|nhấn mạnh|nghiêm túc|hào hứng)\]", "", RegexOptions.IgnoreCase);
            cleanDisplay = Regex.Replace(cleanDisplay, @"\s{2,}", " ").Trim();

            string naturalSpeech = rawText;
            naturalSpeech = Regex.Replace(naturalSpeech, @"^-\s*\[[^\]]+\]:\s*", "", RegexOptions.Multiline);
            naturalSpeech = Regex.Replace(naturalSpeech, @"\[(ngắt|nghỉ)\]", ", ... ", RegexOptions.IgnoreCase);
            naturalSpeech = Regex.Replace(naturalSpeech, @"\[nhấn:\s*([^\]]+)\]", " $1, ", RegexOptions.IgnoreCase);
            naturalSpeech = Regex.Replace(naturalSpeech, @"\[[^\]]+\]", "", RegexOptions.IgnoreCase);
            naturalSpeech = naturalSpeech.Replace("\n", ", ").Trim();

            string normalSpeech = Regex.Replace(cleanDisplay, @"^-\s*\[[^\]]+\]:\s*", "", RegexOptions.Multiline);
            normalSpeech = normalSpeech.Replace("...", ",").Replace("!", ".").Replace("\n", " ").Trim();

            return new SubtitleItem
            {
                Index = index,
                Start = start,
                End = end,
                RawText = rawText,
                CleanDisplayText = cleanDisplay,
                NaturalSpeechText = naturalSpeech,
                NormalSpeechText = normalSpeech,
                NaturalPitchOffsetHz = pitchOffset,
                NaturalRateOffsetPercent = rateOffset
            };
        }

        public static string BuildCleanSrtFileContent(List<SubtitleItem> subtitles)
        {
            StringBuilder sb = new();
            for (int i = 0; i < subtitles.Count; i++)
            {
                var s = subtitles[i];
                sb.AppendLine((i + 1).ToString());
                sb.AppendLine($"{s.Start:hh\\:mm\\:ss\\,fff} --> {s.End:hh\\:mm\\:ss\\,fff}");
                sb.AppendLine(s.CleanDisplayText);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static string InjectSteganography(string originalText, string secretMessage)
        {
            if (string.IsNullOrEmpty(originalText) || string.IsNullOrEmpty(secretMessage))
                return originalText;

            StringBuilder hidden = new();
            byte[] bytes = Encoding.UTF8.GetBytes(secretMessage);
            foreach (byte b in bytes)
            {
                for (int i = 7; i >= 0; i--)
                {
                    hidden.Append(((b >> i) & 1) == 0 ? ZwZero : ZwOne);
                }
            }
            hidden.Append(ZwEnd);

            return hidden.ToString() + originalText;
        }

        public static string ExtractSteganography(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            List<byte> bits = new();
            List<byte> bytes = new();

            foreach (char c in text)
            {
                if (c == ZwZero) bits.Add(0);
                else if (c == ZwOne) bits.Add(1);
                else if (c == ZwEnd) break;
            }

            if (bits.Count >= 8)
            {
                for (int i = 0; i + 8 <= bits.Count; i += 8)
                {
                    byte b = 0;
                    for (int j = 0; j < 8; j++)
                    {
                        b = (byte)((b << 1) | bits[i + j]);
                    }
                    bytes.Add(b);
                }
                try
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }
                catch
                {
                    return string.Empty;
                }
            }

            return string.Empty;
        }
    }

    // =========================================================================
    // BỘ TẠO GIỌNG ĐỌC NEURAL AI (TIKTOK / YOUTUBE VOICE ENGINE)
    // =========================================================================
    public static class NeuralTtsHelper
    {
        public static async Task<int> GenerateAllVoicesAsync(List<SubtitleItem> items, TtsConfig config, IProgress<DownloadProgress>? progress = null)
        {
            int completed = 0;
            int total = items.Count;

            for (int i = 0; i < total; i++)
            {
                var item = items[i];
                double pct = ((i + 1) * 100.0) / total;
                progress?.Report(new DownloadProgress
                {
                    Percent = pct,
                    Message = $"Đang tạo giọng AI câu {i + 1}/{total}: {item.CleanDisplayText}"
                });

                string? audioPath = await GenerateSingleVoiceAsync(item, config);
                if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
                {
                    item.AudioFilePath = audioPath;
                    completed++;
                }
            }

            return completed;
        }

        public static async Task<string?> GenerateSingleVoiceAsync(SubtitleItem item, TtsConfig config)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string cacheFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TtsCache");
                    if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                    string textToSpeak = config.NaturalMode ? item.NaturalSpeechText : item.NormalSpeechText;
                    if (string.IsNullOrWhiteSpace(textToSpeak)) return null;

                    int finalRate = config.BaseRatePercent + (config.NaturalMode ? item.NaturalRateOffsetPercent : 0);
                    int finalPitch = config.BasePitchHz + (config.NaturalMode ? item.NaturalPitchOffsetHz : 0);

                    double durationSec = Math.Max(1.2, (item.End - item.Start).TotalSeconds);
                    int wordCount = textToSpeak.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    double estimatedSec = wordCount / 3.2;
                    if (estimatedSec > durationSec)
                    {
                        int boost = (int)Math.Min(35, ((estimatedSec / durationSec) - 1.0) * 100);
                        finalRate += boost;
                    }

                    string rateStr = $"{(finalRate >= 0 ? "+" : "")}{finalRate}%";
                    string pitchStr = $"{(finalPitch >= 0 ? "+" : "")}{finalPitch}Hz";

                    string hashInput = $"{config.VoiceName}_{config.NaturalMode}_{rateStr}_{pitchStr}_{textToSpeak}";
                    byte[] hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(hashInput));
                    string hashHex = Convert.ToHexString(hashBytes).Substring(0, 10);
                    string outputMp3 = System.IO.Path.Combine(cacheFolder, $"sub_{item.Index:D4}_{hashHex}.mp3");

                    if (File.Exists(outputMp3) && new FileInfo(outputMp3).Length > 200)
                        return outputMp3;

                    string safeText = textToSpeak.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                    string args = $"-m edge_tts --voice \"{config.VoiceName}\" --rate={rateStr} --pitch={pitchStr} --text \"{safeText}\" --write-media \"{outputMp3}\"";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(15000);
                        if (File.Exists(outputMp3) && new FileInfo(outputMp3).Length > 200)
                            return outputMp3;
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            });
        }
    }

    // =========================================================================
    // BỘ XUẤT VIDEO MP4 HOÀN CHỈNH (FFMPEG HARDSUB + TRỘN GIỌNG ĐỌC AI)
    // =========================================================================
    public static class VideoExportHelper
    {
        public static async Task<DownloadResult> ExportVideoAsync(ExportOptions options, IProgress<DownloadProgress>? progress = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    progress?.Report(new DownloadProgress { Percent = 10, Message = "Đang chuẩn bị dữ liệu phụ đề và âm thanh AI..." });

                    string localFfmpeg = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
                    string ffmpegExe = File.Exists(localFfmpeg) ? localFfmpeg : "ffmpeg";

                    string tempFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TtsCache");
                    if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

                    string tempSrtPath = System.IO.Path.Combine(tempFolder, "export_clean_sub.srt");
                    if (options.BurnSubtitles && options.Subtitles.Count > 0)
                    {
                        string cleanSrt = SrtHelper.BuildCleanSrtFileContent(options.Subtitles);
                        File.WriteAllText(tempSrtPath, cleanSrt, new UTF8Encoding(true));
                    }

                    var voicedItems = new List<SubtitleItem>();
                    if (options.MixVoiceover)
                    {
                        foreach (var item in options.Subtitles)
                        {
                            if (!string.IsNullOrEmpty(item.AudioFilePath) && File.Exists(item.AudioFilePath))
                            {
                                voicedItems.Add(item);
                            }
                        }
                    }

                    StringBuilder args = new();
                    args.Append($"-y -i \"{options.InputVideoPath}\" ");

                    foreach (var vItem in voicedItems)
                    {
                        args.Append($"-i \"{vItem.AudioFilePath}\" ");
                    }

                    string? vfFilter = null;
                    if (options.BurnSubtitles && options.Subtitles.Count > 0)
                    {
                        string escapedSrt = tempSrtPath.Replace("\\", "/").Replace(":", "\\:");
                        string assColor = GetAssPrimaryColorHex(options.SubtitleColorIndex);
                        vfFilter = $"subtitles='{escapedSrt}':force_style='FontSize=20,PrimaryColour={assColor},OutlineColour=&H80000000,BorderStyle=3,Outline=1,Shadow=1,MarginV=25'";
                    }

                    if (voicedItems.Count > 0)
                    {
                        StringBuilder filterComplex = new();
                        string origVolStr = options.OriginalVideoVolume.ToString("0.00", CultureInfo.InvariantCulture);
                        filterComplex.Append($"[0:a]volume={origVolStr}[a0];");

                        for (int i = 0; i < voicedItems.Count; i++)
                        {
                            int delayMs = (int)Math.Max(0, voicedItems[i].Start.TotalMilliseconds);
                            filterComplex.Append($"[{i + 1}:a]adelay={delayMs}|{delayMs},volume=1.6[a{i + 1}];");
                        }

                        filterComplex.Append("[a0]");
                        for (int i = 0; i < voicedItems.Count; i++)
                        {
                            filterComplex.Append($"[a{i + 1}]");
                        }
                        filterComplex.Append($"amix=inputs={voicedItems.Count + 1}:duration=first:dropout_transition=0:normalize=0[aout]");

                        args.Append($"-filter_complex \"{filterComplex}\" ");
                        if (!string.IsNullOrEmpty(vfFilter))
                        {
                            args.Append($"-vf \"{vfFilter}\" ");
                        }
                        args.Append("-map 0:v:0 -map \"[aout]\" ");
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(vfFilter))
                        {
                            args.Append($"-vf \"{vfFilter}\" ");
                        }
                        args.Append("-map 0:v:0 -map 0:a? ");
                    }

                    args.Append($"-c:v libx264 -preset ultrafast -crf 23 -c:a aac -b:a 192k \"{options.OutputVideoPath}\"");

                    progress?.Report(new DownloadProgress { Percent = 45, Message = "FFmpeg đang ghép phụ đề & lồng tiếng AI..." });

                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegExe,
                        Arguments = args.ToString(),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = new Process { StartInfo = psi };
                    StringBuilder errLog = new();
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errLog.AppendLine(e.Data);
                            if (e.Data.Contains("time="))
                            {
                                progress?.Report(new DownloadProgress { Percent = 75, Message = $"Đang kết xuất: {e.Data.Trim()}" });
                            }
                        }
                    };

                    proc.Start();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit();

                    if (proc.ExitCode == 0 && File.Exists(options.OutputVideoPath))
                    {
                        progress?.Report(new DownloadProgress { Percent = 100, Message = "Xuất video MP4 hoàn tất!" });
                        return new DownloadResult { Success = true, SavedFilePath = options.OutputVideoPath };
                    }
                    else
                    {
                        return new DownloadResult
                        {
                            Success = false,
                            ErrorMessage = errLog.Length > 0 ? errLog.ToString() : "Không tìm thấy ffmpeg.exe hoặc lệnh xuất thất bại."
                        };
                    }
                }
                catch (Exception ex)
                {
                    return new DownloadResult { Success = false, ErrorMessage = ex.Message };
                }
            });
        }

        private static string GetAssPrimaryColorHex(int colorIndex)
        {
            return colorIndex switch
            {
                1 => "&H0000D7FF", // Vàng Cinema Gold
                2 => "&H00FFFF00", // Xanh Lơ Cyan
                3 => "&H007BFA50", // Xanh Lá Neon
                4 => "&H00C1B6FF", // Hồng Phấn
                5 => "&H0000A5FF", // Cam Sáng
                6 => "&H005555FF", // Đỏ Tươi
                _ => "&H00FFFFFF"  // Trắng
            };
        }
    }

    public static class VideoDownloadHelper
    {
        public static async Task<DownloadResult> DownloadVideoAsync(string url, IProgress<DownloadProgress>? progress = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    progress?.Report(new DownloadProgress { Percent = 10, Message = "Đang kiểm tra liên kết..." });

                    string outputFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
                    if (!Directory.Exists(outputFolder))
                    {
                        Directory.CreateDirectory(outputFolder);
                    }

                    string outputFile = System.IO.Path.Combine(outputFolder, $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                    string ytdlpPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
                    string exeToRun = File.Exists(ytdlpPath) ? ytdlpPath : "yt-dlp";

                    var psi = new ProcessStartInfo
                    {
                        FileName = exeToRun,
                        Arguments = $"-f \"bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4] / bv*+ba/b\" --merge-output-format mp4 -o \"{outputFile}\" \"{url}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = new Process { StartInfo = psi };
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data) && e.Data.Contains("%"))
                        {
                            var match = Regex.Match(e.Data, @"(\d+\.?\d*)%");
                            if (match.Success && double.TryParse(match.Groups[1].Value, out double pct))
                            {
                                progress?.Report(new DownloadProgress { Percent = pct, Message = $"Đang tải: {pct:0}%" });
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.WaitForExit();

                    if (process.ExitCode == 0 && File.Exists(outputFile))
                    {
                        progress?.Report(new DownloadProgress { Percent = 100, Message = "Tải thành công!" });
                        return new DownloadResult { Success = true, SavedFilePath = outputFile };
                    }
                    else
                    {
                        string err = process.StandardError.ReadToEnd();
                        return new DownloadResult { Success = false, ErrorMessage = string.IsNullOrWhiteSpace(err) ? "Tải video thất bại (kiểm tra lại đường dẫn yt-dlp.exe)." : err };
                    }
                }
                catch (Exception ex)
                {
                    return new DownloadResult { Success = false, ErrorMessage = ex.Message };
                }
            });
        }
    }

    public static class AiHelper
    {
        public const string CinemaPromptTemplate =
@"Bạn là chuyên gia dịch thuật phụ đề phim chiếu rạp và đạo diễn lồng tiếng AI chuyên nghiệp.
Hãy dịch toàn bộ nội dung phụ đề/transcript dưới đây sang tiếng Việt tự nhiên, giàu cảm xúc, đồng thời chèn các thẻ điều khiển nhịp đọc để bộ lồng tiếng Neural AI đọc nhấn nhá, cao thấp như người thật:

1. QUY CHUẨN HIỂN THỊ & NGẮT DÒNG:
- Mỗi dòng tối đa không quá 37 ký tự để không tràn màn hình.
- Nếu trong cùng mốc thời gian có 2 người nói, định dạng gạch đầu dòng riêng.

2. QUY CHUẨN ĐÁNH DẤU NHẤN NHÁ CHO GIỌNG ĐỌC AI (SẼ TỰ ẨN TRÊN MÀN HÌNH VIDEO):
- Chèn [ngắt] vào giữa các mệnh đề dài hoặc chỗ cần dừng lấy hơi tự nhiên.
- Bọc từ khóa quan trọng trong thẻ [nhấn: từ khóa] để giọng đọc AI nhấn trọng âm.
- Đầu câu có cảm xúc đặc biệt có thể gắn [cao] (hào hứng, kịch tính, câu hỏi) hoặc [trầm] (sâu lắng, nghiêm túc).
Ví dụ: [trầm] Không thể tin được, [ngắt] đây chính là [nhấn: bí mật] cuối cùng!

3. ĐỊNH DẠNG ĐẦU RA:
- Giữ nguyên định dạng chuẩn tệp phụ đề SRT (Số thứ tự, Mốc thời gian 00:00:00,000 --> 00:00:00,000 và Lời thoại dịch kèm thẻ).
- Không thêm bất kỳ lời bình luận hay giải thích nào ngoài nội dung SRT.";

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(90) };

        public static async Task<AiResponse> TranslateWithAiAsync(string transcript, string apiKey, AiProvider provider)
        {
            try
            {
                string prompt = $"{CinemaPromptTemplate}\n\nNỘI DUNG CẦN DỊCH:\n{transcript}";

                return provider switch
                {
                    AiProvider.Gemini_20_Flash => await CallGeminiAsync(prompt, apiKey),
                    AiProvider.OpenAI_GPT4o => await CallOpenAiAsync(prompt, apiKey, "gpt-4o"),
                    AiProvider.DeepSeek_V3 => await CallDeepSeekAsync(prompt, apiKey),
                    AiProvider.Claude_35 => await CallClaudeAsync(prompt, apiKey),
                    _ => new AiResponse { Success = false, ErrorMessage = "Mô hình AI chưa được hỗ trợ." }
                };
            }
            catch (Exception ex)
            {
                return new AiResponse { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static async Task<AiResponse> CallGeminiAsync(string prompt, string apiKey)
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";
            var payload = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                }
            };

            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var res = await _httpClient.PostAsync(url, content);
            string resText = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return new AiResponse { Success = false, ErrorMessage = $"Gemini Error: {resText}" };

            using var doc = JsonDocument.Parse(resText);
            string reply = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "";

            return new AiResponse { Success = true, TranslatedSrt = CleanMarkdown(reply) };
        }

        private static async Task<AiResponse> CallOpenAiAsync(string prompt, string apiKey, string model)
        {
            string url = "https://api.openai.com/v1/chat/completions";
            var payload = new
            {
                model = model,
                messages = new[] { new { role = "user", content = prompt } }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var res = await _httpClient.SendAsync(request);
            string resText = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return new AiResponse { Success = false, ErrorMessage = $"OpenAI Error: {resText}" };

            using var doc = JsonDocument.Parse(resText);
            string reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "";

            return new AiResponse { Success = true, TranslatedSrt = CleanMarkdown(reply) };
        }

        private static async Task<AiResponse> CallDeepSeekAsync(string prompt, string apiKey)
        {
            string url = "https://api.deepseek.com/chat/completions";
            var payload = new
            {
                model = "deepseek-chat",
                messages = new[] { new { role = "user", content = prompt } }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var res = await _httpClient.SendAsync(request);
            string resText = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return new AiResponse { Success = false, ErrorMessage = $"DeepSeek Error: {resText}" };

            using var doc = JsonDocument.Parse(resText);
            string reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "";

            return new AiResponse { Success = true, TranslatedSrt = CleanMarkdown(reply) };
        }

        private static async Task<AiResponse> CallClaudeAsync(string prompt, string apiKey)
        {
            string url = "https://api.anthropic.com/v1/messages";
            var payload = new
            {
                model = "claude-3-5-sonnet-20241022",
                max_tokens = 4096,
                messages = new[] { new { role = "user", content = prompt } }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var res = await _httpClient.SendAsync(request);
            string resText = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return new AiResponse { Success = false, ErrorMessage = $"Claude Error: {resText}" };

            using var doc = JsonDocument.Parse(resText);
            string reply = doc.RootElement
                .GetProperty("content")[0]
                .GetProperty("text").GetString() ?? "";

            return new AiResponse { Success = true, TranslatedSrt = CleanMarkdown(reply) };
        }

        private static string CleanMarkdown(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            return input.Replace("```srt", "").Replace("```", "").Trim();
        }
    }

    // =========================================================================
    // BỘ TỰ ĐỘNG TẢI CÔNG CỤ CẦN THIẾT CHO MÁY KHÁCH (ZERO-INSTALL BOOTSTRAPPER)
    // =========================================================================
    public static class DependencyBootstrapper
    {
        private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(10) };

        // Link tải bản chạy ngay (Standalone Binary) chính thức từ GitHub Releases
        private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string FfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        public static async Task EnsureToolsReadyAsync(IProgress<DownloadProgress>? progress = null)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string ytdlpPath = System.IO.Path.Combine(baseDir, "yt-dlp.exe");
                string ffmpegPath = System.IO.Path.Combine(baseDir, "ffmpeg.exe");

                // 1. Tự động tải yt-dlp.exe nếu chưa có
                if (!File.Exists(ytdlpPath))
                {
                    progress?.Report(new DownloadProgress { Percent = 10, Message = "Đang tự động tải công cụ tải video (yt-dlp.exe)..." });
                    await DownloadFileAsync(YtDlpUrl, ytdlpPath, progress, "yt-dlp.exe");
                }

                // 2. Tự động tải và giải nén ffmpeg.exe nếu chưa có
                if (!File.Exists(ffmpegPath))
                {
                    progress?.Report(new DownloadProgress { Percent = 30, Message = "Đang tự động tải bộ xử lý video (ffmpeg.exe)..." });
                    string tempZip = System.IO.Path.Combine(baseDir, "ffmpeg_temp.zip");
                    await DownloadFileAsync(FfmpegZipUrl, tempZip, progress, "FFmpeg");

                    progress?.Report(new DownloadProgress { Percent = 85, Message = "Đang giải nén ffmpeg.exe..." });
                    await Task.Run(() =>
                    {
                        using var archive = ZipFile.OpenRead(tempZip);
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                entry.ExtractToFile(ffmpegPath, true);
                                break;
                            }
                        }
                    });

                    if (File.Exists(tempZip)) File.Delete(tempZip);
                }

                // 3. Tự động kiểm tra gói giọng đọc AI (edge-tts) nếu máy có Python
                await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "python",
                            Arguments = "-m pip install edge-tts --quiet",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(10000);
                    }
                    catch { }
                });

                progress?.Report(new DownloadProgress { Percent = 100, Message = "Sẵn sàng! Mọi công cụ (yt-dlp, FFmpeg, AI) đã đầy đủ." });
            }
            catch (Exception ex)
            {
                progress?.Report(new DownloadProgress { Percent = 100, Message = $"Lưu ý: Chưa tải đủ công cụ phụ trợ ({ex.Message})." });
            }
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, IProgress<DownloadProgress>? progress, string toolName)
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double pct = (totalRead * 100.0) / totalBytes.Value;
                    progress?.Report(new DownloadProgress
                    {
                        Percent = pct,
                        Message = $"Đang tự động tải {toolName}: {pct:0}% ({totalRead / 1024 / 1024} MB)"
                    });
                }
            }
        }
    }
}
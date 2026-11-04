using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

        private bool _isDraggingSlider = false;
        private bool _isDraggingBlur = false;
        private Border? _activeDragBox = null;
        private Point _dragStartPoint;
        private double _origLeft, _origTop;
        private bool _isUpdatingAttributionText = false;
        private bool _isSyncingStyleControls = false;

        // Lưu trữ cấu hình kiểu chữ, màu chữ & nền độc lập cho Phụ đề, Credit và Miễn trừ trách nhiệm
        private class BoxStyleState
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

            Loaded += (s, e) =>
            {
                UpdateAttributionDisplay();
                CenterAllBoxesInitial();
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
            VideoPlayer.Source = new Uri(filePath);
            VideoPlayer.Play();
            _playbackTimer.Start();
            LblStatus.Text = $"Đang phát: {System.IO.Path.GetFileName(filePath)}";
        }

        private void VideoPlayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (VideoPlayer.Source == null) return;
            if (_playbackTimer.IsEnabled)
            {
                VideoPlayer.Pause();
                _playbackTimer.Stop();
                LblStatus.Text = "Tạm dừng";
            }
            else
            {
                VideoPlayer.Play();
                _playbackTimer.Start();
                LblStatus.Text = "Đang phát";
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.Source != null)
            {
                VideoPlayer.Play();
                _playbackTimer.Start();
                LblStatus.Text = "Đang phát";
            }
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Pause();
            _playbackTimer.Stop();
            LblStatus.Text = "Tạm dừng";
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Stop();
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
            VideoPlayer.Position = VideoPlayer.Position - TimeSpan.FromSeconds(5);
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.Source == null) return;
            VideoPlayer.Position = VideoPlayer.Position + TimeSpan.FromSeconds(5);
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // ================= ĐỒNG BỘ TIẾN TRÌNH THỜI GIAN =================
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

            // Xử lý hiển thị phụ đề tương ứng
            if (_subtitles.Count > 0)
            {
                string match = string.Empty;
                foreach (var item in _subtitles)
                {
                    if (cur >= item.Start && cur <= item.End)
                    {
                        match = item.FormattedText;
                        break;
                    }
                }
                TxtSubtitleRender.Text = match;
            }
        }

        private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void TimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
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

                // Khi click vào hộp nào trên màn hình, tự động chuyển mục tùy chỉnh sang hộp đó
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

            // Tự hít vào tâm dọc & ngang nếu lệch < 15px
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

        // ================= ĐỊNH DẠNG KIỂU CHỮ, MÀU CHỮ & NỀN CHO PHỤ ĐỀ / CREDIT / DISCLAIMER =================
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
                1 => Color.FromRgb(30, 30, 45), // Xanh đen VS
                2 => Color.FromRgb(45, 45, 48), // Xám tối
                3 => Color.FromRgb(90, 20, 20), // Đỏ mờ
                _ => Colors.Black               // Đen mặc định
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

        // ================= PHÂN TÍCH VÀ CẬP NHẬT PHỤ ĐỀ (REAL-TIME SYNC) =================
        private void TxtSrtEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            ReloadSubtitlesFromEditor();
        }

        private void BtnApplySub_Click(object sender, RoutedEventArgs e)
        {
            ReloadSubtitlesFromEditor();
            MessageBox.Show($"Đã nạp thành công {_subtitles.Count} câu phụ đề!", "Thông báo");
        }

        private void ReloadSubtitlesFromEditor()
        {
            _subtitles.Clear();
            string content = TxtSrtEditor.Text;
            if (string.IsNullOrWhiteSpace(content)) return;

            var items = SrtHelper.ParseSrt(content);
            _subtitles.AddRange(items);
            LblStatus.Text = $"Đã nhận diện {_subtitles.Count} câu phụ đề.";
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

        // ================= AI DỊCH THUẬT & PROMPT CHUẨN RẠP =================
        private void BtnCopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(AiHelper.CinemaPromptTemplate);
            MessageBox.Show("Đã sao chép Prompt Chuẩn Rạp Phim vào bộ nhớ tạm!\nBạn có thể dán trực tiếp vào Gemini, Claude, GPT hoặc DeepSeek.", "Thành công");
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
            LblStatus.Text = "AI đang xử lý dịch thuật chuẩn rạp...";

            AiResponse res = await AiHelper.TranslateWithAiAsync(rawTranscript, apiKey, provider);

            BtnAutoAiSub.IsEnabled = true;
            if (res.Success)
            {
                TxtSrtEditor.Text = res.TranslatedSrt;
                LblStatus.Text = "AI đã dịch xong phụ đề!";
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
    // CÁC MÔ HÌNH VÀ HÀM BỔ TRỢ GOM VÀO CÙNG FILE ĐỂ TỐI ƯU TUYỆT ĐỐI
    // =========================================================================

    public class SubtitleItem
    {
        public int Index { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string RawText { get; set; } = string.Empty;
        public string FormattedText { get; set; } = string.Empty;
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
                        string text = string.Join("\n", lines, timelineIdx + 1, lines.Length - (timelineIdx + 1)).Trim();
                        result.Add(new SubtitleItem
                        {
                            Index = count++,
                            Start = start,
                            End = end,
                            RawText = text,
                            FormattedText = text
                        });
                    }
                }
            }

            return result;
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
@"Bạn là chuyên gia dịch thuật phụ đề phim chiếu rạp chuyên nghiệp. 
Hãy dịch toàn bộ nội dung phụ đề/transcript dưới đây sang tiếng Việt tự nhiên, chính xác, đảm bảo các quy chuẩn điện ảnh quốc tế:

1. QUY CHUẨN THOẠI 2 NGƯỜI NÓI CÙNG LÚC:
- Nếu trong cùng mốc thời gian có 2 người cùng nói, bắt buộc định dạng gạch đầu dòng riêng:
  - [Nhân vật 1]: Lời thoại của người 1
  - [Nhân vật 2]: Lời thoại của người 2

2. ĐỘ DÀI & NGẮT DÒNG:
- Mỗi dòng tối đa không quá 37 ký tự để không tràn màn hình.
- Ngắt dòng theo cụm từ tự nhiên, đúng ngữ pháp tiếng Việt.

3. ĐỊNH DẠNG ĐẦU RA:
- Giữ nguyên định dạng chuẩn tệp phụ đề SRT (Số thứ tự, Mốc thời gian 00:00:00,000 --> 00:00:00,000 và Lời thoại dịch).
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
}
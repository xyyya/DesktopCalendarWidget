using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopCalendarWidget
{
    public partial class NoteWindow : Window
    {
        private readonly MainWindow _mainWindow;
        public MainWindow.NoteData Note { get; }
        private bool _initializing = true;
        private bool _edgeHidden;
        private bool _dragging;
        private bool _openedFromHover;
        private double _normalWidth = 340;
        private double _normalHeight = 390;
        private double _normalLeft;
        private double _normalTop;
        private DockEdge _dockEdge = DockEdge.Right;
        private readonly DispatcherTimer _hoverMonitorTimer;
        private DateTime _hoverExpandStarted = DateTime.MinValue;

        private const double CapsuleWidth = 64;
        private const double CapsuleHeight = 26;
        private const int CollapseDurationMs = 550;

        public NoteWindow(MainWindow mainWindow, MainWindow.NoteData note)
        {
            InitializeComponent();
            Localization.ApplyWpfLanguage(this);
            Localization.ApplyToVisualTree(this);
            _mainWindow = mainWindow;
            Localization.LanguageChanged += Localization_LanguageChanged;
            Closed += NoteWindow_Closed;
            Note = note;
            DataContext = note;

            _hoverMonitorTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(70)
            };
            _hoverMonitorTimer.Tick += HoverMonitorTimer_Tick;

            txtTitle.Text = (note.Title == "新便签" || note.Title == "New Note") ? Localization.T("新便签") : note.Title;
            if (note.Title == "新便签" || note.Title == "New Note")
                note.Title = txtTitle.Text;
            txtContent.Text = note.Content;
            chkCompleted.IsChecked = note.IsCompleted;
            txtContent.FontSize = note.FontSize > 0 ? note.FontSize : 13;

            var sizeItem = cmbFontSize.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => double.TryParse(i.Content?.ToString(), out var s) && Math.Abs(s - txtContent.FontSize) < 0.01);
            if (sizeItem != null) sizeItem.IsSelected = true;

            var colorItem = cmbFontColor.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag?.ToString() ?? "") == (note.FontColor ?? ""));
            if (colorItem != null) colorItem.IsSelected = true;
            else cmbFontColor.SelectedIndex = 0;

            ApplyTheme();
            _initializing = false;
            // Apply once after all controls have been populated so startup in English
            // also localizes labels/items that are not reliably reached by VisualTreeHelper.
            ApplyLanguage();
        }


        private void Localization_LanguageChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(ApplyLanguage), DispatcherPriority.Loaded);
        }

        private void NoteWindow_Closed(object? sender, EventArgs e)
        {
            Localization.LanguageChanged -= Localization_LanguageChanged;
            Closed -= NoteWindow_Closed;
        }

        private void PopulateTaskCombo()
        {
            cmbTask.Items.Clear();
            cmbTask.Items.Add(new ComboBoxItem { Content = Localization.T("独立便签"), Tag = "" });
            foreach (var task in _mainWindow.AllTasksForNotes.OrderBy(t => t.Title))
                cmbTask.Items.Add(new ComboBoxItem { Content = task.Title, Tag = task.Id });

            var selected = cmbTask.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag?.ToString() ?? "") == (Note.TaskId ?? ""));
            if (selected != null) selected.IsSelected = true;
            else cmbTask.SelectedIndex = 0;
        }

        private void Task_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || !IsLoaded) return;
            string? id = (cmbTask.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            Note.TaskId = string.IsNullOrWhiteSpace(id) ? null : id;
            if (LinkedLabel != null)
                LinkedLabel.Text = string.IsNullOrWhiteSpace(Note.TaskId) ? Localization.T("独立便签") : Localization.T("已关联任务");
            SaveNoteSilently();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme();
            RestoreNormalPosition();
            PopulateTaskCombo();
            if (LinkedLabel != null)
                LinkedLabel.Text = string.IsNullOrWhiteSpace(Note.TaskId) ? Localization.T("独立便签") : Localization.T("已关联任务");
            Activate();

            // 不再启动“1 秒后自动收起”计时器。
            // 便签只有在用户真正把它拖到屏幕边缘后，才会收起成胶囊。
        }

        public void ApplyLanguage()
        {
            Localization.ApplyWpfLanguage(this);
            Localization.ApplyToVisualTree(this);
            Title = Localization.T("便签");

            // 便签编辑区的关键文字显式刷新，确保 ComboBox/模板内部文字也跟随语言切换。
            TitleBar.Text = Note.IsCompleted
                ? "✓ " + ((txtTitle.Text == "新便签" || txtTitle.Text == "New Note" || string.IsNullOrWhiteSpace(txtTitle.Text)) ? Localization.T("便签") : txtTitle.Text)
                : Localization.T("便签");
            // Explicitly localize the editor labels and ComboBox items.
            // These controls can live inside custom templates and may not be present
            // in the visual tree at the moment a language switch occurs.
            if (lblNoteName != null) lblNoteName.Text = Localization.T("便签名");
            if (lblFontSize != null) lblFontSize.Text = Localization.T("字号");
            if (lblFontColor != null) lblFontColor.Text = Localization.T("颜色");
            if (btnSave != null) btnSave.Content = Localization.T("保存");
            if (btnDelete != null) btnDelete.Content = Localization.T("删除");
            if (chkCompleted != null) chkCompleted.Content = Localization.T("完成");
            if (cmbTask != null) cmbTask.ToolTip = Localization.T("关联任务");
            if (cmbFontColor != null && cmbFontColor.Items.Count >= 6)
            {
                string[] labels = Localization.IsEnglish
                    ? new[] { "Default", "White", "Blue", "Green", "Yellow", "Red" }
                    : new[] { "默认", "白色", "蓝色", "绿色", "黄色", "红色" };
                for (int i = 0; i < labels.Length; i++)
                    if (cmbFontColor.Items[i] is ComboBoxItem item) item.Content = labels[i];
            }
            if (LinkedLabel != null) LinkedLabel.Text = string.IsNullOrWhiteSpace(Note.TaskId) ? Localization.T("独立便签") : Localization.T("已关联任务");

            bool wasInitializing = _initializing;
            _initializing = true;
            try
            {
                PopulateTaskCombo();
                if (LinkedLabel != null)
                {
                    LinkedLabel.Text = string.IsNullOrWhiteSpace(Note.TaskId)
                        ? Localization.T("独立便签")
                        : Localization.T("已关联任务");
                }
                TitleBar.Text = Note.IsCompleted
                    ? "✓ " + (string.IsNullOrWhiteSpace(txtTitle.Text) ? Localization.T("便签") : txtTitle.Text)
                    : Localization.T("便签");
            }
            finally
            {
                _initializing = wasInitializing;
            }

            // 最后再写一次，防止模板刷新或 VisualTree 本地化覆盖这些值。
            if (lblNoteName != null) lblNoteName.Text = Localization.T("便签名");
            if (lblFontSize != null) lblFontSize.Text = Localization.T("字号");
            if (lblFontColor != null) lblFontColor.Text = Localization.T("颜色");
            if (btnSave != null) btnSave.Content = Localization.T("保存");
            if (btnDelete != null) btnDelete.Content = Localization.T("删除");
            if (chkCompleted != null) chkCompleted.Content = Localization.T("完成");
            if (cmbTask != null) cmbTask.ToolTip = Localization.T("关联任务");
            Title = Localization.T("便签");
        }

        public void ApplyTheme()
        {
            foreach (var key in new[] { "WindowBg", "CardBg", "ControlBg", "ControlHoverBg", "DropDownBg", "BorderBrushKey", "SubBorderBrushKey", "TextPrimary", "TextSecondary", "TextMuted", "AccentBrush", "AccentLightBrush" })
                if (_mainWindow.Resources.Contains(key)) Resources[key] = _mainWindow.Resources[key];
            ApplyFontColor();
        }

        private void ApplyFontColor()
        {
            if (txtContent == null) return;
            if (string.IsNullOrWhiteSpace(Note.FontColor))
                txtContent.Foreground = (Brush)(Resources["TextPrimary"] ?? Brushes.White);
            else
            {
                try { txtContent.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(Note.FontColor)!; }
                catch { txtContent.Foreground = (Brush)(Resources["TextPrimary"] ?? Brushes.White); }
            }
        }

        private void FontStyle_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || !IsLoaded) return;
            if (double.TryParse((cmbFontSize.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var size))
            {
                Note.FontSize = size;
                txtContent.FontSize = size;
            }
            if (cmbFontColor.SelectedItem is ComboBoxItem color)
                Note.FontColor = color.Tag?.ToString() ?? "";
            ApplyFontColor();
            SaveNoteSilently();
        }

        private void RestoreNormalPosition()
        {
            Width = _normalWidth;
            Height = _normalHeight;
            RootBorder.CornerRadius = new CornerRadius(16);
            RootBorder.Padding = new Thickness(12);
            EditorContent.Visibility = Visibility.Visible;
            EditorContent.Opacity = 1;
            CapsuleContent.Visibility = Visibility.Collapsed;
            CapsuleContent.Opacity = 0;

            if (Note.WindowLeft.HasValue && Note.WindowTop.HasValue)
            {
                Left = Note.WindowLeft.Value;
                Top = Note.WindowTop.Value;
            }
            else
            {
                Rect area = SystemParameters.WorkArea;
                Left = Math.Max(area.Left + 10, area.Right - Width - 18);
                Top = area.Top + 18;
            }

            _normalLeft = Left;
            _normalTop = Top;
        }

        private enum DockEdge { None, Left, Right, Top, Bottom }

        private void StartHoverMonitor()
        {
            if (!_hoverMonitorTimer.IsEnabled)
                _hoverMonitorTimer.Start();
        }

        private void StopHoverMonitor()
        {
            if (_hoverMonitorTimer.IsEnabled)
                _hoverMonitorTimer.Stop();
        }

        private void HoverMonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (_dragging || _edgeHidden || !_openedFromHover)
            {
                if (_edgeHidden || !_openedFromHover)
                    StopHoverMonitor();
                return;
            }

            // 展开动画期间不要让 Window 的边界变化触发“假离开”。
            // 先等动画基本完成，再用屏幕坐标判断鼠标是否真的离开了整个便签区域。
            if ((DateTime.UtcNow - _hoverExpandStarted).TotalMilliseconds < CollapseDurationMs)
                return;

            GetCursorPos(out var pt);
            var rect = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
            const double margin = 2.0;
            rect.Inflate(margin, margin);

            if (!rect.Contains(pt.X, pt.Y))
            {
                DockEdge edge = _dockEdge != DockEdge.None ? _dockEdge : FindDockEdge();
                if (edge != DockEdge.None)
                    HideToEdge(edge);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // 只有真正贴近屏幕边缘时才认为是“贴边”。
        // 原来的 FindNearestEdge 无论便签放在哪里都会强行选一个最近边缘，
        // 导致便签刚打开就被收起，而且之后看起来像“固定住”。
        private DockEdge FindDockEdge()
        {
            Rect area = SystemParameters.WorkArea;
            const double threshold = 14.0;

            if (Math.Abs(Left - area.Left) <= threshold)
                return DockEdge.Left;
            if (Math.Abs(area.Right - (Left + Width)) <= threshold)
                return DockEdge.Right;
            if (Math.Abs(Top - area.Top) <= threshold)
                return DockEdge.Top;
            if (Math.Abs(area.Bottom - (Top + Height)) <= threshold)
                return DockEdge.Bottom;

            return DockEdge.None;
        }

        private void HideIfAtEdge()
        {
            if (_edgeHidden || _dragging) return;

            Rect area = SystemParameters.WorkArea;
            const double snap = 36.0;
            DockEdge edge = DockEdge.None;

            if (Left <= area.Left + snap)
            {
                Left = area.Left;
                edge = DockEdge.Left;
            }
            else if (Left + Width >= area.Right - snap)
            {
                Left = area.Right - Width;
                edge = DockEdge.Right;
            }
            else if (Top <= area.Top + snap)
            {
                Top = area.Top;
                edge = DockEdge.Top;
            }
            else if (Top + Height >= area.Bottom - snap)
            {
                Top = area.Bottom - Height;
                edge = DockEdge.Bottom;
            }

            if (edge != DockEdge.None)
            {
                // First magnetically snap the full note to the edge, then collapse to a capsule.
                double snapLeft = Left, snapTop = Top;
                switch (edge)
                {
                    case DockEdge.Left: snapLeft = area.Left; break;
                    case DockEdge.Right: snapLeft = area.Right - Width; break;
                    case DockEdge.Top: snapTop = area.Top; break;
                    case DockEdge.Bottom: snapTop = area.Bottom - Height; break;
                }
                _dockEdge = edge;
                if (Math.Abs(Left - snapLeft) < 1 && Math.Abs(Top - snapTop) < 1)
                { HideToEdge(edge); return; }
                var duration = new Duration(TimeSpan.FromMilliseconds(180));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                int pending = 0;
                EventHandler? completed = null;
                completed = (sender, args) =>
                {
                    pending--;
                    if (pending <= 0)
                    {
                        BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null);
                        Left = snapLeft; Top = snapTop; HideToEdge(edge);
                    }
                };
                if (Math.Abs(Left - snapLeft) >= 1)
                { pending++; var a = new DoubleAnimation(snapLeft, duration) { EasingFunction = ease }; a.Completed += completed; BeginAnimation(LeftProperty, a); }
                if (Math.Abs(Top - snapTop) >= 1)
                { pending++; var a = new DoubleAnimation(snapTop, duration) { EasingFunction = ease }; a.Completed += completed; BeginAnimation(TopProperty, a); }
            }
        }

        private void HideToEdge(DockEdge edge)
        {
            if (_edgeHidden || _dragging || edge == DockEdge.None) return;

            // 记录展开状态下的尺寸与位置，之后可从胶囊恢复。
            _normalWidth = Math.Max(220, Width);
            _normalHeight = Math.Max(220, Height);
            _normalLeft = Left;
            _normalTop = Top;
            _dockEdge = edge;
            _edgeHidden = true;
            _openedFromHover = false;
            StopHoverMonitor();

            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            EditorContent.BeginAnimation(OpacityProperty, null);
            CapsuleContent.BeginAnimation(OpacityProperty, null);

            Rect area = SystemParameters.WorkArea;
            double targetLeft = _normalLeft;
            double targetTop = _normalTop;
            switch (edge)
            {
                case DockEdge.Left:
                    targetLeft = area.Left;
                    targetTop = Math.Clamp(_normalTop + (_normalHeight - CapsuleHeight) / 2, area.Top, area.Bottom - CapsuleHeight);
                    break;
                case DockEdge.Right:
                    targetLeft = area.Right - CapsuleWidth;
                    targetTop = Math.Clamp(_normalTop + (_normalHeight - CapsuleHeight) / 2, area.Top, area.Bottom - CapsuleHeight);
                    break;
                case DockEdge.Top:
                    targetLeft = Math.Clamp(_normalLeft + (_normalWidth - CapsuleWidth) / 2, area.Left, area.Right - CapsuleWidth);
                    targetTop = area.Top;
                    break;
                case DockEdge.Bottom:
                    targetLeft = Math.Clamp(_normalLeft + (_normalWidth - CapsuleWidth) / 2, area.Left, area.Right - CapsuleWidth);
                    targetTop = area.Bottom - CapsuleHeight;
                    break;
            }

            // 先完整设置可见状态，避免动画中断后再次展开出现“黑屏/黑块”。
            EditorContent.Visibility = Visibility.Collapsed;
            EditorContent.Opacity = 1;
            CapsuleContent.Visibility = Visibility.Visible;
            CapsuleContent.Opacity = 1;
            RootBorder.Background = Resources["WindowBg"] as Brush ?? Brushes.Transparent;
            RootBorder.BorderBrush = Resources["BorderBrushKey"] as Brush ?? Brushes.Transparent;
            RootBorder.Padding = new Thickness(0);
            RootBorder.CornerRadius = edge switch
            {
                DockEdge.Left => new CornerRadius(0, CapsuleHeight / 2, CapsuleHeight / 2, 0),
                DockEdge.Right => new CornerRadius(CapsuleHeight / 2, 0, 0, CapsuleHeight / 2),
                DockEdge.Top => new CornerRadius(0, 0, CapsuleHeight / 2, CapsuleHeight / 2),
                _ => new CornerRadius(CapsuleHeight / 2, CapsuleHeight / 2, 0, 0)
            };

            var duration = new Duration(TimeSpan.FromMilliseconds(CollapseDurationMs));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            BeginAnimation(WidthProperty, new DoubleAnimation(CapsuleWidth, duration) { EasingFunction = ease });
            BeginAnimation(HeightProperty, new DoubleAnimation(CapsuleHeight, duration) { EasingFunction = ease });
            BeginAnimation(LeftProperty, new DoubleAnimation(targetLeft, duration) { EasingFunction = ease });
            BeginAnimation(TopProperty, new DoubleAnimation(targetTop, duration) { EasingFunction = ease });
        }

        private void ShowFromEdge(bool animate = true)
        {
            if (!_edgeHidden) return;

            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            EditorContent.BeginAnimation(OpacityProperty, null);
            CapsuleContent.BeginAnimation(OpacityProperty, null);

            Rect area = SystemParameters.WorkArea;
            Point cursor = PointToScreen(Mouse.GetPosition(this));
            double targetLeft = _normalLeft;
            double targetTop = _normalTop;

            // 悬停展开时，优先让鼠标仍位于展开后的便签内部，避免一展开就触发 MouseLeave。
            if (_dockEdge == DockEdge.Left)
            {
                targetLeft = area.Left;
                targetTop = Math.Clamp(cursor.Y - _normalHeight / 2, area.Top, area.Bottom - _normalHeight);
            }
            else if (_dockEdge == DockEdge.Right)
            {
                targetLeft = area.Right - _normalWidth;
                targetTop = Math.Clamp(cursor.Y - _normalHeight / 2, area.Top, area.Bottom - _normalHeight);
            }
            else if (_dockEdge == DockEdge.Top)
            {
                targetLeft = Math.Clamp(cursor.X - _normalWidth / 2, area.Left, area.Right - _normalWidth);
                targetTop = area.Top;
            }
            else if (_dockEdge == DockEdge.Bottom)
            {
                targetLeft = Math.Clamp(cursor.X - _normalWidth / 2, area.Left, area.Right - _normalWidth);
                targetTop = area.Bottom - _normalHeight;
            }

            _normalLeft = targetLeft;
            _normalTop = targetTop;
            _edgeHidden = false;
            _openedFromHover = animate;
            _hoverExpandStarted = animate ? DateTime.UtcNow : DateTime.MinValue;
            if (animate)
                StartHoverMonitor();
            else
                StopHoverMonitor();

            Width = CapsuleWidth;
            Height = CapsuleHeight;
            Left = _dockEdge switch
            {
                DockEdge.Left => area.Left,
                DockEdge.Right => area.Right - CapsuleWidth,
                DockEdge.Top or DockEdge.Bottom => Math.Clamp(targetLeft + (_normalWidth - CapsuleWidth) / 2, area.Left, area.Right - CapsuleWidth),
                _ => targetLeft
            };
            Top = _dockEdge switch
            {
                DockEdge.Top => area.Top,
                DockEdge.Bottom => area.Bottom - CapsuleHeight,
                DockEdge.Left or DockEdge.Right => Math.Clamp(targetTop + (_normalHeight - CapsuleHeight) / 2, area.Top, area.Bottom - CapsuleHeight),
                _ => targetTop
            };

            RootBorder.Background = Resources["WindowBg"] as Brush ?? Brushes.Transparent;
            RootBorder.BorderBrush = Resources["BorderBrushKey"] as Brush ?? Brushes.Transparent;
            RootBorder.Padding = new Thickness(12);
            RootBorder.CornerRadius = new CornerRadius(16);

            // 任何之前未完成的透明动画都在这里清掉，保证重新展开后内容一定可见。
            EditorContent.Visibility = Visibility.Visible;
            EditorContent.Opacity = 1;
            CapsuleContent.Visibility = Visibility.Collapsed;
            CapsuleContent.Opacity = 0;

            if (!animate)
            {
                Width = _normalWidth;
                Height = _normalHeight;
                Left = targetLeft;
                Top = targetTop;
                SaveNoteSilently();
                return;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(CollapseDurationMs));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            BeginAnimation(WidthProperty, new DoubleAnimation(_normalWidth, duration) { EasingFunction = ease });
            BeginAnimation(HeightProperty, new DoubleAnimation(_normalHeight, duration) { EasingFunction = ease });
            BeginAnimation(LeftProperty, new DoubleAnimation(targetLeft, duration) { EasingFunction = ease });
            BeginAnimation(TopProperty, new DoubleAnimation(targetTop, duration) { EasingFunction = ease });

            Activate();
        }

        private void Completed_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || !IsLoaded) return;
            Note.IsCompleted = chkCompleted.IsChecked == true;
            _mainWindow.SetNoteCompletionFromWindow(Note, Note.IsCompleted);
            TitleBar.Text = Note.IsCompleted ? "✓ " + (string.IsNullOrWhiteSpace(txtTitle.Text) ? Localization.T("便签") : txtTitle.Text) : Localization.T("便签");
        }

        private void SaveNoteSilently()
        {
            if (!IsLoaded) return;
            Note.Title = string.IsNullOrWhiteSpace(txtTitle.Text) ? Localization.T("无标题便签") : txtTitle.Text.Trim();
            Note.Content = txtContent.Text ?? string.Empty;
            Note.IsCompleted = chkCompleted.IsChecked == true;
            if (!_edgeHidden)
            {
                Note.WindowLeft = Left;
                Note.WindowTop = Top;
                _normalLeft = Left;
                _normalTop = Top;
            }
            _mainWindow.SaveNoteFromWindow(Note);
        }

        private void Save_Click(object sender, RoutedEventArgs e) => SaveNoteSilently();

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Localization.T("删除便签") + "?",
                Localization.T("提示"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _mainWindow.DeleteNoteFromWindow(Note);
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        private static bool IsInteractiveElement(DependencyObject? current)
        {
            while (current != null)
            {
                if (current is TextBoxBase || current is ComboBox || current is ButtonBase || current is CheckBox)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

            BeginNativeDrag(fromCapsule: _edgeHidden);
            e.Handled = true;
        }

        private void RootBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

            BeginNativeDrag(fromCapsule: _edgeHidden);
            e.Handled = true;
        }

        private void Capsule_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_edgeHidden || e.ButtonState != MouseButtonState.Pressed) return;
            BeginNativeDrag(fromCapsule: true);
            e.Handled = true;
        }

        private void FreezeCurrentVisualState()
        {
            // 展开动画尚未结束时开始拖动，必须先把“当前看到的值”固化成窗口真实属性，
            // 再清掉动画。否则 WPF 动画仍然占据 Left/Top/Width/Height，拖动结束后会把
            // 窗口拉回动画的目标位置（也就是原来的胶囊位置），造成“拖走又弹回来”。
            double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            double currentLeft = Left;
            double currentTop = Top;

            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);

            Width = Math.Max(220, currentWidth);
            Height = Math.Max(220, currentHeight);
            Left = currentLeft;
            Top = currentTop;
        }

        private void BeginNativeDrag(bool fromCapsule)
        {
            if (!IsLoaded) return;

            // 用户一旦按下开始拖动，就彻底退出“悬停自动收起”状态。
            // 后续窗口位置变化不应再被 HoverMonitor 当成离开便签。
            _openedFromHover = false;
            StopHoverMonitor();

            if (fromCapsule && _edgeHidden)
            {
                // 从胶囊拖动：先无动画恢复为完整便签，再马上进入原生拖动。
                ShowFromEdge(animate: false);
                Activate();
            }
            else
            {
                // 从已经展开的便签开始拖动：如果展开动画还在进行，冻结当前视觉状态，
                // 防止拖动结束后动画把窗口拉回之前的胶囊位置。
                FreezeCurrentVisualState();
            }

            _dragging = true;
            try
            {
                ReleaseCapture();
                var hwnd = new WindowInteropHelper(this).Handle;
                SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            finally
            {
                _dragging = false;
            }

            _normalLeft = Left;
            _normalTop = Top;
            HideIfAtEdge();
            SaveNoteSilently();
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            // 只负责“胶囊 -> 展开”。
            // “展开 -> 收起”不再依赖 Window.MouseLeave，避免窗口在动画缩放/移动过程中
            // 反复产生 MouseEnter/MouseLeave 导致展开、收起互相打架。
            if (_edgeHidden && !_dragging)
                ShowFromEdge(animate: true);
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // 贴边胶囊：鼠标移入自动展开；鼠标离开展开后的便签区域自动收起。

        protected override void OnClosed(EventArgs e)
        {
            StopHoverMonitor();
            if (IsLoaded) SaveNoteSilently();
            base.OnClosed(e);
        }
    }
}

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using CommunityToolkit.WinUI.Notifications;

namespace DesktopCalendarWidget
{
    // 日期任务标记转换器：返回 Brush 颜色（蓝点表示有未完成，绿点表示全完成）
    public class TaskDayToBrushConverter : IValueConverter, IMultiValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime date && Application.Current.MainWindow is MainWindow mainWin)
            {
                // 绿色状态看“当天实际存在的全部任务”，不能先按 ShowInCalendar 过滤。
                // 否则某个任务被设置为“不显示在日历”时，即使它未完成，也会让
                // “当天所有任务完成”这个判断失真；更重要的是，循环任务完成后
                // 需要参与这里的全完成判断。
                var allTasksForDate = mainWin.GetTasksForDate(date).ToList();
                if (allTasksForDate.Any())
                {
                    bool allCompleted = allTasksForDate.All(t =>
                        t.CompletedDates.Any(d => d.Date == date.Date));

                    if (allCompleted)
                    {
                        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"));
                    }

                    // 蓝色仍然只显示“日历中可见的、未完成的普通任务”。
                    // 循环任务无论是否完成，都不会单独触发蓝点。
                    bool hasUncompletedNonRecurringTask = allTasksForDate
                        .Where(t => t.ShowInCalendar)
                        .Any(t => !t.IsRecurring &&
                                  !t.CompletedDates.Any(d => d.Date == date.Date));

                    if (hasUncompletedNonRecurringTask)
                    {
                        return mainWin.GetThemeBrush("AccentLightBrush");
                    }
                }
            }
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        // MultiBinding 版本：第二个值只是一个刷新令牌，用来确保任务完成状态
        // 变化后，日历上的小点会重新计算，而不依赖 WPF 是否重建 CalendarDayButton。
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null && values.Length > 0 && values[0] is DateTime date)
            {
                return Convert(date, targetType, parameter, culture);
            }

            return Brushes.Transparent;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 悬浮窗 ToolTip 转换器：当鼠标移到日期上时，显示当天的任务详情
    public class TaskDayToToolTipConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (Application.Current.MainWindow is not MainWindow mainWin || value == null)
                return null;

            DateTime targetDate;

            if (value is DateTime dt)
            {
                targetDate = dt;
            }
            else if (DateTime.TryParse(value.ToString(), out DateTime parsedDate))
            {
                targetDate = parsedDate;
            }
            else
            {
                return null;
            }

            var tasks = mainWin.GetTasksForDate(targetDate);
            if (tasks != null && tasks.Any())
            {
                var lines = tasks.Select(t =>
                {
                    bool isCompleted = t.CompletedDates.Contains(targetDate.Date);
                    string statusMark = isCompleted ? "[✓]" : "[ ]";
                    return $"{statusMark} {t.Title}";
                });
                return $"{targetDate:yyyy-MM-dd} 任务:\n" + string.Join("\n", lines);
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public partial class MainWindow : Window
    {
        // 每次任务状态发生变化时递增，用作日历小点的刷新触发器。
        public static readonly DependencyProperty CalendarRefreshVersionProperty =
            DependencyProperty.Register(nameof(CalendarRefreshVersion), typeof(int), typeof(MainWindow), new PropertyMetadata(0));

        public int CalendarRefreshVersion
        {
            get => (int)GetValue(CalendarRefreshVersionProperty);
            private set => SetValue(CalendarRefreshVersionProperty, value);
        }

        // Win32 API 用于修改窗口扩展样式
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // Win32 窗口扩展样式常量
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public class TaskDisplayModel
        {
            public TaskItemData Task { get; set; } = new TaskItemData();
            public DateTime DisplayDate { get; set; }
        }

        public class TaskItemData
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Title { get; set; } = string.Empty;
            public DateTime TargetDate { get; set; }
            public bool IsRecurring { get; set; }
            public string RecurrenceUnit { get; set; } = "Day";
            public int RecurrenceInterval { get; set; } = 1;
            public bool ShowInCalendar { get; set; } = true;
            public HashSet<DateTime> CompletedDates { get; set; } = new HashSet<DateTime>();
            public HashSet<DateTime> SkippedDates { get; set; } = new HashSet<DateTime>();
        }

        public class AppSettingsData
        {
            public bool IsEdgeHideEnabled { get; set; } = false;
            public bool IsAutoStartEnabled { get; set; } = false;
            public double Opacity { get; set; } = 1.0;
            
            // 外观主题：System (跟随系统), Dark (深色), Light (浅色)
            public string ThemeMode { get; set; } = "System";

            // --- 喝水提醒设定 ---
            public bool IsWaterReminderEnabled { get; set; } = false;
            public int WaterTimesPerDay { get; set; } = 8;
            public string WaterStartTime { get; set; } = "09:00";
            public int WaterIntervalHours { get; set; } = 1;
            public int WaterIntervalMinutes { get; set; } = 0;
            
            // 追踪状态
            public DateTime LastWaterReminderDate { get; set; } = DateTime.MinValue;
            public int WaterRemindersSentToday { get; set; } = 0;
        }

        private List<TaskItemData> _allTasks = new List<TaskItemData>();
        
        private readonly string _dataFilePath;
        private readonly string _settingsFilePath;
        
        private TaskItemData? _currentEditingTask = null;
        private AppSettingsData _currentSettings = new AppSettingsData();
        private DispatcherTimer? _edgeHideTimer;
        
        private DispatcherTimer? _midnightTimer;
        private DispatcherTimer? _waterTimer;
        private DateTime _lastCheckedDate = DateTime.Today;

        public MainWindow()
        {
            // 初始化 UI 组件
            InitializeComponent();

            // 主动初始化 CommunityToolkit 的 Toast Compat。
            // 这样程序启动后就会完成未打包 WPF 通知所需的注册，
            // 不再依赖手工创建 AUMID 快捷方式。
            try
            {
                _ = ToastNotificationManagerCompat.CreateToastNotifier();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Toast notification initialization failed: {ex}");
            }

            string appDataFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopCalendarWidget");
            if (!Directory.Exists(appDataFolder))
            {
                Directory.CreateDirectory(appDataFolder);
            }
            _dataFilePath = System.IO.Path.Combine(appDataFolder, "tasks.json");
            _settingsFilePath = System.IO.Path.Combine(appDataFolder, "settings.json");

            this.SourceInitialized += MainWindow_SourceInitialized;
            LoadTasks();
            LoadSettings();

            // 应用外观主题
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            this.Unloaded += (s, e) => SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;

            ApplyAutoStartRegistry(_currentSettings.IsAutoStartEnabled);

            InitEdgeHideTimer();
            MainCalendar.SelectedDate = DateTime.Today;

            InitMidnightTimer();
            InitWaterTimer();
        }

        #region 外观主题（深色/浅色/跟随系统）处理逻辑

        private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_currentSettings.ThemeMode == "System")
                    {
                        ApplyTheme();
                    }
                });
            }
        }

        public void ApplyTheme()
        {
            string mode = _currentSettings.ThemeMode ?? "System";
            bool isDark = true;

            if (mode == "System")
            {
                isDark = IsSystemInDarkMode();
            }
            else if (mode == "Light")
            {
                isDark = false;
            }
            else
            {
                isDark = true;
            }

            SetThemeResources(isDark);
            RefreshTaskList();
            RefreshCalendarView();
        }

        private bool IsSystemInDarkMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    object? registryValue = key.GetValue("AppsUseLightTheme");
                    if (registryValue != null)
                    {
                        return (int)registryValue == 0;
                    }
                }
            }
            catch { }
            return true;
        }

        private void SetThemeResources(bool isDark)
        {
            if (isDark)
            {
                SetBrush("WindowBg", "#18181A");
                SetBrush("CardBg", "#222226");
                SetBrush("ControlBg", "#2D2D30");
                SetBrush("ControlHoverBg", "#38383D");
                SetBrush("DropDownBg", "#1E1E22");
                SetBrush("BorderBrushKey", "#2D2D30");
                SetBrush("SubBorderBrushKey", "#3F3F46");
                SetBrush("TextPrimary", "#F0F0F0");
                SetBrush("TextSecondary", "#A0A0A5");
                SetBrush("TextMuted", "#71717A");
                SetBrush("ThumbBg", "#3F3F46");
                SetBrush("AccentBrush", "#2563EB");
                SetBrush("AccentLightBrush", "#60A5FA");
                SetBrush("CalendarHeaderBtnText", "#A0A0A5");
            }
            else
            {
                SetBrush("WindowBg", "#F3F4F6");
                SetBrush("CardBg", "#FFFFFF");
                SetBrush("ControlBg", "#E5E7EB");
                SetBrush("ControlHoverBg", "#D1D5DB");
                SetBrush("DropDownBg", "#FFFFFF");
                SetBrush("BorderBrushKey", "#E5E7EB");
                SetBrush("SubBorderBrushKey", "#D1D5DB");
                SetBrush("TextPrimary", "#111827");
                SetBrush("TextSecondary", "#4B5563");
                SetBrush("TextMuted", "#9CA3AF");
                SetBrush("ThumbBg", "#C1C1C1");
                SetBrush("AccentBrush", "#2563EB");
                SetBrush("AccentLightBrush", "#2563EB");
                SetBrush("CalendarHeaderBtnText", "#4B5563");
            }
        }

        private void SetBrush(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            this.Resources[key] = brush;
        }

        public Brush GetThemeBrush(string key)
        {
            if (this.Resources.Contains(key) && this.Resources[key] is Brush brush)
                return brush;
            return Brushes.Gray;
        }

        private void CmbThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || cmbThemeMode.SelectedItem is not ComboBoxItem selectedItem) return;
            string newTheme = selectedItem.Tag?.ToString() ?? "System";
            if (_currentSettings.ThemeMode != newTheme)
            {
                _currentSettings.ThemeMode = newTheme;
                SaveSettings();
                ApplyTheme();
            }
        }

        #endregion

        private void InitMidnightTimer()
        {
            _midnightTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            
            _midnightTimer.Tick += (s, e) =>
            {
                DateTime today = DateTime.Today;

                if (today != _lastCheckedDate)
                {
                    if (MainCalendar.SelectedDate == _lastCheckedDate)
                    {
                        MainCalendar.SelectedDate = today;
                    }
                    else
                    {
                        RefreshCalendarView();
                    }

                    _lastCheckedDate = today;
                }
            };
            
            _midnightTimer.Start();
        }

        #region 喝水提醒逻辑

        private void InitWaterTimer()
        {
            _waterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _waterTimer.Tick += (s, e) => CheckWaterReminder();
            _waterTimer.Start();
        }

        private void CheckWaterReminder()
        {
            if (!_currentSettings.IsWaterReminderEnabled) return;

            DateTime now = DateTime.Now;
            
            if (_currentSettings.LastWaterReminderDate.Date != now.Date)
            {
                _currentSettings.WaterRemindersSentToday = 0;
                _currentSettings.LastWaterReminderDate = now.Date;
                SaveSettings();
            }

            if (_currentSettings.WaterRemindersSentToday >= _currentSettings.WaterTimesPerDay) return;

            if (!TimeSpan.TryParse(_currentSettings.WaterStartTime, out TimeSpan startTime))
                startTime = new TimeSpan(9, 0, 0);

            TimeSpan interval = new TimeSpan(_currentSettings.WaterIntervalHours, _currentSettings.WaterIntervalMinutes, 0);
            if (interval.TotalMinutes <= 0) return;

            DateTime todayStart = now.Date.Add(startTime);
            DateTime expectedNext = todayStart.Add(TimeSpan.FromMinutes(interval.TotalMinutes * _currentSettings.WaterRemindersSentToday));

            if (now >= expectedNext)
            {
                ShowWaterReminder();
                _currentSettings.WaterRemindersSentToday++;

                while (_currentSettings.WaterRemindersSentToday < _currentSettings.WaterTimesPerDay)
                {
                    DateTime next = todayStart.Add(TimeSpan.FromMinutes(interval.TotalMinutes * _currentSettings.WaterRemindersSentToday));
                    if (now >= next)
                        _currentSettings.WaterRemindersSentToday++;
                    else
                        break;
                }
                SaveSettings();
            }
        }

        private void ShowWaterReminder()
        {
            try
            {
                // CommunityToolkit 的 ToastNotificationManagerCompat 会为未打包的 WPF
                // 应用自动完成通知所需的注册，不需要手动创建 AUMID 快捷方式。
                // 先显式创建 Compat notifier，确保注册已经完成。
                _ = ToastNotificationManagerCompat.CreateToastNotifier();

                new ToastContentBuilder()
                    .AddText("水精灵提醒您该喝水咯(∠・ω< )⌒★")
                    .AddText("为了您的健康，请及时补充水分！最好顺便起来走动走动！")
                    .Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Windows 通知发送失败：\n\n{ex.Message}\n\n" +
                    "请确认 Windows 通知功能已开启，并重新启动本程序。",
                    "通知发送失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                System.Diagnostics.Debug.WriteLine($"Toast notification failed: {ex}");
            }
        }

        #endregion

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        public List<TaskItemData> GetTasksForDate(DateTime date)
        {
            DateTime pureDate = date.Date;
            return _allTasks.Where(task => IsTaskMatchDate(task, pureDate)).ToList();
        }

        private void RefreshCalendarView()
        {
            if (MainCalendar == null) return;

            // 让 Converter 重新计算。
            CalendarRefreshVersion++;

            var current = MainCalendar.SelectedDate;
            MainCalendar.SelectedDate = null;
            MainCalendar.SelectedDate = current;

            // WPF Calendar 的 CalendarDayButton 有时不会因为内部任务数据变化
            // 自动重绘模板里的 MultiBinding，因此再主动刷新已经生成的 Ellipse。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainCalendar.UpdateLayout();
                RefreshCalendarDotBindings(MainCalendar);
            }), DispatcherPriority.Loaded);
        }

        private static void RefreshCalendarDotBindings(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is Ellipse ellipse)
                {
                    var expression = BindingOperations.GetBindingExpressionBase(ellipse, Ellipse.FillProperty);
                    expression?.UpdateTarget();
                }

                RefreshCalendarDotBindings(child);
            }
        }

        #region 抽屉动画控制 (Drawer Animation)

        private void OpenTaskEditDrawer(TaskItemData? taskToEdit)
        {
            _currentEditingTask = taskToEdit;
            bool isEditMode = taskToEdit != null;

            lblDrawerTitle.Text = isEditMode ? "修改任务" : "新建任务";
            txtTitle.Text = isEditMode ? taskToEdit!.Title : "新任务";
            
            dpTaskDate.SelectedDate = isEditMode ? taskToEdit!.TargetDate : (MainCalendar.SelectedDate ?? DateTime.Today);

            chkRecurring.IsChecked = isEditMode ? taskToEdit!.IsRecurring : false;
            txtInterval.Text = isEditMode ? taskToEdit!.RecurrenceInterval.ToString() : "1";
            chkShowInCalendar.IsChecked = isEditMode ? taskToEdit!.ShowInCalendar : true;

            if (isEditMode)
            {
                cmbUnit.SelectedIndex = taskToEdit!.RecurrenceUnit switch
                {
                    "Week" => 1,
                    "Month" => 2,
                    "Year" => 3,
                    _ => 0
                };
            }
            else
            {
                cmbUnit.SelectedIndex = 0;
            }

            AnimateDrawer(HistoryTransform, 650);
            AnimateDrawer(SettingsTransform, 650);
            AnimateDrawer(TaskEditTransform, 0);
        }

        private void OpenHistoryDrawer()
        {
            RefreshHistoryList();
            AnimateDrawer(TaskEditTransform, 650);
            AnimateDrawer(SettingsTransform, 650);
            AnimateDrawer(HistoryTransform, 0);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            chkEdgeHide.IsChecked = _currentSettings.IsEdgeHideEnabled;
            chkAutoStart.IsChecked = _currentSettings.IsAutoStartEnabled;
            if (sliderOpacity != null)
            {
                sliderOpacity.Value = _currentSettings.Opacity;
            }
            
            // 绑定主题模式
            string theme = _currentSettings.ThemeMode ?? "System";
            cmbThemeMode.SelectedIndex = theme switch
            {
                "Dark" => 1,
                "Light" => 2,
                _ => 0
            };

            // 绑定喝水设置项 UI
            chkWaterEnable.IsChecked = _currentSettings.IsWaterReminderEnabled;
            txtWaterTimes.Text = _currentSettings.WaterTimesPerDay.ToString();
            txtWaterStartTime.Text = _currentSettings.WaterStartTime;
            txtWaterHour.Text = _currentSettings.WaterIntervalHours.ToString();
            txtWaterMin.Text = _currentSettings.WaterIntervalMinutes.ToString();
            if (panelWaterConfig != null)
            {
                panelWaterConfig.IsEnabled = _currentSettings.IsWaterReminderEnabled;
                panelWaterConfig.Opacity = _currentSettings.IsWaterReminderEnabled ? 1.0 : 0.5;
            }

            AnimateDrawer(TaskEditTransform, 650);
            AnimateDrawer(HistoryTransform, 650);
            AnimateDrawer(SettingsTransform, 0);
        }

        private void CloseDrawers_Click(object sender, RoutedEventArgs e)
        {
            AnimateDrawer(TaskEditTransform, 650);
            AnimateDrawer(HistoryTransform, 650);
            AnimateDrawer(SettingsTransform, 650);
            _currentEditingTask = null;
        }

        private void AnimateDrawer(TranslateTransform transform, double targetX)
        {
            DoubleAnimation anim = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void ChkRecurring_Changed(object sender, RoutedEventArgs e)
        {
            if (panelRecurring == null) return;
            bool isChecked = chkRecurring.IsChecked ?? false;
            panelRecurring.IsEnabled = isChecked;
            panelRecurring.Opacity = isChecked ? 1.0 : 0.5;
        }

        #endregion

        #region 快捷日期点击逻辑

        private void SetToday_Click(object sender, RoutedEventArgs e)
        {
            dpTaskDate.SelectedDate = DateTime.Today;
        }

        private void SetTomorrow_Click(object sender, RoutedEventArgs e)
        {
            dpTaskDate.SelectedDate = DateTime.Today.AddDays(1);
        }

        private void SetNextWeek_Click(object sender, RoutedEventArgs e)
        {
            dpTaskDate.SelectedDate = DateTime.Today.AddDays(7);
        }

        private void SetNextMonth_Click(object sender, RoutedEventArgs e)
        {
            dpTaskDate.SelectedDate = DateTime.Today.AddMonths(1);
        }

        private void SetNextYear_Click(object sender, RoutedEventArgs e)
        {
            dpTaskDate.SelectedDate = DateTime.Today.AddYears(1);
        }

        private void SetNDaysLater_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtNDays.Text, out int days))
            {
                dpTaskDate.SelectedDate = DateTime.Today.AddDays(days);
            }
            else
            {
                MessageBox.Show("请输入有效的天数！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        #region 按钮事件处理

        private void AddTask_Click(object sender, RoutedEventArgs e)
        {
            OpenTaskEditDrawer(null);
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            OpenHistoryDrawer();
        }

        private void ConfirmTask_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtTitle.Text))
            {
                MessageBox.Show("任务名称不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int.TryParse(txtInterval.Text, out int interval);
            if (interval < 1) interval = 1;

            string unitStr = cmbUnit.SelectedIndex switch
            {
                1 => "Week",
                2 => "Month",
                3 => "Year",
                _ => "Day"
            };

            DateTime selectedTargetDate = dpTaskDate.SelectedDate ?? DateTime.Today;
            bool isRecurring = chkRecurring.IsChecked ?? false;
            bool showInCalendar = chkShowInCalendar.IsChecked ?? true;

            if (_currentEditingTask != null)
            {
                _currentEditingTask.Title = txtTitle.Text;
                _currentEditingTask.TargetDate = selectedTargetDate;
                _currentEditingTask.IsRecurring = isRecurring;
                _currentEditingTask.RecurrenceInterval = interval;
                _currentEditingTask.RecurrenceUnit = unitStr;
                _currentEditingTask.ShowInCalendar = showInCalendar;
            }
            else
            {
                _allTasks.Add(new TaskItemData
                {
                    Title = txtTitle.Text,
                    TargetDate = selectedTargetDate,
                    IsRecurring = isRecurring,
                    RecurrenceInterval = interval,
                    RecurrenceUnit = unitStr,
                    ShowInCalendar = showInCalendar
                });
            }

            MainCalendar.SelectedDate = selectedTargetDate;

            SaveTasks();
            RefreshTaskList();
            RefreshCalendarView();
            CloseDrawers_Click(sender, e);
        }

        #endregion

        #region 视图刷新逻辑

        private void MainCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshTaskList();
        }

        private void RefreshTaskList()
        {
            if (TaskListPanel == null) return;
            TaskListPanel.Children.Clear();

            DateTime selectedDate = (MainCalendar.SelectedDate ?? DateTime.Today).Date;

            var todayTasks = _allTasks
                .Where(t => IsTaskMatchDate(t, selectedDate))
                .OrderBy(t => t.CompletedDates.Contains(selectedDate))
                .Select(t => new TaskDisplayModel { Task = t, DisplayDate = selectedDate })
                .ToList();

            var todayTaskIds = todayTasks.Select(t => t.Task.Id).ToHashSet();

            var pastUnfinishedTasks = new List<TaskDisplayModel>();
            foreach (var t in _allTasks)
            {
                if (todayTaskIds.Contains(t.Id)) continue;
                if (t.TargetDate.Date < selectedDate)
                {
                    DateTime? lastUnfinishedDate = GetLastUnfinishedDateBefore(t, selectedDate);
                    if (lastUnfinishedDate.HasValue)
                    {
                        pastUnfinishedTasks.Add(new TaskDisplayModel
                        {
                            Task = t,
                            DisplayDate = lastUnfinishedDate.Value
                        });
                    }
                }
            }

            var futureTasks = new List<TaskDisplayModel>();
            foreach (var t in _allTasks)
            {
                if (todayTaskIds.Contains(t.Id)) continue;

                DateTime? nextDate = GetNextMatchDateAfter(t, selectedDate);
                if (nextDate.HasValue)
                {
                    futureTasks.Add(new TaskDisplayModel
                    {
                        Task = t,
                        DisplayDate = nextDate.Value
                    });
                }
            }
            futureTasks = futureTasks.OrderBy(t => t.DisplayDate).ToList();

            AddTaskCategorySection("以前的任务 (逾期)", pastUnfinishedTasks, selectedDate, isExpandedByDefault: false, showDateLabel: true);
            AddTaskCategorySection("今日任务", todayTasks, selectedDate, isExpandedByDefault: true, showDateLabel: false);
            AddTaskCategorySection("未来任务", futureTasks, selectedDate, isExpandedByDefault: false, showDateLabel: true);
        }

        private DateTime? GetLastUnfinishedDateBefore(TaskItemData task, DateTime selectedDate)
        {
            if (!task.IsRecurring)
            {
                if (task.TargetDate.Date < selectedDate && !task.CompletedDates.Contains(task.TargetDate.Date))
                {
                    return task.TargetDate.Date;
                }
                return null;
            }

            for (DateTime d = selectedDate.AddDays(-1); d >= task.TargetDate.Date; d = d.AddDays(-1))
            {
                if (IsTaskMatchDate(task, d) && !task.CompletedDates.Contains(d))
                {
                    return d;
                }
            }
            return null;
        }

        private DateTime? GetNextMatchDateAfter(TaskItemData task, DateTime selectedDate)
        {
            if (!task.IsRecurring)
            {
                return task.TargetDate.Date > selectedDate ? task.TargetDate.Date : null;
            }

            DateTime start = selectedDate.Date > task.TargetDate.Date ? selectedDate.Date.AddDays(1) : task.TargetDate.Date;
            DateTime maxLimit = selectedDate.Date.AddYears(5);

            for (DateTime d = start; d <= maxLimit; d = d.AddDays(1))
            {
                if (IsTaskMatchDate(task, d))
                {
                    return d;
                }
            }
            return null;
        }

        private void AddTaskCategorySection(string categoryTitle, List<TaskDisplayModel> displayTasks, DateTime selectedDate, bool isExpandedByDefault, bool showDateLabel)
        {
            Expander categoryExpander = new Expander
            {
                Header = $"{categoryTitle} ({displayTasks.Count})",
                IsExpanded = isExpandedByDefault && displayTasks.Count > 0,
                Foreground = GetThemeBrush("TextSecondary"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            if (displayTasks.Count == 0)
            {
                categoryExpander.Content = new TextBlock
                {
                    Text = "暂无任务",
                    Foreground = GetThemeBrush("TextMuted"),
                    FontSize = 11,
                    Margin = new Thickness(8, 4, 0, 8)
                };
                TaskListPanel.Children.Add(categoryExpander);
                return;
            }

            StackPanel itemContainer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            foreach (var item in displayTasks)
            {
                var task = item.Task;
                DateTime taskItemDate = item.DisplayDate.Date;
                bool isCompleted = task.CompletedDates.Contains(taskItemDate);

                Border taskCard = new Border
                {
                    Background = GetThemeBrush("CardBg"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                Grid cardGrid = new Grid();
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                CheckBox chkStatus = new CheckBox
                {
                    IsChecked = isCompleted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand
                };

                StackPanel spText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

                TextBlock txtTitle = new TextBlock
                {
                    Text = task.Title,
                    Foreground = isCompleted ? GetThemeBrush("TextMuted") : GetThemeBrush("TextPrimary"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    TextDecorations = isCompleted ? TextDecorations.Strikethrough : null
                };
                spText.Children.Add(txtTitle);

                if (task.IsRecurring)
                {
                    spText.Children.Add(new TextBlock
                    {
                        Text = $"🔁 每 {task.RecurrenceInterval} {task.RecurrenceUnit}",
                        Foreground = isCompleted ? GetThemeBrush("TextMuted") : GetThemeBrush("AccentLightBrush"),
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }

                chkStatus.Click += (s, ev) =>
                {
                    if (chkStatus.IsChecked == true)
                    {
                        task.CompletedDates.Add(taskItemDate);
                    }
                    else
                    {
                        task.CompletedDates.Remove(taskItemDate);
                    }
                    SaveTasks();
                    RefreshTaskList();
                    RefreshCalendarView();
                };

                Grid.SetColumn(chkStatus, 0);
                Grid.SetColumn(spText, 1);
                cardGrid.Children.Add(chkStatus);
                cardGrid.Children.Add(spText);

                if (showDateLabel)
                {
                    TextBlock txtDateLabel = new TextBlock
                    {
                        Text = item.DisplayDate.ToString("M-d"),
                        Foreground = isCompleted ? GetThemeBrush("TextMuted") : GetThemeBrush("TextSecondary"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 2, 0)
                    };
                    Grid.SetColumn(txtDateLabel, 2);
                    cardGrid.Children.Add(txtDateLabel);
                }

                taskCard.Child = cardGrid;

                ContextMenu contextMenu = new ContextMenu();
                MenuItem menuEdit = new MenuItem { Header = "编辑任务" };
                menuEdit.Click += (s, ev) => OpenTaskEditDrawer(task);
                contextMenu.Items.Add(menuEdit);

                if (task.IsRecurring)
                {
                    MenuItem menuDeleteToday = new MenuItem { Header = "仅删除本日任务" };
                    menuDeleteToday.Click += (s, ev) =>
                    {
                        task.SkippedDates.Add(taskItemDate);
                        SaveTasks();
                        RefreshTaskList();
                        RefreshCalendarView();
                    };

                    MenuItem menuDeleteAll = new MenuItem { Header = "删除整个循环任务" };
                    menuDeleteAll.Click += (s, ev) =>
                    {
                        _allTasks.Remove(task);
                        SaveTasks();
                        RefreshTaskList();
                        RefreshCalendarView();
                    };

                    contextMenu.Items.Add(menuDeleteToday);
                    contextMenu.Items.Add(menuDeleteAll);
                }
                else
                {
                    MenuItem menuDelete = new MenuItem { Header = "删除任务" };
                    menuDelete.Click += (s, ev) =>
                    {
                        _allTasks.Remove(task);
                        SaveTasks();
                        RefreshTaskList();
                        RefreshCalendarView();
                    };
                    contextMenu.Items.Add(menuDelete);
                }

                taskCard.ContextMenu = contextMenu;
                itemContainer.Children.Add(taskCard);
            }

            categoryExpander.Content = itemContainer;
            TaskListPanel.Children.Add(categoryExpander);
        }

        private void RefreshHistoryList()
        {
            if (HistoryListPanel == null) return;
            HistoryListPanel.Children.Clear();

            var historyRecords = new List<(TaskItemData Task, DateTime Date)>();
            foreach (var task in _allTasks)
            {
                foreach (var date in task.CompletedDates)
                {
                    historyRecords.Add((task, date));
                }
            }

            historyRecords = historyRecords.OrderByDescending(r => r.Date).ToList();

            if (historyRecords.Count == 0)
            {
                HistoryListPanel.Children.Add(new TextBlock
                {
                    Text = "暂无打卡记录~",
                    Foreground = GetThemeBrush("TextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 30, 0, 0)
                });
                return;
            }

            foreach (var record in historyRecords)
            {
                Border itemCard = new Border
                {
                    Background = GetThemeBrush("CardBg"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                Grid cardGrid = new Grid();
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                StackPanel spInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                spInfo.Children.Add(new TextBlock
                {
                    Text = record.Task.Title,
                    Foreground = GetThemeBrush("TextPrimary"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12
                });
                spInfo.Children.Add(new TextBlock
                {
                    Text = $"📅 {record.Date:yyyy-MM-dd}",
                    Foreground = GetThemeBrush("AccentLightBrush"),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });

                Button btnUndo = new Button
                {
                    Content = "撤回",
                    Background = GetThemeBrush("AccentBrush"),
                    Foreground = Brushes.White,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(4, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    ToolTip = "恢复为未打卡状态"
                };
                btnUndo.Click += (s, ev) =>
                {
                    record.Task.CompletedDates.Remove(record.Date);
                    SaveTasks();
                    RefreshHistoryList();
                    RefreshTaskList();
                    RefreshCalendarView();
                };

                Button btnDelete = new Button
                {
                    Content = "删除",
                    Background = (Brush?)new BrushConverter().ConvertFrom("#DC2626") ?? Brushes.Red,
                    Foreground = Brushes.White,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    ToolTip = "彻底删除此任务"
                };
                btnDelete.Click += (s, ev) =>
                {
                    _allTasks.Remove(record.Task);
                    SaveTasks();
                    RefreshHistoryList();
                    RefreshTaskList();
                    RefreshCalendarView();
                };

                Grid.SetColumn(spInfo, 0);
                Grid.SetColumn(btnUndo, 1);
                Grid.SetColumn(btnDelete, 2);

                cardGrid.Children.Add(spInfo);
                cardGrid.Children.Add(btnUndo);
                cardGrid.Children.Add(btnDelete);

                itemCard.Child = cardGrid;
                HistoryListPanel.Children.Add(itemCard);
            }
        }

        #endregion

        #region 设置与边缘隐藏逻辑

        private enum DockEdge { None, Left, Right, Top }
        private DockEdge _currentDockEdge = DockEdge.None;
        private bool _isHiding = false;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private void InitEdgeHideTimer()
        {
            _edgeHideTimer = new DispatcherTimer();
            _edgeHideTimer.Interval = TimeSpan.FromMilliseconds(150);
            _edgeHideTimer.Tick += (s, e) =>
            {
                if (!_currentSettings.IsEdgeHideEnabled || Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    CheckDockEdge();
                    return;
                }

                if (_currentDockEdge == DockEdge.None) return;

                GetCursorPos(out POINT point);
                Point windowScreenPos = PointToScreen(new Point(0, 0));
                
                bool isMouseOver = point.X >= windowScreenPos.X && 
                                point.X <= windowScreenPos.X + this.ActualWidth &&
                                point.Y >= windowScreenPos.Y && 
                                point.Y <= windowScreenPos.Y + this.ActualHeight;

                bool isDrawerOpen = TaskEditTransform.X == 0 || HistoryTransform.X == 0 || SettingsTransform.X == 0;

                if (isMouseOver || isDrawerOpen)
                {
                    if (_isHiding)
                    {
                        _isHiding = false;
                        ShowFromEdge();
                    }
                }
                else
                {
                    if (!_isHiding)
                    {
                        _isHiding = true;
                        HideToEdge();
                    }
                }
            };
            _edgeHideTimer.Start();
        }

        private void CheckDockEdge()
        {
            Rect workArea = SystemParameters.WorkArea;
            double threshold = 20.0;

            if (this.Left <= workArea.Left + threshold)
            {
                _currentDockEdge = DockEdge.Left;
            }
            else if (this.Left + this.ActualWidth >= workArea.Right - threshold)
            {
                _currentDockEdge = DockEdge.Right;
            }
            else if (this.Top <= workArea.Top + threshold)
            {
                _currentDockEdge = DockEdge.Top;
            }
            else
            {
                _currentDockEdge = DockEdge.None;
                _isHiding = false;
            }
        }

        private void HideToEdge()
        {
            Rect workArea = SystemParameters.WorkArea;
            double targetLeft = this.Left;
            double targetTop = this.Top;

            const double visibleThickness = 12.0;

            switch (_currentDockEdge)
            {
                case DockEdge.Left:
                    targetLeft = workArea.Left - this.ActualWidth + visibleThickness;
                    break;
                case DockEdge.Right:
                    targetLeft = workArea.Right - visibleThickness;
                    break;
                case DockEdge.Top:
                    targetTop = workArea.Top - this.ActualHeight + visibleThickness;
                    break;
            }

            StartWindowAnimation(targetLeft, targetTop);
        }

        private void ShowFromEdge()
        {
            Rect workArea = SystemParameters.WorkArea;
            double targetLeft = this.Left;
            double targetTop = this.Top;

            switch (_currentDockEdge)
            {
                case DockEdge.Left:
                    targetLeft = workArea.Left;
                    break;
                case DockEdge.Right:
                    targetLeft = workArea.Right - this.ActualWidth;
                    break;
                case DockEdge.Top:
                    targetTop = workArea.Top;
                    break;
            }

            StartWindowAnimation(targetLeft, targetTop);
        }

        private void StartWindowAnimation(double targetLeft, double targetTop)
        {
            if (Math.Abs(this.Left - targetLeft) < 1 && Math.Abs(this.Top - targetTop) < 1) return;

            DoubleAnimation animX = new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            DoubleAnimation animY = new DoubleAnimation(targetTop, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            animX.Completed += (s, e) =>
            {
                this.Left = targetLeft;
                this.BeginAnimation(Window.LeftProperty, null);
            };
            animY.Completed += (s, e) =>
            {
                this.Top = targetTop;
                this.BeginAnimation(Window.TopProperty, null);
            };

            this.BeginAnimation(Window.LeftProperty, animX);
            this.BeginAnimation(Window.TopProperty, animY);
        }

        private void SettingOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            _currentSettings.IsEdgeHideEnabled = chkEdgeHide.IsChecked ?? false;
            _currentSettings.IsAutoStartEnabled = chkAutoStart.IsChecked ?? false;

            if (!_currentSettings.IsEdgeHideEnabled && _isHiding)
            {
                _isHiding = false;
                ShowFromEdge();
                _currentDockEdge = DockEdge.None;
            }

            SaveSettings();
            ApplyAutoStartRegistry(_currentSettings.IsAutoStartEnabled);
        }
        
        private void WaterSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            
            bool isEnabled = chkWaterEnable.IsChecked ?? false;
            if (panelWaterConfig != null)
            {
                panelWaterConfig.IsEnabled = isEnabled;
                panelWaterConfig.Opacity = isEnabled ? 1.0 : 0.5;
            }
            
            _currentSettings.IsWaterReminderEnabled = isEnabled;
            SaveSettings();
        }

        private void WaterSetting_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            int.TryParse(txtWaterTimes.Text, out int times);
            if (times < 1) times = 1;
            txtWaterTimes.Text = times.ToString();
            
            if (!TimeSpan.TryParse(txtWaterStartTime.Text, out TimeSpan st))
            {
                txtWaterStartTime.Text = "09:00";
            }
            
            int.TryParse(txtWaterHour.Text, out int h);
            int.TryParse(txtWaterMin.Text, out int m);
            
            _currentSettings.WaterTimesPerDay = times;
            _currentSettings.WaterStartTime = txtWaterStartTime.Text;
            _currentSettings.WaterIntervalHours = h;
            _currentSettings.WaterIntervalMinutes = m;
            
            SaveSettings();
        }
        
        private void WaterInterval_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            
            int.TryParse(txtWaterHour.Text, out int hours);
            int.TryParse(txtWaterMin.Text, out int mins);
            
            if (mins >= 60)
            {
                hours += mins / 60;
                mins = mins % 60;
            }
            
            if (mins < 0) mins = 0;
            if (hours < 0) hours = 0;
            
            txtWaterHour.Text = hours.ToString();
            txtWaterMin.Text = mins.ToString();
            
            WaterSetting_LostFocus(sender, e);
        }

        private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;

            this.Opacity = e.NewValue;
            if (txtOpacityValue != null)
            {
                txtOpacityValue.Text = $"{(int)(e.NewValue * 100)}%";
            }

            _currentSettings.Opacity = e.NewValue;
            SaveSettings();
        }
       
        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            _edgeHideTimer?.Stop();
            _midnightTimer?.Stop();
            _waterTimer?.Stop();
            Application.Current.Shutdown();
        }

        #endregion

        #region 数据与周期计算逻辑

        private bool IsTaskMatchDate(TaskItemData task, DateTime queryDate)
        {
            DateTime pureDate = queryDate.Date;

            if (task.SkippedDates != null && task.SkippedDates.Contains(pureDate))
            {
                return false;
            }

            if (!task.IsRecurring)
            {
                return task.TargetDate.Date == pureDate;
            }

            if (pureDate < task.TargetDate.Date)
            {
                return false;
            }

            TimeSpan diff = pureDate - task.TargetDate.Date;
            switch (task.RecurrenceUnit)
            {
                case "Day":
                    return diff.Days % task.RecurrenceInterval == 0;
                case "Week":
                    return (diff.Days / 7) % task.RecurrenceInterval == 0 && diff.Days % 7 == 0;
                case "Month":
                    int monthDiff = (pureDate.Year - task.TargetDate.Year) * 12 + (pureDate.Month - task.TargetDate.Month);
                    return monthDiff >= 0 && monthDiff % task.RecurrenceInterval == 0 && pureDate.Day == task.TargetDate.Day;
                case "Year":
                    int yearDiff = pureDate.Year - task.TargetDate.Year;
                    return yearDiff >= 0 && yearDiff % task.RecurrenceInterval == 0 && pureDate.Month == task.TargetDate.Month && pureDate.Day == task.TargetDate.Day;
                default:
                    return false;
            }
        }

        private void SaveTasks()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_allTasks, options);
                File.WriteAllText(_dataFilePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存数据失败: {ex.Message}");
            }
        }

        private void LoadTasks()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    string json = File.ReadAllText(_dataFilePath);
                    _allTasks = JsonSerializer.Deserialize<List<TaskItemData>>(json) ?? new List<TaskItemData>();
                }
            }
            catch
            {
                _allTasks = new List<TaskItemData>();
            }
        }

        private void SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_currentSettings, options);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    _currentSettings = JsonSerializer.Deserialize<AppSettingsData>(json) ?? new AppSettingsData();
                }
            }
            catch
            {
                _currentSettings = new AppSettingsData();
            }

            if (_currentSettings.Opacity < 0.1 || _currentSettings.Opacity > 1.0)
            {
                _currentSettings.Opacity = 1.0;
            }
            this.Opacity = _currentSettings.Opacity;
        }

        private void ApplyAutoStartRegistry(bool enable)
        {
            const string appName = "DesktopCalendarWidget";
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    if (enable)
                    {
                        string exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopCalendarWidget.exe");
                        key.SetValue(appName, $"\"{exePath}\"");
                    }
                    else
                    {
                        if (key.GetValue(appName) != null) key.DeleteValue(appName);
                    }
                }
            }
            catch { }
        }

        #endregion
    }
}
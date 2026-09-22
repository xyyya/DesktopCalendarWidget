using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.ComponentModel;
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
                        MainWindow.IsTaskCompletedOnDate(t, date));

                    if (allCompleted)
                    {
                        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"));
                    }

                    // 蓝色仍然只显示“日历中可见的、未完成的普通任务”。
                    // 循环任务无论是否完成，都不会单独触发蓝点。
                    bool hasUncompletedNonRecurringTask = allTasksForDate
                        .Where(t => t.ShowInCalendar)
                        .Any(t => !t.IsRecurring &&
                                  !MainWindow.IsTaskCompletedOnDate(t, date));

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
                    bool isCompleted = MainWindow.IsTaskCompletedOnDate(t, targetDate.Date);
                    string statusMark = isCompleted ? "[✓]" : "[ ]";
                    return $"{statusMark} {t.Title}";
                });
                return $"{targetDate:yyyy-MM-dd} {Localization.T("任务:")}\n" + string.Join("\n", lines);
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public partial class MainWindow : Window, INotifyPropertyChanged
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
            // For recurring tasks, record occurrences explicitly deleted for a specific day.
            // This is separate from SkippedDates so History can distinguish a deletion from a normal skip.
            public HashSet<DateTime> DeletedDates { get; set; } = new HashSet<DateTime>();
            // Deleted tasks remain in history and can be restored or permanently removed.
            public bool IsDeleted { get; set; } = false;
            public DateTime? DeletedAt { get; set; }
            // 新版层级分组：为空表示未分组；支持无限层级。
            public string? GroupId { get; set; }
        }

        public class TaskGroupData
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Name { get; set; } = "新分组";
            public string? ParentGroupId { get; set; }
        }

        public class NoteData
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Title { get; set; } = "新便签";
            public string Content { get; set; } = string.Empty;
            // 为空 = 独立便签；有值 = Task 专属便签。
            public string? TaskId { get; set; }
            public bool IsCompleted { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.Now;
            public DateTime UpdatedAt { get; set; } = DateTime.Now;
            public double? WindowLeft { get; set; }
            public double? WindowTop { get; set; }
            public double FontSize { get; set; } = 13;
            public string FontColor { get; set; } = "#FFFFFFFF";
        }

        public class AppSettingsData
        {
            public bool IsEdgeHideEnabled { get; set; } = false;
            public bool IsAutoStartEnabled { get; set; } = false;
            public double Opacity { get; set; } = 1.0;
            
            // 外观主题：System (跟随系统), Dark (深色), Light (浅色)
            public string ThemeMode { get; set; } = "System";

            // 界面语言：zh-CN / en-US
            public string Language { get; set; } = "en-US";

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
        private List<TaskGroupData> _allGroups = new List<TaskGroupData>();
        private List<NoteData> _allNotes = new List<NoteData>();

        public IReadOnlyList<TaskItemData> AllTasksForNotes => _allTasks;

        private TaskGroupData? _currentEditingGroup;
        private NoteData? _currentEditingNote;
        private readonly List<NoteWindow> _openNoteWindows = new List<NoteWindow>();
        private readonly List<CheckBox> _groupTaskChecks = new List<CheckBox>();
        
        private readonly string _dataFilePath;
        private readonly string _groupsFilePath;
        private readonly string _notesFilePath;
        private readonly string _settingsFilePath;
        
        private TaskItemData? _currentEditingTask = null;
        private AppSettingsData _currentSettings = new AppSettingsData();
        private DispatcherTimer? _edgeHideTimer;
        
        private DispatcherTimer? _midnightTimer;
        private DispatcherTimer? _waterTimer;
        private DateTime _lastCheckedDate = DateTime.Today;
        private bool _isApplyingLanguage;
        // 动态重建任务列表前保存 Expander 展开状态；同一分组可能同时出现在逾期/今日/未来多个区域，因此状态键包含 categoryKey。
        private readonly Dictionary<string, bool> _expanderStates = new Dictionary<string, bool>();
        private bool _isRefreshingTaskList;
        private bool _isRestoringExpanderStates;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string CalendarHeaderText
        {
            get
            {
                DateTime date = MainCalendar?.DisplayDate ?? DateTime.Today;
                CalendarMode mode = MainCalendar?.DisplayMode ?? CalendarMode.Month;

                if (mode == CalendarMode.Year)
                    return Localization.IsEnglish ? date.Year.ToString() : $"{date.Year}年";

                if (mode == CalendarMode.Decade)
                {
                    int decadeStart = (date.Year / 10) * 10;
                    return Localization.IsEnglish ? $"{decadeStart}s" : $"{decadeStart}年代";
                }

                if (!Localization.IsEnglish)
                    return $"{date.Year}年{date.Month}月";

                string[] monthNames =
                {
                    "January", "February", "March", "April", "May", "June",
                    "July", "August", "September", "October", "November", "December"
                };
                return $"{monthNames[date.Month - 1]} {date.Year}";
            }
        }

        private void RaisePropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public MainWindow()
        {
            // 初始化 UI 组件
            InitializeComponent();
            MainCalendar.DisplayDateChanged += (s, e) =>
            {
                RaisePropertyChanged(nameof(CalendarHeaderText));
                Dispatcher.BeginInvoke(new Action(RefreshCalendarLanguage), DispatcherPriority.Loaded);
            };
            MainCalendar.DisplayModeChanged += (s, e) =>
            {
                RaisePropertyChanged(nameof(CalendarHeaderText));
                Dispatcher.BeginInvoke(new Action(RefreshCalendarLanguage), DispatcherPriority.Loaded);
            };

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
            _groupsFilePath = System.IO.Path.Combine(appDataFolder, "groups.json");
            _notesFilePath = System.IO.Path.Combine(appDataFolder, "notes.json");
            _settingsFilePath = System.IO.Path.Combine(appDataFolder, "settings.json");

            this.SourceInitialized += MainWindow_SourceInitialized;
            LoadTasks();
            LoadGroups();
            LoadNotes();
            LoadSettings();

            // 启动时同步“开机自动启动”复选框状态。
            // 否则注册表启动成功后，重启程序时 UI 仍可能显示为未勾选。
            if (chkAutoStart != null)
            {
                chkAutoStart.IsChecked = _currentSettings.IsAutoStartEnabled;
            }

            // 应用外观主题与界面语言
            Localization.SetLanguage(_currentSettings.Language);
            ApplyTheme();
            ApplyLanguage();
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            this.Unloaded += (s, e) => SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;

            ApplyAutoStartRegistry(_currentSettings.IsAutoStartEnabled);

            MainCalendar.SelectedDate = DateTime.Today;

            InitMidnightTimer();
            InitWaterTimer();

            // 等窗口真正显示后再启动贴边隐藏计时器，避免启动瞬间把窗口
            // 判定为“贴边”并直接藏到屏幕外。
            this.ContentRendered += MainWindow_ContentRendered;
        }

        private void MainWindow_ContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= MainWindow_ContentRendered;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                InitEdgeHideTimer();
            }), DispatcherPriority.ApplicationIdle);
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
            foreach (Window window in Application.Current.Windows)
            {
                if (window is NoteWindow noteWindow) noteWindow.ApplyTheme();
            }
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

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingLanguage || !IsLoaded || cmbLanguage == null) return;

            // The language selector uses fixed native names. Selection is determined only
            // by index, so generic UI localization can never alter its visible text.
            string newLanguage = cmbLanguage.SelectedIndex == 1 ? "en-US" : "zh-CN";
            if (string.Equals(_currentSettings.Language, newLanguage, StringComparison.OrdinalIgnoreCase))
                return;

            _currentSettings.Language = newLanguage;
            Localization.SetLanguage(newLanguage);
            SaveSettings();
            ApplyLanguage();
        }

        private static string NormalizeLanguageCode(string? language)
        {
            if (string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "English", StringComparison.OrdinalIgnoreCase))
                return "en-US";

            if (string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "简体中文", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "Simplified Chinese", StringComparison.OrdinalIgnoreCase))
                return "zh-CN";

            return "en-US";
        }

        private void ApplyLanguage()
        {
            _isApplyingLanguage = true;
            try
            {
                _currentSettings.Language = NormalizeLanguageCode(_currentSettings.Language);
                Localization.SetLanguage(_currentSettings.Language);
                Localization.ApplyWpfLanguage(this);

                // Refresh the calendar header immediately whenever the language changes.
                RaisePropertyChanged(nameof(CalendarHeaderText));

                Localization.ApplyToVisualTree(this);

                // Resolve the language label by name instead of relying on a generated field.
                // This avoids a XAML name-field generation issue in some WPF build paths.
                if (FindName("lblLanguage") is TextBlock languageLabel)
                    languageLabel.Text = Localization.T("界面语言");

                // Language selector is intentionally excluded from generic localization.
                // Keep both native language names and select by index only.
                if (cmbLanguage != null)
                {
                    if (cmbLanguage.Items.Count >= 2)
                    {
                        if (cmbLanguage.Items[0] is ComboBoxItem zh) { zh.Content = "简体中文"; zh.Tag = "zh-CN"; }
                        if (cmbLanguage.Items[1] is ComboBoxItem en) { en.Content = "English"; en.Tag = "en-US"; }
                    }
                    cmbLanguage.SelectedIndex = Localization.IsEnglish ? 1 : 0;
                }

            if (cmbThemeMode != null)
            {
                if (cmbThemeMode.Items.Count >= 3)
                {
                    if (cmbThemeMode.Items[0] is ComboBoxItem system) system.Content = Localization.IsEnglish ? "Follow System" : "跟随系统";
                    if (cmbThemeMode.Items[1] is ComboBoxItem dark) dark.Content = Localization.IsEnglish ? "Dark" : "深色模式";
                    if (cmbThemeMode.Items[2] is ComboBoxItem light) light.Content = Localization.IsEnglish ? "Light" : "浅色模式";
                }
            }
            if (chkEdgeHide != null) chkEdgeHide.Content = Localization.T("贴边隐藏");
            if (chkAutoStart != null) chkAutoStart.Content = Localization.T("开机自动启动");
            if (chkWaterEnable != null) chkWaterEnable.Content = Localization.T("开启喝水提醒");
            if (chkRecurring != null) chkRecurring.Content = Localization.T("开启循环提醒");
            if (chkShowInCalendar != null) chkShowInCalendar.Content = Localization.T("在日历中提示小蓝点");
            if (lblDrawerTitle != null) lblDrawerTitle.Text = Localization.T(lblDrawerTitle.Text == "修改任务" || lblDrawerTitle.Text == "Edit Task" ? "修改任务" : "新建任务");
            if (lblGroupDrawerTitle != null) lblGroupDrawerTitle.Text = Localization.T(lblGroupDrawerTitle.Text == "编辑分组" || lblGroupDrawerTitle.Text == "Edit Group" ? "编辑分组" : "新建分组");

            // 自定义周标题使用 DynamicResource，语言切换时会即时刷新。
            string[] weekHeaders = Localization.IsEnglish
                ? new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }
                : new[] { "日", "一", "二", "三", "四", "五", "六" };
            for (int i = 0; i < weekHeaders.Length; i++)
                Resources[$"WeekHeader{i}"] = weekHeaders[i];

            string[] recurrenceLabels = Localization.IsEnglish
                ? new[] { "Day", "Week", "Month", "Year" }
                : new[] { "天", "周", "月", "年" };
            for (int i = 0; i < Math.Min(4, cmbUnit.Items.Count); i++)
            {
                if (cmbUnit.Items[i] is ComboBoxItem item)
                    item.Content = recurrenceLabels[i];
            }

            // WPF Calendar 的标题位于 CalendarItem ControlTemplate 内，
            // 每次语言/月份/显示模式变化都直接刷新模板里的可见 TextBlock。
            RefreshCalendarLanguage();

            lblWaterHourUnit.Text = Localization.IsEnglish ? " h " : " 时 ";
            lblWaterMinuteUnit.Text = Localization.IsEnglish ? " m " : " 分 ";

            // Translate UI defaults only; never alter user-entered task/group names.
            if (txtTitle != null && (txtTitle.Text == "新任务" || txtTitle.Text == "New Task"))
                txtTitle.Text = Localization.T("新任务");
            if (txtGroupName != null && (txtGroupName.Text == "新分组" || txtGroupName.Text == "New Group"))
                txtGroupName.Text = Localization.T("新分组");

            // ContextMenu 的 MenuItem 不一定会出现在普通 VisualTree 中。
            // 这里直接设置每一项，确保点击“+”打开菜单时始终跟随当前语言。
            if (FindResource("AddMenu") is ContextMenu addMenu)
            {
                if (addMenu.Items.Count > 0 && addMenu.Items[0] is MenuItem addTaskItem)
                    addTaskItem.Header = Localization.T("新建任务");
                if (addMenu.Items.Count > 1 && addMenu.Items[1] is MenuItem addGroupItem)
                    addGroupItem.Header = Localization.T("新建分组");
                if (addMenu.Items.Count > 2 && addMenu.Items[2] is MenuItem addSubGroupItem)
                    addSubGroupItem.Header = Localization.T("新建子分组");
            }

            // 动态生成区域使用当前语言重新绘制。
            RefreshTaskList();
            RefreshNotesList();
            RefreshHistoryList();

                foreach (Window window in Application.Current.Windows)
                {
                    if (window is NoteWindow noteWindow)
                        noteWindow.ApplyLanguage();
                }

                RaisePropertyChanged(nameof(CalendarHeaderText));
                if (MainCalendar != null)
                {
                    MainCalendar.Language = System.Windows.Markup.XmlLanguage.GetLanguage(Localization.CurrentLanguage);
                    RefreshCalendarLanguage();
                }
            }
            finally
            {
                _isApplyingLanguage = false;
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
                    .AddText(Localization.T("水精灵提醒您该喝水咯(∠・ω< )⌒★"))
                    .AddText(Localization.T("为了您的健康，请及时补充水分！最好顺便起来走动走动！"))
                    .Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"{Localization.T("通知发送失败")}：\n\n{ex.Message}\n\n" +
                    Localization.T("请确认 Windows 通知功能已开启，并重新启动本程序。"),
                    Localization.T("通知发送失败"),
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
            return _allTasks.Where(task => !task.IsDeleted && IsTaskMatchDate(task, pureDate)).ToList();
        }

        // 完成状态统一按“自然日”比较，而不是 DateTime 精确相等。
        // 这样历史数据即使保存了时间部分，日历小点与任务复选框也不会出现状态不一致。
        public static bool IsTaskCompletedOnDate(TaskItemData task, DateTime date)
        {
            return task.CompletedDates != null && task.CompletedDates.Any(d => d.Date == date.Date);
        }

        private void RememberExpanderState(Expander? exp)
        {
            if (_isRestoringExpanderStates) return;
            if (exp?.Tag is string key && !string.IsNullOrWhiteSpace(key))
                _expanderStates[key] = exp.IsExpanded;
        }

        private void CaptureExpanderStates(DependencyObject? parent)
        {
            if (parent == null) return;

            if (parent is Expander exp)
                RememberExpanderState(exp);

            if (parent is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                    CaptureExpanderStates(child);
            }

            // 收起时 Expander 的 Content 可能不在视觉树中，但仍存在于 Content 属性。
            if (parent is Expander expander && expander.Content is DependencyObject content)
                CaptureExpanderStates(content);
        }

        private void RememberAncestorExpanderStates(DependencyObject? child)
        {
            DependencyObject? current = child;
            while (current != null)
            {
                if (current is Expander exp)
                    RememberExpanderState(exp);
                current = VisualTreeHelper.GetParent(current);
            }
        }

        private bool ResolveExpanderExpanded(bool defaultExpanded, string? stateKey)
        {
            if (!string.IsNullOrWhiteSpace(stateKey) && _expanderStates.TryGetValue(stateKey, out bool previous))
                return previous;
            return defaultExpanded;
        }

        private void RefreshCalendarView()
        {
            if (MainCalendar == null) return;

            // 让 Converter 重新计算。
            CalendarRefreshVersion++;

            // 不再通过“SelectedDate = null → 恢复”强制刷新 Calendar。
            // 那种做法会触发 SelectedDatesChanged，从而再次重建任务列表，
            // 造成 Expander 状态被重复覆盖，表现为用户刚收起的分组又自动展开。
            // CalendarRefreshVersion 已经作为 MultiBinding 输入，足以让日期圆点重新计算。

            // WPF Calendar 的 CalendarDayButton 有时不会因为内部任务数据变化
            // 自动重绘模板里的 MultiBinding，因此再主动刷新已经生成的 Ellipse。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainCalendar.UpdateLayout();
                RefreshCalendarDotBindings(MainCalendar);
            }), DispatcherPriority.Loaded);
        }

        private void RefreshCalendarLanguage()
        {
            if (MainCalendar == null) return;

            ApplyCalendarHeaderText();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainCalendar == null) return;
                MainCalendar.UpdateLayout();
                ApplyCalendarHeaderText();
            }), DispatcherPriority.Loaded);

            // A Calendar can rebuild CalendarItem after the first layout pass (for example
            // after changing DisplayDate/Language). Re-apply at ContextIdle as a final pass.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainCalendar == null) return;
                MainCalendar.UpdateLayout();
                ApplyCalendarHeaderText();
            }), DispatcherPriority.ContextIdle);
        }

        private void ApplyCalendarHeaderText()
        {
            if (MainCalendar == null) return;

            MainCalendar.Language = System.Windows.Markup.XmlLanguage.GetLanguage(Localization.CurrentLanguage);
            MainCalendar.ApplyTemplate();

            // The header TextBlock lives inside CalendarItem's ControlTemplate. Set it
            // through the template namescope so WPF's internal Calendar code cannot replace
            // the visible text with stale culture-specific content.
            if (MainCalendar.Template?.FindName("PART_CalendarItem", MainCalendar) is CalendarItem calendarItem)
            {
                calendarItem.ApplyTemplate();
                if (calendarItem.Template?.FindName("CalendarHeaderTextBlock", calendarItem) is TextBlock header)
                    header.Text = CalendarHeaderText;
            }

            RaisePropertyChanged(nameof(CalendarHeaderText));
            MainCalendar.UpdateLayout();
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
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

            lblDrawerTitle.Text = isEditMode ? Localization.T("修改任务") : Localization.T("新建任务");
            txtTitle.Text = isEditMode ? taskToEdit!.Title : Localization.T("新任务");
            PopulateTaskGroupCombo(isEditMode ? taskToEdit!.GroupId : null);
            
            dpTaskDate.SelectedDate = isEditMode ? taskToEdit!.TargetDate : (MainCalendar.SelectedDate ?? DateTime.Today);

            chkRecurring.IsChecked = isEditMode ? taskToEdit!.IsRecurring : false;
            txtInterval.Text = isEditMode ? taskToEdit!.RecurrenceInterval.ToString() : "1";
            chkShowInCalendar.IsChecked = isEditMode ? taskToEdit!.ShowInCalendar : true;
            ChkRecurring_Changed(chkRecurring, new RoutedEventArgs());

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

            CloseAllDrawersExcept(TaskEditTransform);
            AnimateDrawer(TaskEditTransform, 0);
        }

        private void OpenHistoryDrawer()
        {
            RefreshHistoryList();
            CloseAllDrawersExcept(HistoryTransform);
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

            CloseAllDrawersExcept(SettingsTransform);
            AnimateDrawer(SettingsTransform, 0);
        }

        private void CloseDrawers_Click(object sender, RoutedEventArgs e)
        {
            AnimateDrawer(TaskEditTransform, 650);
            AnimateDrawer(HistoryTransform, 650);
            AnimateDrawer(SettingsTransform, 650);
            AnimateDrawer(GroupEditTransform, 650);
            AnimateDrawer(NotesTransform, 650);
            AnimateDrawer(NoteEditTransform, 650);
            _currentEditingTask = null;
            _currentEditingGroup = null;
            _currentEditingNote = null;
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
                MessageBox.Show(Localization.T("请输入有效的天数！"), Localization.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void PopulateTaskGroupCombo(string? selectedGroupId)
        {
            if (cmbTaskGroup == null) return;
            cmbTaskGroup.Items.Clear();
            cmbTaskGroup.Items.Add(new ComboBoxItem { Content = Localization.T("（未分组）"), Tag = "" });
            foreach (var g in GetGroupTreeRows(null))
            {
                cmbTaskGroup.Items.Add(new ComboBoxItem { Content = g.Text, Tag = g.Id });
            }
            foreach (ComboBoxItem item in cmbTaskGroup.Items)
            {
                if ((item.Tag?.ToString() ?? "") == (selectedGroupId ?? "")) { item.IsSelected = true; break; }
            }
        }

        private List<(string Id, string Text)> GetGroupTreeRows(string? parentId)
        {
            var result = new List<(string, string)>();
            foreach (var g in _allGroups.Where(g => g.ParentGroupId == parentId).OrderBy(g => g.Name))
            {
                AddGroupRowsRecursive(g, 0, result);
            }
            return result;
        }

        private void AddGroupRowsRecursive(TaskGroupData group, int depth, List<(string Id, string Text)> result)
        {
            result.Add((group.Id, new string('　', depth) + "📁 " + group.Name));
            foreach (var child in _allGroups.Where(g => g.ParentGroupId == group.Id).OrderBy(g => g.Name))
                AddGroupRowsRecursive(child, depth + 1, result);
        }

        private void PopulateParentGroupCombo(string? selectedParentId, string? excludedGroupId = null)
        {
            cmbParentGroup.Items.Clear();
            cmbParentGroup.Items.Add(new ComboBoxItem { Content = Localization.T("（顶层分组）"), Tag = "" });
            foreach (var row in GetGroupTreeRows(null))
            {
                // 编辑已有分组时，只排除“当前正在编辑的分组”及其后代。
                // 新建子分组时，selectedParentId 本身就是要挂载的父分组，不能把它排除掉。
                if (!string.IsNullOrWhiteSpace(excludedGroupId))
                {
                    if (row.Id == excludedGroupId) continue;
                    if (IsGroupDescendantOf(row.Id, excludedGroupId!)) continue;
                }
                cmbParentGroup.Items.Add(new ComboBoxItem { Content = row.Text, Tag = row.Id });
            }

            string wanted = selectedParentId ?? "";
            foreach (ComboBoxItem item in cmbParentGroup.Items)
            {
                if ((item.Tag?.ToString() ?? "") == wanted)
                {
                    item.IsSelected = true;
                    break;
                }
            }
            if (cmbParentGroup.SelectedIndex < 0) cmbParentGroup.SelectedIndex = 0;
        }

        private bool IsGroupDescendantOf(string groupId, string ancestorId)
        {
            string? parent = _allGroups.FirstOrDefault(g => g.Id == groupId)?.ParentGroupId;
            var visited = new HashSet<string>();
            while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent))
            {
                if (parent == ancestorId) return true;
                parent = _allGroups.FirstOrDefault(g => g.Id == parent)?.ParentGroupId;
            }
            return false;
        }

        private void AddMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not UIElement target)
                return;

            // ContextMenu 位于 Window.Resources，不能直接通过 x:Name 访问。
            // 每次打开时从资源中取出并设置当前按钮为 PlacementTarget。
            if (FindResource("AddMenu") is not ContextMenu menu)
                return;

            // ContextMenu 是共享资源，因此打开前必须确保它没有挂在其他
            // PlacementTarget 上；重新设置 PlacementTarget 即可安全复用。
            menu.PlacementTarget = target;

            // 菜单是共享资源，打开前再次同步语言，避免首次切换语言后仍显示中文。
            if (menu.Items.Count > 0 && menu.Items[0] is MenuItem addTaskItem)
                addTaskItem.Header = Localization.T("新建任务");
            if (menu.Items.Count > 1 && menu.Items[1] is MenuItem addGroupItem)
                addGroupItem.Header = Localization.T("新建分组");

            menu.IsOpen = true;
        }

        private void AddGroup_Click(object sender, RoutedEventArgs e)
        {
            _currentEditingGroup = null;
            lblGroupDrawerTitle.Text = Localization.T("新建分组");
            txtGroupName.Text = Localization.T("新分组");
            PopulateParentGroupCombo(null);
            PopulateGroupTaskChecks(null);
            CloseAllDrawersExcept(GroupEditTransform);
            AnimateDrawer(GroupEditTransform, 0);
        }

        private void CloseAllDrawersExcept(TranslateTransform except)
        {
            if (except != TaskEditTransform) AnimateDrawer(TaskEditTransform, 650);
            if (except != HistoryTransform) AnimateDrawer(HistoryTransform, 650);
            if (except != SettingsTransform) AnimateDrawer(SettingsTransform, 650);
            if (except != GroupEditTransform) AnimateDrawer(GroupEditTransform, 650);
            if (except != NotesTransform) AnimateDrawer(NotesTransform, 650);
            if (except != NoteEditTransform) AnimateDrawer(NoteEditTransform, 650);
        }

        private void PopulateGroupTaskChecks(string? groupId)
        {
            if (GroupTaskCheckPanel == null) return;
            GroupTaskCheckPanel.Children.Clear();
            _groupTaskChecks.Clear();
            DateTime date = (MainCalendar?.SelectedDate ?? DateTime.Today).Date;
            var tasks = GetTasksForDate(date).OrderBy(t => t.Title).ToList();
            if (tasks.Count == 0)
            {
                GroupTaskCheckPanel.Children.Add(new TextBlock { Text = Localization.T("当前日期没有任务"), Foreground = GetThemeBrush("TextMuted"), FontSize = 10, Margin = new Thickness(2, 4, 0, 4) });
                return;
            }
            foreach (var task in tasks)
            {
                var cb = new CheckBox
                {
                    Style = (Style)FindResource("CompactCheckBox"),
                    Tag = task,
                    IsChecked = groupId != null && task.GroupId == groupId,
                    Foreground = GetThemeBrush("TextPrimary"),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 1, 0, 1),
                    Cursor = Cursors.Hand,
                    Content = task.Title
                };
                _groupTaskChecks.Add(cb);
                GroupTaskCheckPanel.Children.Add(cb);
            }
        }

        private void ConfirmGroup_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtGroupName.Text)) { MessageBox.Show(Localization.T("分组名称不能为空！")); return; }
            string? parentId = (cmbParentGroup.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(parentId)) parentId = null;
            if (_currentEditingGroup != null)
            {
                _currentEditingGroup.Name = txtGroupName.Text.Trim();
                if (_currentEditingGroup.Id != parentId) _currentEditingGroup.ParentGroupId = parentId;
            }
            else
            {
                var newGroup = new TaskGroupData { Name = txtGroupName.Text.Trim(), ParentGroupId = parentId };
                _allGroups.Add(newGroup);
                foreach (var cb in _groupTaskChecks)
                    if (cb.IsChecked == true && cb.Tag is TaskItemData task) task.GroupId = newGroup.Id;
                SaveTasks();
            }
            if (_currentEditingGroup != null)
            {
                foreach (var cb in _groupTaskChecks)
                {
                    if (cb.Tag is not TaskItemData task) continue;
                    if (cb.IsChecked == true) task.GroupId = _currentEditingGroup.Id;
                    else if (task.GroupId == _currentEditingGroup.Id) task.GroupId = null;
                }
                SaveTasks();
            }
            SaveGroups(); RefreshTaskList(); CloseDrawers_Click(sender, e);
        }

        private void OpenGroupEditDrawer(TaskGroupData group)
        {
            _currentEditingGroup = group;
            lblGroupDrawerTitle.Text = Localization.T("编辑分组");
            txtGroupName.Text = group.Name;
            PopulateParentGroupCombo(group.ParentGroupId, group.Id);
            PopulateGroupTaskChecks(group.Id);
            CloseAllDrawersExcept(GroupEditTransform);
            AnimateDrawer(GroupEditTransform, 0);
        }

        private void Notes_Click(object sender, RoutedEventArgs e)
        {
            RefreshNotesList();
            CloseAllDrawersExcept(NotesTransform);
            AnimateDrawer(NotesTransform, 0);
        }

        private void AddNote_Click(object sender, RoutedEventArgs e)
        {
            var note = new NoteData();
            // 新建便签的默认名称随界面语言变化；用户自己修改后的名称永远不自动翻译。
            note.Title = Localization.T("新便签");
            _allNotes.Add(note);
            SaveNotes();
            RefreshNotesList();
            OpenNoteWindow(note);
        }

        private void PopulateNoteTaskCombo(string? selectedTaskId)
        {
            cmbNoteTask.Items.Clear();
            cmbNoteTask.Items.Add(new ComboBoxItem { Content = "📌 " + Localization.T("不关联任务"), Tag = "" });
            foreach (var task in _allTasks.Where(t => !t.IsDeleted).OrderBy(t => t.Title))
                cmbNoteTask.Items.Add(new ComboBoxItem { Content = "📌 " + task.Title, Tag = task.Id });
            foreach (ComboBoxItem item in cmbNoteTask.Items)
                if ((item.Tag?.ToString() ?? "") == (selectedTaskId ?? "")) { item.IsSelected = true; break; }
            if (cmbNoteTask.SelectedIndex < 0) cmbNoteTask.SelectedIndex = 0;
        }

        private void OpenNoteEditDrawer(NoteData? note, bool readOnly)
        {
            // 便签本体始终以独立悬浮窗显示；主窗口里的抽屉只负责便签列表。
            if (note == null)
            {
                note = new NoteData();
                _allNotes.Add(note);
                SaveNotes();
                RefreshNotesList();
            }
            OpenNoteWindow(note);
        }

        private void OpenNoteWindow(NoteData note)
        {
            var existing = _openNoteWindows.FirstOrDefault(w => ReferenceEquals(w.Note, note));
            if (existing != null)
            {
                existing.Activate();
                return;
            }

            var window = new NoteWindow(this, note);
            _openNoteWindows.Add(window);
            window.Closed += (_, _) => _openNoteWindows.Remove(window);
            window.Show();
            window.Activate();
        }

        private void CloseNoteEdit_Click(object sender, RoutedEventArgs e)
        {
            AnimateDrawer(NoteEditTransform, 650);
            _currentEditingNote = null;
        }

        public void SaveNoteFromWindow(NoteData note)
        {
            note.UpdatedAt = DateTime.Now;
            SyncLinkedNoteToTask(note);
            SaveNotes();
            SaveTasks();
            RefreshNotesList();
            RefreshTaskList();
            RefreshHistoryList();
            RefreshCalendarView();
        }

        public void DeleteNoteFromWindow(NoteData note)
        {
            if (!_allNotes.Remove(note)) return;
            SaveNotes();
            RefreshNotesList();
            RefreshTaskList();
            RefreshHistoryList();
            RefreshCalendarView();
        }

        public void SetNoteCompletionFromWindow(NoteData note, bool completed)
        {
            note.IsCompleted = completed;
            SyncLinkedNoteToTask(note);
            SaveNotes();
            SaveTasks();
            RefreshTaskList();
            RefreshHistoryList();
            RefreshCalendarView();
        }

        private void ConfirmNote_Click(object sender, RoutedEventArgs e)
        {
            // 兼容旧抽屉逻辑：实际编辑仍交给独立便签窗口。
            if (_currentEditingNote != null) OpenNoteWindow(_currentEditingNote);
            CloseNoteEdit_Click(sender, e);
        }

        private void SyncLinkedNoteToTask(NoteData note)
        {
            if (string.IsNullOrWhiteSpace(note.TaskId)) return;
            var task = _allTasks.FirstOrDefault(t => t.Id == note.TaskId);
            if (task == null) return;

            DateTime date = MainCalendar?.SelectedDate?.Date ?? DateTime.Today;
            if (!IsTaskMatchDate(task, date))
            {
                if (!task.IsRecurring) date = task.TargetDate.Date;
                else
                {
                    var next = GetNextMatchDateAfter(task, date.AddDays(-1));
                    if (!next.HasValue) return;
                    date = next.Value.Date;
                }
            }

            task.CompletedDates.RemoveWhere(d => d.Date == date.Date);
            if (note.IsCompleted) task.CompletedDates.Add(date.Date);
        }

        private bool HasNotesForTask(string taskId) => _allNotes.Any(n => n.TaskId == taskId);

        private void OpenTaskNotes(TaskItemData task)
        {
            var note = _allNotes.Where(n => n.TaskId == task.Id).OrderByDescending(n => n.UpdatedAt).FirstOrDefault();
            if (note == null)
            {
                note = new NoteData { Title = task.Title + " " + Localization.T("的便签"), TaskId = task.Id };
                _allNotes.Add(note);
                SaveNotes();
                RefreshTaskList();
            }
            OpenNoteWindow(note);
        }

        private void RefreshNotesList()
        {
            if (NotesListPanel == null) return;
            NotesListPanel.Children.Clear();
            var notes = _allNotes.Where(n => string.IsNullOrWhiteSpace(n.TaskId)).OrderByDescending(n => n.UpdatedAt).ToList();
            if (notes.Count == 0)
            {
                NotesListPanel.Children.Add(new TextBlock { Text = Localization.T("还没有独立便签"), Foreground = GetThemeBrush("TextMuted"), Margin = new Thickness(8, 25, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
                return;
            }

            foreach (var note in notes)
            {
                Border card = new Border { Background = GetThemeBrush("CardBg"), CornerRadius = new CornerRadius(8), Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 7), BorderBrush = GetThemeBrush("BorderBrushKey"), BorderThickness = new Thickness(1) };
                Grid grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel info = new StackPanel();
                info.Children.Add(new TextBlock { Text = (note.IsCompleted ? "✓ " : "") + note.Title, Foreground = GetThemeBrush("TextPrimary"), FontWeight = FontWeights.Bold });
                string preview = (note.Content ?? "").Replace("\r", "").Replace("\n", " ");
                if (preview.Length > 70) preview = preview[..70] + "…";
                info.Children.Add(new TextBlock { Text = preview, Foreground = GetThemeBrush("TextMuted"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                grid.Children.Add(info);

                Button open = new Button { Content = Localization.T("打开"), Padding = new Thickness(7, 3, 7, 3), Cursor = Cursors.Hand, Background = GetThemeBrush("ControlBg"), Foreground = GetThemeBrush("TextPrimary"), BorderThickness = new Thickness(0) };
                open.Click += (s, e) => OpenNoteWindow(note);
                Grid.SetColumn(open, 1);
                grid.Children.Add(open);
                card.Child = grid;

                ContextMenu menu = new ContextMenu();
                var edit = new MenuItem { Header = Localization.T("编辑便签") };
                edit.Click += (s, e) => OpenNoteWindow(note);
                var del = new MenuItem { Header = Localization.T("删除便签") };
                del.Click += (s, e) => DeleteNoteFromWindow(note);
                menu.Items.Add(edit);
                menu.Items.Add(del);
                card.ContextMenu = menu;
                NotesListPanel.Children.Add(card);
            }
        }

        private void ConfirmTask_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtTitle.Text))
            {
                MessageBox.Show(Localization.T("任务名称不能为空！"), Localization.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
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
            string? groupId = (cmbTaskGroup?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(groupId)) groupId = null;

            if (_currentEditingTask != null)
            {
                _currentEditingTask.Title = txtTitle.Text;
                _currentEditingTask.TargetDate = selectedTargetDate;
                _currentEditingTask.IsRecurring = isRecurring;
                _currentEditingTask.RecurrenceInterval = interval;
                _currentEditingTask.RecurrenceUnit = unitStr;
                _currentEditingTask.ShowInCalendar = showInCalendar;
                _currentEditingTask.GroupId = groupId;
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
                    ShowInCalendar = showInCalendar,
                    GroupId = groupId
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
            if (TaskListPanel == null || _isRefreshingTaskList) return;

            _isRefreshingTaskList = true;
            try
            {
                // 任务卡片刷新会重建所有 Expander；先记录当前状态，再重新创建。
                CaptureExpanderStates(TaskListPanel);
                var expanderStateSnapshot = new Dictionary<string, bool>(_expanderStates);
                TaskListPanel.Children.Clear();

            DateTime selectedDate = (MainCalendar.SelectedDate ?? DateTime.Today).Date;

            var todayTasks = _allTasks
                .Where(t => !t.IsDeleted && IsTaskMatchDate(t, selectedDate))
                .OrderBy(t => IsTaskCompletedOnDate(t, selectedDate))
                .Select(t => new TaskDisplayModel { Task = t, DisplayDate = selectedDate })
                .ToList();

            var todayTaskIds = todayTasks.Select(t => t.Task.Id).ToHashSet();

            var pastUnfinishedTasks = new List<TaskDisplayModel>();
            foreach (var t in _allTasks)
            {
                if (t.IsDeleted) continue;
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
                if (t.IsDeleted) continue;
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

                AddTaskCategorySection("overdue", Localization.T("逾期任务"), pastUnfinishedTasks, selectedDate, isExpandedByDefault: false, showDateLabel: true);
                AddTaskCategorySection("today", Localization.T("今日任务"), todayTasks, selectedDate, isExpandedByDefault: true, showDateLabel: false);
                AddTaskCategorySection("future", Localization.T("未来任务"), futureTasks, selectedDate, isExpandedByDefault: false, showDateLabel: true);

                // WPF's template binding can re-apply IsExpanded during measure/layout.
                // Restore the pre-refresh snapshot after the entire new tree has loaded.
                Dispatcher.BeginInvoke(new Action(() => RestoreExpanderStates(TaskListPanel, expanderStateSnapshot)), DispatcherPriority.ContextIdle);
            }
            finally
            {
                _isRefreshingTaskList = false;
            }
        }

        private void RestoreExpanderStates(Panel panel, IReadOnlyDictionary<string, bool> snapshot)
        {
            if (panel == null || snapshot == null || snapshot.Count == 0) return;

            _isRestoringExpanderStates = true;
            try
            {
                foreach (Expander exp in FindExpanders(panel))
                {
                    if (exp.Tag is string key && snapshot.TryGetValue(key, out bool state))
                        exp.IsExpanded = state;
                }
            }
            finally
            {
                _isRestoringExpanderStates = false;
            }
        }

        private static IEnumerable<Expander> FindExpanders(DependencyObject parent)
        {
            if (parent is Expander expander)
                yield return expander;

            int visualCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < visualCount; i++)
            {
                foreach (var nested in FindExpanders(VisualTreeHelper.GetChild(parent, i)))
                    yield return nested;
            }

            if (parent is Expander contentExpander && contentExpander.Content is DependencyObject content)
            {
                foreach (var nested in FindExpanders(content))
                    yield return nested;
            }
        }

        private DateTime? GetLastUnfinishedDateBefore(TaskItemData task, DateTime selectedDate)
        {
            if (!task.IsRecurring)
            {
                if (task.TargetDate.Date < selectedDate && !IsTaskCompletedOnDate(task, task.TargetDate.Date))
                {
                    return task.TargetDate.Date;
                }
                return null;
            }

            for (DateTime d = selectedDate.AddDays(-1); d >= task.TargetDate.Date; d = d.AddDays(-1))
            {
                if (IsTaskMatchDate(task, d) && !IsTaskCompletedOnDate(task, d))
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

        private Expander CreateStyledExpander(object header, bool expanded, double leftMargin, double bottomMargin, string? stateKey = null)
        {
            object headerContent;
            if (header is UIElement element)
            {
                // Group headers are already a StackPanel containing icon/title/count.
                // Do not call ToString() on it, otherwise WPF displays
                // "System.Windows.Controls.StackPanel" as the group name.
                headerContent = element;
            }
            else
            {
                headerContent = new TextBlock
                {
                    Text = header?.ToString() ?? string.Empty,
                    Foreground = GetThemeBrush("TextPrimary"),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var exp = new Expander
            {
                Header = headerContent,
                Tag = stateKey,
                Foreground = GetThemeBrush("TextPrimary"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(leftMargin, 0, 0, bottomMargin),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            exp.Template = (ControlTemplate)FindResource("ModernExpanderTemplate");
            exp.IsExpanded = ResolveExpanderExpanded(expanded, stateKey);

            bool handlersAttached = false;
            RoutedEventHandler loadedHandler = null!;
            loadedHandler = (s, e) =>
            {
                if (handlersAttached) return;

                // The ModernExpanderTemplate contains a TwoWay binding between
                // HeaderToggle.IsChecked and Expander.IsExpanded. During template
                // loading WPF can write a transient value back to IsExpanded.
                // Attach state handlers only after that initialization has settled.
                if (stateKey is string key && _expanderStates.TryGetValue(key, out bool previous))
                {
                    _isRestoringExpanderStates = true;
                    try { exp.IsExpanded = previous; }
                    finally { _isRestoringExpanderStates = false; }
                }
                else
                {
                    RememberExpanderState(exp);
                }

                exp.Expanded += (s2, e2) => RememberExpanderState(exp);
                exp.Collapsed += (s2, e2) => RememberExpanderState(exp);
                handlersAttached = true;
                exp.Loaded -= loadedHandler;
            };
            exp.Loaded += loadedHandler;
            return exp;
        }

        private void AddTaskCategorySection(string categoryKey, string categoryTitle, List<TaskDisplayModel> displayTasks, DateTime selectedDate, bool isExpandedByDefault, bool showDateLabel)
        {
            Expander categoryExpander = CreateStyledExpander(
                $"{categoryTitle} ({displayTasks.Count})",
                isExpandedByDefault && displayTasks.Count > 0, 0, 8, $"category:{categoryKey}");
            StackPanel container = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            bool showGroupsWhenEmpty = categoryTitle == Localization.T("今日任务") && _allGroups.Count > 0;
            if (displayTasks.Count == 0 && !showGroupsWhenEmpty)
            {
                container.Children.Add(new TextBlock { Text = Localization.T("暂无任务"), Foreground = GetThemeBrush("TextMuted"), FontSize = 11, Margin = new Thickness(8, 4, 0, 8) });
                categoryExpander.Content = container; TaskListPanel.Children.Add(categoryExpander); return;
            }
            var byGroup = displayTasks.Where(i => !string.IsNullOrWhiteSpace(i.Task.GroupId)).ToList();
            foreach (var root in _allGroups.Where(g => g.ParentGroupId == null).OrderBy(g => g.Name))
                AddGroupTaskSection(root, byGroup, container, 0, showDateLabel, categoryKey);
            foreach (var item in displayTasks.Where(i => string.IsNullOrWhiteSpace(i.Task.GroupId))) AddTaskCard(container, item, showDateLabel);
            categoryExpander.Content = container; TaskListPanel.Children.Add(categoryExpander);
        }

        private void AddGroupTaskSection(TaskGroupData group, List<TaskDisplayModel> items, Panel parent, int depth, bool showDateLabel, string categoryKey)
        {
            var direct = items.Where(i => i.Task.GroupId == group.Id).ToList();
            var children = _allGroups.Where(g => g.ParentGroupId == group.Id).OrderBy(g => g.Name).ToList();
            bool hasDisplayedDescendants = children.Any(c => GroupTreeHasTasks(c, items));
            bool showEmptyGroup = !showDateLabel;
            if (direct.Count == 0 && !hasDisplayedDescendants && !showEmptyGroup) return;

            bool isCompleted = (direct.Count + CountDisplayedChildTasks(group, items)) > 0 &&
                               direct.All(i => IsTaskCompletedOnDate(i.Task, i.DisplayDate.Date)) &&
                               children.Where(c => GroupTreeHasTasks(c, items)).All(c => IsGroupCompletedForDisplay(c, items));

            var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = new TextBlock { Text = isCompleted ? "✓ 📁" : "📁", FontSize = 12, Margin = new Thickness(0, 0, 5, 0), Foreground = isCompleted ? GetThemeBrush("TextMuted") : GetThemeBrush("AccentLightBrush") };
            var title = new TextBlock { Text = group.Name, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = isCompleted ? GetThemeBrush("TextMuted") : GetThemeBrush("TextPrimary"), TextDecorations = isCompleted ? TextDecorations.Strikethrough : null };
            var count = new TextBlock { Text = $" ({CountGroupTasks(group, items)})", FontSize = 10, Foreground = GetThemeBrush("TextMuted") };
            header.Children.Add(icon); header.Children.Add(title); header.Children.Add(count);

            Expander exp = CreateStyledExpander(header, depth < 1, depth * 8, 5, $"group:{categoryKey}:{group.Id}");
            StackPanel inner = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var item in direct) AddTaskCard(inner, item, showDateLabel);
            foreach (var child in children) AddGroupTaskSection(child, items, inner, depth + 1, showDateLabel, categoryKey);
            if (direct.Count == 0 && children.Count == 0)
                inner.Children.Add(new TextBlock { Text = Localization.T("暂无任务"), Foreground = GetThemeBrush("TextMuted"), FontSize = 10, Margin = new Thickness(10, 3, 0, 6) });
            exp.Content = inner;

            var menu = new ContextMenu();
            var addChild = new MenuItem { Header = Localization.T("新建子分组") };
            addChild.Click += (s, e) => { _currentEditingGroup = null; lblGroupDrawerTitle.Text = Localization.T("新建子分组"); txtGroupName.Text = Localization.T("新分组"); PopulateParentGroupCombo(group.Id, null); CloseAllDrawersExcept(GroupEditTransform); AnimateDrawer(GroupEditTransform, 0); };
            var editGroup = new MenuItem { Header = Localization.T("编辑分组") };
            editGroup.Click += (s, e) => OpenGroupEditDrawer(group);
            var delGroup = new MenuItem { Header = Localization.T("删除分组（保留任务）") };
            delGroup.Click += (s, e) => { foreach (var t in _allTasks.Where(t => t.GroupId == group.Id)) t.GroupId = group.ParentGroupId; foreach (var c in _allGroups.Where(g => g.ParentGroupId == group.Id)) c.ParentGroupId = group.ParentGroupId; _allGroups.Remove(group); SaveGroups(); SaveTasks(); RefreshTaskList(); };
            menu.Items.Add(addChild); menu.Items.Add(editGroup); menu.Items.Add(delGroup);
            exp.ContextMenu = menu;
            parent.Children.Add(exp);
        }

        private int CountDisplayedChildTasks(TaskGroupData group, List<TaskDisplayModel> items) =>
            _allGroups.Where(g => g.ParentGroupId == group.Id).Sum(c => CountGroupTasks(c, items));

        private bool IsGroupCompletedForDisplay(TaskGroupData group, List<TaskDisplayModel> items)
        {
            var direct = items.Where(i => i.Task.GroupId == group.Id).ToList();
            var children = _allGroups.Where(g => g.ParentGroupId == group.Id).Where(c => GroupTreeHasTasks(c, items)).ToList();
            int count = direct.Count + children.Sum(c => CountGroupTasks(c, items));
            if (count == 0) return false;
            return direct.All(i => IsTaskCompletedOnDate(i.Task, i.DisplayDate.Date)) && children.All(c => IsGroupCompletedForDisplay(c, items));
        }

        private bool GroupTreeHasTasks(TaskGroupData group, List<TaskDisplayModel> items) => items.Any(i => i.Task.GroupId == group.Id) || _allGroups.Where(g => g.ParentGroupId == group.Id).Any(c => GroupTreeHasTasks(c, items));
        private int CountGroupTasks(TaskGroupData group, List<TaskDisplayModel> items) => items.Count(i => i.Task.GroupId == group.Id) + _allGroups.Where(g => g.ParentGroupId == group.Id).Sum(c => CountGroupTasks(c, items));

        private string GetRecurrenceUnitLabel(string unit)
        {
            if (Localization.IsEnglish)
            {
                return unit switch
                {
                    "Week" => "Week",
                    "Month" => "Month",
                    "Year" => "Year",
                    _ => "Day"
                };
            }

            return unit switch
            {
                "Week" => Localization.T("周"),
                "Month" => Localization.T("月"),
                "Year" => Localization.T("年"),
                _ => Localization.T("天")
            };
        }

        private void AddTaskCard(Panel itemContainer, TaskDisplayModel item, bool showDateLabel)
        {
            var task = item.Task; DateTime taskItemDate = item.DisplayDate.Date; bool isCompleted = IsTaskCompletedOnDate(task, taskItemDate);
            Border taskCard = new Border { Background = GetThemeBrush("CardBg"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6) };
            Grid cardGrid = new Grid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            CheckBox chkStatus = new CheckBox { Style = (Style)FindResource("CompactCheckBox"), IsChecked = isCompleted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand };
            StackPanel spText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            spText.Children.Add(new TextBlock { Text = task.Title, Foreground = isCompleted ? GetThemeBrush("TextMuted") : GetThemeBrush("TextPrimary"), FontWeight = FontWeights.Bold, FontSize = 13, TextDecorations = isCompleted ? TextDecorations.Strikethrough : null });
            if (task.IsRecurring) spText.Children.Add(new TextBlock { Text = $"🔁 {Localization.T("每")} {task.RecurrenceInterval} {GetRecurrenceUnitLabel(task.RecurrenceUnit)}", Foreground = isCompleted ? GetThemeBrush("TextMuted") : GetThemeBrush("AccentLightBrush"), FontSize = 11, Margin = new Thickness(0,2,0,0) });
            chkStatus.Click += (s, ev) =>
            {
                // Capture the complete tree immediately before the task state changes.
                // Do not rely on the checkbox's visual ancestors: the Expander header/content
                // is generated from a ControlTemplate, so the visual-parent chain can vary.
                CaptureExpanderStates(TaskListPanel);
                bool completed = chkStatus.IsChecked == true;
                // 始终按自然日维护完成记录，清理旧数据中可能带有时间部分的重复日期。
                task.CompletedDates.RemoveWhere(d => d.Date == taskItemDate.Date);
                if (completed)
                    task.CompletedDates.Add(taskItemDate.Date);

                foreach (var n in _allNotes.Where(n => n.TaskId == task.Id))
                    n.IsCompleted = completed;

                SaveTasks();
                SaveNotes();
                RefreshTaskList();
                RefreshCalendarView();
            };
            Grid.SetColumn(chkStatus, 0); Grid.SetColumn(spText, 1); cardGrid.Children.Add(chkStatus); cardGrid.Children.Add(spText);
            if (HasNotesForTask(task.Id)) { var nb = new Button { Content="📝", Background=GetThemeBrush("ControlBg"), Foreground=GetThemeBrush("AccentLightBrush"), BorderThickness=new Thickness(0), Padding=new Thickness(5,2,5,2), Cursor=Cursors.Hand, ToolTip=Localization.T("查看任务便签") }; nb.Click += (s,e)=>OpenTaskNotes(task); Grid.SetColumn(nb,2); cardGrid.Children.Add(nb); }
            if (showDateLabel || task.IsRecurring)
            {
                StackPanel dp = new StackPanel { VerticalAlignment=VerticalAlignment.Top, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(8,0,2,0) };
                if (showDateLabel) dp.Children.Add(new TextBlock { Text=item.DisplayDate.ToString("M-d"), Foreground=isCompleted?GetThemeBrush("TextMuted"):GetThemeBrush("TextSecondary"), FontSize=11, HorizontalAlignment=HorizontalAlignment.Right });
                if (task.IsRecurring) { var st=GetRecurringCompletionStats(task); dp.Children.Add(new TextBlock { Text=Localization.IsEnglish ? $"Completed {st.totalCompleted} times" : $"已完成{st.totalCompleted}次", Foreground=GetThemeBrush("TextMuted"), FontSize=9, HorizontalAlignment=HorizontalAlignment.Right }); dp.Children.Add(new TextBlock { Text=Localization.IsEnglish ? $"Current streak {st.currentStreak}" : $"连续完成{st.currentStreak}次", Foreground=GetThemeBrush("TextMuted"), FontSize=9, HorizontalAlignment=HorizontalAlignment.Right }); dp.Children.Add(new TextBlock { Text=Localization.IsEnglish ? $"Longest streak {st.longestStreak}" : $"最长连续{st.longestStreak}次", Foreground=GetThemeBrush("TextMuted"), FontSize=9, HorizontalAlignment=HorizontalAlignment.Right }); }
                Grid.SetColumn(dp,3); cardGrid.Children.Add(dp);
            }
            taskCard.Child=cardGrid;
            ContextMenu menu=new ContextMenu(); var edit=new MenuItem{Header=Localization.T("编辑任务")}; edit.Click+=(s,e)=>OpenTaskEditDrawer(task); menu.Items.Add(edit);
            var addNote=new MenuItem{Header=Localization.T("新建/查看便签")}; addNote.Click+=(s,e)=>OpenTaskNotes(task); menu.Items.Add(addNote);
            if (task.IsRecurring) { var delToday=new MenuItem{Header=Localization.T("仅删除本日任务")}; delToday.Click+=(s,e)=>{var d=taskItemDate.Date; task.SkippedDates.Add(d); task.DeletedDates ??= new HashSet<DateTime>(); task.DeletedDates.Add(d); SaveTasks(); RefreshTaskList(); RefreshHistoryList(); RefreshCalendarView();}; menu.Items.Add(delToday); var delAll=new MenuItem{Header=Localization.T("删除整个循环任务")}; delAll.Click += (s, e) => SoftDeleteTask(task);menu.Items.Add(delAll); }
            else { var del=new MenuItem{Header=Localization.T("删除任务")}; del.Click += (s, e) => SoftDeleteTask(task);menu.Items.Add(del); }
            taskCard.ContextMenu=menu; itemContainer.Children.Add(taskCard);
        }

        // Soft-delete a task: keep it in _allTasks so it can appear in History and be restored.
        private void SoftDeleteTask(TaskItemData task)
        {
            if (task == null) return;

            task.IsDeleted = true;
            task.DeletedAt = DateTime.Now;
            SaveTasks();
            RefreshTaskList();
            RefreshHistoryList();
            RefreshCalendarView();
        }

        private void RefreshHistoryList()
        {
            if (HistoryListPanel == null) return;
            HistoryListPanel.Children.Clear();

            // A history record is either a normal completion record or the deletion event itself.
            // Keeping the deletion event separate guarantees that deleting an unfinished task still
            // creates a history entry, while restoring it removes the deletion entry automatically.
            var historyRecords = new List<(TaskItemData Task, DateTime Date, bool IsDeletionRecord)>();
            foreach (var task in _allTasks)
            {
                foreach (var date in task.CompletedDates ?? new HashSet<DateTime>())
                    historyRecords.Add((task, date.Date, false));

                // A recurring task can delete only today's occurrence. Those dates must appear in History too.
                foreach (var deletedDate in task.DeletedDates ?? new HashSet<DateTime>())
                    historyRecords.Add((task, deletedDate.Date, true));

                if (task.IsDeleted)
                {
                    DateTime deletedDate = (task.DeletedAt ?? DateTime.Now).Date;
                    bool alreadyRepresentedByCompletion = (task.CompletedDates ?? new HashSet<DateTime>())
                        .Any(d => d.Date == deletedDate);
                    bool alreadyRepresentedByDeletedDate = (task.DeletedDates ?? new HashSet<DateTime>())
                        .Any(d => d.Date == deletedDate);
                    if (!alreadyRepresentedByCompletion && !alreadyRepresentedByDeletedDate)
                        historyRecords.Add((task, deletedDate, true));
                }
            }

            historyRecords = historyRecords
                .OrderByDescending(r => r.Date)
                .ThenBy(r => r.Task.Title)
                .ToList();

            if (historyRecords.Count == 0)
            {
                HistoryListPanel.Children.Add(new TextBlock
                {
                    Text = Localization.T("暂无打卡记录~"),
                    Foreground = GetThemeBrush("TextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 30, 0, 0)
                });
                return;
            }

            foreach (var record in historyRecords)
            {
                Border itemCard = new Border { Background = GetThemeBrush("CardBg"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };
                Grid cardGrid = new Grid();
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                StackPanel spInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                string displayTitle = record.Task.Title + (record.Task.IsDeleted ? (Localization.IsEnglish ? " (Delete)" : "（删除）") : string.Empty);
                titleRow.Children.Add(new TextBlock { Text = displayTitle, Foreground = GetThemeBrush("TextPrimary"), FontWeight = FontWeights.Bold, FontSize = 12 });
                if (!record.Task.IsDeleted && HasNotesForTask(record.Task.Id))
                {
                    var nbtn = new Button { Content = "📝", Background = GetThemeBrush("ControlBg"), Foreground = GetThemeBrush("AccentLightBrush"), BorderThickness = new Thickness(0), Padding = new Thickness(5,2,5,2), Cursor = Cursors.Hand, ToolTip = Localization.T("查看任务便签") };
                    nbtn.Click += (s,e) => OpenTaskNotes(record.Task);
                    titleRow.Children.Add(nbtn);
                }
                spInfo.Children.Add(titleRow);
                spInfo.Children.Add(new TextBlock { Text = $"📅 {record.Date:yyyy-MM-dd}", Foreground = GetThemeBrush("AccentLightBrush"), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) });

                Button btnUndo = new Button { Content = Localization.T("撤回"), Background = GetThemeBrush("AccentBrush"), Foreground = Brushes.White, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 2, 0), Cursor = Cursors.Hand, FontSize = 11, ToolTip = record.Task.IsDeleted ? Localization.T("恢复已删除任务") : Localization.T("恢复为未打卡状态") };
                btnUndo.Click += (s, ev) =>
                {
                    if (record.IsDeletionRecord)
                    {
                        if (record.Task.IsDeleted)
                        {
                            record.Task.IsDeleted = false;
                            record.Task.DeletedAt = null;
                        }
                        else
                        {
                            record.Task.DeletedDates?.RemoveWhere(d => d.Date == record.Date.Date);
                            record.Task.SkippedDates?.RemoveWhere(d => d.Date == record.Date.Date);
                        }
                    }
                    else
                    {
                        record.Task.CompletedDates.RemoveWhere(d => d.Date == record.Date.Date);
                    }
                    SaveTasks(); RefreshHistoryList(); RefreshTaskList(); RefreshCalendarView();
                };

                Button btnDelete = new Button { Content = Localization.T("删除"), Background = (Brush?)new BrushConverter().ConvertFrom("#DC2626") ?? Brushes.Red, Foreground = Brushes.White, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, FontSize = 11, ToolTip = Localization.T("彻底删除此任务") };
                btnDelete.Click += (s, ev) =>
                {
                    _allTasks.Remove(record.Task);
                    _allNotes.RemoveAll(n => n.TaskId == record.Task.Id);
                    SaveTasks(); SaveNotes(); RefreshHistoryList(); RefreshTaskList(); RefreshCalendarView();
                };

                Grid.SetColumn(spInfo, 0); Grid.SetColumn(btnUndo, 1); Grid.SetColumn(btnDelete, 2);
                cardGrid.Children.Add(spInfo); cardGrid.Children.Add(btnUndo); cardGrid.Children.Add(btnDelete);
                itemCard.Child = cardGrid; HistoryListPanel.Children.Add(itemCard);
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
            // 首次启动给用户一个稳定的可见窗口；之后才按正常贴边规则工作。
            CheckDockEdge();
            _edgeHideTimer.Start();
        }

        private void CheckDockEdge()
        {
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 ||
                double.IsNaN(Left) || double.IsNaN(Top))
            {
                _currentDockEdge = DockEdge.None;
                _isHiding = false;
                return;
            }

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

        // 判断某一天是否是循环任务的“计划日”，不考虑“仅删除本日”。
        // 连续完成统计必须把被删除的本日视为一次断档。
        private bool IsTaskScheduledDate(TaskItemData task, DateTime queryDate)
        {
            DateTime pureDate = queryDate.Date;

            if (!task.IsRecurring)
            {
                return task.TargetDate.Date == pureDate;
            }

            if (pureDate < task.TargetDate.Date)
            {
                return false;
            }

            TimeSpan diff = pureDate - task.TargetDate.Date;
            int interval = Math.Max(1, task.RecurrenceInterval);

            switch (task.RecurrenceUnit)
            {
                case "Day":
                    return diff.Days % interval == 0;
                case "Week":
                    return diff.Days % 7 == 0 && (diff.Days / 7) % interval == 0;
                case "Month":
                    int monthDiff = (pureDate.Year - task.TargetDate.Year) * 12 +
                                    (pureDate.Month - task.TargetDate.Month);
                    return monthDiff >= 0 && monthDiff % interval == 0 &&
                           pureDate.Day == task.TargetDate.Day;
                case "Year":
                    int yearDiff = pureDate.Year - task.TargetDate.Year;
                    return yearDiff >= 0 && yearDiff % interval == 0 &&
                           pureDate.Month == task.TargetDate.Month &&
                           pureDate.Day == task.TargetDate.Day;
                default:
                    return false;
            }
        }

        // 返回：累计完成次数、当前连续完成次数、历史最长连续完成次数。
        // “仅删除本日”会打断连续次数，例如：1、2 完成，3 删除，4 完成 => 当前连续完成 1 次。
        private (int totalCompleted, int currentStreak, int longestStreak) GetRecurringCompletionStats(TaskItemData task)
        {
            if (!task.IsRecurring || task.CompletedDates == null || task.CompletedDates.Count == 0)
            {
                return (0, 0, 0);
            }

            var completedDates = task.CompletedDates
                .Select(d => d.Date)
                .Where(d => IsTaskScheduledDate(task, d) &&
                            (task.SkippedDates == null || !task.SkippedDates.Contains(d)))
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            if (completedDates.Count == 0)
            {
                return (0, 0, 0);
            }

            // 连续性按“下一次计划日期”判断，而不是简单按自然日判断，
            // 因此每周/每月等循环也能正确计算。SkippedDates 会明确打断连续。
            int longestStreak = 1;
            int run = 1;

            for (int i = 1; i < completedDates.Count; i++)
            {
                DateTime previous = completedDates[i - 1];
                DateTime current = completedDates[i];
                DateTime expectedNext = GetNextScheduledDate(task, previous);

                bool skippedBetween = HasSkippedScheduledDate(task, previous, current);
                if (current == expectedNext && !skippedBetween)
                {
                    run++;
                }
                else
                {
                    run = 1;
                }

                if (run > longestStreak)
                {
                    longestStreak = run;
                }
            }

            // 当前连续次数：从最近一次完成往前追溯。
            int currentStreak = 1;
            for (int i = completedDates.Count - 1; i > 0; i--)
            {
                DateTime previous = completedDates[i - 1];
                DateTime current = completedDates[i];
                DateTime expectedNext = GetNextScheduledDate(task, previous);

                if (current == expectedNext && !HasSkippedScheduledDate(task, previous, current))
                {
                    currentStreak++;
                }
                else
                {
                    break;
                }
            }

            return (completedDates.Count, currentStreak, longestStreak);
        }

        private DateTime GetNextScheduledDate(TaskItemData task, DateTime date)
        {
            int interval = Math.Max(1, task.RecurrenceInterval);

            switch (task.RecurrenceUnit)
            {
                case "Day":
                    return date.AddDays(interval);
                case "Week":
                    return date.AddDays(7 * interval);
                case "Month":
                    return date.AddMonths(interval);
                case "Year":
                    return date.AddYears(interval);
                default:
                    return date.AddDays(interval);
            }
        }

        private bool HasSkippedScheduledDate(TaskItemData task, DateTime fromExclusive, DateTime toExclusive)
        {
            if (task.SkippedDates == null || task.SkippedDates.Count == 0)
                return false;

            for (DateTime d = fromExclusive.Date.AddDays(1); d < toExclusive.Date; d = d.AddDays(1))
            {
                if (IsTaskScheduledDate(task, d) && task.SkippedDates.Contains(d.Date))
                    return true;
            }

            return false;
        }

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

        private static JsonSerializerOptions CreateJsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
        }

        private void SaveTasks()
        {
            try
            {
                var options = CreateJsonOptions();
                string json = JsonSerializer.Serialize(_allTasks, options);
                File.WriteAllText(_dataFilePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Localization.T("保存数据失败")}: {ex.Message}");
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

        private void SaveGroups()
        {
            try
            {
                var options = CreateJsonOptions();
                File.WriteAllText(_groupsFilePath, JsonSerializer.Serialize(_allGroups, options));
            }
            catch (Exception ex) { MessageBox.Show($"{Localization.T("保存分组失败")}: {ex.Message}"); }
        }

        private void LoadGroups()
        {
            try
            {
                if (File.Exists(_groupsFilePath))
                    _allGroups = JsonSerializer.Deserialize<List<TaskGroupData>>(File.ReadAllText(_groupsFilePath)) ?? new List<TaskGroupData>();
            }
            catch { _allGroups = new List<TaskGroupData>(); }
        }

        private void SaveNotes()
        {
            try
            {
                var options = CreateJsonOptions();
                File.WriteAllText(_notesFilePath, JsonSerializer.Serialize(_allNotes, options));
            }
            catch (Exception ex) { MessageBox.Show($"{Localization.T("保存便签失败")}: {ex.Message}"); }
        }

        private void LoadNotes()
        {
            try
            {
                if (File.Exists(_notesFilePath))
                    _allNotes = JsonSerializer.Deserialize<List<NoteData>>(File.ReadAllText(_notesFilePath)) ?? new List<NoteData>();
            }
            catch { _allNotes = new List<NoteData>(); }
        }

        private void SaveSettings()
        {
            try
            {
                var options = CreateJsonOptions();
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

            _currentSettings.Language = NormalizeLanguageCode(_currentSettings.Language);

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
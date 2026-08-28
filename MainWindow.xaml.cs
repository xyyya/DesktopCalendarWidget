using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopCalendarWidget
{
    // 🔴 日期任务标记转换器：仅当指定日期有“未完成”任务时显示小蓝点
    public class TaskDayToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime date && Application.Current.MainWindow is MainWindow mainWin)
            {
                bool hasUnfinishedTask = mainWin.HasUnfinishedTaskOnDate(date);
                return hasUnfinishedTask ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public partial class MainWindow : Window
    {
        public class TaskItemData
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Title { get; set; } = string.Empty;
            public DateTime TargetDate { get; set; }
            public bool IsRecurring { get; set; }
            public string RecurrenceUnit { get; set; } = "Day";
            public int RecurrenceInterval { get; set; } = 1;
            public HashSet<DateTime> CompletedDates { get; set; } = new HashSet<DateTime>();
        }

        public class AppSettingsData
        {
            public bool IsEdgeHideEnabled { get; set; } = false;
            public bool IsAutoStartEnabled { get; set; } = false;
        }

        private List<TaskItemData> _allTasks = new List<TaskItemData>();
        private readonly string _dataFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tasks.json");
        private readonly string _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        
        private TaskItemData? _currentEditingTask = null;
        private AppSettingsData _currentSettings = new AppSettingsData();
        private DispatcherTimer? _edgeHideTimer;

        public MainWindow()
        {
            InitializeComponent();
            LoadTasks();
            LoadSettings();
            InitEdgeHideTimer();

            MainCalendar.SelectedDate = DateTime.Today;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        // 判断指定日期是否有未完成的任务
        public bool HasUnfinishedTaskOnDate(DateTime date)
        {
            DateTime pureDate = date.Date;
            return _allTasks.Any(task => IsTaskMatchDate(task, pureDate) && !task.CompletedDates.Contains(pureDate));
        }

        // 刷新日历绘制（触发小蓝点转换器刷新）
        private void RefreshCalendarView()
        {
            if (MainCalendar == null) return;
            var current = MainCalendar.SelectedDate;
            MainCalendar.SelectedDate = null;
            MainCalendar.SelectedDate = current;
        }

        #region 抽屉动画控制 (Drawer Animation)

        private void OpenTaskEditDrawer(TaskItemData? taskToEdit)
        {
            _currentEditingTask = taskToEdit;
            bool isEditMode = taskToEdit != null;

            lblDrawerTitle.Text = isEditMode ? "修改任务" : "新建任务";
            txtTitle.Text = isEditMode ? taskToEdit!.Title : "新任务";
            chkRecurring.IsChecked = isEditMode ? taskToEdit!.IsRecurring : false;
            txtInterval.Text = isEditMode ? taskToEdit!.RecurrenceInterval.ToString() : "1";

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

            AnimateDrawer(HistoryTransform, 600);
            AnimateDrawer(SettingsTransform, 600);
            AnimateDrawer(TaskEditTransform, 0);
        }

        private void OpenHistoryDrawer()
        {
            RefreshHistoryList();
            AnimateDrawer(TaskEditTransform, 600);
            AnimateDrawer(SettingsTransform, 600);
            AnimateDrawer(HistoryTransform, 0);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            chkEdgeHide.IsChecked = _currentSettings.IsEdgeHideEnabled;
            chkAutoStart.IsChecked = _currentSettings.IsAutoStartEnabled;

            AnimateDrawer(TaskEditTransform, 600);
            AnimateDrawer(HistoryTransform, 600);
            AnimateDrawer(SettingsTransform, 0);
        }

        private void CloseDrawers_Click(object sender, RoutedEventArgs e)
        {
            AnimateDrawer(TaskEditTransform, 600);
            AnimateDrawer(HistoryTransform, 600);
            AnimateDrawer(SettingsTransform, 600);
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

            if (_currentEditingTask != null)
            {
                _currentEditingTask.Title = txtTitle.Text;
                _currentEditingTask.IsRecurring = chkRecurring.IsChecked ?? false;
                _currentEditingTask.RecurrenceInterval = interval;
                _currentEditingTask.RecurrenceUnit = unitStr;
            }
            else
            {
                _allTasks.Add(new TaskItemData
                {
                    Title = txtTitle.Text,
                    TargetDate = MainCalendar.SelectedDate ?? DateTime.Today,
                    IsRecurring = chkRecurring.IsChecked ?? false,
                    RecurrenceInterval = interval,
                    RecurrenceUnit = unitStr
                });
            }

            SaveTasks();
            RefreshTaskList();
            RefreshCalendarView(); // 刷新日历小蓝点
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

            Brush GetBrush(string hex) => (Brush?)new BrushConverter().ConvertFrom(hex) ?? Brushes.Gray;
            DateTime selectedDate = (MainCalendar.SelectedDate ?? DateTime.Today).Date;

            // 🔵 关键改动：打卡完成后自动沉底（按照 未完成在上、已完成在下 排序）
            var matchedTasks = _allTasks
                .Where(t => IsTaskMatchDate(t, selectedDate))
                .OrderBy(t => t.CompletedDates.Contains(selectedDate))
                .ToList();

            foreach (var task in matchedTasks)
            {
                bool isCompletedToday = task.CompletedDates.Contains(selectedDate);

                Border taskCard = new Border
                {
                    Background = GetBrush("#222226"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 8, 8, 8),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                Grid cardGrid = new Grid();
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                CheckBox chkStatus = new CheckBox
                {
                    IsChecked = isCompletedToday,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand
                };

                StackPanel spText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                TextBlock txtTitle = new TextBlock
                {
                    Text = task.Title,
                    Foreground = isCompletedToday ? GetBrush("#666666") : Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    TextDecorations = isCompletedToday ? TextDecorations.Strikethrough : null
                };
                spText.Children.Add(txtTitle);

                if (task.IsRecurring)
                {
                    spText.Children.Add(new TextBlock
                    {
                        Text = $"🔁 每 {task.RecurrenceInterval} {task.RecurrenceUnit}",
                        Foreground = isCompletedToday ? GetBrush("#444444") : GetBrush("#60A5FA"),
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }

                chkStatus.Click += (s, ev) =>
                {
                    if (chkStatus.IsChecked == true)
                    {
                        task.CompletedDates.Add(selectedDate);
                    }
                    else
                    {
                        task.CompletedDates.Remove(selectedDate);
                    }
                    SaveTasks();
                    RefreshTaskList();
                    RefreshCalendarView(); // 勾选/取消勾选后实时更新蓝点显隐
                };

                Grid.SetColumn(chkStatus, 0);
                Grid.SetColumn(spText, 1);
                cardGrid.Children.Add(chkStatus);
                cardGrid.Children.Add(spText);
                taskCard.Child = cardGrid;

                ContextMenu contextMenu = new ContextMenu();
                MenuItem menuEdit = new MenuItem { Header = "编辑任务" };
                menuEdit.Click += (s, ev) => OpenTaskEditDrawer(task);

                MenuItem menuDelete = new MenuItem { Header = "删除任务" };
                menuDelete.Click += (s, ev) =>
                {
                    _allTasks.Remove(task);
                    SaveTasks();
                    RefreshTaskList();
                    RefreshCalendarView(); // 删除任务后实时更新蓝点显隐
                };

                contextMenu.Items.Add(menuEdit);
                contextMenu.Items.Add(menuDelete);
                taskCard.ContextMenu = contextMenu;

                TaskListPanel.Children.Add(taskCard);
            }
        }

        private void RefreshHistoryList()
        {
            if (HistoryListPanel == null) return;
            HistoryListPanel.Children.Clear();

            Brush GetBrush(string hex) => (Brush?)new BrushConverter().ConvertFrom(hex) ?? Brushes.Gray;

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
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 30, 0, 0)
                });
                return;
            }

            foreach (var record in historyRecords)
            {
                Border itemCard = new Border
                {
                    Background = GetBrush("#222226"),
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
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12
                });
                spInfo.Children.Add(new TextBlock
                {
                    Text = $"📅 {record.Date:yyyy-MM-dd}",
                    Foreground = GetBrush("#60A5FA"),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });

                Button btnUndo = new Button
                {
                    Content = "撤回",
                    Background = GetBrush("#2563EB"),
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
                    RefreshCalendarView(); // 撤回历史打卡后自动刷新日历蓝点
                };

                Button btnDelete = new Button
                {
                    Content = "删除",
                    Background = GetBrush("#DC2626"),
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
                    RefreshCalendarView(); // 删除任务后自动刷新日历蓝点
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
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
       
        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            _edgeHideTimer?.Stop();
            Application.Current.Shutdown();
        }

        #endregion

        #region 数据持久化逻辑

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
                        string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                        if (exePath != null) key.SetValue(appName, $"\"{exePath}\"");
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

        #region 数据与周期计算逻辑

        private bool IsTaskMatchDate(TaskItemData task, DateTime queryDate)
        {
            if (!task.IsRecurring)
            {
                return task.TargetDate.Date == queryDate.Date;
            }

            if (queryDate.Date < task.TargetDate.Date)
            {
                return false;
            }

            TimeSpan diff = queryDate.Date - task.TargetDate.Date;
            switch (task.RecurrenceUnit)
            {
                case "Day":
                    return diff.Days % task.RecurrenceInterval == 0;
                case "Week":
                    return (diff.Days / 7) % task.RecurrenceInterval == 0 && diff.Days % 7 == 0;
                case "Month":
                    int monthDiff = (queryDate.Year - task.TargetDate.Year) * 12 + (queryDate.Month - task.TargetDate.Month);
                    return monthDiff >= 0 && monthDiff % task.RecurrenceInterval == 0 && queryDate.Day == task.TargetDate.Day;
                case "Year":
                    int yearDiff = queryDate.Year - task.TargetDate.Year;
                    return yearDiff >= 0 && yearDiff % task.RecurrenceInterval == 0 && queryDate.Month == task.TargetDate.Month && queryDate.Day == task.TargetDate.Day;
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

        #endregion
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopCalendarWidget
{
    /// <summary>
    /// Lightweight runtime localization for the desktop widget.
    /// The persisted language is zh-CN or en-US.
    /// Existing user data is never translated; only known UI strings are translated.
    /// </summary>
    public static class Localization
    {
        private static readonly Dictionary<string, string> ZhToEn = new(StringComparer.Ordinal)
        {
            ["程序启动失败"] = "Program failed to start",
            ["程序启动/运行时发生未处理错误"] = "An unhandled error occurred while starting or running the program",
            ["新便签"] = "New Note",
            ["新分组"] = "New Group",
            ["新建任务"] = "New Task",
            ["新建分组"] = "New Group",
            ["新建子分组"] = "New Subgroup",
            ["新建任务或分组"] = "Create Task or Group",
            ["新建便签"] = "New Note",
            ["＋ 新建便签"] = "+ New Note",
            ["软件设置"] = "Settings",
            ["软件设定"] = "Settings",
            ["历史记录"] = "History",
            ["打卡历史记录"] = "Check-in History",
            ["便签"] = "Note",
            ["关闭"] = "Close",
            ["任务标题"] = "Task Title",
            ["所属分组"] = "Group",
            ["上级分组"] = "Parent Group",
            ["分组名称"] = "Group Name",
            ["目标日期"] = "Target Date",
            ["今天"] = "Today",
            ["明天"] = "Tomorrow",
            ["下周"] = "Next Week",
            ["下个月"] = "Next Month",
            ["明年"] = "Next Year",
            ["天后"] = "Days Later",
            ["开启循环提醒"] = "Enable recurring reminder",
            ["间隔"] = "Interval",
            ["周期"] = "Period",
            ["天 (Day)"] = "Day",
            ["周 (Week)"] = "Week",
            ["月 (Month)"] = "Month",
            ["年 (Year)"] = "Year",
            ["在日历中提示小蓝点"] = "Show a blue dot on the calendar",
            ["保存任务"] = "Save Task",
            ["保存分组"] = "Save Group",
            ["保存"] = "Save",
            ["完成"] = "Completed",
            ["当前日期的任务加入此分组"] = "Add tasks from the current date to this group",
            ["可选择关联任务；不选择则作为独立便签"] = "Optionally link a task; leave empty for a standalone note",
            ["字号"] = "Font Size",
            ["颜色"] = "Color",
            ["默认"] = "Default",
            ["白色"] = "White",
            ["蓝色"] = "Blue",
            ["绿色"] = "Green",
            ["黄色"] = "Yellow",
            ["红色"] = "Red",
            ["跟随系统"] = "Follow System",
            ["深色模式"] = "Dark",
            ["浅色模式"] = "Light",
            ["外观主题"] = "Appearance",
            ["界面语言"] = "Language",
            ["贴边隐藏"] = "Hide at screen edge",
            ["当鼠标离开组件时，窗体自动滑向屏幕边缘隐藏。"] = "When the mouse leaves the widget, it slides to the screen edge and hides.",
            ["开机自动启动"] = "Start with Windows",
            ["让桌面小组件在 Windows 启动时自动运行。"] = "Launch the desktop widget automatically when Windows starts.",
            ["窗口不透明度"] = "Window Opacity",
            ["拖动以调整窗口透明度（最低 10%），个人推荐60% - 70%左右。"] = "Drag to adjust opacity (minimum 10%); around 60%–70% is recommended.",
            ["喝水提醒"] = "Water Reminder",
            ["开启喝水提醒"] = "Enable water reminders",
            ["每日提醒次数"] = "Reminders per day",
            ["开始时间 (HH:mm)"] = "Start time (HH:mm)",
            ["间隔时间"] = "Interval",
            ["退出程序"] = "Exit",
            ["修改任务"] = "Edit Task",
            ["新任务"] = "New Task",
            ["分组名称不能为空！"] = "Group name cannot be empty!",
            ["任务名称不能为空！"] = "Task title cannot be empty!",
            ["请输入有效的天数！"] = "Please enter a valid number of days!",
            ["提示"] = "Notice",
            ["（未分组）"] = "(Unassigned)",
            ["（顶层分组）"] = "(Top-level group)",
            ["当前日期没有任务"] = "No tasks for the current date",
            ["还没有独立便签"] = "No standalone notes yet",
            ["打开"] = "Open",
            ["编辑便签"] = "Edit Note",
            ["删除便签"] = "Delete Note",
            ["不关联任务"] = "No linked task",
            ["独立便签"] = "Standalone note",
            ["已关联任务"] = "Linked task",
            ["逾期任务"] = "Overdue Tasks",
            ["今日任务"] = "Today's Tasks",
            ["未来任务"] = "Upcoming Tasks",
            ["暂无任务"] = "No tasks",
            ["编辑分组"] = "Edit Group",
            ["删除分组（保留任务）"] = "Delete Group (Keep Tasks)",
            ["编辑任务"] = "Edit Task",
            ["新建/查看便签"] = "New/View Note",
            ["仅删除本日任务"] = "Delete Today's Occurrence",
            ["删除整个循环任务"] = "Delete Recurring Task",
            ["删除任务"] = "Delete Task",
            ["暂无打卡记录~"] = "No check-in history yet~",
            ["撤回"] = "Undo",
            ["恢复为未打卡状态"] = "Restore to unchecked",
            ["彻底删除此任务"] = "Permanently delete this task",
            ["查看任务便签"] = "View task note",
            ["水精灵提醒您该喝水咯(∠・ω< )⌒★"] = "Aqua Sprite says it's time to drink some water (∠・ω< )⌒★",
            ["为了您的健康，请及时补充水分！最好顺便起来走动走动！"] = "For your health, please stay hydrated! It's also a good time to stand up and move around!",
            ["通知发送失败"] = "Notification Failed",
            ["请确认 Windows 通知功能已开启，并重新启动本程序。"] = "Please make sure Windows notifications are enabled and restart the program.",
            ["任务:"] = "Tasks:",
            ["每"] = "Every",
            ["已完成"] = "Completed",
            ["连续完成"] = "Current streak",
            ["最长连续"] = "Longest streak",
            ["的便签"] = "'s Note",
            ["无标题便签"] = "Untitled Note",
            ["保存数据失败"] = "Failed to save data",
            ["保存分组失败"] = "Failed to save groups",
            ["保存便签失败"] = "Failed to save notes",
            ["删除"] = "Delete",
            ["添加任务"] = "Add Task",
            ["添加分组"] = "Add Group",
            ["天"] = "Day",
            ["周"] = "Week",
            ["月"] = "Month",
            ["年"] = "Year",
            ["日"] = "Sun",
            ["一"] = "Mon",
            ["二"] = "Tue",
            ["三"] = "Wed",
            ["四"] = "Thu",
            ["五"] = "Fri",
            ["六"] = "Sat",
            ["关联任务"] = "Linked task",
            [" 时 "] = " h ",
            [" 分 "] = " m ",
            [" 在日历中提示小蓝点"] = " Show a blue dot on the calendar",
            [" 开启循环提醒"] = " Enable recurring reminder",
            [" 贴边隐藏"] = " Hide at screen edge",
            [" 开机自动启动"] = " Start with Windows",
            [" 开启喝水提醒"] = " Enable water reminders",
            ["将当前日期的任务加入此分组"] = "Add tasks from the current date to this group",
            ["便签名"] = "Note Name",
            ["保存便签"] = "Save Note",
            ["取消"] = "Cancel",
            ["提示小蓝点"] = "Show a blue dot",
            ["循环提醒"] = "Recurring reminder",
        };

        private static readonly Dictionary<string, string> EnToZh = BuildReverseMap();

        public static string CurrentLanguage { get; private set; } = "en-US";
        public static bool IsEnglish => CurrentLanguage == "en-US";
        public static event EventHandler? LanguageChanged;

        public static void SetLanguage(string? language)
        {
            string next = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase)
                ? "en-US"
                : "zh-CN";
            bool changed = !string.Equals(CurrentLanguage, next, StringComparison.OrdinalIgnoreCase);
            CurrentLanguage = next;

            // Keep .NET/WPF date formatting aligned with the UI language. This is important
            // for Calendar/DatePicker generated month and year names, which otherwise may
            // keep the process culture from the startup language.
            var culture = CultureInfo.GetCultureInfo(next);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        public static string T(string chineseText)
        {
            if (!IsEnglish) return chineseText;
            return ZhToEn.TryGetValue(chineseText, out var translated) ? translated : chineseText;
        }

        public static string LookupCurrent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var leading = text.Length - text.TrimStart().Length;
            var trailing = text.Length - text.TrimEnd().Length;
            var core = text.Trim();

            string translated;
            if (IsEnglish)
            {
                if (ZhToEn.TryGetValue(core, out translated!))
                    return new string(' ', leading) + translated + new string(' ', trailing);
                if (core.StartsWith("已完成", StringComparison.Ordinal) && core.EndsWith("次", StringComparison.Ordinal))
                {
                    var number = core.Substring(3, core.Length - 4);
                    translated = $"Completed {number} times";
                    return new string(' ', leading) + translated + new string(' ', trailing);
                }
                return text;
            }
            if (EnToZh.TryGetValue(core, out translated!))
                return new string(' ', leading) + translated + new string(' ', trailing);
            return text;
        }

        public static void ApplyToVisualTree(DependencyObject root)
        {
            if (root == null) return;

            // The language selector is deliberately kept outside generic localization.
            // Its selected-content presenter is a separate visual path from the ComboBoxItem,
            // which previously caused the closed selector to display "Simplified Chinese".
            if (root is FrameworkElement tagged &&
                string.Equals(tagged.Tag?.ToString(), "LanguageSelector", StringComparison.Ordinal))
                return;

            if (root is FrameworkElement fe)
            {
                if (fe.ToolTip is string toolTip && !string.IsNullOrWhiteSpace(toolTip))
                    fe.ToolTip = LookupCurrent(toolTip);

                if (fe is Window window && !string.IsNullOrWhiteSpace(window.Title))
                    window.Title = LookupCurrent(window.Title);
            }

            switch (root)
            {
                case TextBlock tb:
                    tb.Text = LookupCurrent(tb.Text);
                    break;
                case Button button:
                    button.Content = TranslateContent(button.Content);
                    break;
                case CheckBox checkBox:
                    checkBox.Content = TranslateContent(checkBox.Content);
                    break;
                case RadioButton radioButton:
                    radioButton.Content = TranslateContent(radioButton.Content);
                    break;
                case Label label:
                    label.Content = TranslateContent(label.Content);
                    break;
                case MenuItem menuItem:
                    menuItem.Header = TranslateContent(menuItem.Header);
                    break;
                case ComboBoxItem comboBoxItem:
                    // The entire language selector is skipped above. Other ComboBoxItem values
                    // are normal UI strings and can be translated here.
                    comboBoxItem.Content = TranslateContent(comboBoxItem.Content);
                    break;
                case GroupBox groupBox:
                    groupBox.Header = TranslateContent(groupBox.Header);
                    break;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
                ApplyToVisualTree(VisualTreeHelper.GetChild(root, i));
        }

        private static object? TranslateContent(object? content)
        {
            if (content is string text)
                return LookupCurrent(text);
            return content;
        }


        private static Dictionary<string, string> BuildReverseMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in ZhToEn)
            {
                if (!map.ContainsKey(pair.Value))
                    map[pair.Value] = pair.Key;
            }
            return map;
        }

        public static void ApplyWpfLanguage(FrameworkElement element)
        {
            element.Language = XmlLanguage.GetLanguage(CurrentLanguage);
        }
    }
}

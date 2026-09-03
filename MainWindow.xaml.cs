using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

namespace QuickCopy
{
    public partial class MainWindow : Window
    {
        private const int HotkeyId = 0x515A;
        private const int WmHotkey = 0x0312;
        private const int WmNcHitTest = 0x0084;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const double DefaultWindowWidth = 580;
        private const double DefaultWindowHeight = 520;
        private const string ClipboardCategory = "剪贴板";
        private const int ClipboardHistoryLimit = 50;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        private readonly DispatcherTimer clipboardMonitorTimer;
        private readonly Dictionary<string, DemoRecord> demoRecords;
        private readonly HashSet<string> deletedRecordTitles = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        private readonly HashSet<string> deletedCategories = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        private readonly string recordsPath;
        private readonly string clipboardImagesFolder;
        private readonly DispatcherTimer copyToastTimer;
        private uint lastClipboardSequence;
        private string selectedCategory = "内部系统";
        private string selectedTitle;
        private string editingOriginalTitle;
        private string editingOriginalImagePath;
        private bool isLightTheme;
        private bool isPinned;
        private IntPtr pasteTargetWindow;
        private NativeRect pasteTargetCaret;
        private bool hasPasteTargetCaret;

        public MainWindow()
        {
            InitializeComponent();
            demoRecords = CreateDemoRecords();
            recordsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickCopy", "records.xml");
            clipboardImagesFolder = Path.Combine(Path.GetDirectoryName(recordsPath), "clipboard-images");
            LoadSavedRecords();
            deletedCategories.Remove(ClipboardCategory);
            lastClipboardSequence = GetClipboardSequenceNumber();
            clipboardMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            clipboardMonitorTimer.Tick += ClipboardMonitorTimer_Tick;
            clipboardMonitorTimer.Start();
            copyToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            copyToastTimer.Tick += delegate
            {
                copyToastTimer.Stop();
                CopyToast.Visibility = Visibility.Collapsed;
            };
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += delegate
            {
                clipboardMonitorTimer.Stop();
                UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            };
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!String.Equals(selectedCategory, ClipboardCategory, StringComparison.CurrentCultureIgnoreCase)
                && !CategoriesPanel.Children.OfType<Button>()
                    .Any(button => String.Equals(button.Content.ToString(), selectedCategory,
                        StringComparison.CurrentCultureIgnoreCase)))
                SelectFirstCategory();
            RenderRecords();
            PlayEntrance();
            Keyboard.Focus(WindowShell);
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            HwndSource.FromHwnd(helper.Handle).AddHook(WndProc);
            RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, 0x5A);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                if (IsVisible && WindowState != WindowState.Minimized)
                    HideAnimated();
                else
                    ShowAndActivate();
                handled = true;
            }
            else if (msg == WmNcHitTest && ResizeMode != ResizeMode.NoResize)
            {
                NativeRect rect;
                if (GetWindowRect(hwnd, out rect))
                {
                    var x = unchecked((short)lParam.ToInt64());
                    const int resizeBorder = 7;
                    if (x >= rect.Left && x < rect.Left + resizeBorder)
                    {
                        handled = true;
                        return new IntPtr(HtLeft);
                    }
                    if (x <= rect.Right && x > rect.Right - resizeBorder)
                    {
                        handled = true;
                        return new IntPtr(HtRight);
                    }
                }
            }
            return IntPtr.Zero;
        }

        private void ShowAndActivate()
        {
            CapturePasteTarget();
            WindowState = WindowState.Normal;
            Width = DefaultWindowWidth;
            Height = DefaultWindowHeight;
            PositionNearCaret();
            Show();
            Activate();
            Topmost = true;
            Topmost = isPinned;
            Keyboard.Focus(WindowShell);
            PlayEntrance();
        }

        private void PlayEntrance()
        {
            WindowShell.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(145)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ShellTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ShellScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ShellScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void HideAnimated()
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(80));
            fade.Completed += delegate { Hide(); WindowShell.Opacity = 0; };
            WindowShell.BeginAnimation(OpacityProperty, fade);
            ShellTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(80)));
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && EditorOverlay.Visibility == Visibility.Visible)
            {
                CloseEditor();
                e.Handled = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) { HideAnimated(); }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            isPinned = !isPinned;
            Topmost = isPinned;
            if (isPinned)
                PinButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#68C895"));
            else
                PinButton.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            PinButton.ToolTip = isPinned ? "取消窗口置顶（连续粘贴）" : "窗口置顶";
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            editingOriginalTitle = null;
            editingOriginalImagePath = null;
            EditorHeading.Text = "新增记录";
            EditorTitle.Clear();
            EditorRawText.Clear();
            EditorCategory.Text = "其他";
            EditorImage.Source = null;
            EditorImagePreview.Visibility = Visibility.Collapsed;
            EditorTitle.ToolTip = null;
            EditorTitle.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"];
            EditorOverlay.Visibility = Visibility.Visible;
            EditorTitle.Focus();
        }

        private void Category_Click(object sender, RoutedEventArgs e)
        {
            var clicked = sender as Button;
            if (clicked == null) return;
            foreach (var child in CategoriesPanel.Children)
            {
                var button = child as Button;
                if (button != null) button.Tag = null;
            }
            ClipboardButton.Tag = null;
            clicked.Tag = "Selected";
            selectedCategory = clicked.Content.ToString();
            var isClipboard = String.Equals(selectedCategory, ClipboardCategory,
                StringComparison.CurrentCultureIgnoreCase);
            EditRecordButton.IsEnabled = !isClipboard;
            EditRecordButton.Visibility = isClipboard ? Visibility.Collapsed : Visibility.Visible;
            DeleteCategoryButton.IsEnabled = !isClipboard;
            DeleteCategoryButton.Visibility = isClipboard ? Visibility.Hidden : Visibility.Visible;
            RenderRecords();
        }

        private void DeleteCategory_Click(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrEmpty(selectedCategory)) return;
            if (String.Equals(selectedCategory, ClipboardCategory, StringComparison.CurrentCultureIgnoreCase)) return;
            var records = demoRecords.Values.Where(record => record.Category == selectedCategory).ToList();
            var message = records.Count == 0
                ? "确定删除标签“" + selectedCategory + "”吗？"
                : "确定删除标签“" + selectedCategory + "”及其中的 " + records.Count + " 条便签吗？";
            var result = MessageBox.Show(this, message, "删除标签", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            foreach (var record in records)
            {
                if (!String.IsNullOrEmpty(record.ImagePath) && File.Exists(record.ImagePath))
                {
                    try { File.Delete(record.ImagePath); } catch { }
                }
                deletedRecordTitles.Add(record.Title);
                demoRecords.Remove(record.Title);
            }
            deletedCategories.Add(selectedCategory);
            RemoveCategoryButton(selectedCategory);
            SelectFirstCategory();
            SaveRecords();
        }

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            DemoRecord record;
            var title = button == null ? null : button.CommandParameter as string;
            if (String.IsNullOrEmpty(title) || !demoRecords.TryGetValue(title, out record)) return;
            SelectRecord(record.Title);
        }

        private void DeleteRecord_Click(object sender, RoutedEventArgs e)
        {
            DemoRecord record;
            if (String.IsNullOrEmpty(selectedTitle) || !demoRecords.TryGetValue(selectedTitle, out record)) return;
            var result = MessageBox.Show(this, "确定删除便签“" + record.Title + "”吗？", "删除便签",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            if (!String.IsNullOrEmpty(record.ImagePath) && File.Exists(record.ImagePath))
            {
                try { File.Delete(record.ImagePath); } catch { }
            }
            deletedRecordTitles.Add(record.Title);
            demoRecords.Remove(record.Title);
            SaveRecords();
            RenderRecords();
        }

        private void EditRecord_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedRecordEditor();
        }

        private void DisplayRecord(DemoRecord record)
        {
            FieldsPanel.Children.Clear();
            foreach (var field in record.Fields)
                AddFieldRow(field);
            if (!String.IsNullOrEmpty(record.ImagePath) && File.Exists(record.ImagePath))
                AddImageRow(record.ImagePath);
            selectedTitle = record.Title;
        }

        private void AddFieldRow(RecordField field)
        {
            var row = new Grid { Height = 54 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = field.Name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = field.Name
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            row.Children.Add(name);

            var value = new TextBlock
            {
                Text = IsPasswordField(field.Name)
                    ? new string('•', Math.Min(12, Math.Max(6, field.Value.Length)))
                    : field.Value,
                FontFamily = new System.Windows.Media.FontFamily(IsPasswordField(field.Name) ? "Segoe UI" : "Consolas"),
                FontSize = IsPasswordField(field.Name) ? 17 : 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = IsPasswordField(field.Name)
                    ? "单击粘贴"
                    : field.Value + Environment.NewLine + "单击粘贴",
                Cursor = Cursors.Hand,
                Tag = field.Value
            };
            value.MouseLeftButtonDown += PasteText_Click;
            Grid.SetColumn(value, 1);
            row.Children.Add(value);

            FieldsPanel.Children.Add(row);
            var divider = new Border
            {
                Height = 1
            };
            divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
            FieldsPanel.Children.Add(divider);
        }

        private void AddImageRow(string imagePath)
        {
            var bitmap = LoadBitmap(imagePath);
            if (bitmap == null) return;

            var panel = new Grid { Margin = new Thickness(0, 16, 0, 8) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = "图片",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 0, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(label);
            var image = new Image
            {
                Source = bitmap,
                MaxHeight = 260,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                ToolTip = "单击粘贴图片",
                Tag = imagePath
            };
            image.MouseLeftButtonDown += PasteImage_Click;
            Grid.SetColumn(image, 1);
            panel.Children.Add(image);
            FieldsPanel.Children.Add(panel);
        }

        private static bool IsPasswordField(string name)
        {
            var normalized = name.Trim().ToLowerInvariant().Replace(" ", "");
            return normalized == "密码" || normalized == "password" || normalized == "pwd";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            RenderRecords();
        }

        private void OpenSelectedRecordEditor()
        {
            if (String.IsNullOrEmpty(selectedTitle)) return;
            DemoRecord record;
            if (!demoRecords.TryGetValue(selectedTitle, out record)) return;
            if (String.Equals(record.Category, ClipboardCategory, StringComparison.CurrentCultureIgnoreCase)) return;

            editingOriginalTitle = record.Title;
            editingOriginalImagePath = record.ImagePath;
            EditorHeading.Text = "编辑记录";
            EditorTitle.Text = record.Title;
            EditorCategory.Text = record.Category;
            EditorRawText.Text = record.RawText;
            EditorTitle.ToolTip = null;
            EditorTitle.BorderBrush = (Brush)Application.Current.Resources["BorderBrush"];

            var bitmap = LoadBitmap(record.ImagePath);
            EditorImage.Source = bitmap;
            EditorImagePreview.Visibility = bitmap == null ? Visibility.Collapsed : Visibility.Visible;
            EditorOverlay.Visibility = Visibility.Visible;
            EditorRawText.Focus();
        }

        private void PasteText_Click(object sender, MouseButtonEventArgs e)
        {
            var element = sender as FrameworkElement;
            var value = element == null ? null : element.Tag as string;
            if (!String.IsNullOrEmpty(value))
            {
                try
                {
                    Clipboard.SetText(value);
                    lastClipboardSequence = GetClipboardSequenceNumber();
                    PasteToCapturedTarget();
                }
                catch { CopyCompleted(false); }
            }
            e.Handled = true;
        }

        private void PasteImage_Click(object sender, MouseButtonEventArgs e)
        {
            var element = sender as FrameworkElement;
            var bitmap = element == null ? null : LoadBitmap(element.Tag as string);
            if (bitmap == null) return;
            try
            {
                Clipboard.SetImage(bitmap);
                lastClipboardSequence = GetClipboardSequenceNumber();
                PasteToCapturedTarget();
            }
            catch { CopyCompleted(false); }
            e.Handled = true;
        }

        private void CopyCompleted(bool succeeded)
        {
            copyToastTimer.Stop();
            CopyToastText.Text = succeeded ? "已复制" : "复制失败";
            CopyToastText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                succeeded ? "#7ED0A4" : "#E58A8A"));
            CopyToast.Visibility = Visibility.Visible;
            copyToastTimer.Start();
        }

        private void PlaceholderButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null) button.Focus();
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            isLightTheme = !isLightTheme;
            SetBrush("WindowBrush", isLightTheme ? "#F4F6F8" : "#121416");
            SetBrush("SidebarBrush", isLightTheme ? "#FFFFFF" : "#181B1E");
            SetBrush("PanelBrush", isLightTheme ? "#FAFBFC" : "#1D2024");
            SetBrush("PanelAltBrush", isLightTheme ? "#F4F6F8" : "#202429");
            SetBrush("InputBrush", isLightTheme ? "#FFFFFF" : "#14171A");
            SetBrush("HoverBrush", isLightTheme ? "#E9EDF1" : "#282D32");
            SetBrush("SelectedBrush", isLightTheme ? "#DDEBE4" : "#29332F");
            SetBrush("BorderBrush", isLightTheme ? "#D8DDE3" : "#343940");
            SetBrush("DividerBrush", isLightTheme ? "#E1E5E9" : "#30353B");
            SetBrush("TextBrush", isLightTheme ? "#1B2026" : "#F3F5F7");
            SetBrush("MutedBrush", isLightTheme ? "#68717C" : "#949BA5");
            Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBrush"];
            ThemeButton.ToolTip = isLightTheme ? "切换深色主题" : "切换亮色主题";
        }

        private static void SetBrush(string key, string color)
        {
            Application.Current.Resources[key] = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        private void ClipboardMonitorTimer_Tick(object sender, EventArgs e)
        {
            var sequence = GetClipboardSequenceNumber();
            if (sequence == lastClipboardSequence) return;
            lastClipboardSequence = sequence;

            try
            {
                var title = "__clipboard_" + DateTime.UtcNow.Ticks.ToString("D19") + "_" + Guid.NewGuid().ToString("N");
                DemoRecord newRecord;
                if (Clipboard.ContainsImage())
                {
                    var image = Clipboard.GetImage();
                    if (image == null) return;
                    var imagePath = SaveClipboardImage(image);
                    if (String.IsNullOrEmpty(imagePath)) return;
                    newRecord = CreateClipboardRecord(title, "图片", imagePath);
                }
                else
                {
                    if (!Clipboard.ContainsText()) return;
                    var text = Clipboard.GetText().Trim();
                    if (text.Length == 0) return;

                    var latest = demoRecords.Values
                        .Where(record => record.Category == ClipboardCategory)
                        .OrderByDescending(record => record.Title, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (latest != null && String.IsNullOrEmpty(latest.ImagePath) && latest.RawText == text) return;
                    newRecord = CreateClipboardRecord(title, text);
                }

                demoRecords[title] = newRecord;
                var overflow = demoRecords.Values
                    .Where(record => record.Category == ClipboardCategory)
                    .OrderByDescending(record => record.Title, StringComparer.Ordinal)
                    .Skip(ClipboardHistoryLimit)
                    .ToList();
                foreach (var record in overflow)
                {
                    DeleteRecordImage(record);
                    demoRecords.Remove(record.Title);
                }

                SaveRecords();
                if (String.Equals(selectedCategory, ClipboardCategory, StringComparison.CurrentCultureIgnoreCase))
                {
                    selectedTitle = title;
                    RenderRecords();
                }
            }
            catch { }
        }

        private void CancelEditor_Click(object sender, RoutedEventArgs e)
        {
            CloseEditor();
        }

        private void SaveEditor_Click(object sender, RoutedEventArgs e)
        {
            var title = EditorTitle.Text.Trim();
            var rawText = EditorRawText.Text.Trim();
            if (title.Length == 0)
            {
                EditorTitle.ToolTip = "请输入名称";
                EditorTitle.Focus();
                return;
            }
            if (demoRecords.ContainsKey(title) && !String.Equals(title, editingOriginalTitle,
                StringComparison.CurrentCultureIgnoreCase))
            {
                EditorTitle.ToolTip = "这个名称已经存在";
                EditorTitle.BorderBrush = System.Windows.Media.Brushes.IndianRed;
                EditorTitle.Focus();
                return;
            }
            if (rawText.Length == 0 && String.IsNullOrEmpty(editingOriginalImagePath))
            {
                EditorRawText.Focus();
                return;
            }

            var category = EditorCategory.Text.Trim();
            if (category.Length == 0) category = "其他";
            var imagePath = editingOriginalImagePath;
            var record = ParseRecord(title, category, rawText, imagePath);
            if (!String.IsNullOrEmpty(editingOriginalTitle) && !String.Equals(editingOriginalTitle, title,
                StringComparison.CurrentCultureIgnoreCase))
            {
                demoRecords.Remove(editingOriginalTitle);
                deletedRecordTitles.Add(editingOriginalTitle);
            }
            deletedCategories.Remove(category);
            deletedRecordTitles.Remove(title);
            demoRecords[title] = record;
            SaveRecords();
            EnsureCategoryButton(category);
            selectedCategory = category;
            SelectCategoryButton(category);
            SearchBox.Clear();
            RenderRecords();
            SelectRecord(title);
            CloseEditor();
        }

        private void CloseEditor()
        {
            EditorOverlay.Visibility = Visibility.Collapsed;
            editingOriginalTitle = null;
            editingOriginalImagePath = null;
            SearchBox.Focus();
        }

        private void SelectCategoryButton(string category)
        {
            ClipboardButton.Tag = String.Equals(category, ClipboardCategory,
                StringComparison.CurrentCultureIgnoreCase) ? "Selected" : null;
            foreach (var child in CategoriesPanel.Children)
            {
                var button = child as Button;
                if (button != null) button.Tag = button.Content.ToString() == category ? "Selected" : null;
            }
        }

        private void EnsureCategoryButton(string category)
        {
            if (String.Equals(category, ClipboardCategory, StringComparison.CurrentCultureIgnoreCase)) return;
            if (deletedCategories.Contains(category)) return;
            foreach (var child in CategoriesPanel.Children)
            {
                var existing = child as Button;
                if (existing != null && existing.Content.ToString() == category) return;
            }

            var button = new Button
            {
                Content = category,
                Style = (Style)FindResource("NavButton")
            };
            button.Click += Category_Click;
            CategoriesPanel.Children.Add(button);
        }

        private void RemoveCategoryButton(string category)
        {
            Button target = null;
            foreach (var child in CategoriesPanel.Children)
            {
                var button = child as Button;
                if (button != null && button.Content.ToString() == category) target = button;
            }
            if (target != null) CategoriesPanel.Children.Remove(target);
        }

        private void SelectFirstCategory()
        {
            var first = CategoriesPanel.Children.OfType<Button>().FirstOrDefault();
            if (first == null)
            {
                selectedCategory = null;
                RecordsPanel.Children.Clear();
                ClearRecordDisplay();
                DeleteCategoryButton.IsEnabled = false;
                DeleteCategoryButton.Visibility = Visibility.Hidden;
                return;
            }
            selectedCategory = first.Content.ToString();
            SelectCategoryButton(selectedCategory);
            DeleteCategoryButton.IsEnabled = !String.Equals(selectedCategory, ClipboardCategory,
                StringComparison.CurrentCultureIgnoreCase);
            DeleteCategoryButton.Visibility = Visibility.Visible;
            RenderRecords();
        }

        private void RenderRecords()
        {
            if (RecordsPanel == null) return;
            RecordsPanel.Children.Clear();
            if (String.Equals(selectedCategory, ClipboardCategory, StringComparison.CurrentCultureIgnoreCase))
            {
                RenderClipboardHistory();
                return;
            }

            RecordTabsBar.Visibility = Visibility.Visible;
            FieldsPanel.Visibility = Visibility.Visible;
            ClipboardPanel.Visibility = Visibility.Collapsed;
            var query = SearchBox == null ? "" : SearchBox.Text.Trim();
            Button firstButton = null;
            var selectedIsVisible = false;
            var records = demoRecords.Values.Where(record => record.Category == selectedCategory);
            records = records.OrderBy(record => record.Title, StringComparer.CurrentCultureIgnoreCase);
            foreach (var record in records)
            {
                if (query.Length > 0 && record.SearchText.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0) continue;

                var button = new Button
                {
                    Content = record.Title,
                    CommandParameter = record.Title,
                    Style = (Style)FindResource("TopRecordButton"),
                    Tag = record.Title == selectedTitle ? "Selected" : null,
                    ToolTip = record.Title
                };
                button.Click += Record_Click;
                RecordsPanel.Children.Add(button);
                if (firstButton == null) firstButton = button;
                if (record.Title == selectedTitle) selectedIsVisible = true;
            }

            if (!selectedIsVisible && firstButton != null)
                SelectRecord(firstButton.CommandParameter as string);
            else if (firstButton == null)
                ClearRecordDisplay();
        }

        private void RenderClipboardHistory()
        {
            RecordTabsBar.Visibility = Visibility.Collapsed;
            FieldsPanel.Visibility = Visibility.Collapsed;
            ClipboardPanel.Visibility = Visibility.Visible;
            ClipboardPanel.Children.Clear();
            selectedTitle = null;
            EditRecordButton.IsEnabled = false;
            DeleteRecordButton.IsEnabled = false;

            var query = SearchBox == null ? "" : SearchBox.Text.Trim();
            var records = demoRecords.Values
                .Where(record => record.Category == ClipboardCategory)
                .Where(record => query.Length == 0 || record.SearchText.IndexOf(query,
                    StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderByDescending(record => record.Title, StringComparer.Ordinal);

            foreach (var record in records)
                ClipboardPanel.Children.Add(CreateClipboardHistoryRow(record));
        }

        private Border CreateClipboardHistoryRow(DemoRecord record)
        {
            var row = new Border
            {
                MinHeight = 52,
                Padding = new Thickness(0, 7, 0, 7),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Cursor = Cursors.Hand,
                Tag = record.Title,
                ToolTip = "单击粘贴"
            };
            row.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            if (!String.IsNullOrEmpty(record.ImagePath))
            {
                var thumbnail = new Image
                {
                    Source = LoadBitmap(record.ImagePath),
                    Stretch = Stretch.Uniform,
                    ToolTip = "单击粘贴图片"
                };
                var preview = new Border
                {
                    Width = 170,
                    Height = 110,
                    Padding = new Thickness(4),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E7EAED")),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = thumbnail,
                    ToolTip = "单击粘贴图片"
                };
                preview.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                grid.Children.Add(preview);
            }
            else
            {
                var text = new TextBlock
                {
                    Text = record.RawText,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 58,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = record.RawText
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                grid.Children.Add(text);
            }

            var status = new TextBlock
            {
                Text = "✓",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#68C895")),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);

            var deleteButton = new Button
            {
                Content = "×",
                Width = 26,
                Height = 26,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 15,
                Cursor = Cursors.Hand,
                ToolTip = "删除",
                Tag = record.Title
            };
            deleteButton.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            deleteButton.Click += DeleteClipboardItem_Click;
            Grid.SetColumn(deleteButton, 1);
            grid.Children.Add(deleteButton);

            row.Child = grid;
            row.MouseLeftButtonDown += ClipboardRow_Click;
            return row;
        }

        private void ClipboardRow_Click(object sender, MouseButtonEventArgs e)
        {
            var row = sender as Border;
            var title = row == null ? null : row.Tag as string;
            DemoRecord record;
            if (String.IsNullOrEmpty(title) || !demoRecords.TryGetValue(title, out record)) return;

            try
            {
                if (!String.IsNullOrEmpty(record.ImagePath))
                {
                    var bitmap = LoadBitmap(record.ImagePath);
                    if (bitmap == null) throw new InvalidOperationException("图片文件不存在或无法读取。");
                    Clipboard.SetImage(bitmap);
                }
                else
                {
                    Clipboard.SetText(record.RawText);
                }
                lastClipboardSequence = GetClipboardSequenceNumber();
                PasteToCapturedTarget();
                var grid = row.Child as Grid;
                if (grid != null && grid.Children.Count >= 3)
                {
                    var status = grid.Children[1] as TextBlock;
                    var deleteButton = grid.Children[2] as Button;
                    if (status != null) status.Visibility = Visibility.Visible;
                    if (deleteButton != null) deleteButton.Visibility = Visibility.Collapsed;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        if (status != null) status.Visibility = Visibility.Collapsed;
                        if (deleteButton != null) deleteButton.Visibility = Visibility.Visible;
                    };
                    timer.Start();
                }
            }
            catch { CopyCompleted(false); }
            e.Handled = true;
        }

        private void DeleteClipboardItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var title = button == null ? null : button.Tag as string;
            DemoRecord record;
            if (String.IsNullOrEmpty(title) || !demoRecords.TryGetValue(title, out record)) return;
            DeleteRecordImage(record);
            demoRecords.Remove(title);
            deletedRecordTitles.Add(title);
            SaveRecords();
            RenderClipboardHistory();
            e.Handled = true;
        }

        private void ClearRecordDisplay()
        {
            selectedTitle = null;
            FieldsPanel.Children.Clear();
            EditRecordButton.IsEnabled = false;
            DeleteRecordButton.IsEnabled = false;
        }

        private void SelectRecord(string title)
        {
            DemoRecord record;
            if (!demoRecords.TryGetValue(title, out record)) return;
            selectedTitle = title;
            EditRecordButton.IsEnabled = true;
            DeleteRecordButton.IsEnabled = true;
            DisplayRecord(record);
            foreach (var child in RecordsPanel.Children)
            {
                var button = child as Button;
                if (button != null) button.Tag = button.CommandParameter as string == title ? "Selected" : null;
            }
        }

        private static DemoRecord CreateClipboardRecord(string title, string text, string imagePath = null)
        {
            return new DemoRecord(title, ClipboardCategory,
                new List<RecordField> { new RecordField("内容", text) }, text, imagePath, true);
        }

        private static DemoRecord ParseRecord(string title, string category, string rawText, string imagePath = null)
        {
            var fields = new List<RecordField>();
            foreach (var sourceLine in rawText.Replace("\r", "").Split('\n'))
            {
                var line = sourceLine.Trim();
                if (line.Length == 0) continue;
                var separator = line.IndexOfAny(new[] { ':', '：' });
                if (separator <= 0)
                {
                    fields.Add(new RecordField("内容", line));
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                fields.Add(new RecordField(key, value));
            }

            return new DemoRecord(title, category, fields, rawText, imagePath, true);
        }

        private void LoadSavedRecords()
        {
            try
            {
                if (!File.Exists(recordsPath)) return;
                var document = XDocument.Load(recordsPath);
                foreach (var element in document.Root.Elements("deletedCategory"))
                {
                    var category = (string)element.Attribute("name");
                    if (String.IsNullOrWhiteSpace(category)) continue;
                    deletedCategories.Add(category);
                    RemoveCategoryButton(category);
                }
                deletedCategories.Remove(ClipboardCategory);
                foreach (var element in document.Root.Elements("record"))
                {
                    var title = (string)element.Attribute("title");
                    if (String.IsNullOrWhiteSpace(title)) continue;
                    var category = (string)element.Attribute("category") ?? "其他";
                    var imagePath = (string)element.Attribute("image");
                    var rawText = element.Value;
                    demoRecords[title] = category == ClipboardCategory
                        ? CreateClipboardRecord(title, rawText, imagePath)
                        : ParseRecord(title, category, rawText, imagePath);
                    EnsureCategoryButton(category);
                }
                foreach (var element in document.Root.Elements("deleted"))
                {
                    var title = (string)element.Attribute("title");
                    if (String.IsNullOrWhiteSpace(title)) continue;
                    deletedRecordTitles.Add(title);
                    demoRecords.Remove(title);
                }
            }
            catch { }
        }

        private void SaveRecords()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(recordsPath));
                var document = new XDocument(new XElement("records",
                    demoRecords.Values.Where(record => record.IsSaved).Select(record =>
                        new XElement("record",
                            new XAttribute("title", record.Title),
                            new XAttribute("category", record.Category),
                            String.IsNullOrEmpty(record.ImagePath) ? null : new XAttribute("image", record.ImagePath),
                            new XCData(record.RawText))),
                    deletedRecordTitles.Select(title => new XElement("deleted", new XAttribute("title", title)))));
                foreach (var category in deletedCategories)
                    document.Root.Add(new XElement("deletedCategory", new XAttribute("name", category)));
                document.Save(recordsPath);
            }
            catch { }
        }

        private static Dictionary<string, DemoRecord> CreateDemoRecords()
        {
            return new Dictionary<string, DemoRecord>(StringComparer.CurrentCultureIgnoreCase);
        }

        private static DemoRecord ParseDemoRecord(string title, string category, string rawText)
        {
            var parsed = ParseRecord(title, category, rawText);
            return new DemoRecord(parsed.Title, parsed.Category, parsed.Fields, rawText, null, false);
        }

        private static BitmapSource LoadBitmap(string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return FixFullyTransparentBitmap(bitmap);
            }
            catch { return null; }
        }

        private string SaveClipboardImage(BitmapSource image)
        {
            try
            {
                Directory.CreateDirectory(clipboardImagesFolder);
                var path = Path.Combine(clipboardImagesFolder,
                    DateTime.UtcNow.Ticks.ToString("D19") + "_" + Guid.NewGuid().ToString("N") + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(FixFullyTransparentBitmap(image)));
                using (var stream = File.Create(path)) encoder.Save(stream);
                return path;
            }
            catch { return null; }
        }

        private static BitmapSource FixFullyTransparentBitmap(BitmapSource source)
        {
            if (source == null) return null;

            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            for (var index = 3; index < pixels.Length; index += 4)
                if (pixels[index] != 0) return source;

            var opaque = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }

        private static void DeleteRecordImage(DemoRecord record)
        {
            if (record == null || String.IsNullOrEmpty(record.ImagePath) || !File.Exists(record.ImagePath)) return;
            try { File.Delete(record.ImagePath); } catch { }
        }

        private void CapturePasteTarget()
        {
            pasteTargetWindow = IntPtr.Zero;
            pasteTargetCaret = new NativeRect();
            hasPasteTargetCaret = false;
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == new WindowInteropHelper(this).Handle) return;

            pasteTargetWindow = foreground;

            try
            {
                var focusedElement = AutomationElement.FocusedElement;
                if (focusedElement != null
                    && focusedElement.Current.ProcessId == GetWindowProcessId(foreground))
                {
                    var controlType = focusedElement.Current.ControlType;
                    var bounds = focusedElement.Current.BoundingRectangle;
                    if ((controlType == ControlType.Edit || controlType == ControlType.Document)
                        && !bounds.IsEmpty && bounds.Width > 1 && bounds.Height > 1)
                    {
                        pasteTargetCaret = ScreenPixelsToLogicalRect(
                            bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                        hasPasteTargetCaret = true;
                        return;
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }

            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            uint ignoredProcessId;
            var threadId = GetWindowThreadProcessId(foreground, out ignoredProcessId);
            if (!GetGUIThreadInfo(threadId, ref info) || info.CaretWindow == IntPtr.Zero) return;

            var topLeft = new NativePoint { X = info.Caret.Left, Y = info.Caret.Top };
            var bottomRight = new NativePoint { X = info.Caret.Right, Y = info.Caret.Bottom };
            if (!ClientToScreen(info.CaretWindow, ref topLeft)
                || !ClientToScreen(info.CaretWindow, ref bottomRight)) return;

            pasteTargetCaret = ScreenPixelsToLogicalRect(
                topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            hasPasteTargetCaret = true;
        }

        private NativeRect ScreenPixelsToLogicalRect(double left, double top, double right, double bottom)
        {
            var source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null)
            {
                return new NativeRect
                {
                    Left = (int)left,
                    Top = (int)top,
                    Right = (int)right,
                    Bottom = (int)bottom
                };
            }

            var transform = source.CompositionTarget.TransformFromDevice;
            var topLeft = transform.Transform(new System.Windows.Point(left, top));
            var bottomRight = transform.Transform(new System.Windows.Point(right, bottom));
            return new NativeRect
            {
                Left = (int)topLeft.X,
                Top = (int)topLeft.Y,
                Right = (int)bottomRight.X,
                Bottom = (int)bottomRight.Y
            };
        }

        private void PositionNearCaret()
        {
            var workArea = SystemParameters.WorkArea;
            if (!hasPasteTargetCaret)
            {
                Left = workArea.Left + (workArea.Width - DefaultWindowWidth) / 2;
                Top = workArea.Top + (workArea.Height - DefaultWindowHeight) / 2;
                return;
            }

            const double gap = 12;
            var left = pasteTargetCaret.Right + gap;
            if (left + DefaultWindowWidth > workArea.Right)
                left = pasteTargetCaret.Left - DefaultWindowWidth - gap;

            var top = pasteTargetCaret.Top - DefaultWindowHeight - gap;
            if (top < workArea.Top)
                top = pasteTargetCaret.Bottom + gap;

            Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - DefaultWindowWidth));
            Top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - DefaultWindowHeight));
        }

        private static int GetWindowProcessId(IntPtr window)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            return unchecked((int)processId);
        }

        private void PasteToCapturedTarget()
        {
            if (pasteTargetWindow == IntPtr.Zero)
            {
                CopyCompleted(true);
                return;
            }

            copyToastTimer.Stop();
            var target = pasteTargetWindow;
            if (!isPinned)
            {
                Hide();
                WindowShell.Opacity = 0;
                pasteTargetWindow = IntPtr.Zero;
            }
            SetForegroundWindow(target);
            var pasteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            pasteTimer.Tick += delegate
            {
                pasteTimer.Stop();
                keybd_event(0x11, 0, 0, UIntPtr.Zero);
                keybd_event(0x56, 0, 0, UIntPtr.Zero);
                keybd_event(0x56, 0, 0x0002, UIntPtr.Zero);
                keybd_event(0x11, 0, 0x0002, UIntPtr.Zero);
            };
            pasteTimer.Start();
        }

        private sealed class DemoRecord
        {
            public DemoRecord(string title, string category, List<RecordField> fields, string rawText, string imagePath, bool isSaved)
            {
                Title = title; Category = category; Fields = fields; RawText = rawText; ImagePath = imagePath; IsSaved = isSaved;
            }
            public string Title { get; private set; }
            public string Category { get; private set; }
            public List<RecordField> Fields { get; private set; }
            public string RawText { get; private set; }
            public string ImagePath { get; private set; }
            public bool IsSaved { get; private set; }
            public string SearchText { get { return String.Join(" ", Title, Category, RawText); } }
        }

        private sealed class RecordField
        {
            public RecordField(string name, string value)
            {
                Name = name;
                Value = value;
            }
            public string Name { get; private set; }
            public string Value { get; private set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public int Flags;
            public IntPtr ActiveWindow;
            public IntPtr FocusWindow;
            public IntPtr CaptureWindow;
            public IntPtr MenuOwnerWindow;
            public IntPtr MoveSizeWindow;
            public IntPtr CaretWindow;
            public NativeRect Caret;
        }
    }
}

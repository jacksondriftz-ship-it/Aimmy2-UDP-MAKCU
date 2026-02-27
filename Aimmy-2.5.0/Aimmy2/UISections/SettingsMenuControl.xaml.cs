using AILogic;
using Aimmy2.AILogic;
using Aimmy2.Class;
using Aimmy2.UILibrary;
using Other;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UILibrary;
using Visuality;
using LogLevel = Other.LogManager.LogLevel;

namespace Aimmy2.Controls
{
    public partial class SettingsMenuControl : UserControl
    {
        private MainWindow? _mainWindow;
        private bool _isInitialized;

        // UDP status refresh timer
        private DispatcherTimer? _udpStatusTimer;

        // Local minimize state management
        private readonly Dictionary<string, bool> _localMinimizeState = new()
        {
            { "Model Settings", false },
            { "Settings Menu", false },
            { "Theme Settings", false },
            { "Screen Settings", false }
        };

        // Public properties for MainWindow access
        public StackPanel ModelSettingsPanel => ModelSettings;
        public StackPanel SettingsConfigPanel => SettingsConfig;
        public StackPanel ThemeMenuPanel => ThemeMenu;
        public StackPanel DisplaySelectMenuPanel => DisplaySelectMenu;
        public ScrollViewer SettingsMenuScrollViewer => SettingsMenu;

        public SettingsMenuControl()
        {
            InitializeComponent();
        }

        public void Initialize(MainWindow mainWindow)
        {
            if (_isInitialized) return;

            _mainWindow = mainWindow;
            _isInitialized = true;

            LoadMinimizeStatesFromGlobal();

            LoadModelSettings();
            LoadSettingsConfig();
            LoadThemeMenu();
            LoadDisplaySelectMenu();

            ApplyMinimizeStates();

            DisplayManager.DisplayChanged += OnDisplayChanged;
            AIManager.ClassesUpdated += OnClassesChanged;
            AIManager.DynamicModelStatusChanged += OnDynamicModelStatusChanged;

            UpdateDynamicModelDropdownsVisibility(AIManager.CurrentModelIsDynamic);
            UpdateTargetClassDropdown(_mainWindow!.uiManager.D_TargetClass!);
        }

        #region Minimize State Management

        private void LoadMinimizeStatesFromGlobal()
        {
            foreach (var key in _localMinimizeState.Keys.ToList())
            {
                if (Dictionary.minimizeState.ContainsKey(key))
                    _localMinimizeState[key] = Dictionary.minimizeState[key];
            }
        }

        private void SaveMinimizeStatesToGlobal()
        {
            foreach (var kvp in _localMinimizeState)
                Dictionary.minimizeState[kvp.Key] = kvp.Value;
        }

        private void ApplyMinimizeStates()
        {
            ApplyPanelState("Model Settings", ModelSettingsPanel);
            ApplyPanelState("Settings Menu", SettingsConfigPanel);
            ApplyPanelState("Theme Settings", ThemeMenuPanel);
            ApplyPanelState("Screen Settings", DisplaySelectMenuPanel);
        }

        private void ApplyPanelState(string stateName, StackPanel panel)
        {
            if (_localMinimizeState.TryGetValue(stateName, out bool isMinimized))
                SetPanelVisibility(panel, !isMinimized);
        }

        private void SetPanelVisibility(StackPanel panel, bool isVisible)
        {
            foreach (UIElement child in panel.Children)
            {
                bool shouldStayVisible = child is ATitle || child is ASpacer || child is ARectangleBottom;
                child.Visibility = shouldStayVisible
                    ? Visibility.Visible
                    : (isVisible ? Visibility.Visible : Visibility.Collapsed);
            }
        }

        private void TogglePanel(string stateName, StackPanel panel)
        {
            if (!_localMinimizeState.ContainsKey(stateName)) return;
            _localMinimizeState[stateName] = !_localMinimizeState[stateName];
            SetPanelVisibility(panel, !_localMinimizeState[stateName]);
            SaveMinimizeStatesToGlobal();
        }

        #endregion

        #region Menu Section Loaders

        private void LoadModelSettings()
        {
            var uiManager = _mainWindow!.uiManager;
            var builder = new SectionBuilder(this, ModelSettings);

            builder
                .AddTitle("Model Settings", true, t =>
                {
                    uiManager.AT_ModelSettings = t;
                    t.Minimize.Click += (s, e) => TogglePanel("Model Settings", ModelSettingsPanel);
                })
                .AddDropdown("Image Size", d =>
                {
                    uiManager.D_ImageSize = d;

                    _mainWindow.AddDropdownItem(d, "640");
                    _mainWindow.AddDropdownItem(d, "512");
                    _mainWindow.AddDropdownItem(d, "416");
                    _mainWindow.AddDropdownItem(d, "320");
                    _mainWindow.AddDropdownItem(d, "256");
                    _mainWindow.AddDropdownItem(d, "160");

                    var currentSize = Dictionary.dropdownState["Image Size"];
                    for (int i = 0; i < d.DropdownBox.Items.Count; i++)
                    {
                        if ((d.DropdownBox.Items[i] as ComboBoxItem)?.Content?.ToString() == currentSize)
                        {
                            d.DropdownBox.SelectedIndex = i;
                            break;
                        }
                    }

                    d.DropdownBox.SelectionChanged += async (s, e) =>
                    {
                        if (d.DropdownBox.SelectedItem == null || e.AddedItems.Count == 0) return;

                        var newSize = (d.DropdownBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
                        if (string.IsNullOrEmpty(newSize)) return;

                        if (FileManager.AIManager == null || Dictionary.lastLoadedModel == "N/A")
                        {
                            Dictionary.dropdownState["Image Size"] = newSize;
                            LogManager.Log(LogLevel.Info, $"Image size set to {newSize}x{newSize} (no model loaded)", true, 2000);
                            return;
                        }

                        FileManager.CurrentlyLoadingModel = true;
                        LogManager.Log(LogLevel.Info, $"Image size changing to {newSize}");

                        try
                        {
                            if (FileManager.AIManager != null)
                            {
                                FileManager.AIManager.RequestSizeChange(int.Parse(newSize));
                                await Task.Delay(100);
                            }

                            var modelPath = System.IO.Path.Combine("bin/models", Dictionary.lastLoadedModel);
                            FileManager.AIManager?.Dispose();
                            FileManager.AIManager = null;

                            Dictionary.dropdownState["Image Size"] = newSize;
                            FileManager.AIManager = new AIManager(modelPath);

                            LogManager.Log(LogLevel.Info, $"Successfully changed image size to {newSize}x{newSize}", true, 2000);
                        }
                        catch (Exception ex)
                        {
                            LogManager.Log(LogLevel.Error, $"Error changing image size: {ex.Message}", true, 5000);
                        }
                        finally
                        {
                            FileManager.CurrentlyLoadingModel = false;
                        }
                    };
                }, tooltip: "Resolution the AI uses for detection. Smaller = faster but less accurate.")
                .AddDropdown("Target Class", d =>
                {
                    d.DropdownBox.SelectedIndex = 0;
                    uiManager.D_TargetClass = d;
                    _mainWindow.AddDropdownItem(d, "Best Confidence");
                    UpdateTargetClassDropdown(d);
                }, tooltip: "Which type of target to aim at. Best Confidence picks the most certain detection.")
                .AddSlider("AI Minimum Confidence", "% Confidence", 1, 1, 1, 100, s =>
                {
                    uiManager.S_AIMinimumConfidence = s;
                    s.Slider.PreviewMouseLeftButtonUp += (sender, e) =>
                    {
                        var value = s.Slider.Value;
                        if (value >= 95)
                            LogManager.Log(LogLevel.Warning, "The minimum confidence you have set for Aimmy to be too high and may be unable to detect players.", true);
                        else if (value <= 35)
                            LogManager.Log(LogLevel.Warning, "The minimum confidence you have set for Aimmy may be too low can cause false positives.", true);
                    };
                }, tooltip: "How sure the AI must be before targeting. Higher = fewer false detections but may miss targets.")
                .AddToggle("Enable Model Switch Keybind", t => uiManager.T_EnableModelSwitchKeybind = t,
                    tooltip: "Allow switching between AI models using a hotkey.")
                .AddKeyChanger("Model Switch Keybind", k => uiManager.C_ModelSwitchKeybind = k,
                    tooltip: "Press this key to cycle through available AI models.")
                .AddKeyChanger("Emergency Stop Keybind", k => uiManager.C_EmergencyKeybind = k,
                    tooltip: "Press this key to immediately stop all aim assist functions.")
                .AddSeparator();
        }

        private void LoadSettingsConfig()
        {
            var uiManager = _mainWindow!.uiManager;
            var builder = new SectionBuilder(this, SettingsConfig);

            builder
                .AddTitle("Settings Menu", true, t =>
                {
                    uiManager.AT_SettingsMenu = t;
                    t.Minimize.Click += (s, e) => TogglePanel("Settings Menu", SettingsConfigPanel);
                })
                .AddToggle("Collect Data While Playing", t => uiManager.T_CollectDataWhilePlaying = t,
                    tooltip: "Save screenshots of detections for training new AI models.")
                .AddToggle("Auto Label Data", t => uiManager.T_AutoLabelData = t,
                    tooltip: "Automatically label collected screenshots with detection data.")
                .AddToggle("Mouse Background Effect", t => uiManager.T_MouseBackgroundEffect = t,
                    tooltip: "Show a visual effect on the UI when moving your mouse.")
                .AddToggle("UI TopMost", t => uiManager.T_UITopMost = t,
                    tooltip: "Keep this window above all other windows.")
                .AddToggle("Debug Mode", t => uiManager.T_DebugMode = t,
                    tooltip: "Show extra information useful for troubleshooting problems.")
                .AddButton("Save Config", b =>
                {
                    uiManager.B_SaveConfig = b;
                    b.Reader.Click += (s, e) => new ConfigSaver().ShowDialog();
                }, tooltip: "Save your current settings to a file you can load later.")
                .AddSeparator();
        }

        private void LoadDisplaySelectMenu()
        {
            var uiManager = _mainWindow!.uiManager;
            var builder = new SectionBuilder(this, DisplaySelectMenu);

            builder
                .AddTitle("Screen Settings", true, t =>
                {
                    uiManager.AT_DisplaySelector = t;
                    t.Minimize.Click += (s, e) =>
                        TogglePanel("Screen Settings", DisplaySelectMenuPanel);
                })
                .AddDropdown("Screen Capture Method", d =>
                {
                    d.DropdownBox.SelectedIndex = -1;
                    uiManager.D_ScreenCaptureMethod = d;
                    _mainWindow.AddDropdownItem(d, "DirectX");
                    _mainWindow.AddDropdownItem(d, "GDI+");
                    _mainWindow.AddDropdownItem(d, "UDP Stream");

                    // Show/hide UDP panel when selection changes
                    d.DropdownBox.SelectionChanged += OnCaptureMethodChanged;
                }, tooltip: "How the screen is captured. DirectX is faster, GDI+ works on more systems. UDP Stream receives video from another PC.")
                .AddToggle("StreamGuard", t => uiManager.T_StreamGuard = t,
                    tooltip: "Hide the overlay from screen recordings and streams.")
                .AddSeparator();

            // ── UDP Stream settings panel ──────────────────────────────────────────
            // Built manually so we can hold references for show/hide logic.
            // Inserted before the separator (last 2 children = ARectangleBottom + ASpacer).
            var udpPanel = BuildUDPPanel();
            var insertAt = DisplaySelectMenu.Children.Count - 2;
            DisplaySelectMenu.Children.Insert(insertAt, udpPanel);

            // Set initial visibility based on saved capture method
            var savedMethod = Dictionary.dropdownState.TryGetValue("Screen Capture Method", out var m) ? m : "DirectX";
            udpPanel.Visibility = savedMethod == "UDP Stream" ? Visibility.Visible : Visibility.Collapsed;
            // ──────────────────────────────────────────────────────────────────────

            // DisplaySelector
            uiManager.DisplaySelector = new ADisplaySelector();
            uiManager.DisplaySelector.RefreshDisplays();

            var dsInsert = DisplaySelectMenu.Children.Count - 2;
            DisplaySelectMenu.Children.Insert(dsInsert, uiManager.DisplaySelector);

            var refreshButton = new APButton("Refresh Displays", "Update the list of available monitors.");
            refreshButton.Reader.Click += (s, e) =>
            {
                try
                {
                    DisplayManager.RefreshDisplays();
                    uiManager.DisplaySelector.RefreshDisplays();
                    LogManager.Log(LogLevel.Info, "Display list refreshed successfully.", true);
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogLevel.Error, $"Error refreshing displays: {ex.Message}", true);
                }
            };
            DisplaySelectMenu.Children.Insert(dsInsert + 1, refreshButton);
        }

        // ── UDP panel ────────────────────────────────────────────────────────────

        // Held as fields so OnCaptureMethodChanged can show/hide the panel and
        // the status timer can update the label without searching the visual tree.
        private StackPanel? _udpPanel;
        private TextBlock? _udpStatusText;

        private StackPanel BuildUDPPanel()
        {
            _udpPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

            // ── IP row ────────────────────────────────────────────────────────
            var ipLabel = new TextBlock
            {
                Text = "Sender IP  (use 0.0.0.0 to accept from any address)",
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.TryFindResource("Atkinson Hyperlegible"),
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 2)
            };

            var ipBox = new TextBox
            {
                Text = Dictionary.dropdownState.TryGetValue("UDP Stream IP", out var savedIP) ? savedIP : "0.0.0.0",
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x3F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.TryFindResource("Atkinson Hyperlegible"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                CaretBrush = System.Windows.Media.Brushes.White
            };
            ipBox.TextChanged += (s, e) =>
                Dictionary.dropdownState["UDP Stream IP"] = ipBox.Text.Trim();

            // ── Port row ──────────────────────────────────────────────────────
            var portLabel = new TextBlock
            {
                Text = "UDP Port",
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.TryFindResource("Atkinson Hyperlegible"),
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 2)
            };

            var portBox = new TextBox
            {
                Text = Dictionary.dropdownState.TryGetValue("UDP Stream Port", out var savedPort) ? savedPort : "11000",
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x3F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.TryFindResource("Atkinson Hyperlegible"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                CaretBrush = System.Windows.Media.Brushes.White
            };
            // Only allow digits in the port box
            portBox.PreviewTextInput += (s, e) =>
            {
                e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(e.Text, @"^\d+$");
            };
            portBox.TextChanged += (s, e) =>
                Dictionary.dropdownState["UDP Stream Port"] = portBox.Text.Trim();

            // ── Status label ──────────────────────────────────────────────────
            _udpStatusText = new TextBlock
            {
                Text = "Status: Not connected",
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.TryFindResource("Atkinson Hyperlegible"),
                FontSize = 10,
                Margin = new Thickness(4, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };

            // ── Assemble ──────────────────────────────────────────────────────
            _udpPanel.Children.Add(ipLabel);
            _udpPanel.Children.Add(ipBox);
            _udpPanel.Children.Add(portLabel);
            _udpPanel.Children.Add(portBox);
            _udpPanel.Children.Add(_udpStatusText);

            // ── Status refresh timer (500 ms) ─────────────────────────────────
            _udpStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _udpStatusTimer.Tick += (s, e) => RefreshUDPStatus();
            _udpStatusTimer.Start();

            return _udpPanel;
        }

        private void OnCaptureMethodChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_udpPanel == null) return;
            var selected = (((ComboBox)sender).SelectedItem as ComboBoxItem)?.Content?.ToString();
            _udpPanel.Visibility = selected == "UDP Stream" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshUDPStatus()
        {
            if (_udpStatusText == null || _udpPanel?.Visibility != Visibility.Visible) return;
            // CaptureManager is internal to AILogic – read status via the static accessor we added
            _udpStatusText.Text = $"Status: {CaptureManager.UDPStatus}";
        }

        // ────────────────────────────────────────────────────────────────────────

        private void LoadThemeMenu()
        {
            var uiManager = _mainWindow!.uiManager;
            var builder = new SectionBuilder(this, ThemeMenu);

            builder
                .AddTitle("Theme Settings", true, t =>
                {
                    uiManager.AT_ThemeColorWheel = t;
                    t.Minimize.Click += (s, e) =>
                        TogglePanel("Theme Settings", ThemeMenuPanel);
                })
                .AddSeparator();

            uiManager.ThemeColorWheel = new AColorWheel();

            var arrowButton = uiManager.ThemeColorWheel.FindName("ArrowButton") as Button;
            arrowButton.Visibility = Visibility.Visible;

            var insertIndex = ThemeMenu.Children.Count - 2;
            ThemeMenu.Children.Insert(insertIndex, uiManager.ThemeColorWheel);
        }

        #endregion

        #region Helper Methods

        private void OnDisplayChanged(object? sender, DisplayChangedEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    LogManager.Log(LogLevel.Info, $"AI focus switched to Display {e.DisplayIndex + 1} ({e.Bounds.Width}x{e.Bounds.Height})", true);
                    UpdateDisplayRelatedSettings(e);
                }
                catch (Exception) { }
            });
        }

        private void UpdateDisplayRelatedSettings(DisplayChangedEventArgs e)
        {
            Dictionary.sliderSettings["SelectedDisplay"] = e.DisplayIndex;
        }

        public void UpdateImageSizeDropdown(string newSize)
        {
            if (_mainWindow?.uiManager.D_ImageSize != null)
            {
                var dropdown = _mainWindow.uiManager.D_ImageSize;
                for (int i = 0; i < dropdown.DropdownBox.Items.Count; i++)
                {
                    if ((dropdown.DropdownBox.Items[i] as ComboBoxItem)?.Content?.ToString() == newSize)
                    {
                        dropdown.DropdownBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void OnClassesChanged(Dictionary<int, string> classes)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_mainWindow?.uiManager.D_TargetClass != null)
                    UpdateTargetClassDropdown(_mainWindow.uiManager.D_TargetClass, classes);
            });
        }

        private void OnDynamicModelStatusChanged(bool isDynamic)
        {
            Application.Current.Dispatcher.Invoke(() =>
                UpdateDynamicModelDropdownsVisibility(isDynamic));
        }

        private void UpdateDynamicModelDropdownsVisibility(bool isDynamic)
        {
            var imageSizeVisibility = isDynamic ? Visibility.Visible : Visibility.Collapsed;
            if (_mainWindow?.uiManager.D_ImageSize != null)
                _mainWindow.uiManager.D_ImageSize.Visibility = imageSizeVisibility;
        }

        private void UpdateTargetClassDropdown(ADropdown dropdown, Dictionary<int, string>? _classes = null)
        {
            if (dropdown?.DropdownBox == null) return;
            var visibility = _classes != null && _classes.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            dropdown.Visibility = visibility;
            _mainWindow!.uiManager.D_TargetClass!.Visibility = visibility;

            string? selection = (dropdown.DropdownBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

            var removedItems = dropdown.DropdownBox.Items.Cast<ComboBoxItem>()
                .Where(item => item.Content?.ToString() != "Best Confidence")
                .ToList();

            foreach (var item in removedItems)
                dropdown.DropdownBox.Items.Remove(item);

            var classes = _classes ?? FileManager.AIManager?.ModelClasses ?? new Dictionary<int, string>();

            foreach (var kvp in classes.OrderBy(x => x.Key))
                _mainWindow!.AddDropdownItem(dropdown, kvp.Value);

            if (!string.IsNullOrEmpty(selection))
            {
                for (int i = 0; i < dropdown.DropdownBox.Items.Count; i++)
                {
                    if ((dropdown.DropdownBox.Items[i] as ComboBoxItem)?.Content?.ToString() == selection)
                    {
                        dropdown.DropdownBox.SelectedIndex = i;
                        return;
                    }
                }
            }

            dropdown.DropdownBox.SelectedIndex = 0;
        }

        public void Dispose()
        {
            DisplayManager.DisplayChanged -= OnDisplayChanged;
            AIManager.ClassesUpdated -= OnClassesChanged;
            _mainWindow?.uiManager.DisplaySelector?.Dispose();
            _udpStatusTimer?.Stop();
            SaveMinimizeStatesToGlobal();
        }

        #endregion

        #region Control Creation Methods

        private AToggle CreateToggle(string title, string? tooltip = null)
        {
            var toggle = new AToggle(title, tooltip);
            _mainWindow!.toggleInstances[title] = toggle;

            if (Dictionary.toggleState[title])
                toggle.EnableSwitch();
            else
                toggle.DisableSwitch();

            toggle.Reader.Click += (sender, e) =>
            {
                Dictionary.toggleState[title] = !Dictionary.toggleState[title];
                _mainWindow.UpdateToggleUI(toggle, Dictionary.toggleState[title]);
                _mainWindow.Toggle_Action(title);
            };

            return toggle;
        }

        private AKeyChanger CreateKeyChanger(string title, string keybind, string? tooltip = null)
        {
            var keyChanger = new AKeyChanger(title, keybind, tooltip);

            keyChanger.Reader.Click += (sender, e) =>
            {
                keyChanger.KeyNotifier.Content = "...";
                _mainWindow!.bindingManager.StartListeningForBinding(title);

                Action<string, string>? bindingSetHandler = null;
                bindingSetHandler = (bindingId, key) =>
                {
                    if (bindingId == title)
                    {
                        keyChanger.KeyNotifier.Content = KeybindNameManager.ConvertToRegularKey(key);
                        Dictionary.bindingSettings[bindingId] = key;
                        _mainWindow.bindingManager.OnBindingSet -= bindingSetHandler;
                    }
                };

                _mainWindow.bindingManager.OnBindingSet += bindingSetHandler;
            };

            return keyChanger;
        }

        private ASlider CreateSlider(string title, string label, double frequency, double buttonSteps,
            double min, double max, string? tooltip = null)
        {
            var slider = new ASlider(title, label, buttonSteps, tooltip)
            {
                Slider = { Minimum = min, Maximum = max, TickFrequency = frequency }
            };

            slider.Slider.Value = Dictionary.sliderSettings.TryGetValue(title, out var value) ? value : min;
            slider.Slider.ValueChanged += (s, e) => Dictionary.sliderSettings[title] = slider.Slider.Value;

            return slider;
        }

        private ADropdown CreateDropdown(string title, string? tooltip = null) => new(title, title, tooltip);

        #endregion

        #region Section Builder

        private class SectionBuilder
        {
            private readonly SettingsMenuControl _parent;
            private readonly StackPanel _panel;

            public SectionBuilder(SettingsMenuControl parent, StackPanel panel)
            {
                _parent = parent;
                _panel = panel;
            }

            public SectionBuilder AddTitle(string title, bool canMinimize, Action<ATitle>? configure = null)
            {
                var titleControl = new ATitle(title, canMinimize);
                configure?.Invoke(titleControl);
                _panel.Children.Add(titleControl);
                return this;
            }

            public SectionBuilder AddToggle(string title, Action<AToggle>? configure = null, string? tooltip = null)
            {
                var toggle = _parent.CreateToggle(title, tooltip);
                configure?.Invoke(toggle);
                _panel.Children.Add(toggle);
                return this;
            }

            public SectionBuilder AddKeyChanger(string title, Action<AKeyChanger>? configure = null,
                string? defaultKey = null, string? tooltip = null)
            {
                var key = defaultKey ?? Dictionary.bindingSettings[title];
                var keyChanger = _parent.CreateKeyChanger(title, key, tooltip);
                configure?.Invoke(keyChanger);
                _panel.Children.Add(keyChanger);
                return this;
            }

            public SectionBuilder AddSlider(string title, string label, double frequency, double buttonSteps,
                double min, double max, Action<ASlider>? configure = null, string? tooltip = null)
            {
                var slider = _parent.CreateSlider(title, label, frequency, buttonSteps, min, max, tooltip);
                configure?.Invoke(slider);
                _panel.Children.Add(slider);
                return this;
            }

            public SectionBuilder AddDropdown(string title, Action<ADropdown>? configure = null, string? tooltip = null)
            {
                var dropdown = _parent.CreateDropdown(title, tooltip);
                configure?.Invoke(dropdown);
                _panel.Children.Add(dropdown);
                return this;
            }

            public SectionBuilder AddButton(string title, Action<APButton>? configure = null, string? tooltip = null)
            {
                var button = new APButton(title, tooltip);
                configure?.Invoke(button);
                _panel.Children.Add(button);
                return this;
            }

            public SectionBuilder AddSeparator()
            {
                _panel.Children.Add(new ARectangleBottom());
                _panel.Children.Add(new ASpacer());
                return this;
            }
        }

        #endregion
    }
}

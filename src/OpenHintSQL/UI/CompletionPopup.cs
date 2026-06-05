using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;
using OpenHintSQL.Providers;
using OpenHintSQL.Utils;

namespace OpenHintSQL.UI
{
    /// <summary>
    /// Custom WPF popup for displaying SQL completion items.
    /// Dark-themed to match SSMS, with virtualized rendering for performance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The popup does NOT capture keyboard focus — all keyboard events are routed through
    /// <see cref="Completion.CompletionCommandFilter"/> which calls <see cref="SelectNext"/>,
    /// <see cref="SelectPrevious"/>, and <see cref="AcceptSelected"/> as needed.
    /// </para>
    /// <para>
    /// All public methods are thread-safe: they dispatch to the UI thread when necessary.
    /// </para>
    /// </remarks>
    internal sealed class CompletionPopup
    {
        // ═══════════════════════════════════════════════════════════════
        //  THEME COLORS
        // ═══════════════════════════════════════════════════════════════

        private static readonly Color BackgroundColor = (Color)ColorConverter.ConvertFromString("#FFFFFF");
        private static readonly Color ForegroundColor = (Color)ColorConverter.ConvertFromString("#1F2328");
        private static readonly Color SelectionColor = (Color)ColorConverter.ConvertFromString("#BFD8FF");
        private static readonly Color HoverColor = (Color)ColorConverter.ConvertFromString("#F6F8FA");
        private static readonly Color BorderColor = (Color)ColorConverter.ConvertFromString("#D0D7DE");
        private static readonly Color DescriptionColor = (Color)ColorConverter.ConvertFromString("#6E7781");
        private static readonly Color IconBackgroundColor = (Color)ColorConverter.ConvertFromString("#EEF4FF");
        private static readonly Color IconForegroundColor = (Color)ColorConverter.ConvertFromString("#0969DA");
        private static readonly Color SelectionStripeColor = (Color)ColorConverter.ConvertFromString("#005FCC");

        private static readonly Brush BackgroundBrush = new SolidColorBrush(BackgroundColor);
        private static readonly Brush ForegroundBrush = new SolidColorBrush(ForegroundColor);
        private static readonly Brush SelectionBrush = new SolidColorBrush(SelectionColor);
        private static readonly Brush HoverBrush = new SolidColorBrush(HoverColor);
        private static readonly Brush BorderBrush_ = new SolidColorBrush(BorderColor);
        private static readonly Brush DescriptionBrush = new SolidColorBrush(DescriptionColor);
        private static readonly Brush IconBackgroundBrush = new SolidColorBrush(IconBackgroundColor);
        private static readonly Brush IconForegroundBrush = new SolidColorBrush(IconForegroundColor);
        private static readonly Brush SelectionStripeBrush = new SolidColorBrush(SelectionStripeColor);
        private static readonly Brush TransparentBrush = Brushes.Transparent;

        // ═══════════════════════════════════════════════════════════════
        //  LAYOUT CONSTANTS
        // ═══════════════════════════════════════════════════════════════

        private const double PopupMaxHeight = 300;
        private const double PopupWidth = 440;
        private const double DetailPopupMinWidth = 240;
        private const double DetailPopupPreferredWidth = 360;
        private const double DetailPopupMaxWidth = 520;
        private const double DetailPopupPreferredMaxHeight = PopupMaxHeight;
        private const double DetailPopupMaxHeight = 420;
        private const double DetailPopupGap = 4;
        private const double DetailPopupViewportMargin = 14;
        private const double DetailPopupMinHeight = 96;
        private const double FadeInDurationMs = 120;

        // ═══════════════════════════════════════════════════════════════
        //  UI ELEMENTS
        // ═══════════════════════════════════════════════════════════════

        private readonly Popup _popup;
        private readonly Popup _detailPopup;
        private readonly ListBox _listBox;
        private readonly Border _border;
        private readonly Border _detailBorder;
        private readonly ScrollViewer _detailScrollViewer;
        private readonly TextBlock _detailTitleText;
        private readonly TextBlock _detailMetaText;
        private readonly TextBlock _detailDescriptionText;

        // ═══════════════════════════════════════════════════════════════
        //  STATE
        // ═══════════════════════════════════════════════════════════════

        private List<CompletionItemData> _allItems;
        private List<CompletionItemData> _filteredItems;
        private string _currentPrefix;
        private IWpfTextView _currentView;

        /// <summary>
        /// Fired when a completion item is accepted (via Enter, Tab, or mouse click).
        /// </summary>
        public event Action<CompletionItemData> ItemAccepted;

        /// <summary>
        /// Gets whether the popup is currently visible.
        /// </summary>
        public bool IsVisible
        {
            get
            {
                try { return _popup.IsOpen; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Gets whether the popup currently has any items to display.
        /// </summary>
        public bool HasItems
        {
            get
            {
                try { return _filteredItems != null && _filteredItems.Count > 0; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Gets whether the popup is currently displaying a non-insertable status row
        /// such as schema-loading feedback.
        /// </summary>
        public bool IsShowingStatus
        {
            get
            {
                try
                {
                    return _filteredItems != null &&
                           _filteredItems.Any(item => item.Kind == CompletionItemKind.Status);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets the currently selected item, or null if nothing is selected.
        /// </summary>
        public CompletionItemData SelectedItem
        {
            get
            {
                try
                {
                    return _listBox.SelectedItem as CompletionItemData;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Creates a new completion popup with dark-themed styling.
        /// Must be called on the UI thread.
        /// </summary>
        public CompletionPopup()
        {
            // Freeze shared brushes for cross-thread safety
            FreezeBrushes();

            // Build the ListBox with virtualization
            _listBox = CreateListBox();
            _listBox.SelectionChanged += (s, e) => UpdateDetailPopupForSelection();
            _listBox.MouseMove += OnListBoxMouseMove;

            // Wrap in a border for styling
            _border = new Border
            {
                Background = BackgroundBrush,
                BorderBrush = BorderBrush_,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = _listBox,
                MaxHeight = PopupMaxHeight,
                Width = PopupWidth,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 12,
                    ShadowDepth = 2,
                    Opacity = 0.18
                }
            };

            // Create the popup container
            _popup = new Popup
            {
                Child = _border,
                AllowsTransparency = true,
                Placement = PlacementMode.RelativePoint,
                StaysOpen = false,
                Focusable = false,
                PopupAnimation = PopupAnimation.None // We handle animation manually
            };

            _detailTitleText = new TextBlock
            {
                Foreground = ForegroundBrush,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap
            };

            _detailMetaText = new TextBlock
            {
                Foreground = DescriptionBrush,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };

            _detailDescriptionText = new TextBlock
            {
                Foreground = ForegroundBrush,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap
            };

            var detailStack = new StackPanel
            {
                Margin = new Thickness(14, 12, 14, 12)
            };
            detailStack.Children.Add(_detailTitleText);
            detailStack.Children.Add(_detailMetaText);
            detailStack.Children.Add(_detailDescriptionText);

            _detailScrollViewer = new ScrollViewer
            {
                Content = detailStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = DetailPopupPreferredMaxHeight - 8
            };

            _detailBorder = new Border
            {
                Background = BackgroundBrush,
                BorderBrush = BorderBrush_,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = _detailScrollViewer,
                Width = DetailPopupPreferredWidth,
                MaxHeight = DetailPopupPreferredMaxHeight,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 12,
                    ShadowDepth = 2,
                    Opacity = 0.14
                }
            };

            _detailPopup = new Popup
            {
                Child = _detailBorder,
                AllowsTransparency = true,
                Placement = PlacementMode.AbsolutePoint,
                StaysOpen = false,
                Focusable = false,
                PopupAnimation = PopupAnimation.None
            };

            // Prevent the popup from stealing focus
            _popup.Opened += (s, e) =>
            {
                // The popup must not take focus away from the editor
                try
                {
                    var hwndSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(_popup.Child);
                    if (hwndSource != null)
                    {
                        // WS_EX_NOACTIVATE prevents focus stealing
                        const int WS_EX_NOACTIVATE = 0x08000000;
                        const int GWL_EXSTYLE = -20;
                        
                        IntPtr exStyle = NativeMethods.GetWindowLongPtr(hwndSource.Handle, GWL_EXSTYLE);
                        IntPtr newStyle = new IntPtr(exStyle.ToInt64() | WS_EX_NOACTIVATE);
                        NativeMethods.SetWindowLongPtr(hwndSource.Handle, GWL_EXSTYLE, newStyle);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not set WS_EX_NOACTIVATE: {ex.Message}");
                }
            };

            _allItems = new List<CompletionItemData>();
            _filteredItems = new List<CompletionItemData>();
        }

        // ═══════════════════════════════════════════════════════════════
        //  PUBLIC METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows the completion popup at the caret position with the given items.
        /// </summary>
        /// <param name="view">The WPF text view (used for dismissal tracking).</param>
        /// <param name="items">The completion items to display.</param>
        public void Show(IWpfTextView view, List<CompletionItemData> items)
        {
            EnsureUIThread(() =>
            {
                try
                {
                    _currentView = view;
                    _allItems = items ?? new List<CompletionItemData>();
                    _filteredItems = new List<CompletionItemData>(_allItems);
                    _currentPrefix = string.Empty;

                    _listBox.ItemsSource = _filteredItems;
                    Logger.Log($"CompletionPopup.Show with {_filteredItems.Count} item(s)");

                    if (_filteredItems.Count > 0)
                    {
                        _listBox.SelectedIndex = 0;
                    }

                    ResetHorizontalScroll();
                    PositionAtCaret();
                    UpdateDetailPopupForSelection();

                    // Show with fade-in animation
                    _border.Opacity = 0;
                    _popup.IsOpen = true;
                    QueueResetHorizontalScroll();

                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInDurationMs))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    _border.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                catch (Exception ex)
                {
                    Logger.Error("CompletionPopup.Show failed", ex);
                }
            });
        }

        /// <summary>
        /// Dismisses (hides) the completion popup.
        /// </summary>
        public void Dismiss()
        {
            EnsureUIThread(() =>
            {
                try
                {
                    _popup.IsOpen = false;
                    _detailPopup.IsOpen = false;
                    _currentView = null;
                    _allItems = new List<CompletionItemData>();
                    _filteredItems = new List<CompletionItemData>();
                    _listBox.ItemsSource = null;
                }
                catch (Exception ex)
                {
                    Logger.Error("CompletionPopup.Dismiss failed", ex);
                }
            });
        }

        /// <summary>
        /// Moves the selection to the next item in the list.
        /// </summary>
        public void SelectNext()
        {
            EnsureUIThread(() =>
            {
                try
                {
                    if (_listBox.Items.Count == 0) return;

                    int idx = _listBox.SelectedIndex;
                    if (idx < _listBox.Items.Count - 1)
                    {
                        _listBox.SelectedIndex = idx + 1;
                    }
                    _listBox.ScrollIntoView(_listBox.SelectedItem);
                    ResetHorizontalScroll();
                    QueueResetHorizontalScroll();
                }
                catch (Exception ex)
                {
                    Logger.Error("SelectNext failed", ex);
                }
            });
        }

        /// <summary>
        /// Moves the selection to the previous item in the list.
        /// </summary>
        public void SelectPrevious()
        {
            EnsureUIThread(() =>
            {
                try
                {
                    if (_listBox.Items.Count == 0) return;

                    int idx = _listBox.SelectedIndex;
                    if (idx > 0)
                    {
                        _listBox.SelectedIndex = idx - 1;
                    }
                    _listBox.ScrollIntoView(_listBox.SelectedItem);
                    ResetHorizontalScroll();
                    QueueResetHorizontalScroll();
                }
                catch (Exception ex)
                {
                    Logger.Error("SelectPrevious failed", ex);
                }
            });
        }

        /// <summary>
        /// Accepts the currently selected item: dismisses the popup and returns the item.
        /// Also fires the <see cref="ItemAccepted"/> event.
        /// </summary>
        /// <returns>The selected item, or null if nothing was selected.</returns>
        public CompletionItemData AcceptSelected()
        {
            try
            {
                var item = SelectedItem;
                Dismiss();

                if (item != null)
                {
                    ItemAccepted?.Invoke(item);
                }

                return item;
            }
            catch (Exception ex)
            {
                Logger.Error("AcceptSelected failed", ex);
                Dismiss();
                return null;
            }
        }

        /// <summary>
        /// Updates the displayed items by filtering the original list with a new prefix.
        /// Preserves the popup position and keeps the best match selected.
        /// </summary>
        /// <param name="prefix">The new prefix to filter by.</param>
        public void UpdateFilter(string prefix)
        {
            EnsureUIThread(() =>
            {
                try
                {
                    PositionAtCaret();

                    if (string.IsNullOrEmpty(prefix))
                    {
                        _filteredItems = new List<CompletionItemData>(_allItems);
                    }
                    else
                    {
                        _currentPrefix = prefix;
                        _filteredItems = _allItems
                            .Where(item => MatchesFilter(item.Text, prefix))
                            .ToList();
                    }

                    _listBox.ItemsSource = _filteredItems;

                    if (_filteredItems.Count > 0)
                    {
                        // Select the best match (prefer prefix match over substring match)
                        var bestMatch = _filteredItems.FirstOrDefault(item =>
                            item.Text != null &&
                            item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                    _listBox.SelectedItem = bestMatch ?? _filteredItems[0];
                    _listBox.ScrollIntoView(_listBox.SelectedItem);
                    ResetHorizontalScroll();
                    QueueResetHorizontalScroll();
                    UpdateDetailPopupForSelection();
                }

                // If no items match, hide the popup
                if (_filteredItems.Count == 0)
                {
                    _popup.IsOpen = false;
                    _detailPopup.IsOpen = false;
                }
                }
                catch (Exception ex)
                {
                    Logger.Error("UpdateFilter failed", ex);
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        //  LISTBOX CREATION & STYLING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a styled ListBox with virtualization enabled.
        /// </summary>
        private ListBox CreateListBox()
        {
            var listBox = new ListBox
            {
                Background = BackgroundBrush,
                Foreground = ForegroundBrush,
                BorderThickness = new Thickness(0),
                MaxHeight = PopupMaxHeight - 4, // Account for border
                Focusable = false,
                Padding = new Thickness(2),
                // Enable virtualization
                ItemsPanel = new ItemsPanelTemplate(
                    new FrameworkElementFactory(typeof(VirtualizingStackPanel)))
            };

            // Enable UI virtualization
            VirtualizingPanel.SetIsVirtualizing(listBox, true);
            VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(listBox, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);

            // Custom item template (generated in code)
            listBox.ItemTemplate = CreateItemTemplate();

            // Custom style for ListBoxItem (selection colors)
            listBox.ItemContainerStyle = CreateItemContainerStyle();

            // Handle mouse double-click for acceptance
            listBox.MouseDoubleClick += OnListBoxMouseDoubleClick;

            // Single click selection
            listBox.PreviewMouseLeftButtonDown += OnListBoxPreviewMouseDown;
            listBox.MouseLeave += OnListBoxMouseLeave;

            return listBox;
        }

        /// <summary>
        /// Creates the data template for each completion item.
        /// Format: [Icon] Text
        /// </summary>
        private DataTemplate CreateItemTemplate()
        {
            var template = new DataTemplate(typeof(CompletionItemData));

            var rowBorderFactory = new FrameworkElementFactory(typeof(Border));
            rowBorderFactory.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("RowTintBrush"));
            rowBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            rowBorderFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            rowBorderFactory.SetValue(Border.PaddingProperty, new Thickness(0));
            rowBorderFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            // Root: horizontal StackPanel
            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            stackFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));

            // Compact kind badge
            var badgeFactory = new FrameworkElementFactory(typeof(Border));
            badgeFactory.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("BadgeBackgroundBrush"));
            badgeFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            badgeFactory.SetValue(FrameworkElement.WidthProperty, 22.0);
            badgeFactory.SetValue(FrameworkElement.HeightProperty, 18.0);
            badgeFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 8, 1));
            badgeFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconFactory = new FrameworkElementFactory(typeof(Path));
            iconFactory.SetBinding(Path.DataProperty,
                new System.Windows.Data.Binding("IconGeometry"));
            iconFactory.SetBinding(Shape.FillProperty,
                new System.Windows.Data.Binding("BadgeForegroundBrush"));
            iconFactory.SetValue(FrameworkElement.WidthProperty, 12.0);
            iconFactory.SetValue(FrameworkElement.HeightProperty, 12.0);
            iconFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            iconFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            iconFactory.SetValue(Path.StretchProperty, Stretch.Uniform);
            badgeFactory.AppendChild(iconFactory);
            stackFactory.AppendChild(badgeFactory);

            var favoriteFactory = new FrameworkElementFactory(typeof(TextBlock));
            favoriteFactory.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("FavoriteMarker"));
            favoriteFactory.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding("FavoriteBrush"));
            favoriteFactory.SetValue(TextBlock.FontSizeProperty, 12.0);
            favoriteFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            favoriteFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            favoriteFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            stackFactory.AppendChild(favoriteFactory);

            // Main text TextBlock
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("Text"));
            textFactory.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding("PrimaryTextBrush"));
            textFactory.SetValue(TextBlock.FontSizeProperty, 12.5);
            textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            textFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            textFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            stackFactory.AppendChild(textFactory);

            rowBorderFactory.AppendChild(stackFactory);
            template.VisualTree = rowBorderFactory;
            return template;
        }

        /// <summary>
        /// Creates a custom style for ListBoxItem to use dark-themed selection colors.
        /// </summary>
        private Style CreateItemContainerStyle()
        {
            var style = new Style(typeof(ListBoxItem));

            // Default background
            style.Setters.Add(new Setter(Control.BackgroundProperty, TransparentBrush));
            style.Setters.Add(new Setter(Control.ForegroundProperty, ForegroundBrush));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, TransparentBrush));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 3, 2, 3)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(2, 1, 2, 1)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 26.0));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            // Selected state trigger
            var selectedTrigger = new Trigger
            {
                Property = ListBoxItem.IsSelectedProperty,
                Value = true
            };
            selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, SelectionBrush));
            selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, ForegroundBrush));
            selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, SelectionStripeBrush));
            selectedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(4, 0, 0, 0)));
            style.Triggers.Add(selectedTrigger);

            var hoverTrigger = new Trigger
            {
                Property = ListBoxItem.IsMouseOverProperty,
                Value = true
            };
            hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, HoverBrush));
            hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, ForegroundBrush));
            style.Triggers.Add(hoverTrigger);

            var selectedHoverTrigger = new MultiTrigger();
            selectedHoverTrigger.Conditions.Add(new Condition(ListBoxItem.IsSelectedProperty, true));
            selectedHoverTrigger.Conditions.Add(new Condition(ListBoxItem.IsMouseOverProperty, true));
            selectedHoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, SelectionBrush));
            selectedHoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, ForegroundBrush));
            selectedHoverTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, SelectionStripeBrush));
            selectedHoverTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(4, 0, 0, 0)));
            style.Triggers.Add(selectedHoverTrigger);

            return style;
        }

        // ═══════════════════════════════════════════════════════════════
        //  EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles mouse double-click on a list item to accept it.
        /// </summary>
        private void OnListBoxMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                AcceptSelected();
            }
            catch (Exception ex)
            {
                Logger.Error("OnListBoxMouseDoubleClick failed", ex);
            }
        }

        /// <summary>
        /// Handles mouse-down to select items without stealing focus from the editor.
        /// </summary>
        private void OnListBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Find the ListBoxItem under the mouse
                var element = e.OriginalSource as DependencyObject;
                while (element != null && !(element is ListBoxItem))
                {
                    element = VisualTreeHelper.GetParent(element);
                }

                if (element is ListBoxItem listBoxItem)
                {
                    listBoxItem.IsSelected = true;
                    // Don't mark handled — allow double-click to fire
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnListBoxPreviewMouseDown failed", ex);
            }
        }

        private void OnListBoxMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                var element = e.OriginalSource as DependencyObject;
                while (element != null && !(element is ListBoxItem))
                    element = VisualTreeHelper.GetParent(element);

                if (element is ListBoxItem listBoxItem && listBoxItem.DataContext is CompletionItemData item)
                    UpdateDetailPopup(item);
            }
            catch (Exception ex)
            {
                Logger.Warn($"OnListBoxMouseMove failed: {ex.Message}");
            }
        }

        private void OnListBoxMouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                UpdateDetailPopupForSelection();
            }
            catch (Exception ex)
            {
                Logger.Warn($"OnListBoxMouseLeave failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks if a completion item text matches the given filter prefix (case-insensitive).
        /// </summary>
        private static bool MatchesFilter(string text, string prefix)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            // Prefix match
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

            // Substring match
            if (text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        /// <summary>
        /// Anchors the popup to the editor visual at the current caret position.
        /// </summary>
        private void PositionAtCaret()
        {
            try
            {
                if (_currentView?.VisualElement == null)
                    return;

                Point caretPos = _currentView.GetCaretPopupPosition();
                _popup.PlacementTarget = _currentView.VisualElement;
                _popup.HorizontalOffset = caretPos.X;
                _popup.VerticalOffset = caretPos.Y + 2;
            }
            catch (Exception ex)
            {
                Logger.Error("PositionAtCaret failed", ex);
            }
        }

        private void PositionDetailPopup()
        {
            try
            {
                if (_currentView?.VisualElement == null || !_popup.IsOpen)
                    return;

                var layout = CalculateDetailPopupLayout();

                _detailPopup.PlacementTarget = null;
                _detailPopup.HorizontalOffset = layout.HorizontalOffset;
                _detailPopup.VerticalOffset = layout.VerticalOffset;
                _detailBorder.Width = layout.Width;
                _detailBorder.Height = layout.Height;
                _detailBorder.MaxHeight = layout.Height;
                _detailScrollViewer.Height = Math.Max(DetailPopupMinHeight - 8, layout.Height - 8);
                _detailScrollViewer.MaxHeight = Math.Max(DetailPopupMinHeight - 8, layout.Height - 8);
            }
            catch (Exception ex)
            {
                Logger.Warn($"PositionDetailPopup failed: {ex.Message}");
            }
        }

        private void UpdateDetailPopupForSelection()
        {
            UpdateDetailPopup(SelectedItem);
        }

        private void UpdateDetailPopup(CompletionItemData item)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Description) || !_popup.IsOpen)
                {
                    _detailPopup.IsOpen = false;
                    return;
                }

                var title = BuildDetailTitle(item);
                var meta = BuildDetailMeta(item);
                var body = BuildDetailBody(item);

                _detailTitleText.Text = title;
                _detailMetaText.Text = meta;
                _detailDescriptionText.Text = body;

                UpdateDetailPopupSize(title, meta, body);
                PositionDetailPopup();

                if (!_detailPopup.IsOpen)
                    _detailPopup.IsOpen = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateDetailPopup failed: {ex.Message}");
            }
        }

        private void ResetHorizontalScroll()
        {
            try
            {
                _listBox.ApplyTemplate();

                var scrollViewer = FindVisualChild<ScrollViewer>(_listBox);
                scrollViewer?.ScrollToLeftEnd();
            }
            catch (Exception ex)
            {
                Logger.Warn($"ResetHorizontalScroll failed: {ex.Message}");
            }
        }

        private void QueueResetHorizontalScroll()
        {
            try
            {
                var dispatcher = _listBox.Dispatcher;
                if (dispatcher == null)
                    return;

                dispatcher.BeginInvoke(new Action(ResetHorizontalScroll), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                Logger.Warn($"QueueResetHorizontalScroll failed: {ex.Message}");
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    return typed;

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        private static string BuildDetailTitle(CompletionItemData item)
        {
            if (item == null)
                return string.Empty;

            return item.Text ?? string.Empty;
        }

        private static string BuildDetailMeta(CompletionItemData item)
        {
            if (item == null)
                return string.Empty;

            string kindLabel = GetKindLabel(item.Kind);
            if (item.IsFavorite)
                kindLabel += "  |  Favorite";

            return kindLabel;
        }

        private static string BuildDetailBody(CompletionItemData item)
        {
            if (item == null)
                return string.Empty;

            var description = item.Description ?? string.Empty;
            if (string.IsNullOrWhiteSpace(description))
                return string.Empty;

            int separatorIndex = description.IndexOf(':');
            if (separatorIndex > 0 && separatorIndex < description.Length - 1)
            {
                string body = description.Substring(separatorIndex + 1).Trim();
                if (item.Kind == CompletionItemKind.Function || item.Kind == CompletionItemKind.Procedure)
                    return FormatSignatureBody(body);

                return body;
            }

            return item.Kind == CompletionItemKind.Function || item.Kind == CompletionItemKind.Procedure
                ? FormatSignatureBody(description)
                : description;
        }

        private static string FormatSignatureBody(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();
            int openParen = value.IndexOf('(');
            int closeParen = value.LastIndexOf(')');
            if (openParen <= 0 || closeParen <= openParen)
                return value;

            string name = value.Substring(0, openParen).Trim();
            string parameters = value.Substring(openParen + 1, closeParen - openParen - 1).Trim();

            if (string.IsNullOrWhiteSpace(parameters))
                return name + "()";

            var parts = parameters
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();

            if (parts.Count == 0)
                return name + "()";

            return name + "(" + Environment.NewLine +
                   "    " + string.Join("," + Environment.NewLine + "    ", parts) + Environment.NewLine +
                   ")";
        }

        private static string GetKindLabel(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table:
                    return "Table";
                case CompletionItemKind.View:
                    return "View";
                case CompletionItemKind.Alias:
                    return "Alias";
                case CompletionItemKind.Column:
                    return "Column";
                case CompletionItemKind.Function:
                    return "Function";
                case CompletionItemKind.Procedure:
                    return "Procedure";
                case CompletionItemKind.Database:
                    return "Database";
                case CompletionItemKind.Schema:
                    return "Schema";
                case CompletionItemKind.Snippet:
                    return "Snippet";
                case CompletionItemKind.Keyword:
                    return "Keyword";
                case CompletionItemKind.JoinSuggestion:
                    return "Join suggestion";
                case CompletionItemKind.DataType:
                    return "Data type";
                case CompletionItemKind.Status:
                    return "Status";
                default:
                    return kind.ToString();
            }
        }

        private void UpdateDetailPopupSize(string title, string meta, string body)
        {
            try
            {
                double availableWidth = GetMaxAvailableDetailWidth();
                if (availableWidth <= 0)
                    availableWidth = DetailPopupPreferredWidth;

                double maxWidth = Math.Min(DetailPopupMaxWidth, availableWidth);
                double measuredWidth = Math.Max(
                    MeasureTextWidth(title, _detailTitleText),
                    Math.Max(
                        MeasureTextWidth(meta, _detailMetaText),
                        MeasureTextWidth(body, _detailDescriptionText)));

                double desiredWidth = measuredWidth + 42;
                _detailBorder.Width = Math.Max(
                    DetailPopupMinWidth,
                    Math.Min(maxWidth, Math.Max(DetailPopupPreferredWidth, desiredWidth)));
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateDetailPopupSize failed: {ex.Message}");
                _detailBorder.Width = DetailPopupPreferredWidth;
            }
        }

        private DetailPopupLayout CalculateDetailPopupLayout()
        {
            double preferredWidth = _detailBorder.Width > 0
                ? _detailBorder.Width
                : DetailPopupPreferredWidth;

            if (_currentView?.VisualElement == null || _border == null)
            {
                return new DetailPopupLayout
                {
                    Width = preferredWidth,
                    Height = DetailPopupPreferredMaxHeight,
                    HorizontalOffset = _popup.HorizontalOffset + PopupWidth + DetailPopupGap,
                    VerticalOffset = _popup.VerticalOffset
                };
            }

            _border.UpdateLayout();

            double maxWidth = Math.Max(DetailPopupMinWidth, Math.Min(preferredWidth, DetailPopupMaxWidth));
            _detailBorder.Width = maxWidth;
            _detailBorder.Measure(new Size(maxWidth, DetailPopupMaxHeight));

            Point mainTopLeft = ToDipScreenPoint(_border, new Point(0, 0));
            var mainBounds = new Rect(mainTopLeft.X, mainTopLeft.Y, _border.ActualWidth > 0 ? _border.ActualWidth : PopupWidth, _border.ActualHeight > 0 ? _border.ActualHeight : PopupMaxHeight);
            Rect workArea = SystemParameters.WorkArea;

            double rightSpace = workArea.Right - (mainBounds.Right + DetailPopupGap + DetailPopupViewportMargin);
            double leftSpace = mainBounds.Left - workArea.Left - DetailPopupGap - DetailPopupViewportMargin;
            bool placeRight = rightSpace >= preferredWidth || rightSpace >= leftSpace;

            double width = Math.Max(
                DetailPopupMinWidth,
                Math.Min(DetailPopupMaxWidth, Math.Max(Math.Min(preferredWidth, placeRight ? rightSpace : leftSpace), DetailPopupMinWidth)));

            _detailBorder.Width = width;
            _detailBorder.Measure(new Size(width, DetailPopupMaxHeight));
            double top = Math.Max(workArea.Top + DetailPopupViewportMargin, mainBounds.Top);
            double availableHeight = workArea.Bottom - top - DetailPopupViewportMargin;
            double targetHeight = Math.Max(
                DetailPopupMinHeight,
                Math.Min(DetailPopupMaxHeight, Math.Min(mainBounds.Height, availableHeight)));

            double verticalOffset = top;
            if (verticalOffset + targetHeight > workArea.Bottom - DetailPopupViewportMargin)
            {
                verticalOffset = Math.Max(
                    workArea.Top + DetailPopupViewportMargin,
                    workArea.Bottom - DetailPopupViewportMargin - targetHeight);
            }

            if (placeRight)
            {
                return new DetailPopupLayout
                {
                    Width = width,
                    Height = targetHeight,
                    HorizontalOffset = mainBounds.Right + DetailPopupGap,
                    VerticalOffset = verticalOffset
                };
            }

            return new DetailPopupLayout
            {
                Width = width,
                Height = targetHeight,
                HorizontalOffset = Math.Max(
                    workArea.Left + DetailPopupViewportMargin,
                    mainBounds.Left - width - DetailPopupGap),
                VerticalOffset = verticalOffset
            };
        }

        private double GetMaxAvailableDetailWidth()
        {
            try
            {
                if (_currentView?.VisualElement == null)
                    return DetailPopupPreferredWidth;

                var viewWidth = _currentView.VisualElement.ActualWidth;
                var rightSpace = viewWidth - (_popup.HorizontalOffset + PopupWidth + DetailPopupGap + DetailPopupViewportMargin);
                var leftSpace = _popup.HorizontalOffset - DetailPopupGap - DetailPopupViewportMargin;
                var usableSpace = Math.Max(rightSpace, leftSpace);
                return Math.Max(usableSpace, DetailPopupMinWidth);
            }
            catch
            {
                return DetailPopupPreferredWidth;
            }
        }

        private sealed class DetailPopupLayout
        {
            public double Width { get; set; }
            public double Height { get; set; }
            public double HorizontalOffset { get; set; }
            public double VerticalOffset { get; set; }
        }

        private static double MeasureTextWidth(string text, TextBlock template)
        {
            if (string.IsNullOrWhiteSpace(text) || template == null)
                return 0;

            double pixelsPerDip = 1.0;
            if (Application.Current?.MainWindow != null)
                pixelsPerDip = VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip;

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    template.FontFamily,
                    template.FontStyle,
                    template.FontWeight,
                    template.FontStretch),
                template.FontSize,
                Brushes.Black,
                pixelsPerDip);

            var longestLine = text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .DefaultIfEmpty(string.Empty)
                .OrderByDescending(line => line.Length)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(longestLine))
            {
                formatted = new FormattedText(
                    longestLine,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(
                        template.FontFamily,
                        template.FontStyle,
                        template.FontWeight,
                        template.FontStretch),
                    template.FontSize,
                    Brushes.Black,
                    pixelsPerDip);
            }

            return formatted.WidthIncludingTrailingWhitespace;
        }

        private Point ToDipScreenPoint(Visual visual, Point point)
        {
            if (visual == null)
                return point;

            Point screenPoint = visual.PointToScreen(point);
            var source = PresentationSource.FromVisual(visual);
            if (source?.CompositionTarget == null)
                return screenPoint;

            return source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
        }

        /// <summary>
        /// Ensures the given action runs on the UI thread. If already on the UI thread,
        /// executes synchronously; otherwise dispatches to the UI thread.
        /// </summary>
        private void EnsureUIThread(Action action)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher ?? _popup.Dispatcher;
                if (dispatcher == null)
                {
                    action();
                    return;
                }

                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("EnsureUIThread failed", ex);
            }
        }

        /// <summary>
        /// Freezes all shared brushes for thread safety.
        /// </summary>
        private static void FreezeBrushes()
        {
            try
            {
                if (BackgroundBrush.CanFreeze) BackgroundBrush.Freeze();
                if (ForegroundBrush.CanFreeze) ForegroundBrush.Freeze();
                if (SelectionBrush.CanFreeze) SelectionBrush.Freeze();
                if (HoverBrush.CanFreeze) HoverBrush.Freeze();
                if (BorderBrush_.CanFreeze) BorderBrush_.Freeze();
                if (DescriptionBrush.CanFreeze) DescriptionBrush.Freeze();
                if (IconBackgroundBrush.CanFreeze) IconBackgroundBrush.Freeze();
                if (IconForegroundBrush.CanFreeze) IconForegroundBrush.Freeze();
            }
            catch
            {
                // Non-critical — brushes still work if not frozen
            }
        }

        /// <summary>
        /// Native Win32 interop for preventing focus stealing.
        /// Handles 32-bit (SSMS 18-20) and 64-bit (SSMS 21+) platforms.
        /// </summary>
        private static class NativeMethods
        {
            public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            {
                if (IntPtr.Size == 8)
                    return GetWindowLongPtr64(hWnd, nIndex);
                else
                    return GetWindowLongPtr32(hWnd, nIndex);
            }

            public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
            {
                if (IntPtr.Size == 8)
                    return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
                else
                    return SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
            }

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
            private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
            private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
            private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
            private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        }
    }
}

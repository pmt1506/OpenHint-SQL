using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OpenHintSQL.Settings;
using OpenHintSQL.Snippets;

namespace OpenHintSQL.UI
{
    internal sealed class SettingsWindow : Window
    {
        private readonly CheckBox _omitSchemaCheckBox;
        private readonly ObservableCollection<CustomSnippetRow> _rows;
        private readonly DataGrid _grid;

        private SettingsWindow(OpenHintSqlSettings settings)
        {
            Title = "OpenHint SQL Settings";
            Width = 720;
            Height = 520;
            MinWidth = 620;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.White;

            _rows = new ObservableCollection<CustomSnippetRow>(
                settings.CustomSnippets.Select(entry => new CustomSnippetRow
                {
                    Shortcut = entry.Shortcut,
                    Expansion = entry.Expansion,
                    Description = entry.Description
                }));

            var root = new DockPanel { Margin = new Thickness(16) };
            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = "Insert Behavior",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _omitSchemaCheckBox = new CheckBox
            {
                Content = "Bỏ tên schema ở phía trước",
                IsChecked = settings.OmitDboSchemaOnInsert,
                Margin = new Thickness(0, 0, 0, 20)
            };
            content.Children.Add(_omitSchemaCheckBox);

            content.Children.Add(new TextBlock
            {
                Text = "Custom Snippets",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Them viet tat kieu ssf -> SELECT * FROM. Expansion ho tro $cursor$ va \\n.",
                Foreground = new SolidColorBrush(Color.FromRgb(87, 96, 106)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var addButton = new Button
            {
                Content = "Thêm dòng",
                MinWidth = 96,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4)
            };
            addButton.Click += (_, __) => _rows.Add(new CustomSnippetRow());
            toolbar.Children.Add(addButton);

            var removeButton = new Button
            {
                Content = "Xóa dòng",
                MinWidth = 96,
                Padding = new Thickness(12, 4, 12, 4)
            };
            removeButton.Click += (_, __) =>
            {
                if (_grid.SelectedItem is CustomSnippetRow row)
                    _rows.Remove(row);
            };
            toolbar.Children.Add(removeButton);
            content.Children.Add(toolbar);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                RowHeaderWidth = 0,
                ItemsSource = _rows
            };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Shortcut",
                Binding = new Binding(nameof(CustomSnippetRow.Shortcut)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(130)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Expansion",
                Binding = new Binding(nameof(CustomSnippetRow.Expansion)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Description",
                Binding = new Binding(nameof(CustomSnippetRow.Description)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(220)
            });
            content.Children.Add(_grid);

            root.Children.Add(content);
            Content = root;
        }

        public static void Show(Window owner = null)
        {
            var window = new SettingsWindow(SettingsProvider.GetSettings()) { Owner = owner };
            window.ShowDialog();
        }

        private UIElement BuildFooter()
        {
            var panel = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
            var hint = new TextBlock
            {
                Text = "Mở bằng Ctrl+Alt+Q trong query editor.",
                Foreground = new SolidColorBrush(Color.FromRgb(87, 96, 106)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(hint, Dock.Left);
            panel.Children.Add(hint);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelButton = new Button
            {
                Content = "Huỷ",
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 5, 12, 5),
                IsCancel = true
            };
            buttons.Children.Add(cancelButton);

            var saveButton = new Button
            {
                Content = "Lưu",
                MinWidth = 88,
                Padding = new Thickness(12, 5, 12, 5),
                IsDefault = true
            };
            saveButton.Click += (_, __) => SaveAndClose();
            buttons.Children.Add(saveButton);

            DockPanel.SetDock(buttons, Dock.Right);
            panel.Children.Add(buttons);
            return panel;
        }

        private void SaveAndClose()
        {
            var settings = new OpenHintSqlSettings
            {
                OmitDboSchemaOnInsert = _omitSchemaCheckBox.IsChecked == true
            };

            foreach (var row in _rows)
            {
                var shortcut = row.Shortcut?.Trim();
                var expansion = row.Expansion?.Trim();
                var description = row.Description?.Trim();
                if (string.IsNullOrWhiteSpace(shortcut) || string.IsNullOrWhiteSpace(expansion))
                    continue;

                settings.CustomSnippets.Add(new CustomSnippetEntry
                {
                    Shortcut = shortcut,
                    Expansion = expansion,
                    Description = description
                });
            }

            SettingsProvider.SaveSettings(settings);
            SnippetProvider.Instance.Reload();
            DialogResult = true;
            Close();
        }

        private sealed class CustomSnippetRow
        {
            public string Shortcut { get; set; }

            public string Expansion { get; set; }

            public string Description { get; set; }
        }
    }
}
